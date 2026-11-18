# Data model

One DynamoDB table defaults to `SimulationJobs`, with on-demand capacity, encryption, point-in-time recovery and NEW_AND_OLD_IMAGES streams.

| Record | PK | SK |
|---|---|---|
| Logical job | `TENANT#<tenantId>` | `JOB#<jobId>` |
| Attempt snapshot | `TENANT#<tenantId>` | `ATTEMPT#<jobId>#<attemptId>` |

Job and current attempt snapshots are written together using `TransactWriteItems`. Creation requires `attribute_not_exists(PK)` at the exact composite key. Subsequent writes condition on the current numeric `version`; the saved payload advances the version. Code checks attempt identity and worker execution identity before every conditional mutation. A concurrent retry/cancel changes the version and prevents a stale mutation from committing. Conditional conflicts retry with bounded jitter.

Top-level stream/index attributes are `tenantId`, `jobId`, `attemptId`, `status`, `version`, and a JSON `payload`. The payload includes timestamps, input size/filename, pinned S3 version/ETag, provider ID, execution fence, lease owner/expiry, submission-intent timestamp, retry availability, stage/progress/error fields and attempt number. Object keys derive exclusively from server identity and generated IDs.

Listing uses a strongly consistent tenant `Query`, `begins_with(SK, JOB#)`, descending sort keys, 1–100 items and an opaque cursor containing the last job ID. It is lexicographic by UUID, not creation time. Cursors cannot select another tenant: the PK always comes from validated identity. There is no ordinary table scan.

The `Operational` GSI supports only reconciliation:

- `OpsPK = ACTIVE#00` through `ACTIVE#15`, chosen by SHA-256(jobId)'s first byte modulo 16.
- `OpsSK = updatedAt` in UTC round-trip timestamp format.
- Nonterminal jobs and cancellations awaiting provider confirmation appear in this index. Attempt snapshots and settled terminal jobs do not.
- Each scheduled invocation queries at most 25 oldest stale records per shard (400 total), older than two minutes. Successful processing touches or settles each record so later invocations make progress. GSI reads are eventually consistent; recovery reloads the base record before acting.

Each retry creates a new attempt ID and preserves the prior attempt snapshot. These are operational snapshots, not an append-only event audit. Job success includes the current attempt/execution/provider identity and a verified result manifest.

A very active tenant can concentrate job and attempt writes on one pooled tenant partition. Adaptive capacity does not remove all partition throughput limits. Future write sharding can use `TENANT#id#SHARD#n`, stable job routing, and fan-out merge/cursors for tenant lists; this demo does not introduce that complexity prematurely. Metadata retention/archival and attempt-history APIs are future work.
