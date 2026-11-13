using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clc.Logging.Tests;

[TestClass]
public sealed class ConfigurationAndFailureTests
{
    [TestMethod]
    [DataRow("Logging:File:Enabled", "perhaps")]
    [DataRow("Logging:File:Enabled", null)]
    [DataRow("Logging:File:MaxFileSizeBytes", "50MiB")]
    [DataRow("Logging:File:MaxFileSizeBytes", "1023")]
    [DataRow("Logging:File:MaxFileSizeBytes", "1073741825")]
    [DataRow("Logging:File:RetentionDays", "0")]
    [DataRow("Logging:File:RetentionDays", "3651")]
    [DataRow("Logging:File:RetentionDays", "1.5")]
    [DataRow("Logging:File:RetentionDays", "99999999999999999999")]
    [DataRow("Logging:File:MaxArchiveFiles", "0")]
    [DataRow("Logging:File:MaxArchiveFiles", "10001")]
    [DataRow("Logging:File:Rotation", "Daily")]
    [DataRow("Logging:File", "true")]
    [DataRow("Logging:File:Enabled:Nested", "true")]
    [DataRow("Logging:LogLevel:Default", "Verbose")]
    [DataRow("Logging:LogLevel:Default", "2")]
    [DataRow("Logging:LogLevel:Default", "Information,Warning")]
    [DataRow("Logging:LogLevel:Default", null)]
    [DataRow("Logging:LogLevel", "Warning")]
    [DataRow("Logging:LogLevel:App:*", "Debug")]
    [DataRow("Logging:LogLevel:App*", "Debug")]
    public void InvalidSettingsFailClearlyBeforeFileCreation(string key, string? value)
    {
        using var fixture = new LoggingFixture();
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Build(new() { [key] = value }));
        StringAssert.Contains(exception.Message, "Logging:");
        Assert.IsFalse(Directory.Exists(fixture.Logging.DirectoryPath));
    }

    [TestMethod]
    public void ValidSettingsAndCallbackOverridesAreApplied()
    {
        using var fixture = new LoggingFixture(options => options.LogLevels["App"] = LogLevel.Debug);
        using var host = fixture.Build(new()
        {
            ["Logging:File:Enabled"] = "TRUE",
            ["Logging:File:MaxFileSizeBytes"] = "2048",
            ["Logging:File:RetentionDays"] = "1",
            ["Logging:File:MaxArchiveFiles"] = "3",
            ["Logging:LogLevel:Default"] = "error",
            ["Logging:LogLevel:App"] = "Warning"
        });
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Other").LogWarning("hidden");
        factory.CreateLogger("App.Child").LogDebug("visible");
        CollectionAssert.AreEqual(new[] { "visible" }, fixture.Messages());
    }

    [TestMethod]
    public void InvalidCallbackSettingsFailClearly()
    {
        Action<ClcLoggingOptions>[] invalid =
        [
            options => options.File.MaxFileSizeBytes = -1,
            options => options.File.RetentionDays = 0,
            options => options.File.MaxArchiveFiles = 0,
            options => options.FlushTimeout = TimeSpan.Zero,
            options => options.FlushTimeout = TimeSpan.FromMinutes(2),
            options => options.LogLevels["App"] = (LogLevel)100
        ];
        foreach (var configure in invalid)
        {
            using var fixture = new LoggingFixture(configure);
            Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Build());
        }
    }

    [TestMethod]
    public void FileUnavailableFallsBackToStderrWithTheOriginalEvent()
    {
        using var capture = new ErrorCapture();
        using var fixture = new LoggingFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Logging.DirectoryPath)!);
        File.WriteAllText(fixture.Logging.DirectoryPath, "blocking file preserved");
        using (var host = fixture.Build())
        {
            host.Services.GetRequiredService<ILogger<ConfigurationAndFailureTests>>().LogError(new EventId(7, "Unavailable"), "still running {Value}", 12);
        }
        StringAssert.Contains(capture.ToString(), "Clc.Logging: file logging unavailable");
        var record = JsonDocument.Parse(capture.Lines().Single(line => line.StartsWith('{'))).RootElement;
        Assert.AreEqual("still running 12", record.GetProperty("message").GetString());
        Assert.AreEqual(7, record.GetProperty("eventId").GetInt32());
        Assert.AreEqual("blocking file preserved", File.ReadAllText(fixture.Logging.DirectoryPath));
    }

    [TestMethod]
    public void LockedFileFallsBackAndCanRecover()
    {
        using var capture = new ErrorCapture();
        using var fixture = new LoggingFixture();
        Directory.CreateDirectory(fixture.Logging.DirectoryPath);
        using var blocker = new FileStream(fixture.TodayFile, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var host = fixture.Build();
        var logger = host.Services.GetRequiredService<ILogger<ConfigurationAndFailureTests>>();
        logger.LogInformation("blocked");
        StringAssert.Contains(capture.ToString(), "file logging unavailable");
        blocker.Dispose();
        logger.LogInformation("recovered");
        CollectionAssert.AreEqual(new[] { "recovered" }, fixture.Messages());
    }

    [TestMethod]
    public void BootstrapAndConstructionFailuresHaveOneUsefulOutputPath()
    {
        using var capture = new ErrorCapture();
        using var fixture = new LoggingFixture();
        fixture.Logging.Logger.LogInformation("bootstrap");
        try
        {
            fixture.Build(new() { ["Logging:File:Enabled"] = "invalid" });
            Assert.Fail("Expected invalid configuration.");
        }
        catch (InvalidOperationException exception) { fixture.Logging.LogFatal(exception); }
        var records = capture.Lines().Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        Assert.AreEqual(2, records.Length);
        Assert.AreEqual("bootstrap", records[0].GetProperty("message").GetString());
        Assert.AreEqual("Fatal", records[1].GetProperty("level").GetString());
        StringAssert.Contains(records[1].GetProperty("exception").GetString(), "Logging:File:Enabled");
        ClcLogging.WriteStartupFailure(new InvalidOperationException("before creation"));
        StringAssert.Contains(capture.ToString(), "before logging was available");
        StringAssert.Contains(capture.ToString(), "before creation");
    }

    [TestMethod]
    public void ApplicationPropertyPolicyAndTextTransformationApplyWithoutReflectingObjects()
    {
        using var fixture = new LoggingFixture(options =>
        {
            options.IncludeProperty = key => key != "Secret";
            options.TransformText = value => value.Replace("sensitive", "[removed]", StringComparison.Ordinal);
        });
        using var host = fixture.Build();
        var logger = host.Services.GetRequiredService<ILogger<ConfigurationAndFailureTests>>();
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["Secret"] = "scope-secret", ["Allowed"] = "sensitive" });
        logger.LogError(new InvalidOperationException("sensitive"), "sensitive {Allowed} {Secret} {Object}", "sensitive", "event-secret", new ObjectWithPrivateData());
        var record = fixture.Records().Single();
        Assert.IsFalse(record.GetProperty("scope").TryGetProperty("Secret", out _));
        Assert.IsFalse(record.GetProperty("properties").TryGetProperty("Secret", out _));
        Assert.AreEqual("[removed]", record.GetProperty("scope").GetProperty("Allowed").GetString());
        Assert.AreEqual("object-summary", record.GetProperty("properties").GetProperty("Object").GetString());
        Assert.IsFalse(record.GetRawText().Contains("sensitive", StringComparison.Ordinal));
        // Property filtering does not redact already formatted message text.
        StringAssert.Contains(record.GetProperty("message").GetString(), "event-secret");
    }

    [TestMethod]
    public void FormattingFailuresStillProduceValidDiagnosticJson()
    {
        using var fixture = new LoggingFixture(options => options.TransformText = _ => throw new InvalidOperationException("secret failure"));
        using var host = fixture.Build();
        host.Services.GetRequiredService<ILogger<ConfigurationAndFailureTests>>().LogInformation("test");
        var record = fixture.Records().Single();
        Assert.AreEqual("System.InvalidOperationException", record.GetProperty("formattingError").GetString());
        StringAssert.Contains(record.GetProperty("message").GetString(), "formatting failed");
        Assert.IsFalse(record.GetRawText().Contains("secret failure", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TimestampOffsetsFollowEachEventAcrossDaylightSavingTransitions()
    {
        var layout = new ClcJsonLayout(new ClcLoggingOptions());
        foreach (var utc in new[] { new DateTime(2026, 3, 8, 6, 59, 0, DateTimeKind.Utc), new DateTime(2026, 3, 8, 7, 1, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 5, 30, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 6, 30, 0, DateTimeKind.Utc) })
        {
            var entry = new NLog.LogEventInfo(NLog.LogLevel.Info, "test", "test") { TimeStamp = utc };
            var record = JsonDocument.Parse(layout.Render(entry)).RootElement;
            var timestamp = DateTimeOffset.ParseExact(record.GetProperty("timestamp").GetString()!, "O", CultureInfo.InvariantCulture);
            Assert.AreEqual(utc, timestamp.UtcDateTime);
            Assert.AreEqual(TimeZoneInfo.Local.GetUtcOffset(utc), timestamp.Offset);
        }
    }

    private sealed class ObjectWithPrivateData
    {
        public string PrivateData => throw new AssertFailedException("Application objects must not be reflected.");
        public override string ToString() => "object-summary";
    }
}
