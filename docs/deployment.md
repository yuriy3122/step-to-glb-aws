# Deployment and operations

## Prerequisites

Use .NET SDK 10, Python 3.11+ for lint/smoke tools, Docker with buildx, and an authenticated AWS profile with permissions to deploy the three CloudFormation stacks and push ECR/S3 artifacts. AWS credentials come from the normal SDK provider chain (SSO/profile/environment/role); no keys are stored in this repository. Choose a region with two AZs, Batch EC2 capacity for the configured x86 instance families, Cognito, Fargate and the .NET 10 Lambda runtime.

```sh
python3 -m venv .local/python
.local/python/bin/pip install -r scripts/requirements.txt
.local/python/bin/cfn-lint infrastructure/cloudformation/*.yaml
dotnet run --project tools/Deploy -- --region eu-west-1 --environment cad-demo --profile your-profile
```

The C# deployment tool uses the AWS SDK for .NET and performs tool/credential preflight, then:

1. Deploys `cad-demo-bootstrap`: immutable ECR repositories plus encrypted private artifact bucket.
2. Builds/pushes API, scheduler and worker images for **linux/amd64**, using a unique image tag. The Job API image includes the React app built with Node 24. The worker image compiles pinned OCCT source and runs native CTest.
3. Publishes framework-dependent .NET Lambda assemblies, ZIPs their publish directories, and uploads content-addressed artifact keys.
4. Deploys `cad-demo-network`: two-AZ VPC, public/private subnets, a NAT gateway and S3/DynamoDB gateway endpoints.
5. Deploys `cad-demo-platform`: private storage/shared table, IAM and networking, Cognito, ALB/API Gateway/VPC Link/Fargate services, SQS/Lambdas/Batch/EventBridge and CloudWatch. API Gateway serves the UI and its runtime Cognito configuration through the Job API.

Infrastructure is provisioned only by CloudFormation. Image builds, code packaging and account provisioning are explicit application/deployment operations. CloudFormation does not build images. No AWS-console resource setup is required. Lambda deployment uses the supported managed [dotnet10 runtime](https://docs.aws.amazon.com/lambda/latest/dg/lambda-csharp.html). The API's [private ALB integration](https://docs.aws.amazon.com/apigateway/latest/developerguide/http-api-develop-integrations-private.html) uses a listener ARN and strips the stage prefix from forwarded paths.

Parameters include environment name, image URIs, instance types, max vCPUs, Spot flag, exact allowed origins, log retention, VPC/subnets, artifact keys and table name. Region comes from the AWS deployment context. `SimulationJobs` is the default table name, so use a different table-name parameter for a second independent deployment in the same account/region. Set `--table-name` to override the default. Set `--platform-stack-name` to choose the name of the platform stack. Spot is opt-in (`--spot`), max vCPUs defaults to 16. GPU/high-memory/MPI capacity is absent.

For a small Stockholm deployment, choose an amd64 instance type supported by AWS Batch in the region with 2 vCPUs and at least 4 GiB of RAM. AWS Batch rejected `t3.medium` for the template's managed EC2 compute environment. The Batch environment scales to zero when idle.

```sh
dotnet run --project tools/Deploy -- --region eu-north-1 --environment cad-demo \
  --instance-types "$BATCH_INSTANCE_TYPE" --max-vcpus 2
```

On Apple Silicon, the image build uses the ARM64 .NET SDK for portable assemblies and cross-compiles OCCT for x86-64. The image build runs the converter tests on Linux x86-64 under emulation before pushing the worker.

## Provision a tenant account

Accounts must receive their tenant at creation because `custom:tenantId` is immutable. Using the AWS CLI with the deployment outputs:

```sh
aws cognito-idp admin-create-user --region eu-west-1 \
  --user-pool-id YOUR_POOL_ID --username engineer@example.com \
  --user-attributes Name=email,Value=engineer@example.com Name=email_verified,Value=true Name=custom:tenantId,Value=tenant-a
```

Cognito sends the invitation and temporary password to the user; complete the password change in managed sign-in. Use only letters, digits, underscores and hyphens for tenant IDs (1–128 characters). Create another user with tenant-b to validate isolation. The SPA app client cannot assign or change tenants. Do not put passwords, ID tokens or AWS credentials in checked-in configuration.

## Live smoke validation

Open the `FrontendUrl`, sign in, upload `build/native/fixtures/assembly.step`, observe actual transfer/stage progress, and inspect/download the red two-part assembly. Use a valid ID token from the authenticated session without logging it:

```sh
export API_URL=https://YOUR_API_ID.execute-api.eu-west-1.amazonaws.com
# Set ID_TOKEN securely in your shell environment.
python3 scripts/smoke.py --timeout 1800
```

The script performs real API creation, direct S3 upload, completion, polling, result URL issuance and GLB download/header validation. It does not fake Batch. The standard browser smoke script targets local development; inspect the deployed viewer and validate AWS CORS, Cognito callback, IAM, image pulls, dispatch events and recovery separately. Set `OTHER_TENANT_ID_TOKEN` to a tenant-b ID token to make the smoke script check 404 responses for tenant-a job/status/result/cancel requests. Do not claim AWS success until this sequence has run against the deployed account.

Deployment outputs are saved to `artifacts/deployment-outputs.json`. A failed stack deployment prints recent failure events. An image-dependent stack is never deployed before its images exist. Roll back or fix failed stacks through CloudFormation and rerun; unique image tags avoid immutable-tag collisions.

## Monitoring and recovery

CloudWatch groups separate API, scheduler, worker and both Lambda logs. JSON application logs carry tenant/job/attempt/provider/correlation context during processing. API access logs exclude bearer tokens and query strings. The template creates dispatch/event DLQ, queue-age and Lambda error alarms; attach notification actions to your operational SNS/incident channel before production use. Inspect the stream DLQ too; it retains failed stream batches.

The scheduled reconciler republishes stale READY jobs to SQS and repairs provider status by bounded GSI queries. To diagnose uncertain submissions, match `cad-<jobId>-<attemptId>` using Batch JOB_NAME filtering, compare the durable attempt/provider/execution records, and terminate duplicate/stale executions. Do not blindly resubmit the same uncertain attempt. Dispatch DLQ redrive should occur only after correcting the root cause; messages are still checked against durable state when delivered again.

## Container and DynamoDB Local checks

```sh
docker build --platform linux/amd64 -f docker/service.Dockerfile --build-arg PROJECT=JobApi -t cad-api:test .
docker build --platform linux/amd64 -f docker/service.Dockerfile --build-arg PROJECT=Scheduler -t cad-scheduler:test .
docker build --platform linux/amd64 -f docker/worker.Dockerfile -t cad-worker:test .
docker run --rm --entrypoint /usr/local/bin/step-to-glb \
  -v "$PWD/build/native/fixtures:/data" cad-worker:test --input /data/assembly.step --output /tmp/assembly.glb

docker run --rm -d --name cad-ddb -p 127.0.0.1:8000:8000 \
  amazon/dynamodb-local:3.1.0 -jar DynamoDBLocal.jar -inMemory -sharedDb
dotnet run --project tests/DynamoIntegration/DynamoIntegration.csproj -m:1
docker stop cad-ddb
```

To exercise the C# host and native converter together inside the built worker image, run:

```sh
dotnet run --project tests/ContainerSmoke -- cad-worker:test
node tests/frontend/validate-glb.mjs artifacts/container-smoke.glb
```

This uses disposable generated CAD/local metadata in `.local/container-smoke`, checks that the worker waits for final provider confirmation, then verifies success. It is a container packaging/local adapter test, not an AWS smoke.

To test the production SPA under the deployed CSP locally, start the API with `AllowedOrigins=http://127.0.0.1:5173,http://127.0.0.1:5174`, build the frontend, run `python3 tests/frontend/serve-production.py`, then run `WEB_URL=http://127.0.0.1:5174 node tests/frontend/browser-smoke.mjs`. The test server adds only the loopback API to the deployed CSP.

The DynamoDB integration executable refuses non-loopback endpoints and uses randomly generated local-only signing material. It creates/deletes a temporary local table to exercise actual SDK transactions, contention, tenant queries, attempt records and the operational index. It does not create AWS infrastructure.

## Teardown and charges

Stop new submissions and cancel/drain Batch jobs first. Delete the platform stack, then the network stack, then bootstrap when no longer needed. CloudFormation deletion can require waiting for ECS/Batch resource draining. Data buckets, the table, Cognito pool, repositories and bootstrap artifact bucket use Retain where configured; retained data/images still incur charges. Empty/delete retained resources only after intentionally backing up or accepting their loss. Native worker cache/images and the local Colima VM can also be stopped when not in use. See cost guidance before deployment.
