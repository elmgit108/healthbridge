# Flask API routes for the monitoring service.
# Routes are registered via initialize_routes() rather than a Blueprint
# so we can inject the MonitoringManager instance directly.

from flask import Blueprint, jsonify, request, render_template
import dataclasses
from services.monitoring_manager import MonitoringManager
from infrastructure.aws_publisher import CloudWatchPublisher
from infrastructure.azure_publisher import AzureMonitorPublisher 
from core.constants import DEFAULT_METRIC_NAME, SERVICE_MONITORING
from core.models import HealthStatus




metrics_api = Blueprint('metrics_api', __name__)


def initialize_routes(app, manager: MonitoringManager):
    """Register all monitoring API routes with the Flask app."""

    @app.route('/health', methods=['GET'])
    def health_check():
        """This service's own health check — returns immediately without polling downstream."""
        return jsonify({"status": HealthStatus.HEALTHY, "service": SERVICE_MONITORING}), 200

    @app.route('/metrics', methods=['GET'])
    def get_metrics():
        """
        Poll all downstream services and return aggregated health.
        Returns 200 if all healthy, 503 if any component is down.
        """
        health = manager.check_all_services()
        status_code = 200 if health.status == HealthStatus.HEALTHY else 503
        return jsonify(dataclasses.asdict(health)), status_code

    @app.route('/metrics/push/aws', methods=['POST'])
    def push_aws_metric():
        """
        Push a custom metric to AWS CloudWatch.
        Body: {"name": "MetricName", "value": 1.0}
        """
        data = request.json or {}
        name = data.get('name', DEFAULT_METRIC_NAME)
        value = data.get('value', 1.0)

        ok = any(pub.push_metric(name, value)
            for pub in manager.publishers
                if isinstance(pub, CloudWatchPublisher))

        return jsonify({"status": "pushed" if ok else "publish failed"}), 200 if ok else 502

    @app.route('/metrics/push/azure', methods=['POST'])
    def push_azure_metric():
        """
        Push a custom metric to Azure Monitor.
        Body: {"name": "MetricName", "value": 1.0}
        """
        data = request.json or {}
        name = data.get('name', DEFAULT_METRIC_NAME)
        value = data.get('value', 1.0)

        ok = any(pub.push_metric(name, value)
            for pub in manager.publishers
                if isinstance(pub, AzureMonitorPublisher))
        
        return jsonify({"status": "pushed" if ok else "publish failed"}), 200 if ok else 502

    @app.route('/dashboard', methods=['GET'])
    def dashboard():
        """
        Render the visual status dashboard (HTML).
        Polls all services and passes the results to the Jinja2 template.
        """
        health = manager.check_all_services()
        return render_template('dashboard.html', metrics=health)
