using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.CloudFormation;
using Amazon.CloudFormation.Model;
using Amazon.ECR;
using Amazon.ECR.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Parameter = Amazon.CloudFormation.Model.Parameter;
using Tag = Amazon.CloudFormation.Model.Tag;
using InvalidOperationException = System.InvalidOperationException;

if(args.Contains("--help")) {
 Console.WriteLine("dotnet run --project tools/Deploy -- --region REGION [--environment cad-demo] [--profile NAME] [--spot] [--max-vcpus 16] [--instance-types c6i.large,c6a.large] [--standard-cpu-credits] [--platform-stack-name NAME] [--table-name NAME]");
 Console.WriteLine("Creates billable infrastructure exclusively through CloudFormation; builds and pushes linux/amd64 images first.");
 return;
}
var parsed=new Dictionary<string,string>(StringComparer.Ordinal);
for(int i=0;i<args.Length;i++) {
 if(args[i] is "--spot" or "--standard-cpu-credits") {parsed[args[i]]="true";continue;}
 if(!args[i].StartsWith("--",StringComparison.Ordinal)||i+1>=args.Length)throw new ArgumentException("Use --region REGION [--environment cad-demo] [--profile NAME] [--spot] [--max-vcpus 16] [--instance-types c6i.large,c6a.large]");
 parsed[args[i]]=args[++i];
}
if(!parsed.TryGetValue("--region",out var regionName))throw new ArgumentException("--region is required; deployment creates billable AWS resources");
if(parsed.TryGetValue("--profile",out var profile))Environment.SetEnvironmentVariable("AWS_PROFILE",profile);
var environment=parsed.GetValueOrDefault("--environment","cad-demo");
if(!System.Text.RegularExpressions.Regex.IsMatch(environment,"^[a-z][a-z0-9-]{2,25}$"))throw new ArgumentException("Invalid environment name");
var root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../"));
if(!File.Exists(Path.Combine(root,"AGENTS.md")))root=Directory.GetCurrentDirectory();
Directory.SetCurrentDirectory(root);
var region=RegionEndpoint.GetBySystemName(regionName);
using var cf=new AmazonCloudFormationClient(region);using var s3=new AmazonS3Client(region);
using var ecr=new AmazonECRClient(region);using var sts=new AmazonSecurityTokenServiceClient(region);
await Run("docker",["info"],quiet:true);await Run("docker",["buildx","version"]);
await Run("dotnet",["restore","StepGlb.sln","--locked-mode","--disable-parallel"]);
var identity=await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest());
Console.WriteLine($"Deploying to account {identity.Account} in {regionName}");
var bootstrap=await Deploy(environment+"-bootstrap","bootstrap.yaml",new(){["EnvironmentName"]=environment});
var authorization=(await ecr.GetAuthorizationTokenAsync(new GetAuthorizationTokenRequest())).AuthorizationData[0];
var login=Encoding.UTF8.GetString(Convert.FromBase64String(authorization.AuthorizationToken)).Split(':',2);
await Run("docker",["login","--username",login[0],"--password-stdin",authorization.ProxyEndpoint],input:login[1],quiet:true);
var tag=DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");var images=new Dictionary<string,string>();
foreach(var (name,key,project) in new[]{("api","ApiRepositoryUri","JobApi"),("scheduler","SchedulerRepositoryUri","Scheduler"),("worker","WorkerRepositoryUri","")}) {
 var uri=bootstrap[key]+":"+tag;images[name]=uri;
 var command=new List<string>{"buildx","build","--platform","linux/amd64","--push","-t",uri,"-f",name=="worker"?"docker/worker.Dockerfile":"docker/service.Dockerfile"};
 if(project.Length>0)command.AddRange(["--build-arg","PROJECT="+project]);command.Add(".");await Run("docker",command);
}
var bucket=bootstrap["ArtifactBucketName"];var code=new Dictionary<string,string>();
foreach(var (project,key) in new[]{("DispatchLambda","DispatchCodeKey"),("StateRecoveryLambda","RecoveryCodeKey")}) {
 var publish=Path.Combine(root,"artifacts",project);if(Directory.Exists(publish))Directory.Delete(publish,true);
 // Portable framework-dependent IL; these functions have no RID/native dependencies.
 await Run("dotnet",["publish",$"src/backend/{project}/{project}.csproj","-c","Release","--no-restore","-m:1","-o",publish,"/p:UseAppHost=false"]);
 var archive=publish+".zip";if(File.Exists(archive))File.Delete(archive);ZipFile.CreateFromDirectory(publish,archive,CompressionLevel.Optimal,false);
 await using var stream=File.OpenRead(archive);var digest=Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
 var objectKey=$"lambda/{project}/{digest}.zip";
 await s3.PutObjectAsync(new PutObjectRequest{BucketName=bucket,Key=objectKey,FilePath=archive,ServerSideEncryptionMethod=ServerSideEncryptionMethod.AES256});code[key]=objectKey;
}
var network=await Deploy(environment+"-network","network.yaml",new(){["EnvironmentName"]=environment});
var parameters=new Dictionary<string,string>(network);
foreach(var pair in code)parameters[pair.Key]=pair.Value;
parameters["EnvironmentName"]=environment;parameters["ArtifactBucketName"]=bucket;parameters["ApiImageUri"]=images["api"];parameters["SchedulerImageUri"]=images["scheduler"];parameters["WorkerImageUri"]=images["worker"];
parameters["UseSpot"]=parsed.GetValueOrDefault("--spot","false");parameters["MaxVcpus"]=parsed.GetValueOrDefault("--max-vcpus","16");parameters["InstanceTypes"]=parsed.GetValueOrDefault("--instance-types","c6i.large,c6a.large");
parameters["StandardCpuCredits"]=parsed.GetValueOrDefault("--standard-cpu-credits","false");
if (parsed.TryGetValue("--table-name", out var tableName)) parameters["TableName"] = tableName;
var platform=await Deploy(parsed.GetValueOrDefault("--platform-stack-name", environment+"-platform"),"platform.yaml",parameters);
await File.WriteAllTextAsync(Path.Combine(root,"artifacts/deployment-outputs.json"),JsonSerializer.Serialize(platform,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(platform,new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Deployment finished. Provision tenant accounts and run scripts/smoke.py with a real tenant ID token to validate the AWS path.");

async Task<Dictionary<string,string>> Deploy(string name,string template,Dictionary<string,string> values) {
 var body=await File.ReadAllTextAsync(Path.Combine(root,"infrastructure/cloudformation",template));
 var parameters=values.Select(x=>new Parameter{ParameterKey=x.Key,ParameterValue=x.Value}).ToList();
 var tags=new List<Tag>{new(){Key="Project",Value="StepGlb"},new(){Key="Environment",Value=environment}};
 bool exists;
 try{await cf.DescribeStacksAsync(new DescribeStacksRequest{StackName=name});exists=true;}
 catch(AmazonCloudFormationException ex)when(ex.Message.Contains("does not exist",StringComparison.Ordinal)){exists=false;}
 if(exists) {
  try{await cf.UpdateStackAsync(new UpdateStackRequest{StackName=name,TemplateBody=body,Parameters=parameters,Capabilities=["CAPABILITY_IAM"],Tags=tags});}
  catch(AmazonCloudFormationException ex)when(ex.Message.Contains("No updates are to be performed",StringComparison.Ordinal)){return await Outputs(name);}
 }else await cf.CreateStackAsync(new CreateStackRequest{StackName=name,TemplateBody=body,Parameters=parameters,Capabilities=["CAPABILITY_IAM"],Tags=tags,OnFailure=OnFailure.ROLLBACK,RetainExceptOnCreate=true});
 Console.WriteLine($"Waiting for CloudFormation {name}");
 for(int tries=0;tries<240;tries++) {
  await Task.Delay(TimeSpan.FromSeconds(15));
  var stack=(await cf.DescribeStacksAsync(new DescribeStacksRequest{StackName=name})).Stacks[0];
  var status=stack.StackStatus.Value;
  if(status is "CREATE_COMPLETE" or "UPDATE_COMPLETE")return stack.Outputs.ToDictionary(o=>o.OutputKey,o=>o.OutputValue);
  if(status.Contains("FAILED",StringComparison.Ordinal)||status.Contains("ROLLBACK",StringComparison.Ordinal)) {
   var events=await cf.DescribeStackEventsAsync(new DescribeStackEventsRequest{StackName=name});
   foreach(var e in events.StackEvents.Where(e=>e.ResourceStatus.Value.Contains("FAILED",StringComparison.Ordinal)).Take(12))Console.Error.WriteLine($"{e.LogicalResourceId}: {e.ResourceStatusReason}");
   throw new InvalidOperationException($"CloudFormation {name}: {status}");
  }
 }
 throw new TimeoutException($"CloudFormation {name} exceeded one hour; inspect stack status before retrying");
}
async Task<Dictionary<string,string>> Outputs(string name)=>(await cf.DescribeStacksAsync(new DescribeStacksRequest{StackName=name})).Stacks[0].Outputs.ToDictionary(o=>o.OutputKey,o=>o.OutputValue);
static async Task Run(string executable,IEnumerable<string> arguments,string? input=null,bool quiet=false) {
 var info=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardInput=input!=null,RedirectStandardOutput=quiet};
 foreach (var name in new[] { "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN" })
     info.Environment.Remove(name);
 foreach(var argument in arguments)info.ArgumentList.Add(argument);
 using var process=Process.Start(info)??throw new InvalidOperationException($"Could not start {executable}");
 if(input!=null){await process.StandardInput.WriteAsync(input);process.StandardInput.Close();}
 if(quiet)await process.StandardOutput.ReadToEndAsync();
 await process.WaitForExitAsync();if(process.ExitCode!=0)throw new InvalidOperationException($"{executable} exited with {process.ExitCode}");
}
