import os
from typing import Dict, List
from core.interfaces import IHealthChecker, IMetricPublisher
from core.constants import (
    DIMENSION_SERVICE_NAME,
    METRIC_SERVICE_HEALTH,
    SERVICE_GATEWAY,
    SERVICE_HL7,
)
from core.models import AggregatedHealth, HealthStatus, ServiceHealth


class MonitoringManager:
    """
    Central orchestrator for health monitoring and metric publishing.

    This is the "business logic" layer — it doesn't know HOW to check health
    or WHERE to push metrics. Those details are handled by injected implementations
    of IHealthChecker and IMetricPublisher (Dependency Inversion Principle).

    Used by:
      - Flask routes (on-demand health checks and metric pushes)
      - Background scheduler (automatic health sweeps every 30s)
    """

    def __init__(self,
                 health_checker: IHealthChecker,
                 publishers: List[IMetricPublisher],
                 downstream_services: Dict[str, str] = None):

        self.health_checker = health_checker
        self.publishers = publishers

        # Services to monitor — defaults use Docker Compose service names
        self.downstream_services = downstream_services or {
            SERVICE_HL7: os.getenv("HL7_SERVICE_URL", "http://hl7-service:5001"),
            SERVICE_GATEWAY: os.getenv("GATEWAY_URL", "http://gateway:8080")
        }

    def check_all_services(self) -> AggregatedHealth:
        """
        Poll every downstream service's /health endpoint and return an
        aggregated health report. Overall status is "unhealthy" if any
        single component is down.
        """
        overall_status = HealthStatus.HEALTHY
        components = {}

        for name, url in self.downstream_services.items():
            result = self.health_checker.check_health(url)
            components[name] = ServiceHealth(
                service=name,
                status=result['status'],
                timestamp=result['timestamp']
            )
            if result['status'] != HealthStatus.HEALTHY:
                overall_status = HealthStatus.UNHEALTHY

        return AggregatedHealth(status=overall_status, components=components)

    def push_metric_to_all(self, metric_name: str, value: float, dimensions: Dict[str, str] = None):
        """Broadcast a metric to every registered publisher (AWS CloudWatch, Azure Monitor, etc.)."""
        for publisher in self.publishers:
            publisher.push_metric(metric_name, value, dimensions)

    def run_health_sweep_and_publish(self):
        """
        Called by the background scheduler every 30 seconds.
        Checks all services, then pushes a 1.0 (healthy) or 0.0 (unhealthy)
        metric per service to all cloud monitoring providers.
        """
        agg_health = self.check_all_services()

        for service_name, health in agg_health.components.items():
            val = 1.0 if health.status == HealthStatus.HEALTHY else 0.0
            self.push_metric_to_all(
                metric_name=METRIC_SERVICE_HEALTH,
                value=val,
                dimensions={DIMENSION_SERVICE_NAME: service_name}
            )
