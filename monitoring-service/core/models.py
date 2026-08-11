# Data models for health check results.
# Using dataclasses for clean serialization — Flask's jsonify + dataclasses.asdict()
# converts these directly to JSON responses.

from dataclasses import dataclass, field
from typing import Dict
import datetime
from enum import StrEnum

from core.constants import SERVICE_MONITORING


class HealthStatus(StrEnum):
    """The only health values this service reports."""
    HEALTHY = "healthy"
    UNHEALTHY = "unhealthy"

@dataclass
class ServiceHealth:
    """Health status of a single downstream service (e.g., hl7-service, gateway)."""
    service: str       # Service name (matches Docker Compose service name)
    status: HealthStatus        # "healthy" or "unhealthy"
    timestamp: str     # ISO 8601 UTC timestamp of when the check was performed

@dataclass
class AggregatedHealth:
    """Combined health status of all monitored services — returned by GET /metrics."""
    status: HealthStatus                                          # "healthy" if all components are healthy
    service: str = SERVICE_MONITORING                     # Identifies this service as the reporter
    timestamp: str = field(default_factory=lambda: datetime.datetime.utcnow().isoformat() + "Z")
    components: Dict[str, ServiceHealth] = field(default_factory=dict)  # Per-service health details

