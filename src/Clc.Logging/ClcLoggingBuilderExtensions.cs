using Clc.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Clc.Logging;

public static class ClcLoggingBuilderExtensions
{
    /// <summary>Registers host-owned logging using paths established by Clc.Configuration.</summary>
    /// <remarks>Call AddClcConfiguration or AddClcConfigurationWithBootstrap successfully first.</remarks>
    public static IHostApplicationBuilder AddClcLogging(this IHostApplicationBuilder builder, Action<ClcLoggingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddClcLogging(builder.GetClcApplicationPaths(), configure);
    }

    public static IHostApplicationBuilder AddClcLogging(this IHostApplicationBuilder builder, string applicationDirectoryName, Action<ClcLoggingOptions>? configure = null) =>
        AddOwned(builder, () => ClcLogging.CreateOwned(ClcApplicationPaths.Resolve(applicationDirectoryName), configure));

    public static IHostApplicationBuilder AddClcLogging(this IHostApplicationBuilder builder, ClcApplicationPaths paths, Action<ClcLoggingOptions>? configure = null) =>
        AddOwned(builder, () => ClcLogging.CreateOwned(paths, configure));

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
        // Always supply the native Logging binding, even when a host disables
        // defaults or binds a different section. Put it before application filters;
        // a standard host's existing binding keeps its original precedence.
        var firstNewService = builder.Services.Count;
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        var bindings = builder.Services.Skip(firstNewService)
            .Where(service => service.ServiceType == typeof(IConfigureOptions<LoggerFilterOptions>)).ToArray();
        foreach (var binding in bindings.Reverse())
        {
            builder.Services.Remove(binding);
            builder.Services.Insert(0, binding);
        }
        builder.Services.Configure<LoggerFilterOptions>(options.ApplyOverrides);
        if (owned)
        {
            builder.Services.AddSingleton(_ => logging);
            // Let the host track ownership even if the application later removes
            // the NLog provider from its provider collection.
            builder.Services.AddOptions<LoggerFilterOptions>().PostConfigure<ClcLogging>(
                (filters, _) => ClcLoggingOptions.ApplyDefaults(filters));
        }
        else { builder.Services.PostConfigure<LoggerFilterOptions>(ClcLoggingOptions.ApplyDefaults); }
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
        if (builder.Services.Any(service => service.ServiceType == typeof(LoggingRegistration)))
        {
            throw new InvalidOperationException("Clc.Logging is already registered on this builder.");
        }
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
