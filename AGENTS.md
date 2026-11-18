# AGENTS.md — STEP-to-GLB AWS Platform

## Mission
Build a working, deployable reference implementation of a multi-tenant CAD processing platform. A React/TypeScript/Babylon.js client uploads STEP files, a C#/.NET backend orchestrates AWS jobs, and a C++20/OpenCascade worker converts STEP to GLB on AWS Batch EC2. AWS infrastructure is provisioned exclusively with CloudFormation.

This file defines repository-wide engineering rules. Follow the detailed product requirements in `docs/SPEC.md` when present. If a requirement is missing or ambiguous, record the assumption before implementing it. Do not silently change the architecture.

## Non-negotiable stack
- Backend and orchestration: C# / .NET 10, ASP.NET Core, AWS SDK for .NET.
- Native converter: C++20, CMake, a pinned OpenCascade Technology (OCCT) version; actual STEP import, tessellation, and binary glTF/GLB export.
- Frontend: React, TypeScript, Vite, Babylon.js.
- Infrastructure: AWS CloudFormation YAML. No Terraform or CDK.
- Tests: C++ tests, .NET tests, frontend tests, and AWS integration/smoke tests where feasible.

## Agreed architecture — preserve these boundaries
1. Client -> API Gateway -> VPC Link -> internal ALB -> ASP.NET Core Job API on **ECS Fargate**. The Job API is **not** a Lambda.
2. The Job API authenticates and authorizes the tenant, writes jobs/attempts to **one shared DynamoDB table**, and generates S3 presigned upload/download URLs.
3. Browser transfers STEP inputs and GLB outputs **directly to/from private S3** using authorized, short-lived presigned URLs; it never receives unrestricted bucket access or AWS credentials.
4. DynamoDB Streams -> Dispatch Lambda -> SQS dispatch queue (+ DLQ) -> C# Placement Scheduler on ECS Fargate -> AWS Batch job queue -> EC2 worker.
5. The Placement Scheduler selects a compute backend and Batch queue; AWS Batch chooses/provisions EC2 compute. Keep `IComputeBackend` extensible for future hybrid providers; implement only AWS Batch now.
6. AWS Batch emits state-change events to EventBridge -> State/Recovery Lambda -> the **same DynamoDB table**. EventBridge Scheduler invokes reconciliation periodically.
7. S3 contains input objects and per-attempt GLB/manifest outputs. ECR contains runtime images; CloudWatch receives logs/metrics.
8. Current working compute profile: CPU EC2 in AWS Batch; GPU, high-memory, MPI and hybrid/on-prem capacity are optional future extensions. Do not provision expensive unused capacity by default.
9. IAM, VPC networking, security groups, authentication, and encryption remain required even when omitted from a simplified diagram.

## Project responsibilities
- **Job API (C#):** HTTP contracts, authentication/authorization, job creation/listing/status/cancellation, multipart upload coordination, presigned URLs, validation, conditional DynamoDB transitions.
- **Dispatch Lambda (C#):** processes READY transitions from DynamoDB Streams and publishes SQS messages with partial batch failure handling.
- **Scheduler (C#):** long-polls SQS, validates/claims an attempt, selects AWS Batch queue, submits jobs, records the outcome, and acknowledges SQS only after durable handling.
- **Worker Host (C#):** runs in AWS Batch container, downloads input, launches native converter, reads JSON progress from stdout, updates attempt-scoped progress in DynamoDB, uploads GLB and final manifest.
- **Native converter (C++):** implements geometry processing only. Do not couple OCCT code to AWS SDK or browser code.
- **Frontend:** uploads with real byte progress, polls Job API for processing progress, loads authorized GLB in Babylon.js, handles failures and cancellation.

## DynamoDB and multi-tenancy
- Initial pooled table uses `PK = TENANT#<tenantId>` and `SK = JOB#<jobId>`; store attempt records in the same table, e.g. `ATTEMPT#<jobId>#<attemptId>`.
- Resolve `tenantId` from validated identity/server-side mapping, **never** from an untrusted client field.
- Scope reads, writes, presigned URLs, and S3 keys to the authorized tenant and job. Do not use `Scan` for ordinary list operations.
- Use conditional writes/versioning to enforce state-machine transitions and fence stale attempts; design GSIs for documented operational queries only.
- Explicitly acknowledge possible hot tenant partition keys and document future write sharding without implementing it prematurely.

## Reliability rules
- Assume at-least-once delivery and duplicate/out-of-order DynamoDB Streams, SQS, and EventBridge events.
- SQS `ReceiveMessage` is not completion. Acknowledge with `DeleteMessage` only after durable processing.
- AWS Batch `SubmitJob` does **not** provide an application idempotency key. Specifically handle the crash window after successful submission but before persisting its ID. Implement durable attempt records, reconciliation and stale-attempt fencing. Never claim exactly-once execution.
- Terminal states cannot be overwritten by late events; an old attempt cannot publish a current result.
- `SUCCEEDED` requires matching Batch success **and** verified output/manifest, not just the presence of any S3 file.
- Handle retries, DLQ, backoff, timeouts, cancellation, Spot interruption, bounded pagination and stale claims.
- Record meaningful structured logs with `tenantId`, `jobId`, `attemptId`, correlation ID, and provider job ID; do not log secrets or full presigned URLs.

## Geometry and progress correctness
- Import STEP with OCCT (`STEPCAFControl_Reader`/XCAF where appropriate), triangulate using `BRepMesh_IncrementalMesh`, and export real GLB with `RWGltf_CafWriter` for the **pinned OCCT version**. Check actual APIs against installed headers/docs; no invented method signatures.
- Preserve assembly hierarchy, names, colors, units and coordinate transforms where supported. Validate the GLB and load it in Babylon.js.
- The converter emits structured JSON progress lines on stdout. The C# worker parses them and publishes throttled attempt-scoped progress; the API exposes it to the frontend.
- Distinguish **upload byte progress** from **conversion stage progress**. If an OCCT operation has no measurable subprogress, show an indeterminate stage rather than inventing percentages.
- Do not report 100% until outputs are durable and final completion is confirmed.

## Security and deployment
- S3 buckets are private with Block Public Access; URLs are short-lived, object/operation-scoped; configure CORS narrowly and enforce upload limits/validation.
- Use ECS task roles, Batch job roles, Lambda execution roles and least-privilege IAM. No credentials in code, containers, templates or sample configuration.
- Use CloudFormation for VPC/subnets, required endpoints/routing, security groups, API Gateway/VPC Link/ALB/ECS, DynamoDB, S3, SQS, Lambda, Batch, ECR, EventBridge, authentication, CloudWatch, and frontend hosting.
- Account for deployment bootstrap: CloudFormation does not build Docker images; scripts must build/push to ECR before deploying image-dependent resources.
- Keep idle cost low (`minvCpus=0` where appropriate). Document charges for ALB, ECS/Fargate, NAT/networking, CloudWatch and storage. Never claim a completely free idle environment.

## Work sequence and proof of completion
1. Implement and test the standalone C++ converter with real STEP fixtures and valid GLB output.
2. Implement local C# worker, Job API, and React/Babylon.js end-to-end path with genuine progress.
3. Add secure multi-tenant S3, DynamoDB and authentication.
4. Add Dispatch Lambda, SQS, ECS Scheduler, AWS Batch EC2 and container images.
5. Add EventBridge status handling, reconciliation, retries, monitoring and CloudFormation deployment.
6. Run complete AWS smoke test when credentials and an AWS environment are available.

After each stage:
- State exactly which files changed and why.
- Run the applicable build, tests and lint/type checks; report their actual results.
- Identify blocked/unverified functionality. Never claim a deployed or passing system without evidence.
- Do not replace functional requirements with mocks, placeholders, fake GLBs, fake progress or pseudocode.
- Prefer the simplest implementation consistent with the agreed architecture; avoid speculative abstraction and overengineering.

## Expected repository structure
`src/backend/{JobApi,Application,Domain,Infrastructure,Scheduler,DispatchLambda,StateRecoveryLambda,WorkerHost}`
`src/hpc/StepToGlb`
`src/frontend`
`infrastructure/cloudformation`
`docker`
`scripts`
`tests`
`docs/{SPEC,architecture,data-model,security,reliability,deployment,cost-estimation}.md`

Start by reading this file and `docs/SPEC.md` (when available), inspecting the repository, then produce a short plan and implement the first testable slice. Do not stop at the plan.
