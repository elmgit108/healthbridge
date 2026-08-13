import requests
import datetime
import logging
from core.interfaces import IHealthChecker
from core.models import HealthStatus

logger = logging.getLogger(__name__)


class HttpHealthChecker(IHealthChecker):
    """
    Checks service health by sending GET /health to each service URL.

    A service is considered "healthy" only if it returns HTTP 200 within 3 seconds.
    Any timeout, connection error, or non-200 status results in "unhealthy".

    This is the same pattern used by Docker HEALTHCHECK, Kubernetes liveness probes,
    and AWS ELB health checks.
    """

    def check_health(self, service_url: str) -> dict:
        health_status = HealthStatus.UNHEALTHY
        timestamp = datetime.datetime.utcnow().isoformat() + "Z"

        try:
            # 3-second timeout prevents a slow/hung service from blocking the sweep
            response = requests.get(f"{service_url.rstrip('/')}/health", timeout=3)
            if response.status_code == 200:
                health_status = HealthStatus.HEALTHY
        except Exception as e:
            logger.debug("Health check failed", extra={"target": service_url}, exc_info=True)

        return {
            'status': health_status,
            'timestamp': timestamp
        }
