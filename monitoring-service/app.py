# HealthBridge Monitoring & Metrics Service — Python/Flask
#
# Collects health metrics from all microservices and pushes them to
# AWS CloudWatch and Azure Monitor. Provides a visual dashboard for
# real-time service status.
#
# Architecture follows SOLID principles:
#   - IHealthChecker / IMetricPublisher interfaces (core/interfaces.py)
#   - Concrete implementations injected here at startup
#   - MonitoringManager orchestrates checks and publishing
#   - Background scheduler runs health sweeps every 30 seconds

import logging
from flask import Flask
from api.routes import initialize_routes
from services.monitoring_manager import MonitoringManager
from background.scheduler import MetricScheduler

# Concrete implementations of the SOLID interfaces
from infrastructure.aws_publisher import CloudWatchPublisher
from infrastructure.azure_publisher import AzureMonitorPublisher
from infrastructure.http_health_checker import HttpHealthChecker
from infrastructure.tracing import init_tracing

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(name)s - %(levelname)s - %(message)s')
logger = logging.getLogger(__name__)

def create_app():
    """Flask application factory — wires up all dependencies and starts background jobs."""
    app = Flask(__name__)

    # --- OpenTelemetry distributed tracing ---
    # Auto-instruments Flask + requests so a single trace spans gateway → C# → Python.
    # Must be called before routes are registered for full instrumentation.
    init_tracing(app)

    # --- Dependency Injection Wiring ---
    # This manual DI replaces what a framework like FastAPI or .NET does automatically.
    # Each component depends on abstractions (interfaces), not concrete classes.

    # 1. Health checker — polls /health endpoints on downstream services
    health_checker = HttpHealthChecker()

    # 2. Metric publishers — push telemetry to cloud monitoring platforms
    publishers = [
        CloudWatchPublisher(),                             # region comes from AWS_REGION
        AzureMonitorPublisher()                            # Azure Monitor (stub for POC)
    ]

    # 3. Business logic manager — orchestrates health checks and metric publishing
    manager = MonitoringManager(
        health_checker=health_checker,
        publishers=publishers
    )

    # 4. Wire Flask routes to the manager
    initialize_routes(app, manager)

    # 5. Start background job — sweeps health every 30s and pushes to cloud
    scheduler = MetricScheduler(manager)
    scheduler.start()

    logger.info("HealthBridge Monitoring Service started successfully!")

    return app

if __name__ == "__main__":
    app = create_app()
    app.run(host="0.0.0.0", port=5002)
