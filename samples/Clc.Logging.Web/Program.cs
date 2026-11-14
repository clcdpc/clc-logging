using Clc.Configuration;
using Clc.Logging;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder();
var paths = ConfigurationFixture.Paths(args.Single());
ConfigurationFixture.Initialize(builder, paths);
builder.AddClcLogging();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using (var app = builder.Build())
{
    var logging = app.Services.GetRequiredService<ClcLogging>();
    if (!ReferenceEquals(paths, builder.GetClcApplicationPaths()) ||
        logging.DirectoryPath != Path.Combine(paths.ApplicationDataDirectory, "Logs") ||
        logging.ApplicationName != Path.GetFileName(paths.ApplicationDataDirectory)) {
        throw new InvalidOperationException("Web did not reuse configured application paths.");
    }
    app.MapGet("/", (ILoggerFactory factory) =>
    {
        var logger = factory.CreateLogger("Web.Sample");
        using var scope = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = "web-smoke" });
        logger.LogInformation(new EventId(20, "WebRequest"), "Web served {Count}", 1);
        return "ok";
    });
    await app.StartAsync();
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    if (await client.GetStringAsync(app.Urls.Single()) != "ok") { throw new InvalidOperationException("Unexpected HTTP response."); }
    await app.StopAsync();
}
Console.WriteLine("Web package consumer passed.");
