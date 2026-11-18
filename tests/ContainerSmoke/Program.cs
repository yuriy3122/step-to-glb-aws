using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using StepGlb.Application;
using StepGlb.Domain;
using StepGlb.Infrastructure;

var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
var directory=Path.Combine(root,".local/container-smoke",Guid.NewGuid().ToString("N"));
var options=new PlatformOptions{Mode="Local",LocalRoot=directory};
var store=new LocalJobStore(options);var objects=new LocalObjectStore(options);var compute=new LocalComputeBackend(options);
var service=new JobService(store,objects,compute,options);
var fixture=Path.Combine(root,"build/native/fixtures/assembly.step");
var (created,_)=await service.CreateAsync("container-tenant","assembly.step",new FileInfo(fixture).Length,false,default);
var input=objects.PathFor(created.InputKey);Directory.CreateDirectory(Path.GetDirectoryName(input)!);File.Copy(fixture,input);
await service.CompleteUploadAsync(created.TenantId,created.JobId,default);
var current=(await store.GetAsync(created.TenantId,created.JobId))!;
if(!await store.SaveAsync(current.Transition(JobStatus.DISPATCHING) with{SubmissionStartedAt=DateTimeOffset.UtcNow,ProviderJobId=created.ProviderName},current.Version))throw new Exception("Claim failed");
Directory.CreateDirectory(Path.Combine(directory,"providers"));
// This disposable fixture directory contains only generated CAD and test metadata.
// Allow the image's non-root UID to access a bind mount created by the host UID.
if(!OperatingSystem.IsWindows()) {
 foreach(var d in Directory.EnumerateDirectories(directory,"*",SearchOption.AllDirectories).Prepend(directory))File.SetUnixFileMode(d,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute);
 foreach(var f in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).Where(f=>!f.EndsWith("url-signing-key",StringComparison.Ordinal)))File.SetUnixFileMode(f,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.OtherRead|UnixFileMode.OtherWrite);
}
var info=new ProcessStartInfo("docker"){UseShellExecute=false};
foreach(var value in new[]{"run","--rm","--mount",$"type=bind,src={directory},dst=/data","-e","Platform__Mode=Local","-e","DOTNET_ENVIRONMENT=Development","-e","Platform__LocalRoot=/data","-e",$"TENANT_ID={created.TenantId}","-e",$"JOB_ID={created.JobId}","-e",$"ATTEMPT_ID={created.AttemptId}","-e",$"LOCAL_PROVIDER_ID={created.ProviderName}",args.FirstOrDefault()??"hpc-worker:test"})info.ArgumentList.Add(value);
using var process=Process.Start(info)??throw new Exception("Docker did not start");
await process.WaitForExitAsync();if(process.ExitCode!=0)throw new Exception($"Worker image exited {process.ExitCode}");
var pending=(await store.GetAsync(created.TenantId,created.JobId))!;
if(pending.Status!=JobStatus.RUNNING||pending.Stage!="confirming"||pending.ProgressPercent==100)throw new Exception("Worker published premature logical success");
if(await objects.VerifyAsync(pending,default)==null)throw new Exception("Linux worker output manifest/checksum did not match");
await new RecoveryService(store,objects,compute,options,NullLogger<RecoveryService>.Instance).ReconcileAsync(created.TenantId,created.JobId,default);
var complete=(await store.GetAsync(created.TenantId,created.JobId))!;
if(complete.Status!=JobStatus.SUCCEEDED||complete.ProgressPercent!=100)throw new Exception("Provider/output confirmation failed");
Directory.CreateDirectory(Path.Combine(root,"artifacts"));File.Copy(objects.PathFor(complete.OutputKey),Path.Combine(root,"artifacts/container-smoke.glb"),true);
Console.WriteLine("PASS: non-root Linux C# host -> real OCCT STEP conversion -> durable GLB/manifest -> checksum verification -> provider confirmation -> SUCCEEDED");
