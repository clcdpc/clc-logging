using Clc.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// An absolute existing data root keeps this executable smoke fixture isolated.
// Production can use ClcLogging.Create("my-worker") instead.
ClcLogging? logging = null;
try
{
    logging = ClcLogging.CreateForDirectory(args.Single());
    logging.Logger.LogInformation("Worker bootstrap");
    var builder = Host.CreateApplicationBuilder();
    builder.AddClcLogging(logging);
    builder.Services.AddHostedService<Worker>();
    using var host = builder.Build();
    await host.RunAsync();
    Console.WriteLine("Worker package consumer passed.");
    return 0;
}
catch (Exception error)
{
    if (logging is null) { ClcLogging.WriteStartupFailure(error); }
    else { logging.LogFatal(error); }
    return 1;
}
finally { logging?.Dispose(); }

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
