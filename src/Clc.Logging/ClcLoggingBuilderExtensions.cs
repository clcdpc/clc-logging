using Clc.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clc.Logging;

public static class ClcLoggingBuilderExtensions
{
    public static IHostApplicationBuilder AddClcLogging(this IHostApplicationBuilder builder, string applicationDirectoryName, Action<ClcLoggingOptions>? configure = null) =>
        AddOwned(builder, () => ClcLogging.Create(applicationDirectoryName, configure));

    public static IHostApplicationBuilder AddClcLogging(this IHostApplicationBuilder builder, ClcApplicationPaths paths, Action<ClcLoggingOptions>? configure = null) =>
        AddOwned(builder, () => ClcLogging.Create(paths, configure));

    /// <summary>Registers caller-owned logging. Dispose it after the host and any fatal-error handler.</summary>
    public static IHostApplicationBuilder AddClcLogging(this IHostApplicationBuilder builder, ClcLogging logging)
        => Register(builder, logging, owned: false);

    private static IHostApplicationBuilder Register(IHostApplicationBuilder builder, ClcLogging logging, bool owned)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(logging);
        if (builder.Services.Any(service => service.ServiceType == typeof(LoggingRegistration)))
        {
            throw new InvalidOperationException("Clc.Logging is already registered on this builder.");
        }
        var options = logging.Register(builder.Configuration);
        builder.Logging.ClearProviders();
        builder.Services.Configure<LoggerFilterOptions>(options.ApplyFilters);
        if (owned) { builder.Services.AddSingleton(_ => logging); }
        builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(services =>
        {
            if (owned) { services.GetRequiredService<ClcLogging>(); }
            return logging.CreateProvider();
        });
        builder.Services.AddSingleton(new LoggingRegistration());
        return builder;
    }

    private static IHostApplicationBuilder AddOwned(IHostApplicationBuilder builder, Func<ClcLogging> create)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var logging = create();
        try
        {
            return Register(builder, logging, owned: true);
        }
        catch
        {
            logging.Dispose();
            throw;
        }
    }

    private sealed class LoggingRegistration;
}
