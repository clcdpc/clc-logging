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

    private ClcLogging(string applicationDataDirectory, Action<ClcLoggingOptions>? configure)
    {
        ApplicationName = Path.GetFileName(Path.TrimEndingDirectorySeparator(applicationDataDirectory));
        if (string.IsNullOrWhiteSpace(ApplicationName)) { throw new ArgumentException("An application data directory with a name is required.", nameof(applicationDataDirectory)); }
        DirectoryPath = Path.Combine(applicationDataDirectory, "Logs");
        _configure = configure;
        try
        {
            // Bootstrap always uses stderr, so settings/retention are authoritative only
            // after the application's configuration has been loaded.
            var options = new ClcLoggingOptions();
            ConfigureTargets(options, bootstrap: true);
            _bootstrap = CreateLoggerFactory(options);
        }
        catch
        {
            _factory.Dispose();
            throw;
        }
    }

    public static ClcLogging Create(string applicationDirectoryName, Action<ClcLoggingOptions>? configure = null) =>
        Create(ClcApplicationPaths.Resolve(applicationDirectoryName), configure);

    public static ClcLogging Create(ClcApplicationPaths paths, Action<ClcLoggingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new ClcLogging(paths.ApplicationDataDirectory, configure);
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
        if (_reportedExceptions.TryGetValue(exception, out _)) { return; }
        _factory.GetLogger("Clc.Logging.Startup").Fatal(exception, "Application startup or execution failed.");
        Flush();
    }

    /// <summary>Last-resort output when creation/path resolution itself fails.</summary>
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
        _bootstrap = CreateLoggerFactory(options);
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

    private ILoggerFactory CreateLoggerFactory(ClcLoggingOptions options) => LoggerFactory.Create(builder =>
    {
        builder.Services.Configure<LoggerFilterOptions>(options.ApplyFilters);
        builder.AddProvider(CreateProvider());
    });

    private void ConfigureTargets(ClcLoggingOptions options, bool bootstrap = false)
    {
        Flush();
        var configuration = new LoggingConfiguration(_factory);
        var layout = new ClcJsonLayout(options, error => _reportedExceptions.GetValue(error, _ => new object()));
        Target target = new ProcessOutputTarget(layout) { Name = "stderr" };
        if (!bootstrap && options.File.Enabled)
        {
            var file = new FileTarget("file")
            {
                // A literal layout component prevents ${...} in an existing path/name
                // from being interpreted as an NLog renderer.
                FileName = new CompoundLayout
                {
                    Layouts = { Layout.FromLiteral(Path.Combine(DirectoryPath, ApplicationName + "-")), new SimpleLayout("${shortdate}.jsonl") }
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
            var fallback = new FallbackGroupTarget("file-with-stderr-fallback") { ReturnToFirstOnSuccess = true };
            fallback.Targets.Add(file);
            fallback.Targets.Add(new ProcessOutputTarget(layout, fileFailed: true) { Name = "file-failure-stderr" });
            target = fallback;
        }
        configuration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, target);
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
