using Microsoft.Extensions.Logging;
using StepGlb.Application;
using StepGlb.Domain;

namespace StepGlb.Infrastructure;

public enum DispatchOutcome { Durable, Retry }
public sealed class DispatchService(IJobStore store, IComputeBackend compute, PlatformOptions options, ILogger<DispatchService> log)
{
    public async Task<DispatchOutcome> DispatchAsync(DispatchRequest request, CancellationToken ct)
    {
        var job = await store.GetAsync(request.TenantId, request.JobId, ct);
        if (job == null || job.AttemptId != request.AttemptId || job.Terminal || job.ProviderJobId != null) return DispatchOutcome.Durable;
        if (job.Status != JobStatus.READY) return DispatchOutcome.Retry;
        if (job.NotBefore > DateTimeOffset.UtcNow) return DispatchOutcome.Retry;
        var owner = Guid.NewGuid().ToString("N");
        var claim = job.Transition(JobStatus.DISPATCHING) with { LeaseOwner = owner, LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(options.LeaseSeconds) };
        if (!await store.SaveAsync(claim, job.Version, ct)) return DispatchOutcome.Retry;
        // Persist submission intent before the non-idempotent external call. An uncertain
        // intent is recovered by job-name lookup; it is NEVER blindly submitted again.
        var intent = await store.UpdateAsync(job.TenantId, job.JobId, j => j.AttemptId == job.AttemptId && !j.Terminal && j.LeaseOwner == owner && j.SubmissionStartedAt == null
            ? j with { SubmissionStartedAt = DateTimeOffset.UtcNow } : null, ct);
        if (intent == null) return DispatchOutcome.Durable;
        using var scope = log.BeginScope(new Dictionary<string, object?> { ["tenantId"] = job.TenantId, ["jobId"] = job.JobId, ["attemptId"] = job.AttemptId, ["correlationId"] = job.JobId });
        try {
            var result = await compute.SubmitAsync(new(intent), ct);
            var bound = await store.UpdateAsync(job.TenantId, job.JobId, j => j.AttemptId == job.AttemptId && (j.ProviderJobId == null || j.ProviderJobId == result.ProviderJobId)
                ? j with { ProviderJobId = result.ProviderJobId, Status = j.Status == JobStatus.DISPATCHING ? JobStatus.SUBMITTED : j.Status } : null, ct);
            log.LogInformation("Batch submission durably recorded {providerJobId}", result.ProviderJobId);
            if (bound == null || bound.Status == JobStatus.CANCELLED) await compute.CancelAsync(result.ProviderJobId, ct);
            return DispatchOutcome.Durable;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            log.LogWarning(ex, "Submission outcome uncertain; reconciliation will search by deterministic name");
            return DispatchOutcome.Retry;
        }
    }
}

public sealed class RecoveryService(IJobStore store, IObjectStore objects, IComputeBackend compute, PlatformOptions options, ILogger<RecoveryService> log)
{
    public async Task ReconcileAsync(string tenant, string id, CancellationToken ct)
    {
        var job = await store.GetAsync(tenant, id, ct);
        if (job == null || job.Terminal && job.Status != JobStatus.CANCELLED) return;
        using var scope = log.BeginScope(new Dictionary<string, object?> { ["tenantId"] = tenant, ["jobId"] = id, ["attemptId"] = job.AttemptId, ["providerJobId"] = job.ProviderJobId, ["correlationId"] = id });
        if (job.Status == JobStatus.UPLOADING) {
            if (job.CreatedAt < DateTimeOffset.UtcNow.AddHours(-24)) {
                await objects.AbortMultipartAsync(job, ct);
                await FailOrRetryAsync(job, "UPLOAD_EXPIRED", "Upload expired after 24 hours", false, ct);
            } else await TouchAsync(job, ct);
            return;
        }
        if (job.Status == JobStatus.READY) { await TouchAsync(job, ct); return; }
        var provider = job.ProviderJobId;
        if (provider == null) {
            if (job.LeaseUntil > DateTimeOffset.UtcNow) return;
            var found = await compute.FindAsync(job, ct);
            if (found.Count > 1) {
                // Bind one execution, cancel extras. Worker execution-id CAS adds a second fence.
                foreach (var duplicate in found.OrderBy(x => x.ProviderJobId, StringComparer.Ordinal).Skip(1)) await compute.CancelAsync(duplicate.ProviderJobId, ct);
            }
            provider = found.OrderBy(x => x.ProviderJobId, StringComparer.Ordinal).FirstOrDefault()?.ProviderJobId;
            if (provider == null) {
                if (job.Status == JobStatus.CANCELLED) {
                    await store.UpdateAsync(tenant, id, j => Same(j, job) ? j with {
                        CancellationConfirmed = j.SubmissionStartedAt == null || j.SubmissionStartedAt < DateTimeOffset.UtcNow.AddMinutes(-10)
                    } : null, ct);
                    return;
                }
                if (job.SubmissionStartedAt == null) {
                    await store.UpdateAsync(tenant, id, j => Same(j, job) && j.Status == JobStatus.DISPATCHING && j.SubmissionStartedAt == null
                        ? j with { Status = JobStatus.READY, LeaseOwner = null, LeaseUntil = null } : null, ct);
                } else if (job.SubmissionStartedAt < DateTimeOffset.UtcNow.AddMinutes(-10)) {
                    await FailOrRetryAsync(job, "SUBMISSION_UNCERTAIN", "No Batch match after uncertainty horizon; old attempt fenced", true, ct);
                } else await TouchAsync(job, ct);
                return;
            }
            var bound = await store.UpdateAsync(tenant, id, j => Same(j, job) && j.ProviderJobId == null ? j with { ProviderJobId = provider } : null, ct);
            if (bound == null) return;
            job = bound;
        }
        if (job.Status == JobStatus.CANCELLED) {
            var state = await compute.GetStatusAsync(provider, ct);
            if (state.State is "FAILED" or "SUCCEEDED")
                await store.UpdateAsync(tenant, id, j => Same(j, job) ? j with { CancellationConfirmed = true } : null, ct);
            else { await compute.CancelAsync(provider, ct); await TouchAsync(job, ct); }
            return;
        }
        var actual = await compute.GetStatusAsync(provider, ct);
        if (actual.State == "SUCCEEDED") {
            if (await objects.VerifyAsync(job, ct) == null) {
                await FailOrRetryAsync(job, "OUTPUT_INVALID", "Provider succeeded but matching output/manifest/checksum was not verified", true, ct);
                return;
            }
            await store.UpdateAsync(tenant, id, j => Same(j, job) && !j.Terminal && j.ProviderJobId == provider && j.ExecutionId == job.ExecutionId
                ? j.Transition(JobStatus.SUCCEEDED) : null, ct);
        } else if (actual.State == "FAILED") {
            var reason = actual.Reason ?? "Compute execution failed";
            if (job.ErrorMessage != null) reason += "; " + job.ErrorMessage;
            await FailOrRetryAsync(job, "COMPUTE_FAILED", reason[..Math.Min(reason.Length, 4096)], true, ct);
        }
        else {
            var target = actual.State == "RUNNING" ? JobStatus.RUNNING : JobStatus.QUEUED;
            await store.UpdateAsync(tenant, id, j => Same(j, job) && !j.Terminal
                ? Job.CanTransition(j.Status, target) ? j.Transition(target) : j : null, ct);
        }
    }
    static bool Same(Job a, Job b) => a.AttemptId == b.AttemptId;
    Task<Job?> TouchAsync(Job job, CancellationToken ct) => store.UpdateAsync(job.TenantId, job.JobId, j => Same(j, job) ? j : null, ct);
    async Task FailOrRetryAsync(Job job, string code, string reason, bool retry, CancellationToken ct) {
        log.LogWarning("Attempt failed {errorCode}: {errorMessage}", code, reason);
        // A retry creates a NEW fenced attempt while the logical job is nonterminal.
        // Terminal logical jobs are never reopened by provider events.
        await store.UpdateAsync(job.TenantId, job.JobId, j => !Same(j, job) || j.Terminal ? null :
            retry && j.AttemptNumber < options.MaxAttempts ? j with {
                AttemptId = Guid.NewGuid().ToString("N"), AttemptNumber = j.AttemptNumber + 1, Status = JobStatus.READY,
                ProviderJobId = null, ExecutionId = null, LeaseOwner = null, LeaseUntil = null, SubmissionStartedAt = null,
                NotBefore = DateTimeOffset.UtcNow.AddSeconds(Math.Min(120, 10 * Math.Pow(2, j.AttemptNumber - 1))),
                ProgressPercent = null, Stage = "retrying", Message = reason, ErrorCode = null, ErrorMessage = null, StartedAt = null
            } : j.Transition(JobStatus.FAILED) with { ErrorCode = code, ErrorMessage = reason }, ct);
    }
}
