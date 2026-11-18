# CODEX TASK: Implement an AWS-Based Distributed STEP-to-GLB Processing Platform

## 1. Goal

Implement a complete, deployable, production-oriented demonstration of a distributed, multi-tenant CAD processing platform using AWS services.

The example HPC workload is converting CAD models from STEP format into binary glTF 2.0 (GLB), using **C++ and OpenCascade Technology (OCCT)**.

The backend and AWS infrastructure orchestration must be implemented in **C# / ASP.NET Core / .NET 10**.

The frontend must use **React, TypeScript and Babylon.js** to upload STEP files, track conversion progress, and display the resulting GLB models.

All AWS infrastructure must be deployed using **AWS CloudFormation**, not Terraform, CDK, or manually created AWS resources.

This is an actual software engineering project, not a mockup. Write runnable source code, Dockerfiles, CloudFormation templates, automated tests, deployment scripts, and documentation.

## 2. Required Architecture

Implement the following architecture:

```text
                         CLIENT / WEB UI
                    React + Babylon.js
                              |
                              v
                        API Gateway
                              |
                           VPC Link
                              |
                              v
                        Internal ALB
                              |
                              v
                    ECS Fargate: Job API
                       ASP.NET Core
                        /         \
                       v           v
                  DynamoDB     S3 Presigned URLs
                       |           |
                 DynamoDB       Client uploads
                   Streams      STEP directly
                       |           |
                       v           v
                 Dispatch Lambda  S3 Input Bucket
                       |
                       v
                  SQS Dispatch Queue
                       |
                       v
                ECS Fargate: Scheduler
                       |
                       v
                    AWS Batch
                       |
                       v
                EC2 Compute Environment
                       |
                       v
                 C# Worker Host
                       |
                       v
                C++ OpenCascade
                STEP -> Mesh -> GLB
                       |
                       v
                 S3 Result Bucket

AWS Batch -> EventBridge -> State/Recovery Lambda
                                  |
                                  v
                               DynamoDB

EventBridge Scheduler -> State/Recovery Lambda

Job API, Scheduler, Lambda, Batch workers
                    |
                    v
                 CloudWatch
```

Important architectural constraints:

1. Job API runs as an ASP.NET Core container in **ECS Fargate**, not Lambda.
2. Application state is stored in **one shared DynamoDB table**.
3. DynamoDB Streams triggers a Dispatch Lambda.
4. Dispatch Lambda publishes to SQS.
5. Placement Scheduler runs as a separate C# service in ECS Fargate.
6. Scheduler submits jobs to AWS Batch.
7. HPC conversion runs on EC2 instances provisioned by AWS Batch.
8. AWS Batch state events are handled through EventBridge.
9. A State/Recovery Lambda updates and reconciles DynamoDB.
10. S3 stores STEP inputs and GLB outputs.
11. Amazon ECR stores container images.
12. CloudWatch collects logs and operational metrics.
13. Hybrid/on-premise computing is a future extension, not part of the initial functional implementation.

The Scheduler must use an abstraction such as `IComputeBackend` so additional providers can eventually be implemented, but only the AWS Batch provider needs to work.

## 3. Technology Stack

### Backend

- C# / .NET 10
- ASP.NET Core Web API
- AWS SDK for .NET
- Dependency Injection
- Strongly typed configuration
- Structured logging
- xUnit or equivalent automated testing

### HPC Conversion

- C++20
- OpenCascade Technology (OCCT)
- CMake
- Linux
- Docker
- STEPCAFControl_Reader
- XCAF document representation
- BRepMesh_IncrementalMesh
- RWGltf_CafWriter

### Frontend

- React
- TypeScript
- Vite
- Babylon.js
- Babylon.js GLB loader
- Responsive user interface

### Infrastructure

- AWS CloudFormation (YAML)
- Amazon API Gateway
- Application Load Balancer
- ECS Fargate
- AWS Batch with EC2
- Amazon S3
- DynamoDB
- DynamoDB Streams
- Lambda
- SQS with DLQ
- EventBridge
- EventBridge Scheduler
- ECR
- CloudWatch
- IAM and VPC networking

Use official AWS SDKs. Do not use AWS access keys hardcoded in source code.

## 4. Main User Scenario

Implement the following complete scenario.

1. A user signs in to the web application.
2. The user selects a STEP file.
3. The frontend requests creation of a conversion job.
4. Job API authenticates the user and determines the tenant.
5. Job API creates a DynamoDB record with status `UPLOADING`.
6. Job API generates an S3 Presigned URL for the specific input object.
7. The frontend uploads the STEP file directly to S3.
8. The upload progress bar shows actual transferred bytes.
9. The frontend notifies Job API when uploading finishes.
10. Job API validates the uploaded object using S3 metadata.
11. Job API updates the job status to `READY`.
12. DynamoDB Streams invokes Dispatch Lambda.
13. Dispatch Lambda publishes a message to SQS.
14. Placement Scheduler reads the message and submits a job to AWS Batch.
15. AWS Batch provisions suitable EC2 compute resources.
16. A C# worker host downloads the STEP file from S3.
17. The worker starts the C++ OpenCascade executable.
18. C++ reads STEP geometry, triangulates it and exports GLB.
19. Actual conversion progress is reported to the backend.
20. The C# worker uploads the completed GLB and manifest to S3.
21. The Batch job finishes successfully.
22. EventBridge sends the Batch status event to State/Recovery Lambda.
23. Lambda validates successful completion and updates DynamoDB.
24. The frontend detects completion through the Job API.
25. Job API generates a Presigned GET URL for the result.
26. Babylon.js loads the GLB file directly from S3.
27. The user can rotate, zoom, inspect and download the resulting model.

The full process must work end-to-end in the deployed AWS environment.

## 5. Multi-Tenant DynamoDB Data Model

Use one DynamoDB table named `SimulationJobs`.

Design around the following initial structure:

```text
PK = TENANT#<tenantId>
SK = JOB#<jobId>
```

Suggested job attributes:

```text
tenantId
jobId
status
createdAt
updatedAt
inputBucket
inputKey
outputBucket
outputKey
attemptId
batchJobId
batchJobQueue
progressPercent
progressStage
progressMessage
errorCode
errorMessage
startedAt
completedAt
version
```

Persist attempt records in the same table, for example:

```text
PK = TENANT#<tenantId>
SK = ATTEMPT#<jobId>#<attemptId>
```

Use tenant-scoped `Query` operations for listing jobs.

Avoid table-wide scans for normal API functionality.

Add a GSI only when required by a documented access pattern.

Support pagination.

Use conditional updates and optimistic concurrency to prevent invalid state transitions.

The DynamoDB schema must support multiple users and multiple jobs per tenant.

Do not accept a trusted `tenantId` directly from the browser. Resolve it server-side from the authenticated user's identity or a securely maintained user-to-tenant mapping. Such mappings may be stored in the same DynamoDB table.

Document the hot-partition risk of using one partition key for a highly active tenant. Keep this simple key structure for the MVP, but explain how tenant sharding could be introduced later.

## 6. Job State Machine

Implement an explicit state machine.

Required logical states:

```text
UPLOADING
READY
DISPATCHING
SUBMITTED
QUEUED
RUNNING
SUCCEEDED
FAILED
CANCELLED
```

Distinguish application states from the native AWS Batch states.

Rules:

- A job cannot become READY before its input exists and is verified.
- A job cannot be submitted twice merely because the same SQS message was delivered twice.
- A failed attempt must not overwrite the result or state of a newer attempt.
- A terminal job must not return to RUNNING because a delayed event arrives.
- Percent progress must never decrease within the same attempt.
- Do not mark a job SUCCEEDED simply because a GLB object exists in S3.
- Validate Batch completion, matching attempt identity and result manifest.

Use conditional DynamoDB updates.

Include explicit transition validation and unit tests.

## 7. C# Job API

Implement an ASP.NET Core Web API with the following endpoints:

```http
POST   /api/jobs
POST   /api/jobs/{jobId}/upload/complete
GET    /api/jobs
GET    /api/jobs/{jobId}
GET    /api/jobs/{jobId}/progress
GET    /api/jobs/{jobId}/result
POST   /api/jobs/{jobId}/cancel
GET    /health
```

`POST /api/jobs` should create a job and return an upload session containing the Presigned S3 URL.

For large files, support multipart upload with endpoints for initiating the upload, requesting part URLs and completing the multipart upload.

Job creation and upload completion must be separate operations.

Implement cancellation with appropriate AWS Batch APIs.

Use DTOs, validation, typed service interfaces and structured errors.

Avoid placing AWS SDK calls directly into HTTP controller methods.

Suggested structure:

```text
Api/
  Controllers/
  Contracts/
Application/
  Jobs/
  Storage/
  Scheduling/
Domain/
  Job/
  JobAttempt/
Infrastructure/
  DynamoDb/
  S3/
  AWSBatch/
```

Use the AWS SDK for .NET with IAM task role credentials.

## 8. S3 Security — Critical Requirement

The browser must transfer STEP and GLB files directly to and from S3.

However, it must NEVER receive AWS credentials or unrestricted bucket access.

Implement the following:

- Private S3 buckets
- S3 Block Public Access enabled
- Bucket owner enforced object ownership
- Server-side encryption
- Short-lived Presigned URLs
- URLs scoped to a specific object and operation
- Tenant-aware access authorization in Job API
- Input key validation
- No arbitrary S3 key selection by the user
- Correct S3 CORS configuration
- Input format and upload size limits
- Verification of upload completion
- No logging of full Presigned URLs

Use object keys similar to:

```text
inputs/<tenantId>/<jobId>/input.step

results/<tenantId>/<jobId>/<attemptId>/model.glb
results/<tenantId>/<jobId>/<attemptId>/manifest.json
```

Uploading a file directly to S3 does NOT mean granting the user access to an entire bucket.

Clearly document this distinction.

Use scoped IAM roles for signing operations where appropriate. Do not rely on object prefixes alone as a security boundary.

Provide an authentication mechanism suitable for a real multi-tenant application. Cognito/JWT authentication may be added as a supporting component, but do not replace the agreed Job API architecture with Lambda.

## 9. C++ OpenCascade Conversion Engine

Implement a real standalone C++ command-line program:

```bash
step-to-glb \
  --input /data/input.step \
  --output /data/model.glb \
  --linear-deflection 0.1 \
  --angular-deflection 0.5
```

Use OpenCascade for the entire CAD processing pipeline.

The implementation should:

1. Initialize an XCAF document.
2. Read the STEP file using `STEPCAFControl_Reader`.
3. Preserve assembly structure where supported.
4. Preserve object names and colors where supported.
5. Validate the imported geometry.
6. Identify shapes requiring tessellation.
7. Generate triangulation with `BRepMesh_IncrementalMesh`.
8. Export a binary glTF 2.0 file with `RWGltf_CafWriter`.
9. Check the export result and verify the output file.
10. Return meaningful process exit codes.

Do not generate a fake GLB or substitute a Python conversion library.

Use a pinned, documented version of OpenCascade.

Verify actual OCCT API signatures against that version. Do not invent OpenCascade methods or assume every OCCT release has identical APIs.

Support input units and correct coordinate conversion to glTF conventions.

Document tessellation quality/performance trade-offs.

Handle invalid STEP files, missing geometry, empty models, out-of-memory conditions and export failures.

### C++ Progress Reporting

The C++ application must emit structured JSON progress events to stdout.

Example:

```json
{"stage":"reading","percent":10,"message":"Reading STEP file"}
{"stage":"importing","percent":25,"message":"Importing CAD assembly"}
{"stage":"meshing","percent":55,"message":"Generating triangulation"}
{"stage":"exporting","percent":85,"message":"Writing GLB"}
{"stage":"validating","percent":95,"message":"Validating output"}
```

These values must be based on actual processing stages.

Where OpenCascade exposes meaningful progress information, connect it to the progress reporting mechanism.

Do not pretend that real-time percentage precision exists for an OCCT operation that does not expose measurable subprogress.

For such operations, report a stage as active and use indeterminate progress if necessary.

The C++ executable must not require any AWS SDK dependency.

Keep the CAD conversion engine independent from cloud infrastructure.

## 10. C# AWS Batch Worker Host

Implement a separate .NET console application that runs inside the AWS Batch container.

The worker host is responsible for:

1. Reading job and attempt metadata from environment variables or arguments.
2. Downloading the input STEP file from S3.
3. Starting the native C++ OpenCascade converter.
4. Reading structured JSON progress events from stdout.
5. Updating progress in DynamoDB through AWS SDK for .NET.
6. Uploading the GLB to the output bucket.
7. Writing a result manifest.
8. Reporting errors with clear exit codes.
9. Handling interruption and cancellation.

Use `System.Diagnostics.Process` to execute the C++ program.

Capture stdout and stderr separately.

Use stage-aware progress reporting.

Throttle DynamoDB progress updates to avoid unnecessary write costs. A configurable minimum time interval and percentage-change threshold should be supported.

Use conditional updates verifying `attemptId` so an old worker cannot overwrite a newer attempt.

The C++ executable performs all geometry processing.

The C# host performs all AWS integration.

This separation is intentional.

## 11. Progress Bar Requirements

The frontend must display two distinct progress phases.

### Upload Progress

Show actual bytes uploaded to S3.

Use an upload method with real byte-level progress events.

The progress bar must reflect real network transfer progress.

### Conversion Progress

Display:

```text
Job status
Current processing stage
Progress percentage where measurable
Elapsed time
Error message if failed
```

Example:

```text
Status: RUNNING

Reading STEP geometry       Completed
Generating triangulation    In progress
Exporting GLB               Pending

Progress: 57%
```

The frontend should poll:

```http
GET /api/jobs/{jobId}/progress
```

Use a configurable interval, initially approximately 2 seconds.

Stop polling when the job reaches a terminal state.

Use exponential backoff during transient API failures.

Show an indeterminate indicator while AWS Batch is waiting for compute capacity.

Never simulate conversion progress with a frontend timer.

Progress must originate from the actual C++ conversion process through the C# worker.

Do not display 100% completed until the output is stored and the job is confirmed successful.

## 12. Babylon.js Viewer

Implement a functional 3D viewer.

Required features:

- GLB loading via Babylon.js
- ArcRotateCamera
- Mouse orbit
- Zoom
- Pan
- Auto-fit to model bounds
- Scene lighting
- Configurable background
- Loading indicator
- Error state for invalid GLB
- Download GLB button
- Proper cleanup and disposal of Babylon resources

Retrieve a Presigned GET URL from Job API.

Use that URL to load the model directly from S3.

Ensure the loader handles URLs with query-string signatures correctly and does not break GLB format detection.

Support multiple uploaded jobs and selecting which result to display.

The frontend must never assume the S3 bucket is publicly accessible.

## 13. Dispatch Lambda and Amazon SQS

Implement DynamoDB Streams event processing.

When a job transitions to READY:

1. Filter the relevant stream event.
2. Validate job identity and status.
3. Send a dispatch request to SQS.
4. Preserve tenantId, jobId and attemptId.
5. Handle partial batch failures correctly.

Use an SQS Dead-Letter Queue.

Configure appropriate visibility timeout and retry behavior.

Handle at-least-once message delivery.

The Dispatch Lambda must not submit the HPC job directly in the default architecture.

Submission is the responsibility of the Placement Scheduler.

## 14. Placement Scheduler — C# Service

Implement a small, independent .NET Worker Service on ECS Fargate.

Use long polling of SQS.

Responsibilities:

- Read dispatch messages.
- Validate the current DynamoDB state.
- Atomically claim a dispatch attempt.
- Select the compute backend.
- Submit the job to AWS Batch.
- Persist the AWS Batch Job ID.
- Delete the SQS message only after the dispatch outcome has been durably recorded.
- Retry transient AWS errors.
- Release or expire abandoned claims.
- Send structured logs to CloudWatch.

Implement:

```csharp
public interface IComputeBackend
{
    Task<ComputeSubmissionResult> SubmitAsync(
        ComputeJobRequest request,
        CancellationToken cancellationToken);

    Task<ComputeJobStatus> GetStatusAsync(
        string providerJobId,
        CancellationToken cancellationToken);

    Task CancelAsync(
        string providerJobId,
        CancellationToken cancellationToken);
}
```

Provide a working `AwsBatchComputeBackend`.

Do not implement Azure or on-premise backends now.

Keep interfaces suitable for future hybrid-cloud support.

For this demo, all STEP-to-GLB conversions use the CPU Batch queue.

Support configuration for future GPU, High RAM and MPI queues without treating them as necessary for STEP conversion.

### Critical Idempotency Scenario

Consider this failure:

1. Scheduler invokes `SubmitJob`.
2. AWS Batch accepts the job.
3. Scheduler crashes before saving the returned Batch Job ID.
4. The SQS message becomes visible again.

The next Scheduler instance must not blindly submit an identical job.

Implement durable dispatch attempt tracking and recovery of an uncertain submission.

Use deterministic Batch job names containing job/attempt identity and reconciliation through AWS Batch APIs, including `ListJobs` with the `JOB_NAME` filter where appropriate.

Document remaining race conditions and limitations. Do not claim exactly-once execution if the underlying services cannot guarantee it.

Use attempt fencing so stale or duplicate Batch executions cannot publish an authoritative result.

## 15. AWS Batch Infrastructure

Use AWS Batch managed EC2 compute environments.

Implement a working CPU Compute Environment for the conversion demo.

Requirements:

- EC2-based compute
- Configurable instance families
- Configurable On-Demand or Spot capacity
- `minvCpus = 0` for cost-sensitive demo environments
- Configurable `maxvCpus`
- AWS Batch Job Queue
- AWS Batch Job Definition
- ECR-hosted container image
- Correct IAM job execution and job roles
- CloudWatch logging
- Retry strategy
- Timeout configuration

Use an EC2-compatible AWS Batch job definition, not a Fargate job definition, for the HPC worker.

Create a suitable container image containing:

- .NET worker host
- C++ converter
- OpenCascade runtime dependencies

Implement multi-stage Docker builds.

Make High RAM, GPU and MPI configuration an optional future extension. Do not provision expensive unused infrastructure by default.

Ensure the demo can scale EC2 capacity down when idle.

## 16. EventBridge and State/Recovery Lambda

Implement one State/Recovery Lambda function with two event sources:

1. AWS Batch job state change events through EventBridge.
2. Periodic reconciliation through EventBridge Scheduler.

For Batch events:

- Identify the job and attempt.
- Validate the event against DynamoDB.
- Ignore stale or duplicate updates.
- Update the application status.
- On successful completion, verify that the result manifest exists.
- On failure, record meaningful error details.

For reconciliation:

- Find jobs in stale intermediate states.
- Check the actual Batch job status.
- Recover missing or delayed state changes.
- Investigate uncertain submissions.
- Detect abandoned dispatch attempts.
- Repair inconsistent states safely.

Use bounded scans or purpose-designed indexes rather than unrestricted full-table scans in normal operation.

A worker's progress updates and Batch job state events must not race into inconsistent terminal states.

## 17. AWS CloudFormation Deployment

Create CloudFormation YAML templates for all necessary AWS infrastructure.

Suggested organization:

```text
infrastructure/
  cloudformation/
    bootstrap.yaml
    network.yaml
    storage.yaml
    data.yaml
    messaging.yaml
    compute.yaml
    applications.yaml
    monitoring.yaml
    frontend.yaml
```

Provision:

- VPC
- Public/private subnets
- Required routing and networking
- Security groups
- IAM roles and policies
- API Gateway HTTP API
- Private VPC integration
- Internal ALB
- ECS Fargate Job API
- ECS Fargate Scheduler
- DynamoDB
- DynamoDB Streams
- S3 input bucket
- S3 output bucket
- SQS dispatch queue
- SQS DLQ
- Dispatch Lambda
- State/Recovery Lambda
- EventBridge rule
- EventBridge Scheduler
- AWS Batch Job Queue
- AWS Batch EC2 Compute Environment
- AWS Batch Job Definition
- ECR repositories
- CloudWatch log groups and useful alarms
- Frontend static hosting, preferably S3 + CloudFront
- Authentication resources needed for the demo

Use CloudFormation parameters for:

```text
EnvironmentName
AWSRegion
ApiImageUri
WorkerImageUri
SchedulerImageUri
InstanceTypes
MaxVcpus
UseSpot
AllowedOrigins
LogRetentionDays
```

Do not hardcode AWS account IDs or secrets.

CloudFormation must not pretend to build Docker images automatically. Provide a correct bootstrap and deployment procedure that creates ECR repositories, builds/pushes images, and deploys stacks using those image URIs.

Ensure that IAM permissions, NAT/VPC endpoints, image pulling and private networking are configured correctly.

Include deployment outputs such as:

```text
ApiUrl
FrontendUrl
InputBucketName
OutputBucketName
DynamoDbTableName
BatchJobQueueArn
```

Optimize the development deployment for low idle cost.

Document components that incur standing charges, including ALB, Fargate and networking.

Do not claim that the entire stack is free when idle.

## 18. Local Development

Provide a practical local development mode.

It should be possible to:

1. Compile the C++ converter.
2. Run a test STEP-to-GLB conversion.
3. Run the ASP.NET Core API.
4. Run the React frontend.
5. Run the C# worker locally.
6. Test progress reporting.
7. Load the generated GLB in Babylon.js.

Use an `IComputeBackend` implementation that starts the local worker process for development only.

Use DynamoDB Local or controlled repository substitutes where appropriate.

Do not claim AWS Batch is fully emulated locally if that is not true.

Document which tests require actual AWS resources.

## 19. Automated Tests

Implement tests covering:

### C++ Conversion

- Valid STEP model
- Invalid STEP input
- Empty geometry
- Binary GLB output header
- Nonempty mesh
- Assembly conversion
- Color preservation where supported
- Invalid meshing parameters

Generate a small STEP test fixture from code if necessary.

### C# Backend

- Job creation
- Upload completion validation
- Tenant isolation
- Invalid state transitions
- Conditional DynamoDB updates
- Pagination
- Presigned URL authorization
- Result access authorization

### Scheduler

- Duplicate SQS message
- Expired dispatch lease
- AWS Batch API error
- Crash after SubmitJob
- Retry recovery
- Cancellation
- Dead-letter handling

### Worker

- Progress event parsing
- Monotonic progress updates
- Native process failure
- Missing input
- Upload failure
- Manifest creation
- Attempt fencing

### Frontend

- Upload progress
- Conversion progress
- Failed job display
- Completed job GLB loading
- Different tenants cannot access each other's jobs

Provide an AWS smoke test for a complete deployed conversion.

## 20. Repository Structure

Create a repository similar to:

```text
step-glb-platform/
├── src/
│   ├── backend/
│   │   ├── JobApi/
│   │   ├── Application/
│   │   ├── Domain/
│   │   ├── Infrastructure/
│   │   ├── Scheduler/
│   │   ├── DispatchLambda/
│   │   ├── StateRecoveryLambda/
│   │   └── WorkerHost/
│   ├── hpc/
│   │   └── StepToGlb/
│   │       ├── CMakeLists.txt
│   │       ├── include/
│   │       ├── src/
│   │       └── tests/
│   └── frontend/
│       ├── src/
│       │   ├── api/
│       │   ├── components/
│       │   ├── pages/
│       │   └── viewer/
│       └── package.json
├── infrastructure/
│   └── cloudformation/
├── docker/
├── scripts/
├── tests/
├── docs/
│   ├── architecture.md
│   ├── deployment.md
│   ├── data-model.md
│   ├── security.md
│   ├── reliability.md
│   └── cost-estimation.md
└── README.md
```

## 21. Deliverables

Provide:

1. Complete C# source code.
2. Complete C++ OpenCascade converter.
3. Complete React/TypeScript/Babylon.js frontend.
4. Dockerfiles.
5. CloudFormation infrastructure templates.
6. Deployment scripts.
7. Local development instructions.
8. Automated tests.
9. An architecture diagram consistent with the implementation.
10. API documentation.
11. Database schema.
12. AWS security and IAM documentation.
13. A reliability/failure-mode analysis.
14. AWS cost estimation guidance.
15. A README that explains how to run a real STEP-to-GLB conversion.

## 22. Engineering Quality Requirements

Follow these rules:

- No pseudocode in place of required functionality.
- No fake progress bars.
- No fake GLB files.
- No hardcoded AWS credentials.
- No public S3 buckets.
- No assumed exactly-once processing.
- No arbitrary access to another tenant's files.
- No direct client access to DynamoDB.
- No missing production error handling.
- No unsupported OCCT API calls.
- No unnecessary dependencies.
- No manual AWS-console steps when CloudFormation can implement them.

Use SOLID, clean architecture where useful, but avoid excessive abstractions that make the demo unnecessarily complicated.

Keep geometry processing independent from AWS.

Keep cloud infrastructure logic independent from the CAD kernel.

Use real SDK integrations.

## 23. Implementation Strategy

Work in the following order:

**Phase 1 — HPC engine**

Implement and test the standalone C++ STEP-to-GLB converter with OpenCascade.

Prove the output is a valid GLB and loads in Babylon.js.

**Phase 2 — Local application**

Implement the C# API, local worker execution, frontend and real progress reporting.

Achieve a complete local conversion.

**Phase 3 — AWS storage and database**

Implement DynamoDB, tenant isolation, S3 Presigned URLs and secure object access.

**Phase 4 — Distributed processing**

Implement DynamoDB Streams, Dispatch Lambda, SQS, Scheduler, AWS Batch and the worker container.

**Phase 5 — Reliability**

Implement Batch events, reconciliation, retry handling, idempotency and cancellation.

**Phase 6 — CloudFormation**

Implement deployment automation and verify infrastructure dependencies.

**Phase 7 — End-to-end validation**

Deploy the system and demonstrate:

Upload STEP → AWS Batch conversion → live progress → S3 GLB → Babylon.js viewer.

At the end of each phase:

- List changed files.
- Build the affected projects.
- Run applicable tests.
- Report what actually passed.
- Identify any remaining blockers.
- Do not claim a successful AWS deployment unless it has been tested against AWS.

Begin with a concise implementation plan and repository structure, then proceed directly to implementation. Do not stop after writing the plan or architecture documentation.

The final result must be a functioning reference implementation of a distributed cloud-based CAD conversion platform.