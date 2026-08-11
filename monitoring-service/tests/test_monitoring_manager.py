"""
Tests for MonitoringManager — the business logic layer that orchestrates health
checks and metric publishing.

These run entirely against the fakes in conftest.py, which is the payoff for
depending on IHealthChecker / IMetricPublisher rather than on requests/boto3.
"""

import pytest

from services.monitoring_manager import MonitoringManager
from tests.conftest import ExplodingPublisher, FakeHealthChecker, RecordingPublisher


# --- check_all_services ---------------------------------------------------


def test_reports_healthy_when_every_service_is_up(healthy_checker, two_services):
    manager = MonitoringManager(healthy_checker, [], two_services)

    result = manager.check_all_services()

    assert result.status == "healthy"
    assert result.service == "monitoring-service"
    assert set(result.components) == {"hl7-service", "gateway"}


def test_reports_unhealthy_when_any_single_service_is_down(two_services):
    # One bad component degrades the whole report — this drives the 503 from
    # GET /metrics, which is what alerting keys off.
    checker = FakeHealthChecker(statuses={"http://gateway:8080": "unhealthy"})
    manager = MonitoringManager(checker, [], two_services)

    result = manager.check_all_services()

    assert result.status == "unhealthy"
    assert result.components["gateway"].status == "unhealthy"
    assert result.components["hl7-service"].status == "healthy"


def test_every_configured_service_is_checked(healthy_checker, two_services):
    manager = MonitoringManager(healthy_checker, [], two_services)

    manager.check_all_services()

    assert sorted(healthy_checker.checked_urls) == sorted(two_services.values())


def test_component_entries_carry_the_service_name_and_timestamp(healthy_checker, two_services):
    manager = MonitoringManager(healthy_checker, [], two_services)

    result = manager.check_all_services()

    component = result.components["hl7-service"]
    assert component.service == "hl7-service"
    assert component.status == "healthy"
    assert component.timestamp == "2024-01-15T12:00:00Z"


def test_KNOWN_GAP_an_explicitly_empty_service_map_falls_back_to_the_defaults():
    # Documents a trap in the constructor, not desired behaviour.
    #
    #   self.downstream_services = downstream_services or {...defaults...}
    #
    # An empty dict is falsy, so "monitor nothing" is indistinguishable from
    # "monitor the defaults". A caller that computes the service map from config
    # and gets back an empty result silently starts polling hl7-service and gateway
    # instead. Fix with an explicit `if downstream_services is None:` check.
    manager = MonitoringManager(FakeHealthChecker(), [], {})

    assert manager.downstream_services == {
        "hl7-service": "http://hl7-service:5001",
        "gateway": "http://gateway:8080",
    }


def test_defaults_to_the_docker_compose_service_names_when_none_are_given(monkeypatch):
    monkeypatch.delenv("HL7_SERVICE_URL", raising=False)
    monkeypatch.delenv("GATEWAY_URL", raising=False)

    manager = MonitoringManager(FakeHealthChecker(), [])

    assert manager.downstream_services == {
        "hl7-service": "http://hl7-service:5001",
        "gateway": "http://gateway:8080",
    }


def test_service_urls_are_overridable_by_environment(monkeypatch):
    # This is how the Kubernetes and EC2 deployments point at real hostnames.
    monkeypatch.setenv("HL7_SERVICE_URL", "http://10.0.1.5:5001")
    monkeypatch.setenv("GATEWAY_URL", "http://10.0.1.6:8080")

    manager = MonitoringManager(FakeHealthChecker(), [])

    assert manager.downstream_services["hl7-service"] == "http://10.0.1.5:5001"
    assert manager.downstream_services["gateway"] == "http://10.0.1.6:8080"


# --- push_metric_to_all ---------------------------------------------------


def test_a_metric_is_broadcast_to_every_publisher():
    aws, azure = RecordingPublisher("aws"), RecordingPublisher("azure")
    manager = MonitoringManager(FakeHealthChecker(), [aws, azure], {})

    manager.push_metric_to_all("ServiceHealth", 1.0, {"ServiceName": "hl7-service"})

    for pub in (aws, azure):
        assert pub.published == [("ServiceHealth", 1.0, {"ServiceName": "hl7-service"})]


def test_pushing_with_no_publishers_registered_is_a_no_op():
    manager = MonitoringManager(FakeHealthChecker(), [], {})

    manager.push_metric_to_all("ServiceHealth", 1.0)   # must not raise


def test_dimensions_are_optional():
    publisher = RecordingPublisher()
    manager = MonitoringManager(FakeHealthChecker(), [publisher], {})

    manager.push_metric_to_all("CustomMetric", 42.0)

    assert publisher.published == [("CustomMetric", 42.0, None)]


# --- run_health_sweep_and_publish -----------------------------------------


def test_the_sweep_publishes_one_metric_per_service(healthy_checker, two_services):
    publisher = RecordingPublisher()
    manager = MonitoringManager(healthy_checker, [publisher], two_services)

    manager.run_health_sweep_and_publish()

    assert len(publisher.published) == 2
    assert {dims["ServiceName"] for _, _, dims in publisher.published} == {"hl7-service", "gateway"}


def test_healthy_services_publish_1_and_unhealthy_publish_0(two_services):
    # CloudWatch alarms threshold on this numeric value, so the encoding matters
    # more than the status string does.
    checker = FakeHealthChecker(statuses={"http://gateway:8080": "unhealthy"})
    publisher = RecordingPublisher()
    manager = MonitoringManager(checker, [publisher], two_services)

    manager.run_health_sweep_and_publish()

    values = {dims["ServiceName"]: value for _, value, dims in publisher.published}
    assert values["hl7-service"] == 1.0
    assert values["gateway"] == 0.0


def test_the_sweep_uses_a_consistent_metric_name(healthy_checker, two_services):
    publisher = RecordingPublisher()
    manager = MonitoringManager(healthy_checker, [publisher], two_services)

    manager.run_health_sweep_and_publish()

    assert {name for name, _, _ in publisher.published} == {"ServiceHealth"}


def test_the_sweep_reaches_every_publisher(healthy_checker, two_services):
    aws, azure = RecordingPublisher("aws"), RecordingPublisher("azure")
    manager = MonitoringManager(healthy_checker, [aws, azure], two_services)

    manager.run_health_sweep_and_publish()

    assert len(aws.published) == 2
    assert len(azure.published) == 2


def test_a_publisher_that_returns_false_does_not_stop_the_sweep(healthy_checker, two_services):
    # push_metric returns bool rather than raising; a False must not be mistaken
    # for a reason to abandon the remaining services.
    failing = RecordingPublisher("failing", succeed=False)
    manager = MonitoringManager(healthy_checker, [failing], two_services)

    manager.run_health_sweep_and_publish()

    assert len(failing.published) == 2


def test_KNOWN_GAP_a_raising_publisher_aborts_the_whole_sweep(healthy_checker, two_services):
    # Documents current behaviour, not desired behaviour.
    #
    # push_metric_to_all has no try/except, so a publisher that RAISES (rather than
    # returning False) propagates out of run_health_sweep_and_publish. Under
    # APScheduler that kills the sweep for that tick: publishers registered after the
    # failing one get nothing, and no further services are reported.
    #
    # Both shipped publishers catch their own exceptions, so this cannot fire today —
    # it is a latent trap for the next publisher someone adds. Fix by wrapping the
    # loop body in push_metric_to_all with try/except and logging the failure.
    exploding = ExplodingPublisher()
    never_reached = RecordingPublisher("never-reached")
    manager = MonitoringManager(healthy_checker, [exploding, never_reached], two_services)

    with pytest.raises(RuntimeError):
        manager.run_health_sweep_and_publish()

    assert never_reached.published == []


def test_a_sweep_publishes_exactly_one_metric_per_configured_service():
    # A single-service deployment must produce one metric, not the two that the
    # default service map would yield. (An empty map is unreachable — see
    # test_KNOWN_GAP_an_explicitly_empty_service_map_falls_back_to_the_defaults.)
    publisher = RecordingPublisher()
    manager = MonitoringManager(
        FakeHealthChecker(), [publisher], {"hl7-service": "http://hl7-service:5001"})

    manager.run_health_sweep_and_publish()

    assert len(publisher.published) == 1
    assert publisher.published[0][2] == {"ServiceName": "hl7-service"}
