using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
        File.WriteAllText(path, "{\"existing\":true}\n");
        using (var host = fixture.Build())
        {
            host.Services.GetRequiredService<ILogger<LoggingTests>>().LogInformation("created {Value}", 123);
        }
        fixture.Logging.Dispose();
        var lines = File.ReadAllLines(path);
        Assert.AreEqual(2, lines.Length);
        Assert.IsTrue(JsonDocument.Parse(lines[0]).RootElement.GetProperty("existing").GetBoolean());
        Assert.AreEqual("created 123", JsonDocument.Parse(lines[1]).RootElement.GetProperty("message").GetString());
        CollectionAssert.AreNotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, File.ReadAllBytes(path).Take(3).ToArray());
    }

    [TestMethod]
    public void PreservesStructuredEventScopesTraceExceptionAndCoreFieldNames()
    {
        using var fixture = new LoggingFixture();
        using (var host = fixture.Build())
        {
            var logger = host.Services.GetRequiredService<ILogger<LoggingTests>>();
            using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
            using var scope = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = "correlation-42", ["message"] = "scope-message", ["traceId"] = "supplied-trace" });
            using var state = logger.BeginScope("operation-scope");
            logger.LogError(new EventId(42, "LookupFailed"), new InvalidOperationException("failure text"), "Lookup {Count} for {message}", 3, "event-message\nnext line café");
            var record = fixture.Records().Single();
            Assert.AreEqual(42, record.GetProperty("eventId").GetInt32());
            Assert.AreEqual("LookupFailed", record.GetProperty("eventName").GetString());
            Assert.AreEqual(typeof(LoggingTests).FullName, record.GetProperty("category").GetString());
            Assert.AreEqual("Lookup {Count} for {message}", record.GetProperty("template").GetString());
            Assert.AreEqual("Lookup 3 for event-message\nnext line café", record.GetProperty("message").GetString());
            Assert.AreEqual(3, record.GetProperty("properties").GetProperty("Count").GetInt32());
            Assert.AreEqual("correlation-42", record.GetProperty("scope").GetProperty("CorrelationId").GetString());
            Assert.AreEqual("supplied-trace", record.GetProperty("scope").GetProperty("traceId").GetString());
            Assert.IsTrue(record.GetProperty("scopes").EnumerateArray().Any(s => s.GetString() == "operation-scope"));
            Assert.AreEqual(activity.TraceId.ToString(), record.GetProperty("traceId").GetString());
            Assert.AreEqual(activity.SpanId.ToString(), record.GetProperty("spanId").GetString());
            StringAssert.Contains(record.GetProperty("exception").GetString(), "InvalidOperationException: failure text");
            var timestamp = record.GetProperty("timestamp").GetString()!;
            var parsed = DateTimeOffset.ParseExact(timestamp, "O", CultureInfo.InvariantCulture);
            Assert.AreEqual(TimeZoneInfo.Local.GetUtcOffset(parsed), parsed.Offset);
            Assert.IsTrue(timestamp.EndsWith(parsed.ToString("zzz", CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }
        fixture.Logging.Dispose();
        Assert.AreEqual(1, File.ReadAllLines(fixture.TodayFile).Length);
    }

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
        Assert.AreEqual("process-only", JsonDocument.Parse(output).RootElement.GetProperty("message").GetString());
    }
}

internal sealed class LoggingFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Clc.Logging.Tests", Guid.NewGuid().ToString("N"));
    public ClcLogging Logging { get; }
    public string TodayFile => Path.Combine(Logging.DirectoryPath, $"{Logging.ApplicationName}-{DateTime.Now:yyyy-MM-dd}.jsonl");

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

    public JsonElement[] Records()
    {
        Logging.Flush();
        return Directory.GetFiles(Logging.DirectoryPath, "*.jsonl").SelectMany(ReadLines)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        while (reader.ReadLine() is { } line) { yield return line; }
    }

    public string[] Messages() => Records().Select(record => record.GetProperty("message").GetString()!).ToArray();

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
