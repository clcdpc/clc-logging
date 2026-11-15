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
            Assert.ThrowsExactly<InvalidOperationException>(() => ClcLogging.Create("clc-invalid-options", configure));
            var builder = Host.CreateApplicationBuilder();
            Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddClcLogging("clc-invalid-options", configure));
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
        StringAssert.EndsWith(capture.Lines().Single(), "[ERROR] Clc.Logging.Tests.ConfigurationAndFailureTests - still running 12");
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
        StringAssert.Contains(capture.ToString(), "blocked");
        blocker.Dispose();
        logger.LogInformation("recovered");
        CollectionAssert.AreEqual(new[] { "recovered" }, fixture.Messages());
        Assert.IsFalse(capture.ToString().Contains("recovered", StringComparison.Ordinal));
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
        StringAssert.Contains(capture.ToString(), "[INFO] Clc.Logging.Startup - bootstrap");
        StringAssert.Contains(capture.ToString(), "[FATAL] Clc.Logging.Startup");
        StringAssert.Contains(capture.ToString(), "Logging:File:Enabled");
        ClcLogging.WriteStartupFailure(new InvalidOperationException("before creation"));
        StringAssert.Contains(capture.ToString(), "before logging was available");
        StringAssert.Contains(capture.ToString(), "before creation");
    }

}
