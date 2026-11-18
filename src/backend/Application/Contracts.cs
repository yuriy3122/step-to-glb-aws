using StepGlb.Domain;

namespace StepGlb.Application;

public sealed record JobPage(IReadOnlyList<Job> Jobs, string? NextToken);
public interface IJobStore
{
    Task<Job?> GetAsync(string tenant, string job, CancellationToken ct = default);
    Task<bool> SaveAsync(Job job, long? expectedVersion, CancellationToken ct = default);
    Task<JobPage> ListAsync(string tenant, int limit, string? token, CancellationToken ct = default);
    Task<IReadOnlyList<Job>> StaleAsync(int shard, DateTimeOffset before, int limit, CancellationToken ct = default);
}
public sealed record ObjectInfo(long Bytes, string ETag, string? VersionId = null);
public sealed record UploadPart(int PartNumber, string ETag);
public interface IObjectStore
{
    Task<string> UploadUrlAsync(Job job, CancellationToken ct);
    Task<string> DownloadUrlAsync(Job job, CancellationToken ct);
    Task<ObjectInfo?> HeadInputAsync(Job job, CancellationToken ct);
    Task DownloadInputAsync(Job job, string path, CancellationToken ct);
    Task PublishAsync(Job job, string glb, Manifest manifest, CancellationToken ct);
    Task<Manifest?> VerifyAsync(Job job, CancellationToken ct);
    Task<string> StartMultipartAsync(Job job, CancellationToken ct);
    Task<string> PartUrlAsync(Job job, int part, CancellationToken ct);
    Task CompleteMultipartAsync(Job job, IReadOnlyList<UploadPart> parts, CancellationToken ct);
    Task AbortMultipartAsync(Job job, CancellationToken ct);
}
public sealed record ComputeJobRequest(Job Job);
public sealed record ComputeSubmissionResult(string ProviderJobId);
public sealed record ComputeJobStatus(string ProviderJobId, string State, string? Reason = null);
public interface IComputeBackend
{
    Task<ComputeSubmissionResult> SubmitAsync(ComputeJobRequest request, CancellationToken cancellationToken);
    Task<ComputeJobStatus> GetStatusAsync(string providerJobId, CancellationToken cancellationToken);
    Task CancelAsync(string providerJobId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ComputeJobStatus>> FindAsync(Job job, CancellationToken cancellationToken);
}
public sealed record DispatchRequest(string TenantId, string JobId, string AttemptId);
public sealed class PlatformOptions
{
    public string Mode { get; set; } = "Aws";
    public string Table { get; set; } = "SimulationJobs";
    public string InputBucket { get; set; } = "";
    public string OutputBucket { get; set; } = "";
    public string QueueUrl { get; set; } = "";
    public string BatchQueue { get; set; } = "";
    public string BatchDefinition { get; set; } = "";
    public string LocalRoot { get; set; } = Path.GetFullPath(".local/data");
    public string LocalApiUrl { get; set; } = "http://127.0.0.1:5080";
    public string ConverterPath { get; set; } = Path.GetFullPath("build/native/step-to-glb");
    public string WorkerPath { get; set; } = Path.GetFullPath("src/backend/WorkerHost/bin/Debug/net10.0/WorkerHost.dll");
    public long MaxInputBytes { get; set; } = 512L * 1024 * 1024;
    public int UrlMinutes { get; set; } = 10;
    public int LeaseSeconds { get; set; } = 120;
    public int ProgressIntervalMs { get; set; } = 1000;
    public double ProgressDelta { get; set; } = 1;
    public int MaxAttempts { get; set; } = 3;
}
