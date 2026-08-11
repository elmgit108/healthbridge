# Background job scheduler — runs health sweeps on a fixed interval
# and pushes results to cloud monitoring (CloudWatch / Azure Monitor).
#
# Uses APScheduler (Advanced Python Scheduler) which runs in a background
# thread inside the Flask process — no external cron or Celery needed.

from apscheduler.schedulers.background import BackgroundScheduler
import logging

logger = logging.getLogger(__name__)


class MetricScheduler:
    """
    Periodically runs MonitoringManager.run_health_sweep_and_publish()
    to check all services and push health metrics to cloud providers.

    Default interval: every 30 seconds. This means CloudWatch/Azure Monitor
    will have near-real-time health data for alerting and dashboards.
    """

    def __init__(self, monitoring_manager):
        self.manager = monitoring_manager
        self.scheduler = BackgroundScheduler()

    def start(self):
        """Register the health sweep job and start the scheduler thread."""
        logger.info("Starting background scheduler — health sweep every 30s...")
        self.scheduler.add_job(
            func=self.manager.run_health_sweep_and_publish,
            trigger="interval",
            seconds=30,
            id="health_sweep",
            replace_existing=True  # Prevents duplicate jobs on Flask hot-reload
        )
        self.scheduler.start()
