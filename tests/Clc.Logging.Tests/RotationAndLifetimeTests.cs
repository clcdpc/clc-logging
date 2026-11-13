using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Clc.Logging.Tests;

[TestClass]
public sealed class RotationAndLifetimeTests
{
    [TestMethod]
    public void SizeRolloverPreservesAllEventsAndAppliesFileCap()
    {
        using var fixture = new LoggingFixture();
        using var host = fixture.Build(new() { ["Logging:File:MaxFileSizeBytes"] = "1024", ["Logging:File:MaxArchiveFiles"] = "3" });
        var logger = host.Services.GetRequiredService<ILogger<RotationAndLifetimeTests>>();
        for (var i = 0; i < 30; i++) { logger.LogInformation("Event {Index} {Payload}", i, new string('x', 600)); }
        var files = Directory.GetFiles(fixture.Logging.DirectoryPath, "*.jsonl");
        Assert.IsTrue(files.Length is >= 2 and <= 4, $"Expected capped rollover files, found {files.Length}.");
        var records = fixture.Records();
        Assert.IsTrue(records.Length < 30);
        Assert.IsTrue(records.Any(record => record.GetProperty("properties").GetProperty("Index").GetInt32() == 29));
    }

    [TestMethod]
    public void SizeRolloverWithoutRetentionLossPreservesEveryEvent()
    {
        using var fixture = new LoggingFixture();
        using var host = fixture.Build(new() { ["Logging:File:MaxFileSizeBytes"] = "1024" });
        var logger = host.Services.GetRequiredService<ILogger<RotationAndLifetimeTests>>();
        for (var i = 0; i < 12; i++) { logger.LogInformation("Event {Index} {Payload}", i, new string('x', 700)); }
        Assert.IsTrue(Directory.GetFiles(fixture.Logging.DirectoryPath, "*.jsonl").Length > 1);
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 12).ToArray(), fixture.Records().Select(record => record.GetProperty("properties").GetProperty("Index").GetInt32()).ToArray());
    }

    [TestMethod]
    public void RetentionRemovesOldApplicationFilesAndPreservesUnrelatedFiles()
    {
        using var fixture = new LoggingFixture();
        Directory.CreateDirectory(fixture.Logging.DirectoryPath);
        var old = Path.Combine(fixture.Logging.DirectoryPath, $"test-app-{DateTime.Today.AddDays(-40):yyyy-MM-dd}.001.jsonl");
        File.WriteAllText(old, "old archive");
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));
        var oldDaily = Path.Combine(fixture.Logging.DirectoryPath, $"test-app-{DateTime.Today.AddDays(-41):yyyy-MM-dd}.jsonl");
        File.WriteAllText(oldDaily, "old daily file");
        File.SetCreationTimeUtc(oldDaily, DateTime.UtcNow.AddDays(-41));
        File.SetLastWriteTimeUtc(oldDaily, DateTime.UtcNow.AddDays(-41));
        var unrelated = Path.Combine(fixture.Logging.DirectoryPath, "other-app-2000-01-01.jsonl");
        File.WriteAllText(unrelated, "unrelated");
        File.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow.AddDays(-40));
        using var host = fixture.Build();
        host.Services.GetRequiredService<ILogger<RotationAndLifetimeTests>>().LogInformation("current");
        Assert.IsFalse(File.Exists(old), "NLog should remove the expired application archive.");
        Assert.IsFalse(File.Exists(oldDaily), "NLog should remove the expired daily application file.");
        Assert.AreEqual("unrelated", File.ReadAllText(unrelated));
        Assert.IsTrue(File.Exists(fixture.TodayFile));
    }

    [TestMethod]
    public async Task HostShutdownAndServiceDisposalCanStillLogAndOuterDisposalIsIdempotent()
    {
        using var fixture = new LoggingFixture();
        var builder = fixture.Builder();
        builder.AddClcLogging(fixture.Logging);
        builder.Services.AddHostedService<ShutdownService>();
        using (var host = builder.Build())
        {
            await host.StartAsync();
            await host.StopAsync();
        }
        fixture.Logging.Logger.LogInformation("after host disposal");
        fixture.Logging.Dispose();
        fixture.Logging.Dispose();
        var text = File.ReadAllText(fixture.TodayFile);
        StringAssert.Contains(text, "service start");
        StringAssert.Contains(text, "service stop");
        StringAssert.Contains(text, "service dispose");
        StringAssert.Contains(text, "after host disposal");
    }

    [TestMethod]
    public void HostInstancesOwnIndependentFactoriesAndNeverConfigureGlobalNLog()
    {
        var global = NLog.LogManager.Configuration;
        using var first = new LoggingFixture();
        using var second = new LoggingFixture();
        using var firstHost = first.Build();
        using var secondHost = second.Build();
        firstHost.Services.GetRequiredService<ILogger<RotationAndLifetimeTests>>().LogInformation("first");
        secondHost.Services.GetRequiredService<ILogger<RotationAndLifetimeTests>>().LogInformation("second-before");
        firstHost.Dispose();
        first.Logging.Dispose();
        secondHost.Services.GetRequiredService<ILogger<RotationAndLifetimeTests>>().LogInformation("second-after");
        CollectionAssert.AreEqual(new[] { "second-before", "second-after" }, second.Messages());
        Assert.AreSame(global, NLog.LogManager.Configuration);
    }

    [TestMethod]
    public void BuilderOwnedInstanceIsDisposedWithItsHostAndFiltersCanBeAddedAfterSetup()
    {
        using var capture = new ErrorCapture();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:File:Enabled"] = "false" });
        builder.AddClcLogging("clc-logging-owned-test");
        builder.Logging.AddFilter("Suppressed", LogLevel.None);
        var host = builder.Build();
        var instance = host.Services.GetRequiredService<ClcLogging>();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Suppressed.Child").LogCritical("hidden");
        factory.CreateLogger("App").LogInformation("visible");
        host.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => instance.LogFatal(new InvalidOperationException()));
        Assert.AreEqual(1, capture.Lines().Length);
        StringAssert.Contains(capture.ToString(), "visible");
    }

    [TestMethod]
    public async Task FatalHandlerDoesNotRepeatAnExceptionAlreadyReportedByTheHost()
    {
        using var fixture = new LoggingFixture();
        var builder = fixture.Builder();
        builder.AddClcLogging(fixture.Logging);
        builder.Services.AddHostedService<FailingService>();
        using var host = builder.Build();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.StartAsync());
        fixture.Logging.LogFatal(error);
        fixture.Logging.LogFatal(error);
        var records = fixture.Records().Where(record => record.GetProperty("exception").ValueKind != System.Text.Json.JsonValueKind.Null).ToArray();
        Assert.AreEqual(1, records.Length);
        StringAssert.Contains(records[0].GetProperty("exception").GetString(), "synthetic startup failure");
    }

    [TestMethod]
    public void ExistingDirectoryNamesAreLiteralAndConfigurationIsAStartupSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Clc.Logging.Tests", Guid.NewGuid().ToString("N"));
        var application = Path.Combine(root, "literal-${level}");
        try
        {
            using var logging = ClcLogging.CreateForDirectory(application);
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:LogLevel:App"] = "Debug" });
            builder.AddClcLogging(logging);
            using var host = builder.Build();
            builder.Configuration["Logging:LogLevel:App"] = "None";
            ((IConfigurationRoot)builder.Configuration).Reload();
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App").LogDebug("still enabled");
            host.Dispose();
            logging.Dispose();
            var path = Directory.GetFiles(Path.Combine(application, "Logs")).Single();
            StringAssert.StartsWith(Path.GetFileName(path), "literal-${level}-");
            StringAssert.Contains(File.ReadAllText(path), "still enabled");
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private sealed class FailingService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("synthetic startup failure");
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ShutdownService(ILogger<ShutdownService> logger) : IHostedService, IDisposable
    {
        public Task StartAsync(CancellationToken cancellationToken) { logger.LogInformation("service start"); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { logger.LogInformation("service stop"); return Task.CompletedTask; }
        public void Dispose() => logger.LogInformation("service dispose");
    }
}
