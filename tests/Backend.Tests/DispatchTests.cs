using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.DynamoDBEvents;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using StepGlb.DispatchLambda;
using Xunit;
namespace StepGlb.Tests;
public sealed class DispatchTests
{
    [Fact] public async Task PartialStreamFailuresUseSequenceNumbersAndSkipReadyTouches() {
        using var sqs=new RecordingSqs();var handler=new Function(sqs,"https://sqs.invalid/dispatch");
        object Image(string id) => new { SK=new{S="JOB#"+id},status=new{S="READY"},tenantId=new{S="tenant-a"},jobId=new{S=id},attemptId=new{S="attempt-a"} };
        var records=new[]{
            new{eventID="event-1",dynamodb=new{SequenceNumber="10",NewImage=Image("ok"),OldImage=(object?)null}},
            new{eventID="event-2",dynamodb=new{SequenceNumber="20",NewImage=Image("failure"),OldImage=(object?)null}},
            new{eventID="event-3",dynamodb=new{SequenceNumber="30",NewImage=Image("touch"),OldImage=(object?)Image("touch")}}
        };
        using var stream=new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {Records=records})));
        var input=new DefaultLambdaJsonSerializer().Deserialize<DynamoDBEvent>(stream);
        var response=await handler.Handler(input,new Context());
        Assert.Single(response.BatchItemFailures);Assert.Equal("20",response.BatchItemFailures[0].ItemIdentifier);Assert.Equal(2,sqs.Sends);
    }
    sealed class RecordingSqs() : AmazonSQSClient(new AnonymousAWSCredentials(),RegionEndpoint.USEast1) {
        public int Sends;
        public override Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request,CancellationToken ct=default) {
            Sends++;if(request.MessageBody.Contains("failure"))throw new IOException("SQS transient failure");
            return Task.FromResult(new SendMessageResponse{MessageId="message-id"});
        }
    }
    sealed class Context : ILambdaContext {
        public string AwsRequestId=>"request";public IClientContext ClientContext=>null!;public string FunctionName=>"dispatch";
        public string FunctionVersion=>"1";public ICognitoIdentity Identity=>null!;public string InvokedFunctionArn=>"arn";
        public ILambdaLogger Logger{get;}=new Logger();public string LogGroupName=>"test";public string LogStreamName=>"test";
        public int MemoryLimitInMB=>256;public TimeSpan RemainingTime=>TimeSpan.FromMinutes(1);
    }
    sealed class Logger : ILambdaLogger {public void Log(string message){}public void LogLine(string message){}}
}
