"""Named constants for values used in more than one module.

Service names must match the Docker Compose and Kubernetes service names,
because the same strings are also DNS hostnames and the OpenTelemetry
``service.name`` that identifies spans in Jaeger. Spelling one of them
differently in one place produces a health report or a trace that silently
refers to a service nobody can find.
"""

# --- Service names -----------------------------------------------------------

SERVICE_MONITORING = "monitoring-service"
SERVICE_HL7 = "hl7-service"
SERVICE_GATEWAY = "gateway"

# --- Metric vocabulary -------------------------------------------------------
# These are values *we* choose, so they belong here. Keys such as "MetricName",
# "Namespace" and "Dimensions" in aws_publisher.py are part of the boto3 API
# contract, not ours, and are deliberately left as literals at the call site.

METRIC_SERVICE_HEALTH = "ServiceHealth"
DIMENSION_SERVICE_NAME = "ServiceName"
DEFAULT_METRIC_NAME = "CustomMetric"
