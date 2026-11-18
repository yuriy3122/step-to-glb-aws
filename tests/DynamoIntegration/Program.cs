using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using StepGlb.Application;
using StepGlb.Domain;
using StepGlb.Infrastructure;

var endpoint=new Uri(Environment.GetEnvironmentVariable("DYNAMODB_LOCAL_URL")??"http://127.0.0.1:8000");
if(!endpoint.IsLoopback)throw new InvalidOperationException("This test only creates tables in a loopback DynamoDB Local instance");
using var db=new AmazonDynamoDBClient(new BasicAWSCredentials(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N")),new AmazonDynamoDBConfig{ServiceURL=endpoint.ToString(),AuthenticationRegion="us-east-1"});
var table="CadIntegration"+Guid.NewGuid().ToString("N");
await db.CreateTableAsync(new CreateTableRequest{
 TableName=table,BillingMode=BillingMode.PAY_PER_REQUEST,
 AttributeDefinitions=[new("PK",ScalarAttributeType.S),new("SK",ScalarAttributeType.S),new("OpsPK",ScalarAttributeType.S),new("OpsSK",ScalarAttributeType.S)],
 KeySchema=[new("PK",KeyType.HASH),new("SK",KeyType.RANGE)],
 GlobalSecondaryIndexes=[new GlobalSecondaryIndex{IndexName="Operational",KeySchema=[new("OpsPK",KeyType.HASH),new("OpsSK",KeyType.RANGE)],Projection=new(){ProjectionType=ProjectionType.ALL}}]
});
try{
 var store=new DynamoJobStore(db,new PlatformOptions{Table=table});
 var jobs=new List<Job>();
 for(var i=0;i<5;i++){
  var j=new Job{TenantId="tenant-a",JobId=Guid.NewGuid().ToString("N"),AttemptId=Guid.NewGuid().ToString("N"),FileName="part.step",InputBytes=123};
  Check(await store.SaveAsync(j,null),"Creation transaction");jobs.Add(j);
 }
 var job=jobs[0];
 Check(!await store.SaveAsync(job,null),"Duplicate creation rejected by DynamoDB condition");
 var winners=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>store.SaveAsync(job with{Stage="contended"},0)));
 Check(winners.Count(x=>x)==1,"Exactly one optimistic write wins");
 Check((await store.GetAsync(job.TenantId,job.JobId))!.Version==1,"Version persisted");
 Check(await store.GetAsync("tenant-b",job.JobId)==null,"Cross-tenant lookup absent");
 var first=await store.ListAsync("tenant-a",2,null);var second=await store.ListAsync("tenant-a",2,first.NextToken);
 Check(first.Jobs.Count==2&&second.Jobs.Count==2&&!first.Jobs.Select(x=>x.JobId).Intersect(second.Jobs.Select(x=>x.JobId)).Any(),"Query pagination");
 Check((await store.ListAsync("tenant-b",2,first.NextToken)).Jobs.Count==0,"Cursor cannot change tenant");
 var attempt=await db.GetItemAsync(table,new Dictionary<string,AttributeValue>{{"PK",new("TENANT#tenant-a")},{"SK",new($"ATTEMPT#{job.JobId}#{job.AttemptId}")}},true);
 Check(attempt.Item.Count>0,"Attempt record written in same table");
 var active=await store.StaleAsync(StoreKeys.Shard(job.JobId),DateTimeOffset.UtcNow.AddMinutes(1),25);
 Check(active.Any(x=>x.JobId==job.JobId),"Operational GSI query");
 Console.WriteLine("PASS: DynamoDB Local real SDK transactions, version contention, attempt records, tenant queries, cursors, operational index");
}finally{await db.DeleteTableAsync(table);}
static void Check(bool value,string message){if(!value)throw new Exception(message);}
