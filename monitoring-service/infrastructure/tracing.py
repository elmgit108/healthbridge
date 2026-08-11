"""
OpenTelemetry tracing setup for the HealthBridge monitoring service.

Single Responsibility — this module owns *only* tracing initialization.
The Flask app calls init_tracing(app) once at startup.

Auto-instruments:
  - Flask incoming requests (one span per HTTP request)
  - requests library outgoing calls (one span per downstream call)

Trace context (W3C tracecontext headers) flows automatically between services,
so a single user request shows up as a single trace spanning gateway → C# → Python.
"""

import os
import logging

from opentelemetry import trace
from opentelemetry.sdk.resources import Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.flask import FlaskInstrumentor
from opentelemetry.instrumentation.requests import RequestsInstrumentor

from core.constants import SERVICE_MONITORING

logger = logging.getLogger(__name__)

SERVICE_NAME = SERVICE_MONITORING
SERVICE_VERSION = "1.0.0"


def init_tracing(app):
    """
    Initialize OpenTelemetry tracing for the Flask app.

    The OTLP endpoint is configurable via OTEL_EXPORTER_OTLP_ENDPOINT
    (default: http://jaeger:4317 — matches the docker-compose service name).
    """
    endpoint = os.getenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://jaeger:4317")

    # Resource describes this service to the trace backend
    resource = Resource.create({
        "service.name": SERVICE_NAME,
        "service.version": SERVICE_VERSION,
    })

    # Tracer provider — batches spans and exports via OTLP gRPC
    provider = TracerProvider(resource=resource)
    exporter = OTLPSpanExporter(endpoint=endpoint, insecure=True)
    provider.add_span_processor(BatchSpanProcessor(exporter))

    # Register globally so instrumentors find it
    trace.set_tracer_provider(provider)

    # Auto-instrument Flask + requests library
    FlaskInstrumentor().instrument_app(app)
    RequestsInstrumentor().instrument()

    logger.info("OpenTelemetry tracing initialized — exporting to %s", endpoint)
