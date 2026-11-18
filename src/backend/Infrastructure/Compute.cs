using System.Diagnostics;
using System.Text.Json;
using Amazon.Batch;
using Amazon.Batch.Model;
using StepGlb.Application;
using StepGlb.Domain;

namespace StepGlb.Infrastructure;

public sealed class AwsBatchComputeBackend(IAmazonBatch batch, PlatformOptions options) : IComputeBackend
{
    public async Task<ComputeSubmissionResult> SubmitAsync(ComputeJobRequest request, CancellationToken cancellationToken) {
        var j = request.Job;
        var response = await batch.SubmitJobAsync(new SubmitJobRequest {
            JobName = j.ProviderName, JobQueue = options.BatchQueue, JobDefinition = options.BatchDefinition,
            Parameters = new() { ["tenantId"] = j.TenantId, ["jobId"] = j.JobId, ["attemptId"] = j.AttemptId },
            ContainerOverrides = new ContainerOverrides { Environment = [
                new() { Name = "TENANT_ID", Value = j.TenantId }, new() { Name = "JOB_ID", Value = j.JobId }, new() { Name = "ATTEMPT_ID", Value = j.AttemptId }
            ] }
        }, cancellationToken);
        return new(response.JobId);
    }
    public async Task<ComputeJobStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) {
        var r = await batch.DescribeJobsAsync(new DescribeJobsRequest { Jobs = [providerJobId] }, cancellationToken);
        var job = r.Jobs.FirstOrDefault() ?? throw new NotFoundException();
        return new(job.JobId, job.Status.Value, job.StatusReason);
    }
    public async Task CancelAsync(string providerJobId, CancellationToken cancellationToken) {
        // TerminateJob handles both jobs waiting in a queue and running jobs.
        await batch.TerminateJobAsync(new TerminateJobRequest { JobId = providerJobId, Reason = "Application cancellation or stale execution" }, cancellationToken);
    }
    public async Task<IReadOnlyList<ComputeJobStatus>> FindAsync(Job job, CancellationToken cancellationToken) {
        var matches = new List<ComputeJobStatus>();
        string? next = null;
        for (var page = 0; page < 10; page++) {
            var r = await batch.ListJobsAsync(new ListJobsRequest { JobQueue = options.BatchQueue, MaxResults = 100, NextToken = next,
                Filters = [new KeyValuesPair { Name = "JOB_NAME", Values = [job.ProviderName] }] }, cancellationToken);
            matches.AddRange(r.JobSummaryList.Where(j => j.JobName == job.ProviderName).Select(j => new ComputeJobStatus(j.JobId, j.Status.Value, j.StatusReason)));
            next = r.NextToken;
            if (next == null) return matches;
        }
        throw new ConflictException("Batch name lookup exceeded bounded pagination; operator investigation required");
    }
}

public sealed class LocalComputeBackend(PlatformOptions options) : IComputeBackend
{
    public string RecordPath(string id) { StoreKeys.ValidateIdentity(id); return Path.Combine(options.LocalRoot, "providers", id + ".json"); }
    public async Task<ComputeSubmissionResult> SubmitAsync(ComputeJobRequest request, CancellationToken cancellationToken) {
        var j = request.Job;
        var id = j.ProviderName;
        Directory.CreateDirectory(Path.GetDirectoryName(RecordPath(id))!);
        // CreateNew prevents duplicate local process launches for the same attempt.
        await using (var stream = new FileStream(RecordPath(id), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await JsonSerializer.SerializeAsync(stream, new ComputeJobStatus(id, "STARTING"), JsonDefaults.Options, cancellationToken);
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        info.ArgumentList.Add(options.WorkerPath);
        foreach (var pair in new Dictionary<string, string> { ["TENANT_ID"] = j.TenantId, ["JOB_ID"] = j.JobId, ["ATTEMPT_ID"] = j.AttemptId,
            ["LOCAL_PROVIDER_ID"] = id, ["Platform__Mode"] = "Local", ["Platform__LocalRoot"] = options.LocalRoot,
            ["Platform__ConverterPath"] = options.ConverterPath }) info.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Worker could not be launched");
        return new(id);
    }
    public async Task<ComputeJobStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken) {
        var path = RecordPath(providerJobId);
        var status = JsonSerializer.Deserialize<ComputeJobStatus>(await File.ReadAllTextAsync(path, cancellationToken), JsonDefaults.Options)!;
        return status.State == "STARTING" && File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddMinutes(-35)
            ? new(providerJobId, "FAILED", "Local provider exceeded 35-minute timeout or worker never started") : status;
    }
    public Task CancelAsync(string providerJobId, CancellationToken cancellationToken) => Task.CompletedTask; // Worker observes durable cancellation and kills the native process.
    public async Task<IReadOnlyList<ComputeJobStatus>> FindAsync(Job job, CancellationToken cancellationToken) =>
        File.Exists(RecordPath(job.ProviderName)) ? [await GetStatusAsync(job.ProviderName, cancellationToken)] : [];
}
