using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog.Extensions.Logging;

namespace Clc.Logging.Tests;

[TestClass]
public sealed class FilterAndProviderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void HostConfigurationDoesNotOverrideEarlierApplicationFilters(bool disableDefaults)
    {
        using var fixture = new LoggingFixture();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = disableDefaults });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:App"] = "Error",
            ["Logging:NLog:LogLevel:ProviderApp"] = "Error"
        });
        builder.Logging.AddFilter("App", LogLevel.Debug)
            .AddFilter<NLogLoggerProvider>("ProviderApp", LogLevel.Debug);
        builder.AddClcLogging(fixture.Logging);
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("App").LogDebug("earlier application filter");
        factory.CreateLogger("ProviderApp").LogDebug("earlier provider filter");
        CollectionAssert.AreEquivalent(new[] { "earlier application filter", "earlier provider filter" }, fixture.Messages());
    }

    [TestMethod]
    public void ExistingProviderAndProviderSpecificFiltersSurviveAlongsideNLog()
    {
        using var fixture = new LoggingFixture();
        using var provider = new RecordingProvider();
        var builder = fixture.Builder();
        builder.Logging.AddProvider(provider)
            .AddFilter<RecordingProvider>("App", LogLevel.Error)
            .AddFilter<NLogLoggerProvider>("App", LogLevel.Debug)
            .AddFilter("App.Quiet", LogLevel.None);
        builder.AddClcLogging(fixture.Logging);
        using var host = builder.Build();
        var providers = host.Services.GetServices<ILoggerProvider>().ToArray();
        Assert.IsTrue(providers.Contains(provider));
        Assert.AreEqual(1, providers.OfType<NLogLoggerProvider>().Count());
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("App").LogDebug("debug");
        factory.CreateLogger("App").LogError("error");
        factory.CreateLogger("App.Quiet").LogCritical("provider-specific rules have native precedence");
        CollectionAssert.AreEqual(new[] { "error", "provider-specific rules have native precedence" }, provider.Messages);
        CollectionAssert.AreEqual(new[] { "debug", "error", "provider-specific rules have native precedence" }, fixture.Messages());
        var rules = host.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;
        Assert.IsTrue(rules.Any(rule => rule.CategoryName == "App.Quiet" && rule.LogLevel == LogLevel.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BroadApplicationRulesBeforeOrAfterRegistrationOverrideConventionalCategories(bool after)
    {
        using var fixture = new LoggingFixture();
        var builder = fixture.Builder();
        if (!after) { builder.Logging.AddFilter((_, level) => level >= LogLevel.Debug); }
        builder.AddClcLogging(fixture.Logging);
        if (after) { builder.Logging.AddFilter((_, level) => level >= LogLevel.Debug); }
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Microsoft.Component").LogDebug("microsoft debug");
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogDebug("lifetime debug");
        factory.CreateLogger("System.Net.Http.HttpClient").LogDebug("http debug");
        CollectionAssert.AreEquivalent(new[] { "microsoft debug", "lifetime debug", "http debug" }, fixture.Messages());
    }

    [TestMethod]
    public void ExplicitDefaultAndParentCategoryConfigurationOverrideConventionalDefaults()
    {
        using var fixture = new LoggingFixture();
        using var host = fixture.Build(new()
        {
            ["Logging:LogLevel:Default"] = "Trace",
            ["Logging:LogLevel:Microsoft"] = "Debug",
            ["Logging:LogLevel:Microsoft.Quiet"] = "None",
            ["Logging:LogLevel:System"] = "Error"
        });
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("App").LogTrace("app trace");
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogDebug("lifetime debug");
        factory.CreateLogger("Microsoft.Quiet.Child").LogCritical("hidden");
        factory.CreateLogger("System.Net.Http.HttpClient").LogWarning("hidden");
        factory.CreateLogger("System.Net.Http.HttpClient").LogError("http error");
        CollectionAssert.AreEquivalent(new[] { "app trace", "lifetime debug", "http error" }, fixture.Messages());
    }

    [TestMethod]
    public void LaterSpecificFiltersAndNoneRetainNativePrecedence()
    {
        using var fixture = new LoggingFixture();
        var builder = fixture.Builder();
        builder.Logging.AddFilter("Microsoft", LogLevel.Error).AddFilter("App", LogLevel.Trace);
        builder.AddClcLogging(fixture.Logging);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Debug)
            .AddFilter("App.Quiet", LogLevel.None).AddFilter("App", LogLevel.Warning);
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogDebug("lifetime debug");
        factory.CreateLogger("Microsoft.Other").LogWarning("hidden");
        factory.CreateLogger("App").LogInformation("hidden");
        factory.CreateLogger("App.Quiet.Child").LogCritical("hidden");
        factory.CreateLogger("App.Other").LogWarning("app warning");
        CollectionAssert.AreEquivalent(new[] { "lifetime debug", "app warning" }, fixture.Messages());
    }

    [TestMethod]
    public void ProviderConfigurationAndNativeWildcardRulesArePreserved()
    {
        using var fixture = new LoggingFixture();
        using var host = fixture.Build(new()
        {
            ["Logging:LogLevel:*"] = "Trace",
            ["Logging:NLog:LogLevel:App"] = "Error",
            ["Logging:NLog:LogLevel:App.Detail"] = "Debug"
        });
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogTrace("native wildcard");
        factory.CreateLogger("App").LogWarning("hidden");
        factory.CreateLogger("App.Detail").LogDebug("provider category");
        CollectionAssert.AreEquivalent(new[] { "native wildcard", "provider category" }, fixture.Messages());
    }

    [TestMethod]
    [DataRow(LogLevel.Trace)]
    [DataRow(LogLevel.None)]
    public void ApplicationMinimumIsNeverReset(LogLevel minimum)
    {
        using var fixture = new LoggingFixture();
        var builder = fixture.Builder();
        builder.Logging.SetMinimumLevel(minimum);
        builder.AddClcLogging(fixture.Logging);
        using var host = builder.Build();
        Assert.AreEqual(minimum, host.Services.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.MinLevel);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.Hosting.Lifetime");
        Assert.AreEqual(minimum == LogLevel.Trace, logger.IsEnabled(LogLevel.Trace));
        Assert.AreEqual(minimum == LogLevel.Trace, logger.IsEnabled(LogLevel.Critical));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConsumersCanExplicitlyClearProvidersBeforeOrAfterRegistration(bool after)
    {
        using var fixture = new LoggingFixture();
        var builder = Host.CreateApplicationBuilder();
        using var provider = new RecordingProvider();
        builder.Logging.AddProvider(provider);
        if (!after) { builder.Logging.ClearProviders(); }
        builder.AddClcLogging(fixture.Logging);
        if (after) { builder.Logging.ClearProviders().AddProvider(provider); }
        using var host = builder.Build();
        var providers = host.Services.GetServices<ILoggerProvider>().ToArray();
        Assert.AreEqual(1, providers.Length);
        Assert.AreEqual(!after, providers[0] is NLogLoggerProvider);
        host.Services.GetRequiredService<ILogger<FilterAndProviderTests>>().LogInformation("explicit collection");
        if (after) { CollectionAssert.AreEqual(new[] { "explicit collection" }, provider.Messages); }
        else { CollectionAssert.AreEqual(new[] { "explicit collection" }, fixture.Messages()); }
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
