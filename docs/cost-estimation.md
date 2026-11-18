# Cost estimation

This deployment is **not free when idle**. Batch CPU capacity has `minvCpus=0`, but API/scheduler Fargate services, internal ALB, NAT gateway, a public IPv4 address, logs and retained storage remain billable. This is a reference architecture, not a minimized-cost single-process application.

Estimate using the region-specific [AWS Pricing Calculator](https://calculator.aws/) and current [Fargate](https://aws.amazon.com/fargate/pricing/), [ALB](https://aws.amazon.com/elasticloadbalancing/pricing/), [VPC/NAT](https://aws.amazon.com/vpc/pricing/), [Batch/EC2](https://aws.amazon.com/batch/pricing/), [S3](https://aws.amazon.com/s3/pricing/), [DynamoDB](https://aws.amazon.com/dynamodb/pricing/) and [CloudWatch](https://aws.amazon.com/cloudwatch/pricing/) price pages. No fixed dollar figure is asserted without a selected region/account and current calculator inputs.

| Component | Main estimate inputs |
|---|---|
| Two Fargate services | 730 hours × two tasks, each 0.25 vCPU/0.5 GiB; deployment overlap adds temporary capacity |
| Internal ALB | Load-balancer hours plus LCUs |
| NAT and network | NAT hours, processed GB, public IPv4 hours and possible cross-AZ traffic |
| CPU conversion | EC2 instance-seconds rounded per applicable billing, attached gp3 storage, scale-out overhead and idle draining |
| S3/ECR | Input/output/version/image bytes retained plus requests and transfer |
| DynamoDB | On-demand reads, transactional writes, stream consumers, GSI writes/storage and PITR |
| Lambda/SQS/EventBridge | Requests, GB-seconds, polling, retries and reconciliation schedule |
| CloudWatch | Log ingestion/retention, alarms and any additional metrics |
| API Gateway/Cognito | HTTP API requests, response data transfer and active users |

Use measured representative models to estimate conversion CPU/memory, GLB size and stage duration. The default queue caps at 16 vCPUs; Batch may exceed a requested max by one instance under some allocation strategies, so it is not a hard spending cap. Application retries, uncertain submissions and Spot interruptions can duplicate compute cost. Spot is optional and its lower rates must be weighed against restart cost.

One NAT gateway reduces the demo's standing charge but introduces an AZ failure dependency and possible cross-AZ transfer. Gateway endpoints avoid routing S3/DynamoDB traffic through NAT; interface endpoints for other services also have hourly/data charges, so evaluate them against actual usage. Log retention defaults to 14 days, and Batch workers shut down through the managed environment after demand falls. Retained bucket versions, ECR images, the table and logs can outlive deleted services. Add account budgets/cost alerts through your organization's CloudFormation baseline before a long-running deployment.
