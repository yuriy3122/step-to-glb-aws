using StepGlb.Domain;

namespace StepGlb.Application;

public sealed class JobService(IJobStore store, IObjectStore objects, IComputeBackend compute, PlatformOptions options)
{
    public async Task<Job> GetAsync(string tenant, string id, CancellationToken ct = default) =>
        await store.GetAsync(tenant, id, ct) ?? throw new NotFoundException();
    public async Task<(Job Job, string? UploadUrl)> CreateAsync(string tenant, string name, long bytes, bool multipart, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name != Path.GetFileName(name) || name.Contains('\\') ||
            !(name.EndsWith(".step", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".stp", StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException("Choose a .step or .stp filename without directories");
        if (bytes <= 0 || bytes > options.MaxInputBytes) throw new ValidationException($"Input size must be 1..{options.MaxInputBytes} bytes");
        var job = new Job { TenantId = tenant, JobId = Guid.NewGuid().ToString("N"), AttemptId = Guid.NewGuid().ToString("N"), FileName = name, InputBytes = bytes };
        if (multipart) job = job with { MultipartUploadId = await objects.StartMultipartAsync(job, ct) };
        try {
            if (!await store.SaveAsync(job, null, ct)) throw new ConflictException("Job creation conflict");
        } catch { if (multipart) await objects.AbortMultipartAsync(job, ct); throw; }
        return (job, multipart ? null : await objects.UploadUrlAsync(job, ct));
    }
    public async Task<Job> CompleteUploadAsync(string tenant, string id, CancellationToken ct)
    {
        var job = await GetAsync(tenant, id, ct);
        if (job.Status != JobStatus.UPLOADING) {
            if (job.InputETag != null) return job; // Retried completion after a lost response is idempotent.
            throw new ConflictException("Job is not uploading");
        }
        var info = await objects.HeadInputAsync(job, ct) ?? throw new ValidationException("Input object is missing");
        if (info.Bytes != job.InputBytes || info.Bytes > options.MaxInputBytes) throw new ValidationException("Uploaded object size does not match declared size");
        // Pin the object version (AWS) or ETag (local) so a still-live PUT URL cannot replace the accepted input.
        var next = job.Transition(JobStatus.READY) with { InputETag = info.ETag, InputVersionId = info.VersionId, Stage = "waiting" };
        await SaveAsync(next, job.Version, ct);
        return next;
    }
    public async Task<Job> CancelAsync(string tenant, string id, CancellationToken ct)
    {
        var job = await GetAsync(tenant, id, ct);
        if (job.Terminal) return job;
        var next = job.Transition(JobStatus.CANCELLED, "Cancellation requested");
        await SaveAsync(next, job.Version, ct); // Fence first, even if Batch cancellation fails.
        if (job.MultipartUploadId != null) await objects.AbortMultipartAsync(job, ct);
        if (job.ProviderJobId != null) await compute.CancelAsync(job.ProviderJobId, ct);
        return next;
    }
    public async Task<string> ResultAsync(string tenant, string id, CancellationToken ct)
    {
        var job = await GetAsync(tenant, id, ct);
        if (job.Status != JobStatus.SUCCEEDED) throw new ConflictException("Result is not ready");
        if (await objects.VerifyAsync(job, ct) == null) throw new ConflictException("Result verification failed");
        return await objects.DownloadUrlAsync(job, ct);
    }
    public async Task SaveAsync(Job job, long expectedVersion, CancellationToken ct)
    {
        if (!await store.SaveAsync(job, expectedVersion, ct)) throw new ConflictException("Job changed concurrently; reload before retrying");
    }
}
