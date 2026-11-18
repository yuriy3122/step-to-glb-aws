# Implementation assumptions and decisions

Recorded before implementation; the original request is preserved in SPEC.md.

- The Stockholm deployment serves the React build from the existing Job API container through API Gateway and the internal ALB. The same HTTPS origin serves the UI and job API. Private S3 buckets continue to handle signed CAD file transfers directly.

- OCCT is pinned to **7.9.3**, including local CMake and Linux container builds. CMake must reject a different OCCT version. Native APIs are checked against installed headers and the upstream V7_9_3 sources.
- CAD is imported into a millimetre XCAF document. The exporter converts its Z-up coordinates and length unit to glTF Y-up/metres. Tests check model dimensions and transformed assemblies.
- Native stages without measurable subprogress emit `percent: null`. Native completion means local geometry output was validated; it does not mean the distributed job succeeded. Only the orchestrator may publish 100% after durable output verification and provider success.
- A generated OCCT box and a translated, named, coloured assembly provide redistributable STEP fixtures. These exercise real STEP writing, reading, triangulation and GLB export.
- Development runs on this Apple Silicon machine; the deployed worker targets Linux amd64 EC2. Docker and AWS CLI were absent at inspection, so container/AWS validation must be reported separately from native local results.
- Local development preceded the live deployment. The live AWS smoke test used Stockholm and an administrator-created Cognito test tenant. See [validation results](validation.md).
- AWS SDK for .NET 4.1.0 is pinned using NuGet lockfiles. Frontend dependencies use an npm lockfile; Node 24 LTS is required (the machine's original Node 22.11 predates the build tool requirement).
- CloudFormation uses the deployment region pseudo-parameter `AWS::Region`; a redundant `AWSRegion` template parameter is deliberately omitted to prevent cross-region resource mismatches.
- The demo network uses two private subnets and one NAT gateway. This reduces standing cost but the NAT's AZ is a single point of failure; production should provide AZ-local egress.
- Retries are application attempts (maximum three). Batch's own retry count is one, so a retried container never shares a successful worker's execution fence. Unknown submission outcomes are searched by name for ten minutes before a new fenced attempt is allowed.
- Large browser uploads use sequential 8 MiB S3 multipart parts above 32 MiB. Local mode uses a single streamed upload. The default input limit is 512 MiB.
- Cognito accounts are administrator-created. An immutable, administrator-supplied `custom:tenantId` is readable but not writable by the SPA app client. Sign-in uses authorization code with PKCE and 15-minute ID tokens.
- The application will use an explicit development-only local identity and filesystem object store for local execution. AWS mode requires validated JWT identity and private S3; local substitutes are never advertised as AWS emulation.

- Docker CLI/buildx and a separate local Colima profile were subsequently installed for Linux validation. API, scheduler and worker images were built/tested on Linux ARM64. The deployed Batch worker targets Linux amd64. The live results are recorded separately from the ARM64 local checks.
- Worker/Lambda projects use hosting NuGet packages rather than an ASP.NET shared-framework reference, so they can run on plain .NET 10 runtime images and the managed Lambda runtime.
