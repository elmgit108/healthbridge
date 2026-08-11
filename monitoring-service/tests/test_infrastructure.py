"""
Tests for the infrastructure adapters: HTTP health checking and the two metric
publishers.

The HTTP and boto3 calls are stubbed at the module boundary, so no network
traffic and no AWS credentials are needed.
"""

import datetime

import pytest
import requests

from infrastructure.aws_publisher import CloudWatchPublisher
from infrastructure.azure_publisher import AzureMonitorPublisher
from infrastructure.http_health_checker import HttpHealthChecker


# --- HttpHealthChecker ----------------------------------------------------


class FakeResponse:
    def __init__(self, status_code):
        self.status_code = status_code


def test_a_200_response_is_healthy(monkeypatch):
    monkeypatch.setattr(requests, "get", lambda url, timeout: FakeResponse(200))

    result = HttpHealthChecker().check_health("http://hl7-service:5001")

    assert result["status"] == "healthy"


@pytest.mark.parametrize("status_code", [201, 204, 301, 400, 404, 500, 503])
def test_any_non_200_response_is_unhealthy(monkeypatch, status_code):
    # Deliberately strict: only 200 counts. A 204 or a 301 from a misconfigured
    # reverse proxy should not read as a healthy service.
    monkeypatch.setattr(requests, "get", lambda url, timeout: FakeResponse(status_code))

    result = HttpHealthChecker().check_health("http://hl7-service:5001")

    assert result["status"] == "unhealthy"


def test_a_connection_error_is_unhealthy_rather_than_an_exception(monkeypatch):
    # A down service is the normal case for this code path — it must never take
    # the sweep or the /metrics request down with it.
    def refuse(url, timeout):
        raise requests.exceptions.ConnectionError("connection refused")

    monkeypatch.setattr(requests, "get", refuse)

    result = HttpHealthChecker().check_health("http://hl7-service:5001")

    assert result["status"] == "unhealthy"


def test_a_timeout_is_unhealthy(monkeypatch):
    def time_out(url, timeout):
        raise requests.exceptions.Timeout("timed out")

    monkeypatch.setattr(requests, "get", time_out)

    assert HttpHealthChecker().check_health("http://x:1")["status"] == "unhealthy"


def test_the_health_endpoint_path_is_appended_to_the_service_url(monkeypatch):
    called = {}

    def capture(url, timeout):
        called["url"] = url
        called["timeout"] = timeout
        return FakeResponse(200)

    monkeypatch.setattr(requests, "get", capture)

    HttpHealthChecker().check_health("http://hl7-service:5001")

    assert called["url"] == "http://hl7-service:5001/health"


def test_a_trailing_slash_does_not_produce_a_double_slash(monkeypatch):
    # http://host//health would 404 on some servers.
    called = {}
    monkeypatch.setattr(requests, "get", lambda url, timeout: (called.update(url=url), FakeResponse(200))[1])

    HttpHealthChecker().check_health("http://hl7-service:5001/")

    assert called["url"] == "http://hl7-service:5001/health"


def test_a_bounded_timeout_is_always_supplied(monkeypatch):
    # Without a timeout a hung service would block the sweep thread indefinitely
    # and stall every subsequent health check.
    called = {}
    monkeypatch.setattr(requests, "get", lambda url, timeout: (called.update(timeout=timeout), FakeResponse(200))[1])

    HttpHealthChecker().check_health("http://hl7-service:5001")

    assert called["timeout"] == 3


def test_the_result_carries_an_iso8601_utc_timestamp(monkeypatch):
    monkeypatch.setattr(requests, "get", lambda url, timeout: FakeResponse(200))

    result = HttpHealthChecker().check_health("http://hl7-service:5001")

    assert result["timestamp"].endswith("Z")
    datetime.datetime.fromisoformat(result["timestamp"].rstrip("Z"))   # raises if malformed


def test_a_timestamp_is_recorded_even_when_the_check_fails(monkeypatch):
    def refuse(url, timeout):
        raise requests.exceptions.ConnectionError()

    monkeypatch.setattr(requests, "get", refuse)

    result = HttpHealthChecker().check_health("http://hl7-service:5001")

    assert result["timestamp"].endswith("Z")


# --- CloudWatchPublisher --------------------------------------------------


class FakeCloudWatchClient:
    def __init__(self, fail=False):
        self.fail = fail
        self.calls = []

    def put_metric_data(self, **kwargs):
        if self.fail:
            raise RuntimeError("AWS rejected the request")
        self.calls.append(kwargs)


def publisher_with(client):
    publisher = CloudWatchPublisher.__new__(CloudWatchPublisher)   # skip boto3 construction
    publisher.client = client
    return publisher


def test_a_metric_is_published_under_the_healthbridge_namespace():
    # Terraform's cloudwatch.tf defines alarms against this exact namespace and
    # metric name, so a rename here silently breaks alerting.
    client = FakeCloudWatchClient()
    publisher = publisher_with(client)

    assert publisher.push_metric("ServiceHealth", 1.0) is True

    call = client.calls[0]
    assert call["Namespace"] == "HealthBridge/Services"
    assert call["MetricData"][0]["MetricName"] == "ServiceHealth"
    assert call["MetricData"][0]["Value"] == 1.0


def test_dimensions_are_converted_to_the_cloudwatch_name_value_shape():
    client = FakeCloudWatchClient()

    publisher_with(client).push_metric("ServiceHealth", 0.0, {"ServiceName": "hl7-service"})

    assert client.calls[0]["MetricData"][0]["Dimensions"] == [
        {"Name": "ServiceName", "Value": "hl7-service"}
    ]


def test_absent_dimensions_become_an_empty_list():
    client = FakeCloudWatchClient()

    publisher_with(client).push_metric("CustomMetric", 5.0)

    assert client.calls[0]["MetricData"][0]["Dimensions"] == []


def test_an_aws_error_returns_false_instead_of_raising():
    # The sweep loop calls this for every service; an exception here would abort
    # the whole sweep (see the KNOWN_GAP test in test_monitoring_manager.py).
    publisher = publisher_with(FakeCloudWatchClient(fail=True))

    assert publisher.push_metric("ServiceHealth", 1.0) is False


def test_an_uninitialised_client_returns_false_without_raising():
    # Happens when boto3 cannot find credentials — common in local dev.
    assert publisher_with(None).push_metric("ServiceHealth", 1.0) is False


def test_the_publisher_survives_boto3_failing_at_construction(monkeypatch):
    import infrastructure.aws_publisher as aws_module

    def explode(*args, **kwargs):
        raise RuntimeError("no credentials configured")

    monkeypatch.setattr(aws_module.boto3, "client", explode)

    publisher = CloudWatchPublisher()   # must not raise — the service still starts

    assert publisher.client is None
    assert publisher.push_metric("ServiceHealth", 1.0) is False


# --- AzureMonitorPublisher ------------------------------------------------


def test_the_azure_stub_reports_success():
    assert AzureMonitorPublisher().push_metric("ServiceHealth", 1.0) is True


def test_the_azure_stub_accepts_dimensions():
    assert AzureMonitorPublisher().push_metric("ServiceHealth", 0.0, {"ServiceName": "gateway"}) is True


def test_both_publishers_satisfy_the_same_interface():
    # This substitutability is what lets MonitoringManager stay cloud-agnostic.
    from core.interfaces import IMetricPublisher

    assert isinstance(AzureMonitorPublisher(), IMetricPublisher)
    assert isinstance(publisher_with(None), IMetricPublisher)
