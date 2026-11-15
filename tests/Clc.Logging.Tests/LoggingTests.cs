using System.Globalization;
using System.Text.RegularExpressions;
using Clc.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[assembly: DoNotParallelize]

namespace Clc.Logging.Tests;

[TestClass]
public sealed class LoggingTests
{
    [TestMethod]
    public void DefaultPathsReusePublishedConfigurationPathsWithoutCreatingDirectories()
    {
        var name = "clc-logging-test-" + Guid.NewGuid().ToString("N");
        var paths = ClcApplicationPaths.Resolve(name);
        using var byName = ClcLogging.Create(name);
        using var byPaths = ClcLogging.Create(paths);
        Assert.AreEqual(Path.Combine(paths.ApplicationDataDirectory, "Logs"), byName.DirectoryPath);
        Assert.AreEqual(byName.DirectoryPath, byPaths.DirectoryPath);
        Assert.AreEqual(name, byPaths.ApplicationName);
        Assert.IsFalse(Directory.Exists(paths.ApplicationDataDirectory));
    }

    [TestMethod]
    public void CreatesDirectoryAndAppendsToConventionalFileWithoutOverwritingExistingContent()
    {
        using var fixture = new LoggingFixture();
        var path = fixture.TodayFile;
        Assert.IsFalse(Directory.Exists(fixture.Logging.DirectoryPath));
        Directory.CreateDirectory(fixture.Logging.DirectoryPath);
        File.WriteAllText(path, "existing content\n");
        using (var host = fixture.Build())
        {
            host.Services.GetRequiredService<ILogger<LoggingTests>>().LogInformation("created {Value}", 123);
        }
        fixture.Logging.Dispose();
        using (var restarted = ClcLogging.CreateForDirectory(Path.GetDirectoryName(fixture.Logging.DirectoryPath)!))
        {
            var builder = fixture.Builder();
            builder.AddClcLogging(restarted);
            using var host = builder.Build();
            host.Services.GetRequiredService<ILogger<LoggingTests>>().LogInformation("after restart");
        }
        var lines = File.ReadAllLines(path);
        Assert.AreEqual(3, lines.Length);
        Assert.AreEqual("existing content", lines[0]);
        StringAssert.EndsWith(lines[1], "[INFO] Clc.Logging.Tests.LoggingTests - created 123");
        StringAssert.EndsWith(lines[2], "[INFO] Clc.Logging.Tests.LoggingTests - after restart");
        CollectionAssert.AreNotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3).ToArray());
    }

    [TestMethod]
    public void PlaintextIncludesLocalTimestampLevelCategoryAndRenderedMessageOnly()
    {
        using var fixture = new LoggingFixture();
        using (var host = fixture.Build())
        {
            var logger = host.Services.GetRequiredService<ILogger<LoggingTests>>();
            using var scope = logger.BeginScope(new Dictionary<string, object?> { ["ScopeOnly"] = "hidden metadata" });
            logger.LogInformation(new EventId(42, "LookupCompleted"), "Lookup {Count} café", 3);
            var line = fixture.Lines().Single();
            StringAssert.Matches(line, new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[INFO\] Clc.Logging.Tests.LoggingTests - Lookup 3 café$"));
            var parsed = DateTimeOffset.ParseExact(line[..30], "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            Assert.AreEqual(TimeZoneInfo.Local.GetUtcOffset(parsed), parsed.Offset);
            Assert.IsTrue((DateTimeOffset.Now - parsed).Duration() < TimeSpan.FromSeconds(10));
            Assert.IsFalse(line.Contains("ScopeOnly", StringComparison.Ordinal));
            Assert.IsFalse(line.Contains("LookupCompleted", StringComparison.Ordinal));
            Assert.IsFalse(line.StartsWith('{'));
        }
        fixture.Logging.Dispose();
        Assert.AreEqual(1, File.ReadAllLines(fixture.TodayFile).Length);
    }

    [TestMethod]
    public void ExceptionsUseNativeDetailsAndStackTrace()
    {
        using var fixture = new LoggingFixture();
        using var host = fixture.Build();
        try { ThrowDiagnosticException(); }
        catch (InvalidOperationException exception)
        {
            host.Services.GetRequiredService<ILogger<LoggingTests>>().LogError(exception, "Lookup failed");
        }
        var text = fixture.Text();
        StringAssert.Contains(text, "[ERROR] Clc.Logging.Tests.LoggingTests - Lookup failed");
        StringAssert.Contains(text, "System.InvalidOperationException: failure text");
        StringAssert.Contains(text, "System.ArgumentException: inner detail");
        StringAssert.Contains(text, nameof(ThrowDiagnosticException));
    }

    private static void ThrowDiagnosticException() => throw new InvalidOperationException("failure text", new ArgumentException("inner detail"));

    [TestMethod]
    public void FrameworkDefaultsAndLongestCategoryPrefixesDoNotDuplicateEventsOrSuppressDebugOverrides()
    {
        using var fixture = new LoggingFixture();
        using var host = fixture.Build(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Debug",
            ["Logging:LogLevel:Microsoft.AspNetCore.Secret"] = "None",
            ["Logging:LogLevel:App.Detail"] = "Trace",
            ["Logging:LogLevel:App.Detail.Quiet"] = "Error"
        });
        var factory = host.Services.GetRequiredService<ILoggerFactory>();
        factory.CreateLogger("App").LogDebug("hidden");
        factory.CreateLogger("App").LogInformation("app");
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("lifetime");
        factory.CreateLogger("Microsoft.EntityFrameworkCore.Query").LogInformation("hidden");
        factory.CreateLogger("Microsoft.EntityFrameworkCore.Query").LogWarning("ef");
        factory.CreateLogger("System.Net.Http.HttpClient.Client").LogInformation("hidden");
        factory.CreateLogger("System.Net.Http.HttpClient.Client").LogWarning("http");
        factory.CreateLogger("Microsoft.AspNetCore.Server").LogDebug("diagnostic");
        factory.CreateLogger("Microsoft.AspNetCore.Secret.Callback").LogCritical("hidden");
        factory.CreateLogger("App.Detail.Verbose").LogTrace("trace");
        factory.CreateLogger("App.Detail.Quiet.Child").LogWarning("hidden");
        factory.CreateLogger("App.Detail.Quiet.Child").LogError("quiet-error");
        CollectionAssert.AreEquivalent(new[] { "app", "lifetime", "ef", "http", "diagnostic", "trace", "quiet-error" }, fixture.Messages());
    }

    [TestMethod]
    public void DisabledFilesUseOnlyProcessOutputAndDuplicateRegistrationFails()
    {
        using var capture = new ErrorCapture();
        using var fixture = new LoggingFixture();
        var builder = fixture.Builder(new Dictionary<string, string?> { ["Logging:File:Enabled"] = "false" });
        builder.AddClcLogging(fixture.Logging);
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddClcLogging(fixture.Logging));
        using (var host = builder.Build())
        {
            Assert.AreEqual(1, host.Services.GetServices<ILoggerProvider>().Count());
            host.Services.GetRequiredService<ILogger<LoggingTests>>().LogInformation("process-only");
        }
        Assert.IsFalse(Directory.Exists(fixture.Logging.DirectoryPath));
        var output = capture.Lines().Single();
        StringAssert.EndsWith(output, "[INFO] Clc.Logging.Tests.LoggingTests - process-only");
    }
}

internal sealed class LoggingFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clc.Logging.Tests", Guid.NewGuid().ToString("N"));
    public ClcLogging Logging { get; }
    public string TodayFile => Path.Combine(Logging.DirectoryPath, $"{Logging.ApplicationName}-{DateTime.Now:yyyy-MM-dd}.log");

    public LoggingFixture(Action<ClcLoggingOptions>? configure = null)
    {
        Logging = ClcLogging.CreateForDirectory(Path.Combine(_root, "test-app"), configure);
    }

    public HostApplicationBuilder Builder(Dictionary<string, string?>? values = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        if (values is not null) { builder.Configuration.AddInMemoryCollection(values); }
        return builder;
    }

    public IHost Build(Dictionary<string, string?>? values = null)
    {
        var builder = Builder(values);
        builder.AddClcLogging(Logging);
        return builder.Build();
    }

    public string[] Lines()
    {
        Logging.Flush();
        return Directory.GetFiles(Logging.DirectoryPath, "*.log").SelectMany(ReadLines).ToArray();
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        while (reader.ReadLine() is { } line) { yield return line; }
    }

    public string Text() => string.Join('\n', Lines());

    public string[] Messages() => Lines().Select(line => line[(line.IndexOf(" - ", StringComparison.Ordinal) + 3)..]).ToArray();

    public void Dispose()
    {
        Logging.Dispose();
        if (Directory.Exists(_root)) { Directory.Delete(_root, true); }
    }
}

internal sealed class ErrorCapture : IDisposable
{
    private readonly TextWriter _original = Console.Error;
    private readonly StringWriter _output = new();
    public ErrorCapture() => Console.SetError(_output);
    public string[] Lines() => _output.ToString().Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    public override string ToString() => _output.ToString();
    public void Dispose()
    {
        Console.SetError(_original);
        _output.Dispose();
    }
}
