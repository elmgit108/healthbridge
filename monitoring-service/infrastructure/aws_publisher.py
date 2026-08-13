import boto3
import logging
import os
from typing import Dict
from core.interfaces import IMetricPublisher

logger = logging.getLogger(__name__)

# Used only when neither an explicit argument nor AWS_REGION supplies one.
DEFAULT_AWS_REGION = "ca-central-1"

# CloudWatch namespace these metrics appear under in the console.
METRIC_NAMESPACE = "HealthBridge/Services"


class CloudWatchPublisher(IMetricPublisher):
    """
    Pushes custom application metrics to AWS CloudWatch.

    Metrics are published under the "HealthBridge/Services" namespace, which
    appears in the CloudWatch console alongside standard EC2/RDS metrics.
    The Terraform cloudwatch.tf file sets up alarms that trigger on these metrics.

    Uses boto3 (AWS SDK for Python) — credentials come from environment variables
    (AWS_ACCESS_KEY_ID, AWS_SECRET_ACCESS_KEY) or IAM instance role when running on EC2.
    """

    def __init__(self, region_name: str | None = None):
        # Resolution order: explicit argument, then AWS_REGION from the environment
        # (docker-compose and the k8s manifests both set it), then the default.
        # This previously hardcoded the region, so AWS_REGION was silently ignored
        # and a deployment to another region still published to ca-central-1.
        region_name = region_name or os.getenv("AWS_REGION") or DEFAULT_AWS_REGION
        try:
            self.client = boto3.client('cloudwatch', region_name=region_name)
        except Exception as e:
            # Graceful degradation — service still works without AWS credentials
            logger.warning("Failed to initialize boto3 CloudWatch client",
               extra={"region": region_name}, exc_info=True)
            self.client = None

    def push_metric(self, name: str, value: float, dimensions: Dict[str, str] = None) -> bool:
        if not self.client:
            logger.error("CloudWatch client is not initialized — cannot push metrics",
                extra={"metric": name})
            return False

        # Convert {"ServiceName": "hl7-service"} → [{"Name": "ServiceName", "Value": "hl7-service"}]
        dim_list = [{'Name': k, 'Value': v} for k, v in (dimensions or {}).items()]

        try:
            self.client.put_metric_data(
                Namespace=METRIC_NAMESPACE,
                MetricData=[
                    {
                        'MetricName': name,
                        'Dimensions': dim_list,
                        'Value': value,
                        'Unit': 'None'
                    },
                ]
            )
            logger.info("Pushed AWS metric", extra={"metric": name, "value": value, "namespace": METRIC_NAMESPACE})
            return True
        except Exception as e:
            logger.error("Failed to push metric to AWS", extra={"metric": name}, exc_info=True)
            return False
