using Clc.Configuration;
using Clc.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Only the fixture needs an absolute temporary root. Normal startup uses
// builder.AddClcConfiguration("clc-scheduler-worker") before AddClcLogging().
var builder = Host.CreateApplicationBuilder();
var paths = ConfigurationFixture.Paths(args.Single());
ConfigurationFixture.Initialize(builder, paths);
builder.AddClcLogging();
builder.Services.AddHostedService<Worker>();
using (var host = builder.Build())
{
    var logging = host.Services.GetRequiredService<ClcLogging>();
    if (!ReferenceEquals(paths, builder.GetClcApplicationPaths()) ||
        logging.DirectoryPath != Path.Combine(paths.ApplicationDataDirectory, "Logs") ||
        logging.ApplicationName != Path.GetFileName(paths.ApplicationDataDirectory)) {
        throw new InvalidOperationException("Worker did not reuse configured application paths.");
    }
    await host.RunAsync();
}
Console.WriteLine("Worker package consumer passed.");

sealed class Worker(ILogger<Worker> logger, IHostApplicationLifetime lifetime) : IHostedService, IDisposable
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = "worker-smoke" });
        logger.LogInformation(new EventId(10, "WorkerStarted"), "Worker processed {Count}", 3);
        lifetime.ApplicationStarted.Register(lifetime.StopApplication);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Worker stopping");
        return Task.CompletedTask;
    }

    public void Dispose() => logger.LogInformation("Worker disposed");
}
