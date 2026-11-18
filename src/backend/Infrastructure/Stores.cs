using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using StepGlb.Application;
using StepGlb.Domain;

namespace StepGlb.Infrastructure;

public static class StoreKeys
{
    public static int Shard(string id) => SHA256.HashData(Encoding.UTF8.GetBytes(id))[0] % 16;
    public static string Encode(string id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(id));
    public static string? Decode(string? token)
    {
        if (token == null) return null;
        try {
            var id = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            if (!Guid.TryParseExact(id, "N", out _)) throw new FormatException();
            return id;
        } catch (FormatException) { throw new ValidationException("Invalid page token"); }
    }
    public static void ValidateIdentity(string value)
    {
        if (value.Length is < 1 or > 128 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ValidationException("Invalid server identity");
    }
}
public sealed class DynamoJobStore(IAmazonDynamoDB db, PlatformOptions options) : IJobStore
{
    static Dictionary<string, AttributeValue> Key(string tenant, string sk) => new() { ["PK"] = new($"TENANT#{tenant}"), ["SK"] = new(sk) };
    static Job Read(Dictionary<string, AttributeValue> item) => JsonSerializer.Deserialize<Job>(item["payload"].S, JsonDefaults.Options)!;
    public async Task<Job?> GetAsync(string tenant, string job, CancellationToken ct = default)
    {
        var r = await db.GetItemAsync(new GetItemRequest { TableName = options.Table, Key = Key(tenant, $"JOB#{job}"), ConsistentRead = true }, ct);
        return r.Item is { Count: > 0 } ? Read(r.Item) : null;
    }
    public async Task<bool> SaveAsync(Job job, long? expectedVersion, CancellationToken ct = default)
    {
        job = job with { Version = expectedVersion + 1 ?? 0, UpdatedAt = DateTimeOffset.UtcNow };
        var item = Key(job.TenantId, $"JOB#{job.JobId}");
        item["payload"] = new(JsonSerializer.Serialize(job, JsonDefaults.Options));
        item["version"] = new() { N = job.Version.ToString() };
        item["status"] = new(job.Status.ToString());
        item["tenantId"] = new(job.TenantId); item["jobId"] = new(job.JobId); item["attemptId"] = new(job.AttemptId);
        if (!job.Terminal || job.Status == JobStatus.CANCELLED && !job.CancellationConfirmed) {
            item["OpsPK"] = new($"ACTIVE#{StoreKeys.Shard(job.JobId):D2}");
            item["OpsSK"] = new(job.UpdatedAt.ToString("O"));
        }
        var attempt = new Dictionary<string, AttributeValue>(item) { ["SK"] = new($"ATTEMPT#{job.JobId}#{job.AttemptId}") };
        attempt.Remove("OpsPK"); attempt.Remove("OpsSK");
        var put = new Put {
            TableName = options.Table, Item = item,
            ConditionExpression = expectedVersion == null ? "attribute_not_exists(PK)" : "#v = :v",
        };
        if (expectedVersion != null) {
            put.ExpressionAttributeNames = new() { ["#v"] = "version" };
            put.ExpressionAttributeValues = new() { [":v"] = new() { N = expectedVersion.ToString() } };
        }
        try {
            await db.TransactWriteItemsAsync(new TransactWriteItemsRequest { TransactItems = [
                new() { Put = put }, new() { Put = new Put { TableName = options.Table, Item = attempt } }
            ] }, ct);
            return true;
        } catch (TransactionCanceledException ex) when (ex.CancellationReasons?.Any(r => r.Code == "ConditionalCheckFailed") == true) { return false; }
    }
    public async Task<JobPage> ListAsync(string tenant, int limit, string? token, CancellationToken ct = default)
    {
        var after = StoreKeys.Decode(token);
        var r = await db.QueryAsync(new QueryRequest {
            TableName = options.Table, ConsistentRead = true, Limit = Math.Clamp(limit, 1, 100), ScanIndexForward = false,
            KeyConditionExpression = "PK = :pk AND begins_with(SK, :sk)",
            ExpressionAttributeValues = new() { [":pk"] = new($"TENANT#{tenant}"), [":sk"] = new("JOB#") },
            ExclusiveStartKey = after == null ? null : Key(tenant, $"JOB#{after}")
        }, ct);
        return new(r.Items.Select(Read).ToList(), r.LastEvaluatedKey is { Count: > 0 } k ? StoreKeys.Encode(k["SK"].S[4..]) : null);
    }
    public async Task<IReadOnlyList<Job>> StaleAsync(int shard, DateTimeOffset before, int limit, CancellationToken ct = default)
    {
        var r = await db.QueryAsync(new QueryRequest {
            TableName = options.Table, IndexName = "Operational", Limit = Math.Clamp(limit, 1, 100),
            KeyConditionExpression = "OpsPK = :pk AND OpsSK < :before",
            ExpressionAttributeValues = new() { [":pk"] = new($"ACTIVE#{shard:D2}"), [":before"] = new(before.ToString("O")) }
        }, ct);
        return r.Items.Select(Read).ToList();
    }
}

// Development-only disk store. OS advisory file lock serializes API/worker processes;
// atomic rename publishes each complete JSON record. No AWS services are emulated.
public sealed class LocalJobStore(PlatformOptions options) : IJobStore
{
    string DirectoryFor(string tenant) { StoreKeys.ValidateIdentity(tenant); return Path.Combine(options.LocalRoot, "jobs", tenant); }
    string FileFor(string tenant, string job) { StoreKeys.ValidateIdentity(job); return Path.Combine(DirectoryFor(tenant), job + ".json"); }
    public async Task<Job?> GetAsync(string tenant, string job, CancellationToken ct = default)
    {
        var file = FileFor(tenant, job);
        return File.Exists(file) ? JsonSerializer.Deserialize<Job>(await File.ReadAllTextAsync(file, ct), JsonDefaults.Options) : null;
    }
    public async Task<bool> SaveAsync(Job job, long? expectedVersion, CancellationToken ct = default)
    {
        Directory.CreateDirectory(DirectoryFor(job.TenantId));
        var path = FileFor(job.TenantId, job.JobId);
        await using var mutex = await LockAsync(path + ".lock", ct);
        var current = await GetAsync(job.TenantId, job.JobId, ct);
        if (expectedVersion == null ? current != null : current?.Version != expectedVersion) return false;
        job = job with { Version = expectedVersion + 1 ?? 0, UpdatedAt = DateTimeOffset.UtcNow };
        var json = JsonSerializer.Serialize(job, JsonDefaults.Options);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temp, json, ct);
        File.Move(temp, path, true);
        await File.WriteAllTextAsync(Path.Combine(DirectoryFor(job.TenantId), $"attempt-{job.JobId}-{job.AttemptId}.record"), json, ct);
        return true;
    }
    static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        for (int retry = 0; ; retry++) {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (retry < 100) { await Task.Delay(20, ct); }
        }
    }
    public async Task<JobPage> ListAsync(string tenant, int limit, string? token, CancellationToken ct = default)
    {
        var after = StoreKeys.Decode(token);
        var directory = DirectoryFor(tenant);
        if (!Directory.Exists(directory)) return new([], null);
        var files = Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension)
            .Where(id => after == null || string.CompareOrdinal(id, after) < 0).OrderDescending(StringComparer.Ordinal).Take(Math.Clamp(limit, 1, 100) + 1).ToList();
        var jobs = new List<Job>();
        foreach (var id in files.Take(limit)) jobs.Add((await GetAsync(tenant, id!, ct))!);
        return new(jobs, files.Count > limit ? StoreKeys.Encode(jobs[^1].JobId) : null);
    }
    public async Task<IReadOnlyList<Job>> StaleAsync(int shard, DateTimeOffset before, int limit, CancellationToken ct = default)
    {
        var root = Path.Combine(options.LocalRoot, "jobs");
        if (!Directory.Exists(root)) return [];
        var found = new List<Job>();
        foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)) {
            var job = JsonSerializer.Deserialize<Job>(await File.ReadAllTextAsync(path, ct), JsonDefaults.Options)!;
            if ((!job.Terminal || job.Status == JobStatus.CANCELLED && !job.CancellationConfirmed) && job.UpdatedAt < before && StoreKeys.Shard(job.JobId) == shard) found.Add(job);
        }
        return found.OrderBy(j => j.UpdatedAt).Take(limit).ToList();
    }
}

public static class JobMutations
{
    public static async Task<Job?> UpdateAsync(this IJobStore store, string tenant, string id, Func<Job, Job?> mutate, CancellationToken ct)
    {
        for (int tries = 0; tries < 8; tries++) {
            var current = await store.GetAsync(tenant, id, ct);
            if (current == null) return null;
            var next = mutate(current);
            if (next == null) return null;
            if (await store.SaveAsync(next, current.Version, ct)) return next with { Version = current.Version + 1 };
            await Task.Delay(Random.Shared.Next(10, 60), ct);
        }
        throw new ConflictException("Concurrent updates exceeded retry budget");
    }
}
