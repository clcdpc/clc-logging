using Clc.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog.Extensions.Logging;

namespace Clc.Logging.Tests;

[TestClass]
public sealed class RegistrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConfiguredPathsSupplyIdentityAndDirectoryAndHostOwnsLogging(bool bootstrap)
    {
        using var fixture = new RegistrationFixture();
        var builder = Host.CreateApplicationBuilder();
        ConfigurationFixture.Initialize(builder, fixture.Paths, bootstrap: bootstrap);
        Assert.AreSame(fixture.Paths, builder.GetClcApplicationPaths());
        Assert.AreSame(builder, builder.AddClcLogging());
        builder.Services.AddHostedService<DisposalService>();
        ClcLogging logging;
        using (var host = builder.Build())
        {
            logging = host.Services.GetRequiredService<ClcLogging>();
            Assert.AreEqual(Path.GetFileName(fixture.Paths.ApplicationDataDirectory), logging.ApplicationName);
            Assert.AreEqual(Path.Combine(fixture.Paths.ApplicationDataDirectory, "Logs"), logging.DirectoryPath);
            Assert.AreEqual(1, host.Services.GetServices<ILoggerProvider>().OfType<NLogLoggerProvider>().Count());
            await host.StartAsync();
            host.Services.GetRequiredService<ILogger<RegistrationTests>>().LogInformation("configured event");
            await host.StopAsync();
        }
        Assert.ThrowsExactly<ObjectDisposedException>(() => logging.LogFatal(new InvalidOperationException()));
        var text = string.Join("\n", Directory.GetFiles(logging.DirectoryPath).Select(File.ReadAllText));
        StringAssert.Contains(text, "configured event");
        StringAssert.Contains(text, "owned service stopped");
        StringAssert.Contains(text, "owned service disposed");
    }

    [TestMethod]
    public void CallbackRunsOnceAfterConfigurationAndOverridesItsSettings()
    {
        using var fixture = new RegistrationFixture();
        var builder = Host.CreateApplicationBuilder();
        ConfigurationFixture.Initialize(builder, fixture.Paths, """{"Logging":{"LogLevel":{"Default":"Error","App":"Warning"}}}""");
        var calls = 0;
        builder.AddClcLogging(options =>
        {
            calls++;
            Assert.AreEqual(LogLevel.Warning, options.LogLevels["App"]);
            options.LogLevels["App"] = LogLevel.Debug;
        });
        using (var host = builder.Build())
        {
            var factory = host.Services.GetRequiredService<ILoggerFactory>();
            factory.CreateLogger("App.Child").LogDebug("callback event");
            factory.CreateLogger("Other").LogWarning("hidden event");
        }
        Assert.AreEqual(1, calls);
        var text = File.ReadAllText(Directory.GetFiles(Path.Combine(fixture.Paths.ApplicationDataDirectory, "Logs")).Single());
        StringAssert.Contains(text, "callback event");
        Assert.IsFalse(text.Contains("hidden event", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MissingConfigurationPreservesServicesProvidersFiltersAndDoesNotInvokeCallback()
    {
        var builder = Host.CreateApplicationBuilder();
        var provider = new RecordingProvider();
        builder.Logging.ClearProviders().AddProvider(provider).AddFilter("Suppressed", LogLevel.None);
        var services = builder.Services.ToArray();
        var called = false;
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddClcLogging(_ => called = true));
        StringAssert.Contains(error.Message, "has not successfully initialized");
        StringAssert.Contains(error.Message, "AddClcConfiguration");
        StringAssert.Contains(error.Message, "AddClcConfigurationWithBootstrap");
        CollectionAssert.AreEqual(services, builder.Services.ToArray());
        Assert.IsFalse(called);
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Suppressed.Child").LogCritical("hidden");
        factory.CreateLogger("App").LogInformation("preserved");
        CollectionAssert.AreEqual(new[] { "preserved" }, provider.Messages);
        Assert.IsNull(host.Services.GetService<ClcLogging>());
    }

    [TestMethod]
    public void FailedConfigurationInitializationDoesNotPermitLoggingAndCanBeRetried()
    {
        using var fixture = new RegistrationFixture();
        var builder = Host.CreateApplicationBuilder();
        Assert.ThrowsExactly<ClcConfigurationException>(() =>
            ConfigurationFixture.Initialize(builder, fixture.Paths, "{invalid"));
        var services = builder.Services.ToArray();
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddClcLogging());
        CollectionAssert.AreEqual(services, builder.Services.ToArray());
        ConfigurationFixture.Initialize(builder, fixture.Paths);
        builder.AddClcLogging();
        using var host = builder.Build();
        Assert.AreEqual(Path.Combine(fixture.Paths.ApplicationDataDirectory, "Logs"), host.Services.GetRequiredService<ClcLogging>().DirectoryPath);
    }

    [TestMethod]
    public void ParameterlessRegistrationRejectsNullBuilder()
    {
        IHostApplicationBuilder builder = null!;
        Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddClcLogging());
    }

    [TestMethod]
    public void ExplicitPathsRemainAvailableWithoutConfigurationInitialization()
    {
        using var fixture = new RegistrationFixture();
        var builder = Host.CreateApplicationBuilder();
        var called = false;
        Assert.AreSame(builder, builder.AddClcLogging(fixture.Paths, _ => called = true));
        using var host = builder.Build();
        Assert.IsTrue(called);
        Assert.AreEqual(Path.Combine(fixture.Paths.ApplicationDataDirectory, "Logs"), host.Services.GetRequiredService<ClcLogging>().DirectoryPath);
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.GetClcApplicationPaths());
    }

    [TestMethod]
    public void ExplicitNameRemainsAvailableWithoutConfigurationInitialization()
    {
        using var capture = new ErrorCapture();
        var name = "clc-explicit-" + Guid.NewGuid().ToString("N");
        var builder = Host.CreateApplicationBuilder();
        Assert.AreSame(builder, builder.AddClcLogging(name, options => options.File.Enabled = false));
        using (var host = builder.Build())
        {
            Assert.AreEqual(name, host.Services.GetRequiredService<ClcLogging>().ApplicationName);
            host.Services.GetRequiredService<ILogger<RegistrationTests>>().LogInformation("explicit name");
        }
        StringAssert.Contains(capture.ToString(), "explicit name");
        Assert.IsFalse(Directory.Exists(ClcApplicationPaths.Resolve(name).ApplicationDataDirectory));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DuplicateRegistrationPreservesFirstInstanceAndOwnership(bool callerOwned)
    {
        using var fixture = new RegistrationFixture();
        using var supplied = ClcLogging.Create(fixture.Paths);
        var builder = Host.CreateApplicationBuilder();
        ConfigurationFixture.Initialize(builder, fixture.Paths);
        if (callerOwned) { builder.AddClcLogging(supplied); }
        else { builder.AddClcLogging(); }
        var services = builder.Services.ToArray();
        var callbackCalled = false;
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddClcLogging(_ => callbackCalled = true));
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddClcLogging(fixture.Paths));
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddClcLogging("clc-duplicate"));
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddClcLogging(supplied));
        CollectionAssert.AreEqual(services, builder.Services.ToArray());
        Assert.IsFalse(callbackCalled);
        ClcLogging first;
        using (var host = builder.Build())
        {
            first = callerOwned ? supplied : host.Services.GetRequiredService<ClcLogging>();
            Assert.AreEqual(1, host.Services.GetServices<ILoggerProvider>().OfType<NLogLoggerProvider>().Count());
            host.Services.GetRequiredService<ILogger<RegistrationTests>>().LogInformation("original registration");
        }
        if (callerOwned)
        {
            supplied.Logger.LogInformation("caller still owns logging");
            supplied.Dispose();
            StringAssert.Contains(File.ReadAllText(Directory.GetFiles(supplied.DirectoryPath).Single()), "caller still owns logging");
        }
        else { Assert.ThrowsExactly<ObjectDisposedException>(() => first.LogFatal(new InvalidOperationException())); }
    }

    private sealed class RegistrationFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("Clc.Logging.Registration-").FullName;
        public ClcApplicationPaths Paths { get; }
        public RegistrationFixture() => Paths = ConfigurationFixture.Paths(Path.Combine(_root, "configured-app"));
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class DisposalService(ILogger<DisposalService> logger) : IHostedService, IDisposable
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) { logger.LogInformation("owned service stopped"); return Task.CompletedTask; }
        public void Dispose() => logger.LogInformation("owned service disposed");
    }

    private sealed class RecordingProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);
        public void Dispose() { }
        private sealed class RecordingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                messages.Add(formatter(state, exception));
        }
    }
}
