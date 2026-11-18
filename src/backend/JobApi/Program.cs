using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StepGlb.Application;
using StepGlb.Domain;
using StepGlb.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning); // bearer URLs must not enter request logs
builder.Services.AddPlatform(builder.Configuration, builder.Environment.IsDevelopment());
var local = builder.Configuration["Platform:Mode"] == "Local";
if (local) {
    builder.Services.AddAuthentication("Local").AddScheme<AuthenticationSchemeOptions, LocalAuthentication>("Local", _ => { });
    builder.Services.AddHostedService<LocalOrchestrator>();
} else {
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => {
        o.Authority = builder.Configuration["Auth:Authority"] ?? throw new InvalidOperationException("Auth:Authority required");
        o.Audience = builder.Configuration["Auth:Audience"] ?? throw new InvalidOperationException("Auth:Audience required");
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters { ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30) };
    });
}
builder.Services.AddAuthorization(o => o.AddPolicy("Tenant", p => {
    p.RequireAuthenticatedUser(); p.RequireClaim("custom:tenantId");
    if (!local) p.RequireClaim("token_use", "id");
}));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins((builder.Configuration["AllowedOrigins"] ?? "http://127.0.0.1:5173").Split(',', StringSplitOptions.TrimEntries)).AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddProblemDetails();
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddRateLimiter(o => o.AddFixedWindowLimiter("api", p => { p.PermitLimit = 300; p.Window = TimeSpan.FromMinutes(1); p.QueueLimit = 0; }));
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = local ? 512L * 1024 * 1024 : 1024 * 1024);
var app = builder.Build();
app.UseExceptionHandler(handler => handler.Run(async context => {
    var exception = context.Features.Get<IExceptionHandlerFeature>()!.Error;
    var (code, title) = exception switch {
        NotFoundException => (404, "Job not found"), ValidationException => (400, exception.Message),
        ConflictException => (409, exception.Message), BadHttpRequestException => (400, "Invalid request"),
        _ => (500, "Request failed")
    };
    if (code == 500) app.Logger.LogError(exception, "Request failed {correlationId}", context.TraceIdentifier);
    await Results.Problem(statusCode: code, title: title, extensions: new Dictionary<string, object?> { ["correlationId"] = context.TraceIdentifier }).ExecuteAsync(context);
}));
app.UseCors(); app.UseAuthentication(); app.UseAuthorization(); app.UseRateLimiter();
if (!local && app.Environment.WebRootPath is not null) {
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions {
        OnPrepareResponse = context => {
            var headers = context.Context.Response.Headers;
            headers.CacheControl = "no-store";
            headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' https://*.amazonaws.com https://*.amazoncognito.com; img-src 'self' data: blob:; worker-src 'self' blob:; font-src 'self'; frame-ancestors 'none'; object-src 'none'; base-uri 'self'";
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
        }
    });
    app.MapGet("/config.json", (HttpContext context) => {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new {
            apiUrl = "",
            local = false,
            pollMs = 2000,
            clientId = app.Configuration["Auth:Audience"],
            authDomain = app.Configuration["Auth:Domain"]
        });
    });
}
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
string Tenant(ClaimsPrincipal user) { var tenant = user.FindFirstValue("custom:tenantId")!; StoreKeys.ValidateIdentity(tenant); return tenant; }
void LogJob(string action, Job job) => app.Logger.LogInformation("Job {action} {tenantId} {jobId} {attemptId} {providerJobId} {correlationId}",
    action, job.TenantId, job.JobId, job.AttemptId, job.ProviderJobId, job.JobId);
var api = app.MapGroup("/api/jobs").RequireAuthorization("Tenant").RequireRateLimiting("api");
api.MapPost("", async (CreateJob request, ClaimsPrincipal user, JobService service, CancellationToken ct) => {
    var (job, url) = await service.CreateAsync(Tenant(user), request.FileName, request.Bytes, request.Multipart, ct);
    LogJob("created", job);
    return Results.Created($"/api/jobs/{job.JobId}", new { job, uploadUrl = url, multipart = request.Multipart });
});
api.MapGet("", async (ClaimsPrincipal user, IJobStore store, int? limit, string? token, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(Tenant(user), Math.Clamp(limit ?? 20, 1, 100), token, ct)));
api.MapGet("/{id}", async (string id, ClaimsPrincipal user, JobService service, CancellationToken ct) => Results.Ok(await service.GetAsync(Tenant(user), id, ct)));
api.MapGet("/{id}/progress", async (string id, ClaimsPrincipal user, JobService service, CancellationToken ct) => {
    var job = await service.GetAsync(Tenant(user), id, ct);
    return Results.Ok(new { job.JobId, job.AttemptId, job.Status, job.Stage, job.ProgressPercent, job.Message, job.StartedAt, job.CompletedAt, job.ErrorCode, job.ErrorMessage, job.AttemptNumber });
});
api.MapPost("/{id}/upload/complete", async (string id, ClaimsPrincipal user, JobService service, CancellationToken ct) => {
    var job = await service.CompleteUploadAsync(Tenant(user), id, ct); LogJob("upload-accepted", job); return Results.Ok(job);
});
api.MapGet("/{id}/result", async (string id, ClaimsPrincipal user, JobService service, CancellationToken ct) => Results.Ok(new { url = await service.ResultAsync(Tenant(user), id, ct) }));
api.MapPost("/{id}/cancel", async (string id, ClaimsPrincipal user, JobService service, CancellationToken ct) => {
    var job = await service.CancelAsync(Tenant(user), id, ct); LogJob("cancel-requested", job); return Results.Ok(job);
});
api.MapPost("/{id}/upload/parts/{part:int}", async (string id, int part, ClaimsPrincipal user, JobService service, IObjectStore objects, CancellationToken ct) =>
    Results.Ok(new { url = await objects.PartUrlAsync(await service.GetAsync(Tenant(user), id, ct), part, ct) }));
api.MapPost("/{id}/upload/multipart/complete", async (string id, UploadPart[] parts, ClaimsPrincipal user, JobService service, IObjectStore objects, CancellationToken ct) => {
    var job = await service.GetAsync(Tenant(user), id, ct);
    if (job.Status == JobStatus.UPLOADING) await objects.CompleteMultipartAsync(job, parts, ct);
    return Results.Ok(await service.CompleteUploadAsync(Tenant(user), id, ct));
});
if (local) {
    app.MapPut("/local/objects/{tenant}/{id}", async (string tenant, string id, long expires, string signature, HttpRequest request, LocalObjectStore objects, JobService service, CancellationToken ct) => {
        if (!objects.Authorized("PUT", tenant, id, expires, signature)) return Results.Unauthorized();
        var job = await service.GetAsync(tenant, id, ct);
        if (job.Status != JobStatus.UPLOADING) throw new ConflictException("Upload session is closed");
        if (request.ContentLength != job.InputBytes) throw new ValidationException("Content-Length must match declared input size");
        var path = objects.PathFor(job.InputKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            await using (var output = File.Create(temp)) await request.Body.CopyToAsync(output, ct);
            if (new FileInfo(temp).Length != job.InputBytes) throw new ValidationException("Incomplete input");
            File.Move(temp, path, false); // First upload wins; no accepted input can be overwritten.
        } finally { if (File.Exists(temp)) File.Delete(temp); }
        return Results.NoContent();
    }).AllowAnonymous();
    app.MapGet("/local/objects/{tenant}/{id}", async (string tenant, string id, long expires, string signature, LocalObjectStore objects, JobService service, CancellationToken ct) => {
        if (!objects.Authorized("GET", tenant, id, expires, signature)) return Results.Unauthorized();
        var job = await service.GetAsync(tenant, id, ct);
        if (job.Status != JobStatus.SUCCEEDED) throw new ConflictException("Result is not ready");
        return Results.File(objects.PathFor(job.OutputKey), "model/gltf-binary", fileDownloadName: "model.glb", enableRangeProcessing: true);
    }).AllowAnonymous();
}
app.Run();
public sealed record CreateJob(string FileName, long Bytes, bool Multipart = false);
public sealed class LocalAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "local-developer"), new Claim("custom:tenantId", "local-demo")], "Local")), "Local")));
}
public sealed class LocalOrchestrator(IJobStore store, DispatchService dispatch, RecoveryService recovery, ILogger<LocalOrchestrator> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            for (var shard = 0; shard < 16; shard++) {
                foreach (var job in await store.StaleAsync(shard, DateTimeOffset.UtcNow, 100, ct)) {
                    try {
                        if (job.Status == JobStatus.READY) await dispatch.DispatchAsync(new(job.TenantId, job.JobId, job.AttemptId), ct);
                        else await recovery.ReconcileAsync(job.TenantId, job.JobId, ct);
                    } catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Local orchestration failed {jobId}", job.JobId); }
                }
            }
            await Task.Delay(1000, ct);
        }
    }
}
