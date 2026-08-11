"""
Tests for the health dataclasses and the background scheduler.

The scheduler tests never let a job actually fire on its 30s interval — they
inspect the registered job and trigger it directly, so the suite stays fast.
"""

import dataclasses
import datetime

from apscheduler.schedulers.background import BackgroundScheduler

from background.scheduler import MetricScheduler
from core.models import AggregatedHealth, ServiceHealth
from services.monitoring_manager import MonitoringManager
from tests.conftest import FakeHealthChecker, RecordingPublisher


# --- core.models ----------------------------------------------------------


def test_service_health_carries_the_three_fields_the_dashboard_reads():
    health = ServiceHealth(service="hl7-service", status="healthy", timestamp="2024-01-15T12:00:00Z")

    assert dataclasses.asdict(health) == {
        "service": "hl7-service",
        "status": "healthy",
        "timestamp": "2024-01-15T12:00:00Z",
    }


def test_aggregated_health_identifies_the_reporting_service_by_default():
    aggregate = AggregatedHealth(status="healthy")

    assert aggregate.service == "monitoring-service"
    assert aggregate.components == {}


def test_aggregated_health_generates_an_iso8601_utc_timestamp():
    aggregate = AggregatedHealth(status="healthy")

    assert aggregate.timestamp.endswith("Z")
    datetime.datetime.fromisoformat(aggregate.timestamp.rstrip("Z"))   # raises if malformed


def test_each_aggregate_gets_its_own_components_dict():
    # A mutable default would be shared across every instance — the classic
    # dataclass footgun. default_factory is what prevents it.
    first, second = AggregatedHealth(status="healthy"), AggregatedHealth(status="healthy")

    first.components["hl7-service"] = ServiceHealth("hl7-service", "healthy", "t")

    assert second.components == {}


def test_nested_components_serialise_all_the_way_down():
    # This is what /metrics returns, so a regression here is a broken API response.
    aggregate = AggregatedHealth(
        status="unhealthy",
        components={"gateway": ServiceHealth("gateway", "unhealthy", "2024-01-15T12:00:00Z")},
    )

    assert dataclasses.asdict(aggregate)["components"]["gateway"]["status"] == "unhealthy"


# --- background.scheduler -------------------------------------------------


def build_manager():
    publisher = RecordingPublisher()
    manager = MonitoringManager(
        FakeHealthChecker(default="healthy"),
        [publisher],
        {"hl7-service": "http://hl7-service:5001"},
    )
    return manager, publisher


def test_the_scheduler_starts_and_registers_the_health_sweep_job():
    manager, _ = build_manager()
    scheduler = MetricScheduler(manager)

    scheduler.start()
    try:
        job = scheduler.scheduler.get_job("health_sweep")
        assert job is not None
        assert scheduler.scheduler.running
    finally:
        scheduler.scheduler.shutdown(wait=False)


def test_the_sweep_runs_every_30_seconds():
    # The interval is what makes CloudWatch data near-real-time; Terraform alarm
    # evaluation periods are set against it.
    manager, _ = build_manager()
    scheduler = MetricScheduler(manager)

    scheduler.start()
    try:
        job = scheduler.scheduler.get_job("health_sweep")
        assert job.trigger.interval.total_seconds() == 30
    finally:
        scheduler.scheduler.shutdown(wait=False)


def test_the_registered_job_is_the_managers_sweep():
    manager, publisher = build_manager()
    scheduler = MetricScheduler(manager)

    scheduler.start()
    try:
        job = scheduler.scheduler.get_job("health_sweep")

        job.func()   # invoke directly rather than waiting 30 seconds

        assert publisher.published == [("ServiceHealth", 1.0, {"ServiceName": "hl7-service"})]
    finally:
        scheduler.scheduler.shutdown(wait=False)


def test_restarting_replaces_the_job_instead_of_duplicating_it():
    # replace_existing=True exists because Flask's reloader constructs the app
    # twice; without it the sweep would run twice per interval.
    manager, _ = build_manager()
    scheduler = MetricScheduler(manager)

    scheduler.start()
    try:
        scheduler.scheduler.add_job(
            func=manager.run_health_sweep_and_publish,
            trigger="interval",
            seconds=30,
            id="health_sweep",
            replace_existing=True,
        )

        assert len(scheduler.scheduler.get_jobs()) == 1
    finally:
        scheduler.scheduler.shutdown(wait=False)


def test_a_scheduler_is_created_but_not_started_on_construction():
    # Constructing the app must not start background work as a side effect —
    # start() is the explicit trigger.
    manager, _ = build_manager()

    scheduler = MetricScheduler(manager)

    assert isinstance(scheduler.scheduler, BackgroundScheduler)
    assert not scheduler.scheduler.running
