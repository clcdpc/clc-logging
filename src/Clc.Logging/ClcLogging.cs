using Clc.Configuration;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Config;
using NLog.Extensions.Logging;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Wrappers;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Clc.Logging;

/// <summary>Owns one logging instance. Create before host construction; dispose after the host.</summary>
public sealed class ClcLogging : IDisposable
{
    private readonly LogFactory _factory = new() { AutoShutdown = false };
    private readonly Action<ClcLoggingOptions>? _configure;
    private readonly ConditionalWeakTable<Exception, object> _reportedExceptions = new();
    private ILoggerFactory _bootstrap = null!;
    private TimeSpan _flushTimeout = TimeSpan.FromSeconds(5);
    private int _disposed;
    private bool _registered;

    public string ApplicationName { get; }
    public string DirectoryPath { get; }
    public ILogger Logger => _bootstrap.CreateLogger("Clc.Logging.Startup");

    private ClcLogging(string applicationDataDirectory, Action<ClcLoggingOptions>? configure, bool callerOwned = true)
    {
        ApplicationName = Path.GetFileName(Path.TrimEndingDirectorySeparator(applicationDataDirectory));
        if (string.IsNullOrWhiteSpace(ApplicationName)) { throw new ArgumentException("An application data directory with a name is required.", nameof(applicationDataDirectory)); }
        DirectoryPath = Path.Combine(applicationDataDirectory, "Logs");
        _configure = configure;
        try
        {
            // Bootstrap always uses stderr, so settings/retention are authoritative only
            // after the application's configuration has been loaded.
            var options = callerOwned ? ClcLoggingOptions.Read(null, configure) : new ClcLoggingOptions();
            ConfigureTargets(options, bootstrap: true);
            _bootstrap = CreateLoggerFactory(options);
            _flushTimeout = options.FlushTimeout;
        }
        catch
        {
            _factory.Dispose();
            throw;
        }
    }

    /// <summary>Creates caller-owned stderr bootstrap logging.</summary>
    /// <remarks>The options callback runs now and again with configuration-derived settings at registration.</remarks>
    public static ClcLogging Create(string applicationDirectoryName, Action<ClcLoggingOptions>? configure = null) =>
        Create(ClcApplicationPaths.Resolve(applicationDirectoryName), configure);

    public static ClcLogging Create(ClcApplicationPaths paths, Action<ClcLoggingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new ClcLogging(paths.ApplicationDataDirectory, configure);
    }

    internal static ClcLogging CreateOwned(ClcApplicationPaths paths, Action<ClcLoggingOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new(paths.ApplicationDataDirectory, configure, callerOwned: false);
    }

    /// <summary>Uses an existing, absolute application data directory; its final segment is the application identity.</summary>
    public static ClcLogging CreateForDirectory(string applicationDataDirectory, Action<ClcLoggingOptions>? configure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataDirectory);
        if (!Path.IsPathFullyQualified(applicationDataDirectory)) { throw new ArgumentException("The application data directory must be absolute.", nameof(applicationDataDirectory)); }
        return new ClcLogging(Path.GetFullPath(applicationDataDirectory), configure);
    }

    /// <summary>Records one caller-owned fatal failure, even if host filters disable logging.</summary>
    public void LogFatal(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_reportedExceptions)
        {
            if (_reportedExceptions.TryGetValue(exception, out _)) { return; }
            _reportedExceptions.Add(exception, new object());
        }
        _factory.GetLogger("Clc.Logging.Startup").Fatal(exception, "Application startup or execution failed.");
        Flush();
    }

    /// <summary>Caller-controlled last resort: writes raw exception text without a custom layout or sanitization.</summary>
    public static void WriteStartupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Console.Error.WriteLine("Clc.Logging: application startup failed before logging was available.");
        Console.Error.WriteLine(exception);
    }

    public void Flush()
    {
        if (Volatile.Read(ref _disposed) == 0) { _factory.Flush(_flushTimeout); }
    }

    internal ClcLoggingOptions Register(IConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_registered) { throw new InvalidOperationException("This ClcLogging instance is already registered with a host."); }
        var options = ClcLoggingOptions.Read(configuration, _configure);
        ConfigureTargets(options);
        _bootstrap.Dispose();
        _bootstrap = CreateLoggerFactory(options, configuration);
        _flushTimeout = options.FlushTimeout;
        _registered = true;
        return options;
    }

    internal NLogLoggerProvider CreateProvider() => new(new NLogProviderOptions
    {
        IncludeScopes = true,
        CaptureMessageTemplates = true,
        CaptureMessageProperties = true,
        CaptureEventId = EventIdCaptureType.EventId | EventIdCaptureType.EventName,
        IgnoreEmptyEventId = false,
        RemoveLoggerFactoryFilter = false,
        ShutdownOnDispose = false,
        AutoShutdown = false,
        RegisterServiceProvider = false,
        LoggingConfigurationSectionName = ""
    }, _factory);

    private ILoggerFactory CreateLoggerFactory(ClcLoggingOptions options, IConfiguration? configuration = null) => LoggerFactory.Create(builder =>
    {
        if (configuration is not null) { builder.AddConfiguration(configuration.GetSection("Logging")); }
        builder.Services.Configure<LoggerFilterOptions>(options.ApplyOverrides);
        builder.Services.PostConfigure<LoggerFilterOptions>(ClcLoggingOptions.ApplyDefaults);
        builder.AddProvider(CreateProvider());
    });

    private void ConfigureTargets(ClcLoggingOptions options, bool bootstrap = false)
    {
        Flush();
        var configuration = new LoggingConfiguration(_factory);
        var layout = options.Layout ?? new SimpleLayout(
            @"${date:universalTime=false:format=yyyy-MM-dd HH\:mm\:ss.fff zzz} [${level:uppercase=true}] ${logger} - ${message}${onexception:inner=${newline}${exception:format=tostring}}");
        var stderr = new ConsoleTarget("stderr")
        {
            Layout = layout,
            StdErr = true,
            DetectConsoleAvailable = false,
            ForceWriteLine = true,
            AutoFlush = true
        };
        configuration.AddTarget(stderr);
        Target target = stderr;
        if (!bootstrap && options.File.Enabled)
        {
            var file = new FileTarget("file")
            {
                // A literal layout component prevents ${...} in an existing path/name
                // from being interpreted as an NLog renderer.
                FileName = new CompoundLayout
                {
                    Layouts = { Layout.FromLiteral(Path.Combine(DirectoryPath, ApplicationName + "-")), new SimpleLayout("${shortdate}.log") }
                },
                Layout = layout,
                Encoding = new System.Text.UTF8Encoding(false),
                ArchiveAboveSize = options.File.MaxFileSizeBytes,
                ArchiveSuffixFormat = ".{0:000}",
                MaxArchiveDays = options.File.RetentionDays,
                MaxArchiveFiles = options.File.MaxArchiveFiles,
                ArchiveOldFileOnStartup = false,
                DeleteOldFileOnStartup = false,
                ReplaceFileContentsOnEachWrite = false,
                AutoFlush = true,
                CreateDirs = true,
                KeepFileOpen = true,
                OpenFileCacheSize = 1
            };
            configuration.AddTarget(file);
            var fallback = new FallbackGroupTarget("file-with-stderr-fallback") { ReturnToFirstOnSuccess = true };
            fallback.Targets.Add(file);
            fallback.Targets.Add(stderr);
            target = fallback;
        }
        configuration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target);
        if (!bootstrap) { options.ConfigureNLog?.Invoke(configuration); }
        _factory.Configuration = configuration;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        try { _factory.Flush(_flushTimeout); }
        finally
        {
            _bootstrap.Dispose();
            _factory.Dispose();
        }
    }
}
