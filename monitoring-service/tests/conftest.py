"""
Shared fixtures and test doubles for the monitoring service suite.

Everything here is in-memory: no HTTP calls, no AWS, no Azure, no scheduler
threads. The interfaces in core/interfaces.py are what make that possible —
each fake implements the same contract the real infrastructure does.
"""

import pytest

from core.interfaces import IHealthChecker, IMetricPublisher


class FakeHealthChecker(IHealthChecker):
    """
    Returns a scripted status per URL instead of making an HTTP request.

    Records every URL it was asked about so tests can assert which services
    were actually swept.
    """

    def __init__(self, statuses=None, default="healthy"):
        # {service_url: "healthy"|"unhealthy"}
        self.statuses = statuses or {}
        self.default = default
        self.checked_urls = []

    def check_health(self, service_url: str) -> dict:
        self.checked_urls.append(service_url)
        return {
            "status": self.statuses.get(service_url, self.default),
            "timestamp": "2024-01-15T12:00:00Z",
        }


class RecordingPublisher(IMetricPublisher):
    """Captures published metrics instead of sending them to a cloud provider."""

    def __init__(self, name="recording", succeed=True):
        self.name = name
        self.succeed = succeed
        self.published = []   # list of (metric_name, value, dimensions)

    def push_metric(self, name: str, value: float, dimensions=None) -> bool:
        self.published.append((name, value, dimensions))
        return self.succeed


class ExplodingPublisher(IMetricPublisher):
    """A publisher whose backend is down — raises on every push."""

    def __init__(self):
        self.call_count = 0

    def push_metric(self, name: str, value: float, dimensions=None) -> bool:
        self.call_count += 1
        raise RuntimeError("cloud provider unreachable")


@pytest.fixture
def healthy_checker():
    return FakeHealthChecker(default="healthy")


@pytest.fixture
def unhealthy_checker():
    return FakeHealthChecker(default="unhealthy")


@pytest.fixture
def publisher():
    return RecordingPublisher()


@pytest.fixture
def two_services():
    """The service map used by most tests — mirrors the production defaults."""
    return {
        "hl7-service": "http://hl7-service:5001",
        "gateway": "http://gateway:8080",
    }
