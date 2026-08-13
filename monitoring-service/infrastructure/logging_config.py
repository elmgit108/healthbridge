"""
Structured JSON logging for the HealthBridge monitoring service (ROADMAP A1).

Single Responsibility — this module owns *only* logging configuration.
create_app() calls init_logging() first, then init_request_logging(app) to add
the per-request access log.

Every log line is one JSON object using the same field names the C# and Go
services emit — timestamp / level / message / service / logger — so a single
CloudWatch Logs Insights or Loki query can span all three services.
"""

from datetime import datetime, timezone
import logging
import os
import time

from flask import g, request
from pythonjsonlogger.json import JsonFormatter

from core.constants import SERVICE_MONITORING

# Python spells two levels differently from C# and Go: WARNING vs WARN, and
# CRITICAL vs FATAL. record.levelname is produced from logging._levelToName,
# which holds no aliases, so the rename has to happen here.
# The .get() fallback stops a custom level registered by a library from
# becoming None and disappearing from query results.
_LEVEL_NAME_MAP = {
    "DEBUG": "DEBUG",
    "INFO": "INFO",
    "WARNING": "WARN",
    "ERROR": "ERROR",
    "CRITICAL": "FATAL",
}


class MonitorJsonFormatter(JsonFormatter):
    """JsonFormatter emitting the field names shared by all three services."""

    # Read from OTEL_SERVICE_NAME so logs and traces agree on the service name.
    # docker-compose.yml already sets it; the constant is the local fallback.
    _service_name: str = os.getenv("OTEL_SERVICE_NAME", SERVICE_MONITORING)

    def __init__(self, *args, **kwargs):
        # The library writes tracebacks under "exc_info"; the C# service calls
        # that field "exception". Rename so the wire format stays consistent.
        kwargs.setdefault("rename_fields", {"exc_info": "exception"})
        super().__init__(*args, **kwargs)

    def add_fields(self, json_record, record, message_dict, **kwargs):
        """Per-record hook — add timestamp, short level, service and logger name."""
        super().add_fields(json_record, record, message_dict, **kwargs)

        # isoformat() ends in "+00:00"; the C# service emits "Z". Same instant,
        # different spelling — normalise so all three services match exactly.
        json_record["timestamp"] = (
            datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
        )
        json_record["level"] = _LEVEL_NAME_MAP.get(record.levelname, record.levelname)
        # A call site can override the name with extra={"service": ...}.
        json_record["service"] = record.__dict__.get("service", self._service_name)
        # Which component wrote the line, e.g. "infrastructure.aws_publisher".
        json_record["logger"] = record.name


def init_logging():
    """
    Attach a JSON StreamHandler to the root logger.

    Called first in create_app(), before init_tracing(), so even the tracing
    startup line comes out as JSON.

    The handler goes on the *root* logger rather than a named one: that is what
    converts third-party output (Werkzeug, boto3, APScheduler) to JSON as well,
    without touching any of their code.
    """
    root = logging.getLogger()
    root.setLevel(logging.INFO)

    # Werkzeug writes its own access log as one plain string with no fields.
    # init_request_logging() below replaces it with a structured line, so keep
    # only Werkzeug's warnings and errors. Same idea as the
    # MinimumLevel.Override("Microsoft.AspNetCore", Warning) in the C# service.
    logging.getLogger("werkzeug").setLevel(logging.WARNING)

    # basicConfig, or Flask/Werkzeug, may already have installed a handler.
    # Without this clear every line is printed twice — once JSON, once plain.
    root.handlers.clear()

    handler = logging.StreamHandler()
    handler.setFormatter(MonitorJsonFormatter())
    root.addHandler(handler)


def init_request_logging(app):
    """
    One JSON log line per HTTP request — method, path, status, duration.

    This is the Python version of UseSerilogRequestLogging() in the C# service,
    and it uses the same field names on purpose.
    """
    request_logger = logging.getLogger("request")

    @app.before_request
    def _start_timer():
        # g is per-request storage. It carries the start time to the hook below.
        g._request_start = time.perf_counter()

    # Note: after_request does not run if the route raises an unhandled
    # exception, so failed requests will not appear in this log.
    @app.after_request
    def _log_request(response):
        start = getattr(g, "_request_start", None)
        if start is None:
            return response  # timer never ran; nothing to report

        # perf_counter, not time.time — it is built for measuring durations and
        # does not jump if the system clock is adjusted.
        elapsed_ms = (time.perf_counter() - start) * 1000
        request_logger.info(
            "HTTP %s %s responded %s in %.4f ms",
            request.method,
            request.path,
            response.status_code,
            elapsed_ms,
            extra={
                "RequestMethod": request.method,
                "RequestPath": request.path,
                "StatusCode": response.status_code,
                "Elapsed": round(elapsed_ms, 4),
            },
        )
        # after_request must always return the response, or Flask raises.
        return response