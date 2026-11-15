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
    public void UnrelatedConfigurationDoesNotSuppressStandardLoggingBindingAndBothSectionsReload()
    {
        using var fixture = new LoggingFixture();
        var builder = fixture.Builder(new()
        {
            ["OtherLogging:LogLevel:OtherApp"] = "Warning",
            ["Logging:LogLevel:App"] = "Error"
        });
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("OtherLogging"));
        builder.AddClcLogging(fixture.Logging);
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var app = factory.CreateLogger("App");
        var other = factory.CreateLogger("OtherApp");
        app.LogInformation("hidden");
        app.LogError("app error");
        other.LogInformation("hidden");
        other.LogWarning("other warning");
        CollectionAssert.AreEquivalent(new[] { "app error", "other warning" }, fixture.Messages());

        builder.Configuration["Logging:LogLevel:App"] = "Information";
        builder.Configuration["OtherLogging:LogLevel:OtherApp"] = "None";
        ((IConfigurationRoot)builder.Configuration).Reload();
        app.LogInformation("app reloaded");
        other.LogCritical("hidden");
        CollectionAssert.AreEquivalent(new[] { "app error", "other warning", "app reloaded" }, fixture.Messages());
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ApplicationFiltersBeforeOrAfterRegistrationWinWithUnrelatedConfiguration(bool disableDefaults, bool after)
    {
        using var fixture = new LoggingFixture();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = disableDefaults });
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OtherLogging:LogLevel:OtherApp"] = "Error",
            ["Logging:LogLevel:App"] = "Error",
            ["Logging:NLog:LogLevel:ProviderApp"] = "Error"
        });
        builder.Logging.AddConfiguration(builder.Configuration.GetSection("OtherLogging"));
        if (!after)
        {
            builder.Logging.AddFilter("App", LogLevel.Debug)
                .AddFilter<NLogLoggerProvider>("ProviderApp", LogLevel.Debug);
        }
        builder.AddClcLogging(fixture.Logging);
        if (after)
        {
            builder.Logging.AddFilter("App", LogLevel.Debug)
                .AddFilter<NLogLoggerProvider>("ProviderApp", LogLevel.Debug);
        }
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("App").LogDebug("application filter");
        factory.CreateLogger("ProviderApp").LogDebug("provider filter");
        CollectionAssert.AreEquivalent(new[] { "application filter", "provider filter" }, fixture.Messages());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StandardHostFilteringDoesNotDuplicateEventsOrAccumulateRulesOnReload(bool addClcLogging)
    {
        using var fixture = new LoggingFixture();
        using var provider = new RecordingProvider();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:App"] = "Error",
            ["Logging:LogLevel:EarlierApp"] = "Error",
            ["Logging:LogLevel:LaterApp"] = "Error"
        });
        builder.Logging.ClearProviders().AddProvider(provider).AddFilter("EarlierApp", LogLevel.Debug);
        if (addClcLogging) { builder.AddClcLogging(fixture.Logging); }
        builder.Logging.AddFilter("LaterApp", LogLevel.Debug);
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        var app = factory.CreateLogger("App");
        var earlier = factory.CreateLogger("EarlierApp");
        var later = factory.CreateLogger("LaterApp");
        app.LogInformation("hidden");
        app.LogError("app error");
        earlier.LogDebug("earlier filter");
        later.LogDebug("later filter");
        var expected = new List<string> { "app error", "earlier filter", "later filter" };
        CollectionAssert.AreEqual(expected, provider.Messages);
        if (addClcLogging) { CollectionAssert.AreEqual(expected, fixture.Messages()); }

        var filters = host.Services.GetRequiredService<IOptionsMonitor<LoggerFilterOptions>>();
        var ruleCount = filters.CurrentValue.Rules.Count;
        for (var reload = 0; reload < 3; reload++)
        {
            builder.Configuration["Logging:LogLevel:App"] = reload % 2 == 0 ? "Information" : "Error";
            ((IConfigurationRoot)builder.Configuration).Reload();
            Assert.AreEqual(ruleCount, filters.CurrentValue.Rules.Count);
            Assert.AreEqual(reload % 2 == 0, app.IsEnabled(LogLevel.Information));
            app.LogInformation($"app reload {reload}");
            earlier.LogDebug($"earlier reload {reload}");
            later.LogDebug($"later reload {reload}");
            if (reload % 2 == 0) { expected.Add($"app reload {reload}"); }
            expected.Add($"earlier reload {reload}");
            expected.Add($"later reload {reload}");
            CollectionAssert.AreEqual(expected, provider.Messages);
            if (addClcLogging) { CollectionAssert.AreEqual(expected, fixture.Messages()); }
        }
    }

    [TestMethod]
    [DataRow("MyApp.*", "MyApp.Component")]
    [DataRow("*MyApp", "Component.MyApp")]
    [DataRow("MyApp.*Service", "MyApp.ComponentService")]
    public void UnrelatedWildcardsDoNotSuppressConventionalDefaults(string wildcard, string applicationCategory)
    {
        using var fixture = new LoggingFixture();
        using var host = fixture.Build(new() { [$"Logging:LogLevel:{wildcard}"] = "Trace" });
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger(applicationCategory).LogTrace("application trace");
        factory.CreateLogger("Microsoft.Component").LogInformation("hidden");
        factory.CreateLogger("Microsoft.Component").LogWarning("microsoft warning");
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogDebug("hidden");
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("lifetime information");
        factory.CreateLogger("System.Net.Http.HttpClient").LogInformation("hidden");
        factory.CreateLogger("System.Net.Http.HttpClient").LogWarning("http warning");
        CollectionAssert.AreEquivalent(new[]
        {
            "application trace", "microsoft warning", "lifetime information", "http warning"
        }, fixture.Messages());
    }

    [TestMethod]
    [DataRow("Microsoft*", "Microsoft.Component")]
    [DataRow("*Lifetime", "Microsoft.Hosting.Lifetime")]
    [DataRow("mIcRoSoFt.*Lifetime", "Microsoft.Hosting.Lifetime")]
    [DataRow("System.*Http", "System.Net.Http")]
    [DataRow("*", "Microsoft.Component")]
    [DataRow("*", "Microsoft.Hosting.Lifetime")]
    [DataRow("*", "System.Net.Http.HttpClient")]
    public void MatchingWildcardsOverrideConventionalDefaults(string wildcard, string category)
    {
        using var fixture = new LoggingFixture();
        using var host = fixture.Build(new() { [$"Logging:LogLevel:{wildcard}"] = "Trace" });
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category).LogTrace("explicit wildcard");
        CollectionAssert.AreEqual(new[] { "explicit wildcard" }, fixture.Messages());
    }

    [TestMethod]
    public void ProviderWildcardDoesNotSuppressOtherProvidersConventionalDefaults()
    {
        using var fixture = new LoggingFixture();
        using var provider = new RecordingProvider();
        var builder = fixture.Builder();
        builder.Logging.AddProvider(provider).AddFilter<NLogLoggerProvider>("Microsoft*", LogLevel.Trace);
        builder.AddClcLogging(fixture.Logging);
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Microsoft.Component");
        logger.LogTrace("provider trace");
        logger.LogInformation("provider information");
        logger.LogWarning("shared warning");
        CollectionAssert.AreEqual(new[] { "shared warning" }, provider.Messages);
        CollectionAssert.AreEqual(new[] { "provider trace", "provider information", "shared warning" }, fixture.Messages());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitCategoryFiltersRetainNativePrecedenceOverWildcards(bool after)
    {
        using var fixture = new LoggingFixture();
        var builder = fixture.Builder(new()
        {
            ["Logging:LogLevel:Microsoft*"] = "Trace",
            ["Logging:LogLevel:System.*Http"] = "Trace"
        });
        if (!after) { builder.Logging.AddFilter("Microsoft.Component", LogLevel.Error).AddFilter("System.Net.Http", LogLevel.Error); }
        builder.AddClcLogging(fixture.Logging);
        if (after) { builder.Logging.AddFilter("Microsoft.Component", LogLevel.Error).AddFilter("System.Net.Http", LogLevel.Error); }
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("Microsoft.Component").LogWarning("hidden");
        factory.CreateLogger("Microsoft.Component").LogError("microsoft error");
        factory.CreateLogger("System.Net.Http").LogWarning("hidden");
        factory.CreateLogger("System.Net.Http").LogError("http error");
        CollectionAssert.AreEquivalent(new[] { "microsoft error", "http error" }, fixture.Messages());
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
