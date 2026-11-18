using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StepGlb.Application;
using StepGlb.Domain;
using StepGlb.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.AddPlatform(builder.Configuration);
builder.Services.AddHostedService<QueueScheduler>();
await builder.Build().RunAsync();

public sealed class QueueScheduler(IAmazonSQS sqs, DispatchService dispatch, PlatformOptions options, ILogger<QueueScheduler> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = options.QueueUrl, MaxNumberOfMessages = 1,
                    WaitTimeSeconds = 20, VisibilityTimeout = 180, MessageSystemAttributeNames = ["ApproximateReceiveCount"] }, ct);
                foreach (var message in response.Messages ?? []) {
                    try {
                        var request = JsonSerializer.Deserialize<DispatchRequest>(message.Body, JsonDefaults.Options) ?? throw new ValidationException("Empty dispatch request");
                        StoreKeys.ValidateIdentity(request.TenantId); StoreKeys.ValidateIdentity(request.JobId); StoreKeys.ValidateIdentity(request.AttemptId);
                        var outcome = await dispatch.DispatchAsync(request, ct);
                        if (outcome == DispatchOutcome.Durable)
                            await sqs.DeleteMessageAsync(options.QueueUrl, message.ReceiptHandle, ct);
                        else {
                            var receives = int.TryParse(message.Attributes?.GetValueOrDefault("ApproximateReceiveCount"), out var n) ? n : 1;
                            await sqs.ChangeMessageVisibilityAsync(options.QueueUrl, message.ReceiptHandle, Math.Min(240, 15 * (1 << Math.Min(receives, 4))), ct);
                        }
                    } catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Dispatch failed; message remains for retry/DLQ {messageId}", message.MessageId); }
                }
            } catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "SQS polling failed"); await Task.Delay(TimeSpan.FromSeconds(5), ct); }
        }
    }
}
