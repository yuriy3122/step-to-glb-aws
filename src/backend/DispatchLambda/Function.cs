using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using Amazon.SQS;
using Amazon.SQS.Model;
using StepGlb.Application;
using StepGlb.Domain;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]
namespace StepGlb.DispatchLambda;
public sealed record ItemFailure([property: JsonPropertyName("itemIdentifier")] string ItemIdentifier);
public sealed record BatchResponse([property: JsonPropertyName("batchItemFailures")] IReadOnlyList<ItemFailure> BatchItemFailures);
public sealed class Function
{
    readonly IAmazonSQS sqs;
    readonly string queue;
    public Function() : this(new AmazonSQSClient(), Environment.GetEnvironmentVariable("Platform__QueueUrl") ?? throw new InvalidOperationException("Queue URL missing")) { }
    public Function(IAmazonSQS sqs, string queue) { this.sqs = sqs; this.queue = queue; }
    public async Task<BatchResponse> Handler(DynamoDBEvent input, ILambdaContext context) {
        var failures = new List<ItemFailure>();
        foreach (var record in input.Records) {
            try {
                var image = record.Dynamodb.NewImage;
                if (image == null || !image.TryGetValue("SK", out var sk) || !sk.S.StartsWith("JOB#", StringComparison.Ordinal) ||
                    !image.TryGetValue("status", out var status) || status.S != "READY") continue;
                var old = record.Dynamodb.OldImage;
                if (old != null && old.TryGetValue("status", out var oldStatus) && oldStatus.S == "READY" &&
                    old.TryGetValue("attemptId", out var oldAttempt) && oldAttempt.S == image["attemptId"].S) continue;
                var request = new DispatchRequest(image["tenantId"].S, image["jobId"].S, image["attemptId"].S);
                await sqs.SendMessageAsync(new SendMessageRequest { QueueUrl = queue, MessageBody = JsonSerializer.Serialize(request, JsonDefaults.Options) });
            } catch (Exception ex) {
                context.Logger.LogError($"Dispatch stream failure record={record.EventID} type={ex.GetType().Name}");
                failures.Add(new(record.Dynamodb.SequenceNumber));
            }
        }
        return new(failures);
    }
}
