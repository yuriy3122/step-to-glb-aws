using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StepGlb.Application;
using StepGlb.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddJsonConsole(o => o.IncludeScopes = true);
builder.Services.AddPlatform(builder.Configuration, builder.Environment.IsDevelopment());
using var host = builder.Build();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
string Required(string key) => Environment.GetEnvironmentVariable(key) ?? throw new InvalidOperationException($"Missing {key}");
var provider = Environment.GetEnvironmentVariable("AWS_BATCH_JOB_ID") ?? Required("LOCAL_PROVIDER_ID");
var exit = await host.Services.GetRequiredService<WorkerRunner>().RunAsync(Required("TENANT_ID"), Required("JOB_ID"), Required("ATTEMPT_ID"), provider, stop.Token);
if (host.Services.GetRequiredService<PlatformOptions>().Mode == "Local") {
    var backend = host.Services.GetRequiredService<LocalComputeBackend>();
    var record = backend.RecordPath(provider);
    var temporary = record + ".tmp";
    await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ComputeJobStatus(provider, exit == 0 ? "SUCCEEDED" : "FAILED", $"Worker exit {exit}")));
    File.Move(temporary, record, true);
}
return exit;
