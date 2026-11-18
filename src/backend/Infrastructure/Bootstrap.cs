using Amazon.Batch;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SQS;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StepGlb.Application;

namespace StepGlb.Infrastructure;
public static class Bootstrap
{
    public static IServiceCollection AddPlatform(this IServiceCollection services, IConfiguration configuration, bool development = false) {
        var options = configuration.GetSection("Platform").Get<PlatformOptions>() ?? new();
        if (options.UrlMinutes is < 1 or > 10 || options.MaxInputBytes is < 1 or > 5L * 1024 * 1024 * 1024 ||
            options.ProgressIntervalMs < 100 || options.ProgressDelta <= 0 || options.MaxAttempts is < 1 or > 10 || options.LeaseSeconds < 60)
            throw new InvalidOperationException("Invalid platform limits, progress throttle, lease, or attempt budget");
        services.AddSingleton(options);
        if (options.Mode == "Local") {
            if (!development) throw new InvalidOperationException("Local mode is allowed only in Development");
            services.AddSingleton<LocalJobStore>(); services.AddSingleton<IJobStore>(s => s.GetRequiredService<LocalJobStore>());
            services.AddSingleton<LocalObjectStore>(); services.AddSingleton<IObjectStore>(s => s.GetRequiredService<LocalObjectStore>());
            services.AddSingleton<LocalComputeBackend>(); services.AddSingleton<IComputeBackend>(s => s.GetRequiredService<LocalComputeBackend>());
        } else if (options.Mode == "Aws") {
            if (string.IsNullOrEmpty(options.InputBucket) || string.IsNullOrEmpty(options.OutputBucket) || string.IsNullOrEmpty(options.BatchQueue))
                throw new InvalidOperationException("AWS storage and Batch configuration is required");
            services.AddSingleton<IAmazonDynamoDB>(_ => new AmazonDynamoDBClient());
            services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
            // Bound SDK retries and HTTP timeouts. SubmitJob can still have an uncertain outcome.
            services.AddSingleton<IAmazonBatch>(_ => new AmazonBatchClient(new AmazonBatchConfig { Timeout = TimeSpan.FromSeconds(30), MaxErrorRetry = 2 }));
            services.AddSingleton<IAmazonSQS>(_ => new AmazonSQSClient());
            services.AddSingleton<IJobStore, DynamoJobStore>(); services.AddSingleton<IObjectStore, S3ObjectStore>(); services.AddSingleton<IComputeBackend, AwsBatchComputeBackend>();
        } else throw new InvalidOperationException("Unknown Platform:Mode");
        services.AddSingleton<JobService>(); services.AddSingleton<DispatchService>(); services.AddSingleton<RecoveryService>(); services.AddSingleton<WorkerRunner>();
        return services;
    }
    public static IConfiguration EnvironmentConfiguration() => new ConfigurationBuilder().AddEnvironmentVariables().Build();
}
