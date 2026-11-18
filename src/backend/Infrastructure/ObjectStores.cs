using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using StepGlb.Application;
using StepGlb.Domain;

namespace StepGlb.Infrastructure;

public static class OutputValidation
{
    public static bool Matches(Job j, Manifest m) => m.TenantId == j.TenantId && m.JobId == j.JobId && m.AttemptId == j.AttemptId &&
        m.ProviderJobId == j.ProviderJobId && m.ExecutionId == j.ExecutionId && m.InputETag == j.InputETag && m.OutputKey == j.OutputKey &&
        m.Bytes > 28 && m.Sha256.Length == 64;
    public static async Task<string> HashAsync(string path, CancellationToken ct) {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }
    public static async Task CheckStepAsync(Stream stream, CancellationToken ct) {
        var bytes = new byte[256];
        var read = await stream.ReadAtLeastAsync(bytes, 16, false, ct);
        if (!Encoding.ASCII.GetString(bytes, 0, read).TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("ISO-10303-21;", StringComparison.Ordinal))
            throw new ValidationException("Input is not an ISO-10303-21 STEP file");
    }
}
public sealed class S3ObjectStore(IAmazonS3 s3, PlatformOptions options) : IObjectStore
{
    public Task<string> UploadUrlAsync(Job job, CancellationToken ct) {
        var request = new GetPreSignedUrlRequest { BucketName = options.InputBucket, Key = job.InputKey, Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.AddMinutes(options.UrlMinutes), ContentType = "application/octet-stream" };
        request.Headers["Content-Length"] = job.InputBytes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return s3.GetPreSignedURLAsync(request);
    }
    public Task<string> DownloadUrlAsync(Job job, CancellationToken ct) => s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest {
        BucketName = options.OutputBucket, Key = job.OutputKey, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.AddMinutes(options.UrlMinutes),
        ResponseHeaderOverrides = new ResponseHeaderOverrides { ContentDisposition = "attachment; filename=\"model.glb\"" }
    });
    public async Task<ObjectInfo?> HeadInputAsync(Job job, CancellationToken ct) {
        try {
            var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = options.InputBucket, Key = job.InputKey }, ct);
            using var input = await s3.GetObjectAsync(new GetObjectRequest { BucketName = options.InputBucket, Key = job.InputKey, VersionId = head.VersionId, ByteRange = new ByteRange(0, 255) }, ct);
            await OutputValidation.CheckStepAsync(input.ResponseStream, ct);
            return new(head.ContentLength, head.ETag, head.VersionId);
        } catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }
    public async Task DownloadInputAsync(Job job, string path, CancellationToken ct) {
        using var input = await s3.GetObjectAsync(new GetObjectRequest {
            BucketName = options.InputBucket, Key = job.InputKey, VersionId = job.InputVersionId, EtagToMatch = job.InputETag
        }, ct);
        await using var file = File.Create(path);
        await input.ResponseStream.CopyToAsync(file, ct);
        if (file.Length != job.InputBytes) throw new ValidationException("Input length changed");
    }
    public async Task PublishAsync(Job job, string glb, Manifest manifest, CancellationToken ct) {
        await s3.PutObjectAsync(new PutObjectRequest {
            BucketName = options.OutputBucket, Key = job.OutputKey, FilePath = glb, ContentType = "model/gltf-binary",
            ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(manifest.Sha256)), ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256
        }, ct);
        await s3.PutObjectAsync(new PutObjectRequest {
            BucketName = options.OutputBucket, Key = job.ManifestKey, ContentBody = JsonSerializer.Serialize(manifest, JsonDefaults.Options),
            ContentType = "application/json", ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256
        }, ct);
    }
    public async Task<Manifest?> VerifyAsync(Job job, CancellationToken ct) {
        try {
            using var result = await s3.GetObjectAsync(options.OutputBucket, job.ManifestKey, ct);
            if (result.ContentLength > 16384) return null;
            var manifest = await JsonSerializer.DeserializeAsync<Manifest>(result.ResponseStream, JsonDefaults.Options, ct);
            if (manifest == null || !OutputValidation.Matches(job, manifest)) return null;
            var head = await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = options.OutputBucket, Key = job.OutputKey, ChecksumMode = ChecksumMode.ENABLED }, ct);
            return head.ContentLength == manifest.Bytes && head.ChecksumSHA256 == Convert.ToBase64String(Convert.FromHexString(manifest.Sha256)) ? manifest : null;
        } catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }
    public async Task<string> StartMultipartAsync(Job job, CancellationToken ct) => (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest {
        BucketName = options.InputBucket, Key = job.InputKey, ContentType = "application/octet-stream", ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256
    }, ct)).UploadId;
    public Task<string> PartUrlAsync(Job job, int part, CancellationToken ct) {
        const long partSize = 8L * 1024 * 1024;
        if (job.Status != JobStatus.UPLOADING || job.MultipartUploadId == null || part < 1 || part > (job.InputBytes + partSize - 1) / partSize)
            throw new ValidationException("Invalid multipart session or part");
        var request = new GetPreSignedUrlRequest { BucketName = options.InputBucket, Key = job.InputKey,
            UploadId = job.MultipartUploadId, PartNumber = part, Verb = HttpVerb.PUT, Expires = DateTime.UtcNow.AddMinutes(options.UrlMinutes) };
        request.Headers["Content-Length"] = Math.Min(partSize, job.InputBytes - (part - 1) * partSize).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return s3.GetPreSignedURLAsync(request);
    }
    public async Task CompleteMultipartAsync(Job job, IReadOnlyList<UploadPart> parts, CancellationToken ct) {
        if (job.Status != JobStatus.UPLOADING || job.MultipartUploadId == null || parts.Count != (job.InputBytes + 8L * 1024 * 1024 - 1) / (8L * 1024 * 1024) ||
            parts.Select((p, i) => p.PartNumber != i + 1 || string.IsNullOrWhiteSpace(p.ETag)).Any(x => x)) throw new ValidationException("Invalid multipart completion");
        try {
            await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = options.InputBucket, Key = job.InputKey,
                UploadId = job.MultipartUploadId, PartETags = parts.Select(p => new PartETag(p.PartNumber, p.ETag)).ToList() }, ct);
        } catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) {
            // S3 may have completed the upload before an HTTP response was lost.
            // The use case still verifies header, size and version before READY.
            if (await HeadInputAsync(job, ct) == null) throw;
        }
    }
    public async Task AbortMultipartAsync(Job job, CancellationToken ct) {
        if (job.MultipartUploadId == null) return;
        try { await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = options.InputBucket, Key = job.InputKey, UploadId = job.MultipartUploadId }, ct); }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
    }
}

public sealed class LocalObjectStore(PlatformOptions options) : IObjectStore
{
    // Local signed URLs are bearer capabilities held only by the browser that created a job.
    // Persist a randomly generated signing key; never use this development identity in AWS mode.
    public string PathFor(string key) => Path.Combine(options.LocalRoot, "objects", key.Replace('/', Path.DirectorySeparatorChar));
    byte[] SigningKey() {
        Directory.CreateDirectory(options.LocalRoot);
        var path = Path.Combine(options.LocalRoot, "url-signing-key");
        try {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(RandomNumberGenerator.GetBytes(32));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        } catch (IOException) when (File.Exists(path)) { }
        return File.ReadAllBytes(path);
    }
    string Signature(string method, string tenant, string job, long expires) => Convert.ToHexStringLower(HMACSHA256.HashData(SigningKey(), Encoding.UTF8.GetBytes($"{method}\n{tenant}\n{job}\n{expires}")));
    public bool Authorized(string method, string tenant, string id, long expires, string signature) {
        if (expires < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(signature), Convert.FromHexString(Signature(method, tenant, id, expires))); }
        catch (FormatException) { return false; }
    }
    string Url(Job j, string method) {
        var expires = DateTimeOffset.UtcNow.AddMinutes(options.UrlMinutes).ToUnixTimeSeconds();
        return $"{options.LocalApiUrl}/local/objects/{j.TenantId}/{j.JobId}?expires={expires}&signature={Signature(method, j.TenantId, j.JobId, expires)}";
    }
    public Task<string> UploadUrlAsync(Job job, CancellationToken ct) => Task.FromResult(Url(job, "PUT"));
    public Task<string> DownloadUrlAsync(Job job, CancellationToken ct) => Task.FromResult(Url(job, "GET"));
    public async Task<ObjectInfo?> HeadInputAsync(Job job, CancellationToken ct) {
        var path = PathFor(job.InputKey);
        if (!File.Exists(path)) return null;
        await using (var stream = File.OpenRead(path)) await OutputValidation.CheckStepAsync(stream, ct);
        return new(new FileInfo(path).Length, await OutputValidation.HashAsync(path, ct));
    }
    public async Task DownloadInputAsync(Job job, string path, CancellationToken ct) {
        var source = PathFor(job.InputKey);
        if (await OutputValidation.HashAsync(source, ct) != job.InputETag) throw new ValidationException("Input changed after verification");
        File.Copy(source, path, true);
    }
    public async Task PublishAsync(Job job, string glb, Manifest manifest, CancellationToken ct) {
        var dest = PathFor(job.OutputKey);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(glb, dest, true);
        await File.WriteAllTextAsync(PathFor(job.ManifestKey), JsonSerializer.Serialize(manifest, JsonDefaults.Options), ct);
    }
    public async Task<Manifest?> VerifyAsync(Job job, CancellationToken ct) {
        var path = PathFor(job.ManifestKey);
        if (!File.Exists(path) || !File.Exists(PathFor(job.OutputKey))) return null;
        try {
            var m = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(path, ct), JsonDefaults.Options);
            return m != null && OutputValidation.Matches(job, m) && new FileInfo(PathFor(job.OutputKey)).Length == m.Bytes &&
                await OutputValidation.HashAsync(PathFor(job.OutputKey), ct) == m.Sha256 ? m : null;
        } catch (JsonException) { return null; }
    }
    public Task<string> StartMultipartAsync(Job job, CancellationToken ct) => throw new ValidationException("Multipart upload requires AWS mode; local uploads use one streamed request");
    public Task<string> PartUrlAsync(Job job, int part, CancellationToken ct) => throw new ValidationException("Multipart upload requires AWS mode");
    public Task CompleteMultipartAsync(Job job, IReadOnlyList<UploadPart> parts, CancellationToken ct) => throw new ValidationException("Multipart upload requires AWS mode");
    public Task AbortMultipartAsync(Job job, CancellationToken ct) => Task.CompletedTask;
}
