using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StepGlb.Application;
using StepGlb.Domain;
using StepGlb.Infrastructure;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace StepGlb.StateRecoveryLambda;
public sealed class Function
{
    readonly ServiceProvider services;
    public Function() {
        var collection = new ServiceCollection();
        collection.AddLogging(o => o.AddJsonConsole(c => c.IncludeScopes = true));
        collection.AddPlatform(Bootstrap.EnvironmentConfiguration());
        services = collection.BuildServiceProvider();
    }
    public async Task Handler(JsonElement input, ILambdaContext context) {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, context.RemainingTime.TotalSeconds - 5)));
        var ct = stop.Token;
        var store = services.GetRequiredService<IJobStore>();
        var recovery = services.GetRequiredService<RecoveryService>();
        if (input.TryGetProperty("detail", out var detail) && detail.TryGetProperty("parameters", out var parameters)) {
            var tenant = parameters.GetProperty("tenantId").GetString()!;
            var id = parameters.GetProperty("jobId").GetString()!;
            var attempt = parameters.GetProperty("attemptId").GetString()!;
            var job = await store.GetAsync(tenant, id, ct);
            if (job?.AttemptId == attempt && (job.ProviderJobId == null || job.ProviderJobId == detail.GetProperty("jobId").GetString()))
                await recovery.ReconcileAsync(tenant, id, ct);
            return;
        }
        if (!input.TryGetProperty("reconcile", out var flag) || flag.ValueKind != JsonValueKind.True) throw new ValidationException("Unknown recovery event");
        var sqs = services.GetRequiredService<IAmazonSQS>();
        var options = services.GetRequiredService<PlatformOptions>();
        // 16 operational shards; oldest-first bounded queries. Every processed record is
        // touched or made terminal so later invocations continue through the backlog.
        for (var shard = 0; shard < 16; shard++) {
            foreach (var job in await store.StaleAsync(shard, DateTimeOffset.UtcNow.AddMinutes(-2), 25, ct)) {
                try {
                    if (job.Status == JobStatus.READY) await sqs.SendMessageAsync(new SendMessageRequest {
                        QueueUrl = options.QueueUrl, MessageBody = JsonSerializer.Serialize(new DispatchRequest(job.TenantId, job.JobId, job.AttemptId), JsonDefaults.Options)
                    }, ct);
                    await recovery.ReconcileAsync(job.TenantId, job.JobId, ct);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    context.Logger.LogError($"Recovery failure tenantId={job.TenantId} jobId={job.JobId} attemptId={job.AttemptId} type={ex.GetType().Name}");
                    // Move a permanently broken record out of the oldest position without hiding the error.
                    await store.UpdateAsync(job.TenantId, job.JobId, j => j.AttemptId == job.AttemptId ? j : null, ct);
                }
            }
        }
    }
}
