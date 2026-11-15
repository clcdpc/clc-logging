using System.Globalization;
using Clc.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace Clc.Logging.Tests;

[TestClass]
public sealed class CustomizationTests
{
    [TestMethod]
    public void SimpleLayoutOutputIsUnchangedAndSharedByFileAndStderrFallback()
    {
        using var capture = new ErrorCapture();
        var layout = new SimpleLayout("${level} | ${logger} | ${message}");
        using var fixture = new LoggingFixture(options => options.Layout = layout);
        fixture.Logging.Logger.LogInformation("bootstrap");
        Assert.AreEqual("Info | Clc.Logging.Startup | bootstrap", capture.Lines().Single());
        Directory.CreateDirectory(fixture.Logging.DirectoryPath);
        using var blocker = new FileStream(fixture.TodayFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var host = fixture.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        logger.LogInformation("blocked {Count}", 3);
        Assert.AreEqual("Info | App | blocked 3", capture.Lines().Last());
        blocker.Dispose();
        logger.LogInformation("recovered {Count}", 4);
        CollectionAssert.AreEqual(new[] { "Info | App | recovered 4" }, fixture.Lines());
        Assert.IsFalse(capture.ToString().Contains("recovered", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ConsumerLayoutControlsSanitizationAndReceivesOnlyPermittedEvents()
    {
        using var capture = new ErrorCapture();
        var layout = new SyntheticSafeLayout();
        using var fixture = new LoggingFixture(options => options.Layout = layout);
        fixture.Logging.Logger.LogInformation("bootstrap synthetic-secret");
        StringAssert.Contains(capture.ToString(), "safe:bootstrap [removed]");
        Assert.IsFalse(capture.ToString().Contains("synthetic-secret", StringComparison.Ordinal));
        using (var host = fixture.Build())
        {
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
            logger.LogDebug("suppressed synthetic-secret");
            logger.LogInformation("configured synthetic-secret");
            Assert.AreEqual(2, layout.RenderCalls);
            CollectionAssert.AreEqual(new[] { "safe:configured [removed]" }, fixture.Lines());
            Assert.IsTrue(layout.Initializations >= 2, "NLog must initialize the reused layout for each phase.");
        }
        fixture.Logging.Logger.LogInformation("after host synthetic-secret");
        fixture.Logging.Dispose();
        Assert.AreEqual(layout.Initializations, layout.Closures);
        Assert.IsFalse(fixture.Text().Contains("synthetic-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void NativeLayoutCanReadStructuredPropertiesScopesEventIdsAndTemplates()
    {
        using var fixture = new LoggingFixture(options => options.Layout = new SimpleLayout(
            "${message:raw=true}|${event-properties:Count}|${scopeproperty:CorrelationId}|${scopenested}|${event-properties:EventId}|${event-properties:EventName}"));
        using var host = fixture.Build();
        var logger = host.Services.GetRequiredService<ILogger<CustomizationTests>>();
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = "correlation-42" });
        using var nested = logger.BeginScope("operation-scope");
        logger.LogInformation(new EventId(42, "Lookup"), "Lookup {Count}", 3);
        Assert.AreEqual("Lookup {Count}|3|correlation-42|operation-scope|42|Lookup", fixture.Lines().Single());
    }

    [TestMethod]
    public void CallerOptionsRunAtCreationAndAfterConfigurationAndAdvancedCallbackRunsOnlyOnce()
    {
        var optionsCalls = 0;
        var nativeCalls = 0;
        using var fixture = new LoggingFixture(options =>
        {
            optionsCalls++;
            if (optionsCalls == 1)
            {
                Assert.AreEqual(50 * 1024 * 1024, options.File.MaxFileSizeBytes);
                Assert.IsFalse(options.LogLevels.ContainsKey("App"));
            }
            else
            {
                Assert.AreEqual(2048, options.File.MaxFileSizeBytes);
                Assert.AreEqual(2, options.File.RetentionDays);
                Assert.AreEqual(4, options.File.MaxArchiveFiles);
                Assert.AreEqual(LogLevel.Debug, options.LogLevels["App"]);
            }
            options.Layout = new SimpleLayout("phase-layout:${message}");
            options.ConfigureNLog = configuration =>
            {
                nativeCalls++;
                var file = configuration.FindTargetByName<FileTarget>("file");
                Assert.IsNotNull(file);
                Assert.AreEqual(2048L, file.ArchiveAboveSize);
            };
        });
        Assert.AreEqual(1, optionsCalls);
        Assert.AreEqual(0, nativeCalls);
        using var host = fixture.Build(new()
        {
            ["Logging:File:MaxFileSizeBytes"] = "2048",
            ["Logging:File:RetentionDays"] = "2",
            ["Logging:File:MaxArchiveFiles"] = "4",
            ["Logging:LogLevel:App"] = "Debug"
        });
        Assert.AreEqual(2, optionsCalls);
        Assert.AreEqual(1, nativeCalls);
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App").LogDebug("configured");
        Assert.AreEqual("phase-layout:configured", fixture.Lines().Single());
    }

    [TestMethod]
    public void HostOwnedOptionsRunOnceAfterConfiguredPathsAndSettingsExist()
    {
        using var fixture = new LoggingFixture();
        var paths = ConfigurationFixture.Paths(Path.GetDirectoryName(fixture.Logging.DirectoryPath)!);
        var builder = Host.CreateApplicationBuilder();
        ConfigurationFixture.Initialize(builder, paths, """{"Logging":{"File":{"Enabled":false},"LogLevel":{"App":"Trace"}}}""");
        var calls = 0;
        builder.AddClcLogging(options =>
        {
            calls++;
            Assert.IsFalse(options.File.Enabled);
            Assert.AreEqual(LogLevel.Trace, options.LogLevels["App"]);
        });
        using var host = builder.Build();
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void HostOwnsLayoutLifetimeEvenIfItsNLogProviderIsExplicitlyRemoved()
    {
        using var fixture = new LoggingFixture();
        var paths = ConfigurationFixture.Paths(Path.GetDirectoryName(fixture.Logging.DirectoryPath)!);
        var layout = new SyntheticSafeLayout();
        var builder = fixture.Builder();
        builder.AddClcLogging(paths, options => options.Layout = layout);
        builder.Logging.ClearProviders();
        using (var host = builder.Build())
        {
            Assert.IsFalse(host.Services.GetServices<ILoggerProvider>().Any());
            Assert.AreEqual(1, layout.Initializations);
            Assert.AreEqual(0, layout.Closures);
        }
        Assert.AreEqual(1, layout.Closures);
    }

    [TestMethod]
    public void NativeCallbackSeesDefaultsBeforeActivationAndCanModifyTargetsAndRules()
    {
        var global = NLog.LogManager.Configuration;
        var additional = new MemoryTarget("additional") { Layout = "extra:${message}" };
        LoggingConfiguration? final = null;
        using var fixture = new LoggingFixture(options => options.ConfigureNLog = configuration =>
        {
            final = configuration;
            Assert.AreNotSame(configuration, configuration.LogFactory.Configuration);
            var file = configuration.FindTargetByName<FileTarget>("file");
            var stderr = configuration.FindTargetByName<ConsoleTarget>("stderr");
            Assert.IsNotNull(file);
            Assert.IsNotNull(stderr);
            Assert.AreSame(file.Layout, stderr.Layout);
            Assert.IsInstanceOfType<SimpleLayout>(file.Layout);
            Assert.AreEqual(1, configuration.LoggingRules.Count);
            file.Layout = new SimpleLayout("modified:${message}");
            configuration.LoggingRules[0].SetLoggingLevels(NLog.LogLevel.Error, NLog.LogLevel.Fatal);
            configuration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, additional);
        });
        using var host = fixture.Build();
        Assert.IsNotNull(final);
        Assert.AreSame(final, final.LogFactory.Configuration);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App");
        logger.LogInformation("info");
        logger.LogError("error");
        CollectionAssert.AreEqual(new[] { "modified:error" }, fixture.Lines());
        CollectionAssert.AreEqual(new[] { "extra:info", "extra:error" }, additional.Logs.ToArray());
        Assert.AreSame(global, NLog.LogManager.Configuration);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeCallbackCanReplaceAllConventionalTargetsIncludingWithFilesDisabled(bool disabled)
    {
        using var capture = new ErrorCapture();
        var replacement = new MemoryTarget("replacement") { Layout = "replacement:${message}" };
        using var fixture = new LoggingFixture(options =>
        {
            options.File.Enabled = !disabled;
            options.ConfigureNLog = configuration =>
            {
                configuration.LoggingRules.Clear();
                foreach (var target in configuration.AllTargets.ToArray()) { configuration.RemoveTarget(target.Name); }
                configuration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, replacement);
            };
        });
        using var host = fixture.Build();
        host.Services.GetRequiredService<ILogger<CustomizationTests>>().LogInformation("custom only");
        CollectionAssert.AreEqual(new[] { "replacement:custom only" }, replacement.Logs.ToArray());
        Assert.IsFalse(Directory.Exists(fixture.Logging.DirectoryPath));
        Assert.AreEqual("", capture.ToString());
    }

    [TestMethod]
    public void DailyFilenamesAndTimestampOffsetsFollowNativeEventTimestamps()
    {
        LoggingConfiguration? configuration = null;
        using var fixture = new LoggingFixture(options => options.ConfigureNLog = value => configuration = value);
        using var host = fixture.Build();
        Assert.IsNotNull(configuration);
        var file = configuration.FindTargetByName<FileTarget>("file");
        Assert.IsNotNull(file);
        Assert.IsFalse(file.Encoding.GetPreamble().Any());
        Assert.AreEqual(50 * 1024 * 1024L, file.ArchiveAboveSize);
        Assert.AreEqual(30, file.MaxArchiveDays);
        Assert.AreEqual(300, file.MaxArchiveFiles);
        var logger = configuration.LogFactory.GetLogger("Native.App");
        var yesterday = DateTime.Today.AddDays(-1).AddHours(12);
        logger.Log(new NLog.LogEventInfo(NLog.LogLevel.Info, "Native.App", "yesterday") { TimeStamp = yesterday });
        logger.Log(new NLog.LogEventInfo(NLog.LogLevel.Info, "Native.App", "today") { TimeStamp = DateTime.Today.AddHours(12) });
        var yesterdayFile = Path.Combine(fixture.Logging.DirectoryPath, $"test-app-{yesterday:yyyy-MM-dd}.log");
        StringAssert.Contains(File.ReadAllText(yesterdayFile), "yesterday");
        StringAssert.Contains(fixture.Text(), "today");
        Assert.IsTrue(File.Exists(fixture.TodayFile));
        foreach (var utc in new[]
        {
            new DateTime(2026, 3, 8, 6, 59, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 8, 7, 1, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc)
        })
        {
            var entry = new NLog.LogEventInfo(NLog.LogLevel.Info, "Native.App", "test") { TimeStamp = utc };
            var rendered = file.Layout.Render(entry);
            var timestamp = DateTimeOffset.ParseExact(rendered[..30], "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            Assert.AreEqual(utc, timestamp.UtcDateTime);
            Assert.AreEqual(TimeZoneInfo.Local.GetUtcOffset(utc), timestamp.Offset);
        }
    }

    // Test-only consumer policy; Clc.Logging knows nothing about this transformation.
    private sealed class SyntheticSafeLayout : Layout
    {
        public int Initializations { get; private set; }
        public int Closures { get; private set; }
        public int RenderCalls { get; private set; }
        private bool _initialized;
        protected override void InitializeLayout()
        {
            base.InitializeLayout();
            _initialized = true;
            Initializations++;
        }
        protected override string GetFormattedMessage(NLog.LogEventInfo logEvent)
        {
            Assert.IsTrue(_initialized);
            RenderCalls++;
            return "safe:" + logEvent.FormattedMessage.Replace("synthetic-secret", "[removed]", StringComparison.Ordinal);
        }
        protected override void CloseLayout()
        {
            _initialized = false;
            Closures++;
            base.CloseLayout();
        }
    }
}
