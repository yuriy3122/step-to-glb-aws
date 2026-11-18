# Reliability and failure analysis

The application assumes at-least-once delivery. It does not promise exactly-once compute execution.

Normal logical transitions are UPLOADING → READY → DISPATCHING → SUBMITTED → QUEUED → RUNNING → SUCCEEDED. Provider events can skip intermediate stages. Nonterminal states can fail or cancel; terminal logical jobs never reopen because of late events. A failed *attempt* can create a new READY attempt while the logical job remains nonterminal. Three attempts are allowed, with exponential availability delays of 10 and 20 seconds. The old attempt ID, provider binding and execution fence cannot authorize the new result.

| Failure/race | Handling |
|---|---|
| Missing, oversized or wrong-format upload | No READY transition; API verifies object metadata/header before conditional write |
| Presigned PUT reused after acceptance | Worker reads the accepted version and ETag, not the mutable current S3 object |
| Duplicate stream/SQS delivery | Conditional READY claim; only one claimant persists submission intent |
| Lease expires before submission intent | Recovery releases it to READY |
| Batch accepts SubmitJob; scheduler crashes before saving ID | Durable intent remains; recovery uses deterministic job name with `ListJobs` `JOB_NAME` filter, then binds its provider ID |
| Submission timeout/unknown outcome | Never immediately resubmit the same attempt; search for up to ten minutes, then fence it with a new attempt |
| Duplicate Batch executions | Bind one provider, terminate extras, and atomically claim one worker execution ID before downloading input |
| Old worker or late event after retry | Compare current attempt/provider/execution plus optimistic version at every mutation |
| Cancellation races with submission | Mark CANCELLED first; bind/cancel any subsequently returned provider ID; recovery searches uncertain submissions and confirms termination |
| Native failure, input missing, upload failure | Worker exits nonzero; provider state/recovery records failure or creates a new fenced attempt |
| Spot interruption/container timeout | Batch fails; recovery retries within attempt budget; output keys remain scoped to each attempt |
| Worker output exists but provider failed | Never SUCCEEDED; compute success and matching manifest/checksum must both be verified |
| Batch success but invalid/missing manifest | Retry or fail with OUTPUT_INVALID |
| Missing/late Batch event | Scheduled bounded reconciliation reloads authoritative provider state |
| SQS handler error | No DeleteMessage; visibility/backoff, receive-count redrive to DLQ |
| Stream partial failure | Return failed DynamoDB sequence identifiers with ReportBatchItemFailures; bounded retry and stream DLQ |
| Dispatch stream message expires or DLQs | Stale READY reconciliation republishes through SQS, preserving scheduler ownership of submission |

`SubmitJob` has no application idempotency token. The AWS SDK is configured with bounded request timeouts/retries, but a network delay or AWS eventual visibility can exceed an application's uncertainty horizon. Deterministic names are not unique constraints. Rare duplicate executions may still consume CPU; fencing ensures that a stale/duplicate execution cannot become the authoritative current result. A worker that loses DynamoDB access stops processing to preserve the fence. Batch's container retry count is one; application retries create distinct attempts.

A worker polls the durable fence every second and heartbeats every 30 seconds. Conversion is limited to 30 minutes; the Batch attempt timeout is 35 minutes. Native stdout and stderr drain separately to avoid a pipe deadlock. Stage changes publish immediately; intermediate numeric progress, if emitted, is throttled by configurable interval and delta. The native converter currently emits indeterminate stage progress only.

SQS uses 20-second long polls, one message per receive, 180-second visibility, bounded backoff and a DLQ after eight receives. The scheduler acknowledges only a persisted submission outcome or a message proven obsolete (different/terminal/already-bound attempt). Visibility expiry is not completion. AWS APIs, eventual GSI visibility, retry timings and provider termination behavior remain to be verified in AWS.

Reconciliation queries 16 GSI shards, at most 25 oldest stale jobs each. Reloading the strongly consistent base record avoids decisions from stale GSI snapshots. Records are touched/settled to move the bounded window forward; repeatedly broken records log errors and are touched so one bad job cannot indefinitely starve a shard. Cancellations leave the operational index once provider termination is observed, or no submission intent exists; uncertain cancelled submissions are searched for ten minutes. A much later accepted stale worker is rejected by its fence, but may consume capacity until Batch termination/timeout.

Operational limits: no durable full event audit, automated DLQ redrive, human notification subscription, or tenant quota accounting is implemented. DLQ alarms must be connected to the operator's notification channel. A deployment/process failure before local worker launch can leave a local provider record STARTING; local mode is development-only and does not provide all Batch recovery guarantees. STARTING records older than 35 minutes are treated as failed and retried within the application budget.

Official references: [Batch ListJobs filtering](https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html), [DynamoDB stream partial responses](https://docs.aws.amazon.com/lambda/latest/dg/services-ddb-batchfailurereporting.html).
