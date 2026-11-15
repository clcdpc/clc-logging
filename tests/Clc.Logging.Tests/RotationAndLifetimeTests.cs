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
        var files = Directory.GetFiles(fixture.Logging.DirectoryPath, "*.log");
        Assert.IsTrue(files.Length is >= 2 and <= 4, $"Expected capped rollover files, found {files.Length}.");
        var records = fixture.Messages();
        Assert.IsTrue(records.Length < 30);
        Assert.IsTrue(records.Any(record => record.StartsWith("Event 29 ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SizeRolloverWithoutRetentionLossPreservesEveryEvent()
    {
        using var fixture = new LoggingFixture();
        using var host = fixture.Build(new() { ["Logging:File:MaxFileSizeBytes"] = "1024" });
        var logger = host.Services.GetRequiredService<ILogger<RotationAndLifetimeTests>>();
        for (var i = 0; i < 12; i++) { logger.LogInformation("Event {Index} {Payload}", i, new string('x', 700)); }
        var files = Directory.GetFiles(fixture.Logging.DirectoryPath, "*.log");
        Assert.IsTrue(files.Length > 1);
        Assert.IsTrue(files.Any(path => System.Text.RegularExpressions.Regex.IsMatch(path, @"\.\d{3}\.log$")));
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 12).ToArray(), fixture.Messages().Select(record => int.Parse(record.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture)).ToArray());
    }

    [TestMethod]
    public void RetentionRemovesOldApplicationFilesAndPreservesUnrelatedFiles()
    {
        using var fixture = new LoggingFixture();
        Directory.CreateDirectory(fixture.Logging.DirectoryPath);
        var old = Path.Combine(fixture.Logging.DirectoryPath, $"test-app-{DateTime.Today.AddDays(-40):yyyy-MM-dd}.001.log");
        File.WriteAllText(old, "old archive");
        File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));
        var oldDaily = Path.Combine(fixture.Logging.DirectoryPath, $"test-app-{DateTime.Today.AddDays(-41):yyyy-MM-dd}.log");
        File.WriteAllText(oldDaily, "old daily file");
        File.SetCreationTimeUtc(oldDaily, DateTime.UtcNow.AddDays(-41));
        File.SetLastWriteTimeUtc(oldDaily, DateTime.UtcNow.AddDays(-41));
        var unrelated = Path.Combine(fixture.Logging.DirectoryPath, "other-app-2000-01-01.log");
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
        fixture.Logging.Flush();
        fixture.Logging.Flush();
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
    public async Task ConcurrentHostsWriteOnlyToTheirOwnFactories()
    {
        using var first = new LoggingFixture();
        using var second = new LoggingFixture();
        await Task.WhenAll(WriteEvents(first, "first"), WriteEvents(second, "second"));
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 100).Select(i => $"first {i}").ToArray(), first.Messages());
        CollectionAssert.AreEquivalent(Enumerable.Range(0, 100).Select(i => $"second {i}").ToArray(), second.Messages());

        static Task WriteEvents(LoggingFixture fixture, string name) => Task.Run(() =>
        {
            using var host = fixture.Build();
            var logger = host.Services.GetRequiredService<ILogger<RotationAndLifetimeTests>>();
            for (var i = 0; i < 100; i++) { logger.LogInformation("{Name} {Index}", name, i); }
        });
    }

    [TestMethod]
    public void FatalLoggingUsesCustomLayoutDespiteDisabledMicrosoftFiltersAndSuppressesRepeatedCalls()
    {
        using var fixture = new LoggingFixture(options => options.Layout = new NLog.Layouts.SimpleLayout("${level}|${message}|${exception:format=message}"));
        using var host = fixture.Build(new() { ["Logging:LogLevel:Default"] = "None" });
        host.Services.GetRequiredService<ILogger<RotationAndLifetimeTests>>().LogCritical("hidden");
        var error = new InvalidOperationException("fatal detail");
        Parallel.For(0, 10, _ => fixture.Logging.LogFatal(error));
        fixture.Logging.LogFatal(new InvalidOperationException("another detail"));
        fixture.Logging.Flush();
        fixture.Logging.Flush();
        CollectionAssert.AreEqual(new[]
        {
            "Fatal|Application startup or execution failed.|fatal detail",
            "Fatal|Application startup or execution failed.|another detail"
        }, fixture.Lines());
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
    public async Task FatalHandlerSuppressesOnlyRepeatedFatalCallsOnTheSameException()
    {
        using var fixture = new LoggingFixture();
        var builder = fixture.Builder();
        builder.AddClcLogging(fixture.Logging);
        builder.Services.AddHostedService<FailingService>();
        using var host = builder.Build();
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.StartAsync());
        fixture.Logging.LogFatal(error);
        fixture.Logging.LogFatal(error);
        var text = fixture.Text();
        Assert.AreEqual(1, fixture.Lines().Count(line => line.Contains("[FATAL]", StringComparison.Ordinal)));
        // The host's earlier error remains an independent event.
        Assert.AreEqual(2, fixture.Lines().Count(line => line.Contains("System.InvalidOperationException: synthetic startup failure", StringComparison.Ordinal)));
        StringAssert.Contains(text, "synthetic startup failure");
    }

    [TestMethod]
    public void ExistingDirectoryNamesAreLiteralAndNativeFiltersCanReload()
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
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App").LogDebug("hidden");
            builder.Configuration["Logging:LogLevel:App"] = "Debug";
            ((IConfigurationRoot)builder.Configuration).Reload();
            host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("App").LogDebug("enabled again");
            host.Dispose();
            logging.Dispose();
            var path = Directory.GetFiles(Path.Combine(application, "Logs")).Single();
            StringAssert.StartsWith(Path.GetFileName(path), "literal-${level}-");
            StringAssert.Contains(File.ReadAllText(path), "enabled again");
            Assert.IsFalse(File.ReadAllText(path).Contains("hidden", StringComparison.Ordinal));
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
