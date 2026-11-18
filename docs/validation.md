# Validation and implementation record

This repository started with only AGENTS.md. The original request is preserved verbatim in SPEC.md; assumptions were recorded before implementation. The local checks and the live AWS smoke test below cover the tested configurations and sample files.

## Phase 1: native engine

Changed `src/hpc/StepToGlb/{CMakeLists.txt,include/converter.hpp,src/converter.cpp,src/main.cpp,tests/converter_tests.cpp}` to implement the real pinned OCCT pipeline, argument/error handling, JSON stage output, atomic output publication and generated fixtures. Added `scripts/build-native.sh` and `tests/frontend/validate-glb.mjs` for repeatable validation.

Evidence: native Release build and CTest pass. Box, translated coloured/named assembly and inch-unit box produce real GLBs. The Khronos validator reports zero errors for all three, and Babylon.js loads them with nonempty meshes, red materials, expected dimensions in metres, Y-up orientation and assembly/name preservation. Negative tests cover invalid/missing STEP, empty geometry, invalid/nonfinite mesh parameters, same input/output, truncated GLB and JSON escaping. This proves the tested fixtures; arbitrary industrial models remain a broader regression task.

## Phase 2: local application

Changed all projects under `src/backend`, `Directory.Build.props`, `global.json`, `StepGlb.sln`, the React client under `src/frontend`, `scripts/run-local.sh`, `scripts/smoke.py`, and backend/browser tests. These implement the tenant-scoped API, durable local store, separate C# worker process, native stdout/stderr integration, progress/cancellation, manifest verification, actual browser transfer progress, polling and Babylon viewer. The viewer supports orbit/zoom/pan, fit-to-bounds, lighting, background selection, signed URLs, download, disposal and error/loading states.

Evidence: the .NET solution builds with zero warnings/errors; 25 xUnit tests pass. A real HTTP smoke uploads the STEP assembly, executes the native converter, verifies durable output plus local provider success, then downloads a GLB. Chromium additionally uploads and renders it with nonuniform WebGL pixels and no page errors. Browser failure tests use real empty STEP geometry, exhaust bounded retries, show FAILED without a result link, and exercise cancellation. The production bundle also passed the browser test under the CloudFormation CSP with a local API allowance, including a pixel check for background selection. Screenshot: `artifacts/local-workspace.png`.

## Phases 3–5: AWS adapters and reliability

Changed `src/backend/Infrastructure/{Stores,ObjectStores,Compute,DispatchAndRecovery,Bootstrap,WorkerRunner}.cs`, the API's JWT setup, and the DispatchLambda/Scheduler/StateRecoveryLambda projects. AWS mode uses official AWS SDK for .NET clients for S3, DynamoDB, SQS and Batch. It includes versioned inputs, scoped presigning/multipart, conditional transactional job/attempt records, durable submission intent, name lookup after uncertain submissions, worker execution fencing, bounded application retries, terminal-state protection, cancellation and output/provider verification.

Evidence: actual AWS SDK presigning tests confirm content-length signing and scoped part numbering. Unit tests cover tenant isolation, pagination, atomic request conditions, duplicate dispatch, crash-after-accept recovery, expired leases, retries, cancellation, invalid manifests, native failure, worker fencing and stream partial responses. A separate integration executable passed against DynamoDB Local using real SDK transactions and queries: creation conflict, one winner under version contention, job/attempt records, tenant isolation, cursor behavior and operational GSI. This does not verify IAM, S3 service behavior, Cognito, SQS redelivery, Batch capacity or EventBridge timing in AWS.

## Phase 6: deployment artifacts

Changed `infrastructure/cloudformation/{bootstrap,network,platform}.yaml`, `docker/{service,worker}.Dockerfile`, and the C# `tools/Deploy` application. Added run/check/smoke automation and deployment, security, reliability, architecture, data-model, geometry and cost documentation. `docs/file-manifest.txt` lists exact source/configuration files added to this initially empty repository; AGENTS.md was not modified.

Evidence: cfn-lint passes all three templates. The C# deployment tool builds and its help command runs. Both C# Lambda projects publish successfully. Linux ARM64 API and scheduler Docker images build; the API container answers `/health`, including with a read-only root filesystem and writable `/tmp`. The Linux ARM64 worker image builds pinned OCCT from source and passes native CTest. The non-root container smoke passed the full C# host → native converter → durable GLB/manifest → checksum verification → provider confirmation path. Its output also passes Khronos/Babylon geometry validation. Worker/Lambda runtime configurations require only Microsoft.NETCore.App; ASP.NET is confined to the API/runtime used by service images. The deployment tool builds/pushes linux/amd64 images for the configured x86 EC2 families; ARM64 local image checks do not prove x86 runtime behavior.

## Phase 7: Live AWS smoke test

CloudFormation deployed the application in Stockholm (`eu-north-1`). The Job API served the React app through API Gateway and the internal ALB. The live health and UI endpoints returned HTTP 200, and unauthenticated access to the jobs API returned HTTP 401.

A signed-in browser uploaded `block.stp` and `as1_pe.stp` from the [STEP Tools AP203 sample set](https://www.steptools.com/docs/stpfiles/ap203/index.html). Both jobs succeeded on an On-Demand `c7i-flex.large` Batch worker, with one attempt per file. Each GLB rendered in Babylon.js and passed Khronos validation with zero errors. The downloaded results matched the checksums in S3, and both Batch and DynamoDB recorded success.

Live multi-tenant isolation, multipart uploads, Spot interruption, and recovery from service failures remain separate AWS validation cases.

Known limits are documented in reliability/security/deployment. The production frontend build currently emits a size warning for the lazily loaded Babylon.js viewer chunk; type checking and compilation pass. npm and NuGet vulnerability checks reported no known vulnerable packages at the time checked. The final check script passed: native CTest, all-project locked .NET build (zero warnings/errors), 25 backend tests, 4 frontend tests, three Khronos/Babylon fixture checks, production frontend build and all CloudFormation templates. Base images and pinned packages still require ongoing update/scanning. There is no automated refresh-token/session-renewal flow, tenant billing quota, immutable event audit or configured alarm notification subscription.
