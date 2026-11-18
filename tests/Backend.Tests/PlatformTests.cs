using System.Net;
using System.Text.Json;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Logging.Abstractions;
using StepGlb.Application;
using StepGlb.Domain;
using StepGlb.Infrastructure;
using Xunit;

namespace StepGlb.Tests;
public sealed class PlatformTests : IDisposable
{
    readonly PlatformOptions options = new() { Mode = "Local", LocalRoot = Path.Combine(Path.GetTempPath(), "cad-tests-" + Guid.NewGuid().ToString("N")) };
    readonly LocalJobStore store;
    readonly LocalObjectStore objects;
    readonly TestCompute compute = new();
    readonly JobService service;
    static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    public PlatformTests() {
        store = new(options); objects = new(options); service = new(store, objects, compute, options);
        options.ConverterPath = Path.Combine(Root, "build/native/step-to-glb");
    }
    public void Dispose() { if (Directory.Exists(options.LocalRoot)) Directory.Delete(options.LocalRoot, true); }
    Job NewJob(string tenant="tenant-a") => new() {TenantId=tenant, JobId=Guid.NewGuid().ToString("N"), AttemptId=Guid.NewGuid().ToString("N"), FileName="box.step", InputBytes=10};
    async Task<Job> Ready() {
        var source=Path.Combine(Root,"build/native/fixtures/box.step");
        var (job,_)=await service.CreateAsync("tenant-a","box.step",new FileInfo(source).Length,false,default);
        var dest=objects.PathFor(job.InputKey);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);File.Copy(source,dest);
        await service.CompleteUploadAsync(job.TenantId,job.JobId,default);
        return (await store.GetAsync(job.TenantId,job.JobId))!;
    }
    [Fact] public async Task CreateAndCompleteRequireVerifiedInput() {
        var (job,url)=await service.CreateAsync("tenant-a","part.step",10,false,default);
        Assert.Equal(JobStatus.UPLOADING,job.Status);Assert.Contains(job.JobId,url);
        await Assert.ThrowsAsync<ValidationException>(()=>service.CompleteUploadAsync("tenant-a",job.JobId,default));
        var dest=objects.PathFor(job.InputKey);Directory.CreateDirectory(Path.GetDirectoryName(dest)!);await File.WriteAllTextAsync(dest,"ISO-10303-21; HEADER;");
        await Assert.ThrowsAsync<ValidationException>(()=>service.CompleteUploadAsync("tenant-a",job.JobId,default));
    }
    [Fact] public async Task UploadCompletionIsIdempotentAfterAcceptedInput() {
        var j=await Ready();var again=await service.CompleteUploadAsync(j.TenantId,j.JobId,default);
        Assert.Equal(j.AttemptId,again.AttemptId);Assert.Equal(j.Version,again.Version);Assert.Equal(JobStatus.READY,again.Status);
    }
    [Fact] public async Task LocalProviderWithoutAWorkerEventuallyFails() {
        var backend=new LocalComputeBackend(options);var path=backend.RecordPath("local-abandoned");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new ComputeJobStatus("local-abandoned","STARTING"),JsonDefaults.Options));
        File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddMinutes(-36));
        Assert.Equal("FAILED",(await backend.GetStatusAsync("local-abandoned",default)).State);
    }
    [Theory][InlineData("../../a.step",10)][InlineData("a.exe",10)][InlineData("a.step",0)]
    public async Task InvalidUploadRejected(string name,long bytes) => await Assert.ThrowsAsync<ValidationException>(()=>service.CreateAsync("tenant-a",name,bytes,false,default));
    [Fact] public async Task TenantIsolationIncludesListingResultsAndCancellation() {
        var (job,_)=await service.CreateAsync("tenant-a","part.step",10,false,default);
        Assert.Null(await store.GetAsync("tenant-b",job.JobId));
        Assert.Empty((await store.ListAsync("tenant-b",20,null)).Jobs);
        await Assert.ThrowsAsync<NotFoundException>(()=>service.ResultAsync("tenant-b",job.JobId,default));
        await Assert.ThrowsAsync<NotFoundException>(()=>service.CancelAsync("tenant-b",job.JobId,default));
    }
    [Fact] public async Task ConditionalWritesHaveOneWinner() {
        var j=NewJob();Assert.True(await store.SaveAsync(j,null));Assert.False(await store.SaveAsync(j,null));
        var results=await Task.WhenAll(Enumerable.Range(0,12).Select(_=>store.SaveAsync(j with{Stage="x"},0)));
        Assert.Single(results, x=>x);Assert.Equal(1,(await store.GetAsync(j.TenantId,j.JobId))!.Version);
    }
    [Fact] public async Task PaginationIsBoundedAndTenantScoped() {
        for(int i=0;i<5;i++)await store.SaveAsync(NewJob(),null);
        var first=await store.ListAsync("tenant-a",2,null);var second=await store.ListAsync("tenant-a",2,first.NextToken);
        Assert.Equal(2,first.Jobs.Count);Assert.Empty(first.Jobs.Select(j=>j.JobId).Intersect(second.Jobs.Select(j=>j.JobId)));
        Assert.Empty((await store.ListAsync("tenant-b",2,first.NextToken)).Jobs);
        await Assert.ThrowsAsync<ValidationException>(()=>store.ListAsync("tenant-a",2,"bad token"));
    }
    [Fact] public void TerminalStatesCannotBeReopened() {
        foreach(var terminal in new[]{JobStatus.SUCCEEDED,JobStatus.FAILED,JobStatus.CANCELLED})
            foreach(var target in Enum.GetValues<JobStatus>())Assert.False(Job.CanTransition(terminal,target));
        Assert.Throws<ConflictException>(()=>NewJob().Transition(JobStatus.RUNNING));
    }
    [Fact] public async Task DuplicateDispatchSubmitsOnce() {
        var j=await Ready();var dispatcher=new DispatchService(store,compute,options,NullLogger<DispatchService>.Instance);
        var request=new DispatchRequest(j.TenantId,j.JobId,j.AttemptId);
        Assert.Equal(DispatchOutcome.Durable,await dispatcher.DispatchAsync(request,default));
        Assert.Equal(DispatchOutcome.Durable,await dispatcher.DispatchAsync(request,default));Assert.Equal(1,compute.Submissions);
    }
    [Fact] public async Task CrashAfterSubmitIsRecoveredWithoutResubmitting() {
        var j=await Ready();compute.ThrowAfterAccept=true;
        var dispatcher=new DispatchService(store,compute,options,NullLogger<DispatchService>.Instance);
        var request=new DispatchRequest(j.TenantId,j.JobId,j.AttemptId);
        Assert.Equal(DispatchOutcome.Retry,await dispatcher.DispatchAsync(request,default));
        Assert.Equal(DispatchOutcome.Retry,await dispatcher.DispatchAsync(request,default));Assert.Equal(1,compute.Submissions);
        await store.UpdateAsync(j.TenantId,j.JobId,x=>x with{LeaseUntil=DateTimeOffset.UtcNow.AddSeconds(-1)},default);
        await new RecoveryService(store,objects,compute,options,NullLogger<RecoveryService>.Instance).ReconcileAsync(j.TenantId,j.JobId,default);
        var recovered=(await store.GetAsync(j.TenantId,j.JobId))!;Assert.Equal("provider-1",recovered.ProviderJobId);Assert.Equal(1,compute.Submissions);
    }
    [Fact] public async Task ExpiredLeaseBeforeSubmissionCanBeReleased() {
        var j=await Ready();await store.SaveAsync(j.Transition(JobStatus.DISPATCHING) with{LeaseUntil=DateTimeOffset.UtcNow.AddSeconds(-1)},j.Version);
        await new RecoveryService(store,objects,compute,options,NullLogger<RecoveryService>.Instance).ReconcileAsync(j.TenantId,j.JobId,default);
        Assert.Equal(JobStatus.READY,(await store.GetAsync(j.TenantId,j.JobId))!.Status);
    }
    [Fact] public async Task SuccessRequiresMatchingManifestAndProviderCompletion() {
        var j=await Ready();await store.SaveAsync(j.Transition(JobStatus.DISPATCHING) with{ProviderJobId="provider-1",ExecutionId="execution"},j.Version);
        compute.State="SUCCEEDED";options.MaxAttempts=1;
        await new RecoveryService(store,objects,compute,options,NullLogger<RecoveryService>.Instance).ReconcileAsync(j.TenantId,j.JobId,default);
        var failed=(await store.GetAsync(j.TenantId,j.JobId))!;Assert.Equal(JobStatus.FAILED,failed.Status);Assert.Equal("OUTPUT_INVALID",failed.ErrorCode);
    }
    [Fact] public async Task FailedProviderCreatesNewFencedAttempt() {
        var j=await Ready();await store.SaveAsync(j.Transition(JobStatus.DISPATCHING) with{ProviderJobId="provider-1"},j.Version);compute.State="FAILED";
        await new RecoveryService(store,objects,compute,options,NullLogger<RecoveryService>.Instance).ReconcileAsync(j.TenantId,j.JobId,default);
        var retry=(await store.GetAsync(j.TenantId,j.JobId))!;Assert.Equal(JobStatus.READY,retry.Status);Assert.NotEqual(j.AttemptId,retry.AttemptId);Assert.Equal(2,retry.AttemptNumber);
        var outcome=await new DispatchService(store,compute,options,NullLogger<DispatchService>.Instance).DispatchAsync(new(j.TenantId,j.JobId,j.AttemptId),default);
        Assert.Equal(DispatchOutcome.Durable,outcome);Assert.Equal(0,compute.Submissions);
    }
    [Fact] public async Task CancellationFencesLateEvents() {
        var j=await Ready();await service.CancelAsync(j.TenantId,j.JobId,default);
        Assert.Equal(DispatchOutcome.Durable,await new DispatchService(store,compute,options,NullLogger<DispatchService>.Instance).DispatchAsync(new(j.TenantId,j.JobId,j.AttemptId),default));
        Assert.Equal(JobStatus.CANCELLED,(await store.GetAsync(j.TenantId,j.JobId))!.Status);Assert.Equal(0,compute.Submissions);
    }
    [Fact] public void NativeProgressMustBeValidAndBelowCompletion() {
        Assert.Null(ProgressEvent.Parse("{\"stage\":\"reading\",\"percent\":null,\"message\":\"Reading\"}").Percent);
        Assert.Throws<ValidationException>(()=>ProgressEvent.Parse("{\"stage\":\"meshing\",\"percent\":100,\"message\":\"x\"}"));
        Assert.Throws<ValidationException>(()=>ProgressEvent.Parse("{\"stage\":\"invented\",\"percent\":10,\"message\":\"x\"}"));
    }
    [Fact] public async Task SignedLocalUrlsAreMethodIdentityAndExpiryScoped() {
        var j=NewJob();var url=await objects.UploadUrlAsync(j,default);var query=new Uri(url).Query.TrimStart('?').Split('&').Select(x=>x.Split('=')).ToDictionary(x=>x[0],x=>x[1]);
        var expires=long.Parse(query["expires"]);var signature=query["signature"];
        Assert.True(objects.Authorized("PUT",j.TenantId,j.JobId,expires,signature));Assert.False(objects.Authorized("GET",j.TenantId,j.JobId,expires,signature));
        Assert.False(objects.Authorized("PUT","tenant-b",j.JobId,expires,signature));Assert.False(objects.Authorized("PUT",j.TenantId,j.JobId,1,signature));
    }
    [Fact] public async Task RealNativeWorkerPublishesManifestButWaitsForProviderSuccess() {
        var j=await Ready();await store.SaveAsync(j.Transition(JobStatus.DISPATCHING) with{SubmissionStartedAt=DateTimeOffset.UtcNow,ProviderJobId="provider-1"},j.Version);
        var runner=new WorkerRunner(store,objects,options,NullLogger<WorkerRunner>.Instance);
        Assert.Equal(0,await runner.RunAsync(j.TenantId,j.JobId,j.AttemptId,"provider-1",default));
        var done=(await store.GetAsync(j.TenantId,j.JobId))!;Assert.Equal("confirming",done.Stage);Assert.NotEqual(JobStatus.SUCCEEDED,done.Status);Assert.NotEqual(100,done.ProgressPercent);
        Assert.NotNull(await objects.VerifyAsync(done,default));
        Assert.Equal(9,await runner.RunAsync(j.TenantId,j.JobId,j.AttemptId,"provider-1",default));
        compute.State="SUCCEEDED";await new RecoveryService(store,objects,compute,options,NullLogger<RecoveryService>.Instance).ReconcileAsync(j.TenantId,j.JobId,default);
        Assert.Equal(JobStatus.SUCCEEDED,(await store.GetAsync(j.TenantId,j.JobId))!.Status);
        await File.AppendAllTextAsync(objects.PathFor(j.OutputKey),"tampered");Assert.Null(await objects.VerifyAsync(done,default));
    }
    [Fact] public async Task MissingInputAndNativeFailureDoNotPublishSuccess() {
        var j=await Ready();await store.SaveAsync(j.Transition(JobStatus.DISPATCHING) with{SubmissionStartedAt=DateTimeOffset.UtcNow,ProviderJobId="provider-1"},j.Version);
        options.ConverterPath="/usr/bin/false";
        Assert.Equal(1,await new WorkerRunner(store,objects,options,NullLogger<WorkerRunner>.Instance).RunAsync(j.TenantId,j.JobId,j.AttemptId,"provider-1",default));
        var failed=(await store.GetAsync(j.TenantId,j.JobId))!;Assert.Equal("WORKER_FAILED",failed.ErrorCode);Assert.False(File.Exists(objects.PathFor(j.ManifestKey)));
    }
    [Fact] public async Task MissingAcceptedInputFailsBeforeNativeExecution() {
        var j=await Ready();await store.SaveAsync(j.Transition(JobStatus.DISPATCHING) with{SubmissionStartedAt=DateTimeOffset.UtcNow,ProviderJobId="provider-1"},j.Version);
        File.Delete(objects.PathFor(j.InputKey));
        Assert.Equal(1,await new WorkerRunner(store,objects,options,NullLogger<WorkerRunner>.Instance).RunAsync(j.TenantId,j.JobId,j.AttemptId,"provider-1",default));
        Assert.Null(await objects.VerifyAsync((await store.GetAsync(j.TenantId,j.JobId))!,default));
    }
    [Fact] public async Task OutputUploadFailureCannotPublishCompletion() {
        var j=await Ready();await store.SaveAsync(j.Transition(JobStatus.DISPATCHING) with{SubmissionStartedAt=DateTimeOffset.UtcNow,ProviderJobId="provider-1"},j.Version);
        var runner=new WorkerRunner(store,new UploadFailure(objects),options,NullLogger<WorkerRunner>.Instance);
        Assert.Equal(1,await runner.RunAsync(j.TenantId,j.JobId,j.AttemptId,"provider-1",default));
        var failed=(await store.GetAsync(j.TenantId,j.JobId))!;Assert.NotEqual(JobStatus.SUCCEEDED,failed.Status);Assert.Contains("Simulated upload failure",failed.ErrorMessage);Assert.False(File.Exists(objects.PathFor(j.ManifestKey)));
    }
    [Fact] public async Task DynamoWritesJobAndAttemptAtomicallyWithVersionCondition() {
        using var db=new RecordingDynamo();var dynamo=new DynamoJobStore(db,options);var j=NewJob();
        Assert.True(await dynamo.SaveAsync(j,7));Assert.NotNull(db.Last);Assert.Equal(2,db.Last.TransactItems.Count);
        var put=db.Last.TransactItems[0].Put;Assert.Equal("#v = :v",put.ConditionExpression);Assert.Equal("7",put.ExpressionAttributeValues[":v"].N);
        Assert.Equal("8",put.Item["version"].N);Assert.StartsWith("ATTEMPT#",db.Last.TransactItems[1].Put.Item["SK"].S);
        db.Conflict=true;Assert.False(await dynamo.SaveAsync(j,7));
    }
    [Fact] public async Task S3PresigningBindsMethodObjectAndContentLength() {
        using var s3=new AmazonS3Client(new BasicAWSCredentials(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N")),RegionEndpoint.EUWest1);
        var adapter=new S3ObjectStore(s3,new PlatformOptions{InputBucket="cad-local-signing-test",OutputBucket="cad-local-results-test"});
        var job=NewJob();var url=await adapter.UploadUrlAsync(job,default);
        Assert.Contains(job.InputKey,new Uri(url).AbsolutePath);
        Assert.Contains("content-length",Uri.UnescapeDataString(url));
        Assert.Contains("X-Amz-Expires=600",url);
        var multi=job with{InputBytes=9*1024*1024,MultipartUploadId="upload-id"};
        var part=await adapter.PartUrlAsync(multi,2,default);
        Assert.Contains("content-length",Uri.UnescapeDataString(part));Assert.Contains("partNumber=2",part);
        await Assert.ThrowsAsync<ValidationException>(()=>adapter.PartUrlAsync(multi,3,default));
    }
    sealed class RecordingDynamo() : AmazonDynamoDBClient(new AnonymousAWSCredentials(),RegionEndpoint.USEast1) {
        public TransactWriteItemsRequest? Last;public bool Conflict;
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request,CancellationToken ct=default) {
            Last=request;if(Conflict)throw new TransactionCanceledException("conflict"){CancellationReasons=[new CancellationReason{Code="ConditionalCheckFailed"}]};
            return Task.FromResult(new TransactWriteItemsResponse{HttpStatusCode=HttpStatusCode.OK});
        }
    }
    sealed class UploadFailure(IObjectStore inner):IObjectStore {
        public Task<string> UploadUrlAsync(Job j,CancellationToken ct)=>inner.UploadUrlAsync(j,ct);
        public Task<string> DownloadUrlAsync(Job j,CancellationToken ct)=>inner.DownloadUrlAsync(j,ct);
        public Task<ObjectInfo?> HeadInputAsync(Job j,CancellationToken ct)=>inner.HeadInputAsync(j,ct);
        public Task DownloadInputAsync(Job j,string path,CancellationToken ct)=>inner.DownloadInputAsync(j,path,ct);
        public Task PublishAsync(Job j,string path,Manifest m,CancellationToken ct)=>throw new IOException("Simulated upload failure");
        public Task<Manifest?> VerifyAsync(Job j,CancellationToken ct)=>inner.VerifyAsync(j,ct);
        public Task<string> StartMultipartAsync(Job j,CancellationToken ct)=>inner.StartMultipartAsync(j,ct);
        public Task<string> PartUrlAsync(Job j,int part,CancellationToken ct)=>inner.PartUrlAsync(j,part,ct);
        public Task CompleteMultipartAsync(Job j,IReadOnlyList<UploadPart> parts,CancellationToken ct)=>inner.CompleteMultipartAsync(j,parts,ct);
        public Task AbortMultipartAsync(Job j,CancellationToken ct)=>inner.AbortMultipartAsync(j,ct);
    }
    sealed class TestCompute : IComputeBackend {
        public int Submissions;public bool ThrowAfterAccept;public string State="SUBMITTED";bool accepted;
        public Task<ComputeSubmissionResult> SubmitAsync(ComputeJobRequest request,CancellationToken ct) {Submissions++;accepted=true;if(ThrowAfterAccept)throw new IOException("Crash after accepted submission");return Task.FromResult(new ComputeSubmissionResult("provider-1"));}
        public Task<ComputeJobStatus> GetStatusAsync(string id,CancellationToken ct)=>Task.FromResult(new ComputeJobStatus(id,State));
        public Task CancelAsync(string id,CancellationToken ct)=>Task.CompletedTask;
        public Task<IReadOnlyList<ComputeJobStatus>> FindAsync(Job job,CancellationToken ct)=>Task.FromResult<IReadOnlyList<ComputeJobStatus>>(accepted?[new("provider-1",State)]:[]);
    }
}
