# Core interfaces — define the contracts that infrastructure implementations must fulfill.
# These abstractions allow us to swap AWS for Azure (or add GCP) without changing
# the business logic in MonitoringManager.

from abc import ABC, abstractmethod
from typing import Dict

class IMetricPublisher(ABC):
    """
    Interface for pushing telemetry metrics to a cloud monitoring provider.

    Implementations: CloudWatchPublisher (AWS), AzureMonitorPublisher (Azure).
    Follows the Open/Closed Principle — add new cloud providers by creating
    a new class, not by modifying existing code.
    """
    @abstractmethod
    def push_metric(self, name: str, value: float, dimensions: Dict[str, str] = None) -> bool:
        """Push a named metric with optional key/value dimensions. Returns True on success."""
        pass

class IHealthChecker(ABC):
    """
    Interface for checking whether a remote service is alive.

    The default implementation (HttpHealthChecker) calls GET /health on each
    service URL and expects a 200 response.
    """
    @abstractmethod
    def check_health(self, service_url: str) -> dict:
        """Returns {'status': 'healthy'|'unhealthy', 'timestamp': '...'}."""
        pass
