"""
Tests for the Flask routes.

The app is assembled here directly rather than through app.create_app(), which
would also start OpenTelemetry exporters and the APScheduler background thread.
initialize_routes() is the seam that makes that possible.
"""

from core import models
import dataclasses

import pytest
from flask import Flask

from api.routes import initialize_routes
from infrastructure.aws_publisher import CloudWatchPublisher
from infrastructure.azure_publisher import AzureMonitorPublisher
from services.monitoring_manager import MonitoringManager
from tests.conftest import FakeHealthChecker
from core.models import HealthStatus

def build_client(checker=None, publishers=None, services=None):
    """Builds a Flask test client wired to a manager with the given collaborators."""
    manager = MonitoringManager(
        checker or FakeHealthChecker(default= HealthStatus.HEALTHY),
        publishers if publishers is not None else [],
        services or {"hl7-service": "http://hl7-service:5001", "gateway": "http://gateway:8080"},
    )

    app = Flask(__name__, template_folder="../templates")
    app.config.update(TESTING=True)
    initialize_routes(app, manager)

    return app.test_client(), manager


# --- GET /health ----------------------------------------------------------


def test_health_returns_200_without_polling_downstream():
    # This service's own liveness must not depend on its dependencies, or a single
    # downstream outage takes the monitoring service out of the load balancer too.
    checker = FakeHealthChecker(default="unhealthy")
    client, _ = build_client(checker=checker)

    response = client.get("/health")

    assert response.status_code == 200
    assert response.get_json() == {"status": "healthy", "service": "monitoring-service"}
    assert checker.checked_urls == []   # nothing was polled


# --- GET /metrics ---------------------------------------------------------


def test_metrics_returns_200_and_the_aggregate_when_all_services_are_up():
    client, _ = build_client()

    response = client.get("/metrics")

    assert response.status_code == 200
    payload = response.get_json()
    assert payload["status"] == "healthy"
    assert payload["service"] == "monitoring-service"
    assert set(payload["components"]) == {"hl7-service", "gateway"}


def test_metrics_returns_503_when_a_service_is_down():
    # The gateway and any external uptime check treat this status code as the signal.
    checker = FakeHealthChecker(statuses={"http://gateway:8080": "unhealthy"})
    client, _ = build_client(checker=checker)

    response = client.get("/metrics")

    assert response.status_code == 503
    assert response.get_json()["status"] == "unhealthy"


def test_metrics_serialises_nested_component_dataclasses():
    # dataclasses.asdict() has to recurse into ServiceHealth, otherwise the
    # components come out as unserialisable objects and Flask 500s.
    client, _ = build_client()

    component = client.get("/metrics").get_json()["components"]["hl7-service"]

    assert component == {
        "service": "hl7-service",
        "status": "healthy",
        "timestamp": "2024-01-15T12:00:00Z",
    }


def test_metrics_response_matches_the_dataclass_shape():
    client, manager = build_client()

    payload = client.get("/metrics").get_json()

    assert set(payload) == set(dataclasses.asdict(manager.check_all_services()))


# --- POST /metrics/push/aws -----------------------------------------------


class FakeCloudWatch(CloudWatchPublisher):
    """A CloudWatchPublisher that records instead of calling AWS."""

    def __init__(self):
        self.client = None
        self.published = []

    def push_metric(self, name, value, dimensions=None):
        self.published.append((name, value, dimensions))
        return True


class FakeAzure(AzureMonitorPublisher):
    def __init__(self):
        self.published = []

    def push_metric(self, name, value, dimensions=None):
        self.published.append((name, value, dimensions))
        return True


def test_push_sends_the_named_metric_to_the_aws_publisher_only():
    aws, azure = FakeCloudWatch(), FakeAzure()
    client, _ = build_client(publishers=[aws, azure])

    response = client.post("/metrics/push/aws", json={"name": "TestMetric", "value": 42.0})

    assert response.status_code == 200
    assert response.get_json() == {"status": "pushed"}
    assert aws.published == [("TestMetric", 42.0, None)]
    assert azure.published == []   # routing is by publisher type


def test_push_defaults_the_metric_name_and_value_when_omitted():
    aws = FakeCloudWatch()
    client, _ = build_client(publishers=[aws])

    client.post("/metrics/push/aws", json={})

    assert aws.published == [("CustomMetric", 1.0, None)]


@pytest.mark.parametrize(
    "kwargs,expected_status",
    [
        (dict(content_type="application/json"), 400),                    # declared JSON, empty body
        (dict(data="not json", content_type="application/json"), 400),   # declared JSON, malformed
        (dict(), 415),                                                   # no content type at all
    ],
)
def test_push_rejects_a_missing_or_malformed_body(kwargs, expected_status):
    # Pins real behaviour, and shows the `data = request.json or {}` guard in
    # routes.py is vestigial: on modern Flask, request.json RAISES for an absent or
    # malformed body rather than returning None, so the `or {}` never runs. Clients
    # get 400/415, not the defaulted CustomMetric the guard implies.
    aws = FakeCloudWatch()
    client, _ = build_client(publishers=[aws])

    response = client.post("/metrics/push/aws", **kwargs)

    assert response.status_code == expected_status
    assert aws.published == []


def test_push_reports_failure_when_no_aws_publisher_is_registered():
    # The loop finds no match, so nothing is published. The caller must be told:
    # answering 200 here is what let the endpoint claim success while doing
    # nothing at all.
    client, _ = build_client(publishers=[FakeAzure()])

    response = client.post("/metrics/push/aws", json={"name": "M", "value": 1.0})

    assert response.status_code == 502
    assert response.get_json() == {"status": "publish failed"}


def test_push_reports_failure_when_the_publisher_returns_false():
    # A registered publisher whose backend rejected the metric. Previously this
    # also returned 200 — the result of push_metric was discarded.
    class FailingCloudWatch(FakeCloudWatch):
        def push_metric(self, name, value, dimensions=None):
            super().push_metric(name, value, dimensions)
            return False

    aws = FailingCloudWatch()
    client, _ = build_client(publishers=[aws])

    response = client.post("/metrics/push/aws", json={"name": "M", "value": 1.0})

    assert response.status_code == 502
    assert aws.published == [("M", 1.0, None)]   # it was attempted


# --- POST /metrics/push/azure ---------------------------------------------


def test_azure_push_sends_to_the_azure_publisher_only():
    aws, azure = FakeCloudWatch(), FakeAzure()
    client, _ = build_client(publishers=[aws, azure])

    response = client.post("/metrics/push/azure", json={"name": "TestMetric", "value": 7.0})

    assert response.status_code == 200
    assert response.get_json() == {"status": "pushed"}
    assert azure.published == [("TestMetric", 7.0, None)]
    assert aws.published == []


def test_azure_push_defaults_match_the_aws_endpoint():
    azure = FakeAzure()
    client, _ = build_client(publishers=[azure])

    client.post("/metrics/push/azure", json={})

    assert azure.published == [("CustomMetric", 1.0, None)]


# --- GET /dashboard -------------------------------------------------------


def test_dashboard_renders_html_with_the_current_health():
    client, _ = build_client()

    response = client.get("/dashboard")

    assert response.status_code == 200
    assert "text/html" in response.content_type

    # The template title-cases the service key: "hl7-service" renders as "Hl7 Service".
    body = response.get_data(as_text=True)
    assert "Hl7 Service" in body
    assert "Gateway" in body
    assert "OPERATIONAL" in body


def test_dashboard_still_renders_when_a_service_is_down():
    checker = FakeHealthChecker(statuses={"http://gateway:8080": "unhealthy"})
    client, _ = build_client(checker=checker)

    response = client.get("/dashboard")

    assert response.status_code == 200
    body = response.get_data(as_text=True)
    assert "unhealthy" in body
    assert "DEGRADED" in body


# --- method and path constraints ------------------------------------------


@pytest.mark.parametrize(
    "method,path",
    [
        ("post", "/health"),
        ("post", "/metrics"),
        ("get", "/metrics/push/aws"),
        ("get", "/metrics/push/azure"),
    ],
)
def test_endpoints_reject_the_wrong_http_method(method, path):
    client, _ = build_client()

    assert getattr(client, method)(path).status_code == 405


def test_an_unknown_path_returns_404():
    client, _ = build_client()

    assert client.get("/not-a-route").status_code == 404
