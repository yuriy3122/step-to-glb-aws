# Architecture

See the [architecture diagram in the README](../README.md#architecture) and its [Mermaid source](architecture.mmd).

The API and placement scheduler are separate Fargate services. The conversion worker uses an EC2-compatible Batch job definition and never runs on Fargate. Lambda functions handle stream dispatch and state/recovery only. AWS Batch owns EC2 selection/provisioning; the placement abstraction chooses the configured CPU queue. GPU, MPI, high-memory and hybrid capacity are not provisioned.

The Job API container serves the React build through the same API Gateway, VPC Link and internal ALB used by job requests. The browser signs in with Cognito and transfers CAD files directly to and from private S3 using short-lived presigned URLs.

The C++ library has no AWS dependencies. The C# worker downloads a pinned input version, launches the converter, consumes JSON stdout separately from bounded stderr, conditionally publishes progress, and uploads a checksum-bearing GLB plus manifest. Provider success is confirmed by recovery, not inferred from native process completion.

In local Development mode the API hosts a small polling orchestrator, a filesystem object adapter, and a disk store with cross-process locks. `LocalComputeBackend` launches the same worker DLL. The native pipeline and viewer are unchanged. AWS mode uses actual SDK calls; local orchestration is not a Batch emulator.
