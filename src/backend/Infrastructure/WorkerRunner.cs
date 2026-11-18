using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using StepGlb.Application;
using StepGlb.Domain;

namespace StepGlb.Infrastructure;

public sealed class WorkerRunner(IJobStore store, IObjectStore objects, PlatformOptions options, ILogger<WorkerRunner> log)
{
    public async Task<int> RunAsync(string tenant, string id, string attempt, string provider, CancellationToken ct)
    {
        var execution = Guid.NewGuid().ToString("N");
        using var scope = log.BeginScope(new Dictionary<string, object?> { ["tenantId"] = tenant, ["jobId"] = id,
            ["attemptId"] = attempt, ["providerJobId"] = provider, ["correlationId"] = id });
        var job = await store.UpdateAsync(tenant, id, j => j.AttemptId == attempt && !j.Terminal && j.SubmissionStartedAt != null &&
            j.ExecutionId == null && (j.ProviderJobId == null || j.ProviderJobId == provider)
            ? j.Transition(JobStatus.RUNNING) with { ExecutionId = execution, ProviderJobId = provider, StartedAt = DateTimeOffset.UtcNow, Stage = "downloading" } : null, ct);
        if (job == null) { log.LogWarning("Stale or duplicate worker rejected before input download"); return 9; }
        var directory = Path.Combine(Path.GetTempPath(), "step-glb", execution);
        Directory.CreateDirectory(directory);
        try {
            var input = Path.Combine(directory, "input.step");
            var output = Path.Combine(directory, "model.glb");
            await objects.DownloadInputAsync(job, input, ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stop.CancelAfter(TimeSpan.FromMinutes(30));
            var info = new ProcessStartInfo(options.ConverterPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "--input", input, "--output", output }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Native converter did not start");
            using var registration = stop.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
            var stderr = DrainErrorAsync(process.StandardError, stop.Token);
            var monitor = MonitorAsync(job, execution, process, stop);
            string? stage = null;
            double? previous = null;
            var publishedAt = DateTimeOffset.MinValue;
            try {
                while (await process.StandardOutput.ReadLineAsync(stop.Token) is { } line) {
                    if (line.Length > 8192) throw new ValidationException("Oversized native progress line");
                    var progress = ProgressEvent.Parse(line);
                    var changedStage = progress.Stage != stage;
                    if (changedStage || DateTimeOffset.UtcNow - publishedAt >= TimeSpan.FromMilliseconds(options.ProgressIntervalMs) &&
                        progress.Percent is { } percent && (previous == null || percent - previous >= options.ProgressDelta)) {
                        var updated = await store.UpdateAsync(tenant, id, j => Current(j, job, execution) ? j with {
                            Stage = progress.Stage, Message = progress.Message,
                            ProgressPercent = progress.Percent is { } p ? Math.Max(j.ProgressPercent ?? 0, p) : j.ProgressPercent
                        } : null, stop.Token);
                        if (updated == null) throw new OperationCanceledException("Worker fence lost");
                        stage = progress.Stage; previous = updated.ProgressPercent; publishedAt = DateTimeOffset.UtcNow;
                    }
                }
                await process.WaitForExitAsync(stop.Token);
                var error = await stderr;
                if (process.ExitCode != 0) throw new ValidationException($"Native converter exit {process.ExitCode}: {error}");
                await EnsureCurrentAsync(job, execution, ct);
                await ValidateGlbAsync(output, ct);
                await store.UpdateAsync(tenant, id, j => Current(j, job, execution) ? j with { Stage = "uploading-output", Message = "Persisting GLB and manifest" } : null, ct);
                var manifest = new Manifest(tenant, id, attempt, provider, execution, job.InputETag!, job.OutputKey,
                    new FileInfo(output).Length, await OutputValidation.HashAsync(output, ct), DateTimeOffset.UtcNow);
                await EnsureCurrentAsync(job, execution, ct);
                await objects.PublishAsync(job, output, manifest, ct);
                await EnsureCurrentAsync(job, execution, ct);
                if (await objects.VerifyAsync(job, ct) == null) throw new ValidationException("Uploaded output could not be verified");
                await store.UpdateAsync(tenant, id, j => Current(j, job, execution) ? j with { Stage = "confirming", Message = "Output durable; awaiting compute success confirmation" } : null, ct);
                log.LogInformation("Output and manifest durable; worker exiting successfully");
                return 0;
            } finally {
                stop.Cancel();
                try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                await monitor;
            }
        } catch (Exception ex) when (ex is not OutOfMemoryException) {
            log.LogError(ex, "Worker failed");
            // Do not mark the logical job terminal: provider events/reconciliation own that decision.
            await store.UpdateAsync(tenant, id, j => Current(j, job, execution) ? j with { ErrorCode = "WORKER_FAILED", ErrorMessage = ex.Message[..Math.Min(ex.Message.Length, 2048)] } : null, CancellationToken.None);
            return ct.IsCancellationRequested ? 130 : 1;
        } finally { Directory.Delete(directory, true); }
    }
    static bool Current(Job current, Job expected, string execution) => current.AttemptId == expected.AttemptId && current.ExecutionId == execution && !current.Terminal;
    async Task EnsureCurrentAsync(Job job, string execution, CancellationToken ct) {
        var current = await store.GetAsync(job.TenantId, job.JobId, ct);
        if (current == null || !Current(current, job, execution)) throw new OperationCanceledException("Worker fence lost");
    }
    async Task MonitorAsync(Job job, string execution, Process process, CancellationTokenSource stop) {
        var heartbeat = DateTimeOffset.UtcNow;
        try {
            while (!stop.IsCancellationRequested) {
                await Task.Delay(1000, stop.Token);
                await EnsureCurrentAsync(job, execution, stop.Token);
                if (DateTimeOffset.UtcNow - heartbeat > TimeSpan.FromSeconds(30)) {
                    await store.UpdateAsync(job.TenantId, job.JobId, j => Current(j, job, execution) ? j : null, stop.Token);
                    heartbeat = DateTimeOffset.UtcNow;
                }
            }
        } catch (OperationCanceledException) { stop.Cancel(); }
        catch (Exception ex) { log.LogError(ex, "Worker monitor failed; stop execution to preserve fencing"); stop.Cancel(); }
    }
    static async Task<string> DrainErrorAsync(StreamReader reader, CancellationToken ct) {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0) if (result.Length < 65536) result.Append(buffer, 0, Math.Min(read, 65536 - result.Length));
        return result.ToString();
    }
    public static async Task ValidateGlbAsync(string path, CancellationToken ct) {
        await using var stream = File.OpenRead(path);
        var header = new byte[12];
        await stream.ReadExactlyAsync(header, ct);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x46546c67 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 2 ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != stream.Length || stream.Length <= 28)
            throw new ValidationException("Invalid binary glTF output");
    }
}
