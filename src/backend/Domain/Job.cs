using System.Text.Json.Serialization;

namespace StepGlb.Domain;

[JsonConverter(typeof(JsonStringEnumConverter<JobStatus>))]
public enum JobStatus { UPLOADING, READY, DISPATCHING, SUBMITTED, QUEUED, RUNNING, SUCCEEDED, FAILED, CANCELLED }

public sealed record Job
{
    public required string TenantId { get; init; }
    public required string JobId { get; init; }
    public required string AttemptId { get; init; }
    public required string FileName { get; init; }
    public required long InputBytes { get; init; }
    public JobStatus Status { get; init; } = JobStatus.UPLOADING;
    public long Version { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? ProviderJobId { get; init; }
    public string? ExecutionId { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
    public DateTimeOffset? SubmissionStartedAt { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public bool CancellationConfirmed { get; init; }
    public string? MultipartUploadId { get; init; }
    public string? InputETag { get; init; }
    public string? InputVersionId { get; init; }
    public string Stage { get; init; } = "uploading";
    public double? ProgressPercent { get; init; }
    public string? Message { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int AttemptNumber { get; init; } = 1;
    public bool Terminal => Status is JobStatus.SUCCEEDED or JobStatus.FAILED or JobStatus.CANCELLED;
    public string InputKey => $"inputs/{TenantId}/{JobId}/input.step";
    public string OutputKey => $"results/{TenantId}/{JobId}/{AttemptId}/model.glb";
    public string ManifestKey => $"results/{TenantId}/{JobId}/{AttemptId}/manifest.json";
    public string ProviderName => $"cad-{JobId}-{AttemptId}";

    public Job Transition(JobStatus next, string? message = null)
    {
        if (!CanTransition(Status, next)) throw new ConflictException($"Invalid transition {Status} -> {next}");
        return this with {
            Status = next, Message = message ?? (next == JobStatus.SUCCEEDED ? "Compute success confirmed; GLB and manifest verified" : Message),
            CompletedAt = next is JobStatus.SUCCEEDED or JobStatus.FAILED or JobStatus.CANCELLED ? DateTimeOffset.UtcNow : null,
            ProgressPercent = next == JobStatus.SUCCEEDED ? 100 : ProgressPercent,
            Stage = next == JobStatus.SUCCEEDED ? "completed" : Stage
        };
    }
    public static bool CanTransition(JobStatus from, JobStatus to) => from switch {
        JobStatus.UPLOADING => to is JobStatus.READY or JobStatus.FAILED or JobStatus.CANCELLED,
        JobStatus.READY => to is JobStatus.DISPATCHING or JobStatus.CANCELLED or JobStatus.FAILED,
        JobStatus.DISPATCHING => to is JobStatus.SUBMITTED or JobStatus.QUEUED or JobStatus.RUNNING or JobStatus.SUCCEEDED or JobStatus.FAILED or JobStatus.CANCELLED,
        JobStatus.SUBMITTED => to is JobStatus.QUEUED or JobStatus.RUNNING or JobStatus.SUCCEEDED or JobStatus.FAILED or JobStatus.CANCELLED,
        JobStatus.QUEUED => to is JobStatus.RUNNING or JobStatus.SUCCEEDED or JobStatus.FAILED or JobStatus.CANCELLED,
        JobStatus.RUNNING => to is JobStatus.SUCCEEDED or JobStatus.FAILED or JobStatus.CANCELLED,
        _ => false
    };
}
public sealed class ConflictException(string message) : Exception(message);
public sealed class NotFoundException() : Exception("Job not found");
public sealed class ValidationException(string message) : Exception(message);

public sealed record ProgressEvent(string Stage, double? Percent, string Message)
{
    public static ProgressEvent Parse(string json)
    {
        var value = System.Text.Json.JsonSerializer.Deserialize<ProgressEvent>(json, JsonDefaults.Options)
            ?? throw new ValidationException("Empty converter event");
        if (value.Stage is not ("reading" or "importing" or "meshing" or "exporting" or "validating" or "converted") ||
            value.Percent is { } n && (!double.IsFinite(n) || n < 0 || n >= 100) || value.Message.Length > 1024)
            throw new ValidationException("Invalid converter progress event");
        return value;
    }
}
public sealed record Manifest(string TenantId, string JobId, string AttemptId, string ProviderJobId,
    string ExecutionId, string InputETag, string OutputKey, long Bytes, string Sha256, DateTimeOffset CreatedAt);
public static class JsonDefaults { public static readonly System.Text.Json.JsonSerializerOptions Options = new(System.Text.Json.JsonSerializerDefaults.Web); }
