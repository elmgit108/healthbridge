import logging
from typing import Dict
from core.interfaces import IMetricPublisher

logger = logging.getLogger(__name__)


class AzureMonitorPublisher(IMetricPublisher):
    """
    Placeholder for Azure Monitor publishing. Not implemented — returns False.

    Currently a stub implementation for the POC — logs metrics instead of sending
    them to Azure. In production, this would use:
      - azure-monitor-ingestion (Data Collection Rules API)
      - or azure-mgmt-monitor (classic metrics API)
      - authenticated via DefaultAzureCredential (managed identity on Azure VMs)

    The interface contract is identical to CloudWatchPublisher, so the
    MonitoringManager doesn't know or care which cloud it's pushing to.
    """

    def __init__(self):
        # Production would initialize: MetricsClient(credential=DefaultAzureCredential())
        logger.info("Azure Monitor Publisher initialized", extra={"mode": "stub"})

    def push_metric(self, name: str, value: float, dimensions: Dict[str, str] = None) -> bool:
        dim_str = str(dimensions) if dimensions else "None"
        # TODO: Replace with actual Azure Monitor API call post-POC
        logger.info("AzureMonitorPublisher not implemented", extra={"metric": name, "value": value, "dimensions": dim_str})
        return False
