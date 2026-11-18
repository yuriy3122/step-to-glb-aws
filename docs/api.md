# Job API

All `/api/jobs` routes require a validated Cognito ID JWT with the configured issuer/audience, `token_use=id`, and an administrator-assigned `custom:tenantId`. Development Local mode supplies a fixed tenant. Tenant IDs, object keys, provider IDs and attempts are never accepted as trusted job-creation fields.

| Method and path | Request | Response |
|---|---|---|
| `POST /api/jobs` | `{ "fileName":"part.step", "bytes":12345, "multipart":false }` | 201 `{ job, uploadUrl, multipart }` |
| `POST /api/jobs/{id}/upload/complete` | `{}` | Verified job in READY |
| `GET /api/jobs?limit=20&token=...` | — | `{ jobs, nextToken }`, maximum 100 |
| `GET /api/jobs/{id}` | — | Current tenant-scoped job |
| `GET /api/jobs/{id}/progress` | — | Status, attempt, stage, optional percent, timestamps and errors |
| `GET /api/jobs/{id}/result` | — | `{ url }`, only after verified SUCCEEDED |
| `POST /api/jobs/{id}/cancel` | `{}` | Durably cancelled job; provider termination is reconciled |
| `POST /api/jobs/{id}/upload/parts/{number}` | `{}` | `{ url }` for this upload's numbered part |
| `POST /api/jobs/{id}/upload/multipart/complete` | `[{"partNumber":1,"eTag":"..."},...]` | Completes S3 multipart, verifies object, transitions to READY |
| `GET /health` | — | Public liveness probe |

Set `multipart=true` when creating the job to initiate a multipart session. Use sequential 8 MiB parts (the last may be smaller); signed part requests bind their expected content length. S3 ETags must be passed exactly, including their quotes. A multipart session is not available in local mode. Cancelling aborts an incomplete S3 multipart upload; the bucket lifecycle is a fallback.

The single PUT uses `Content-Type: application/octet-stream` and its declared Content-Length. The browser sends the Blob directly to the signed URL. Do not proxy file bytes through API Gateway; its limits are unrelated to direct S3 transfers. Upload completion verifies length and the STEP exchange header, pins the accepted S3 VersionId/ETag, then writes READY conditionally. The native converter performs full STEP/geometry validation later.

Result URLs expire after ten minutes and are bearer capabilities; obtain a new URL if it expires before loading/downloading. The SPA specifies `.glb` explicitly to Babylon's loader. GET requests never make the S3 bucket public.

Errors use Problem Details with HTTP 400 validation, 401/403 authentication/authorization, 404 absent or other-tenant job, 409 state/version conflict, and 500 unexpected provider/internal failure. Responses carry a correlation ID. A concurrent request should reload the job before retrying a state-changing operation. The API has a per-task request limiter and API Gateway has route throttles; this demonstration does not implement tenant billing quotas.

The progress endpoint has no synthetic time-based progress. A null percentage means indeterminate. Worker stage updates are immediate at stage boundaries and throttled during any measurable percentage changes. An attempt's numeric progress cannot decrease; a new retry attempt may reset it. Only confirmed logical success publishes 100%.
