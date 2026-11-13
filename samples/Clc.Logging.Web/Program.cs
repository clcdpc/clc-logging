using Clc.Logging;
using Microsoft.Extensions.Logging;

ClcLogging? logging = null;
try
{
    logging = ClcLogging.CreateForDirectory(args.Single());
    logging.Logger.LogInformation("Web bootstrap");
    var builder = WebApplication.CreateBuilder();
    builder.AddClcLogging(logging);
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var app = builder.Build();
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
    Console.WriteLine("Web package consumer passed.");
    return 0;
}
catch (Exception error)
{
    if (logging is null) { ClcLogging.WriteStartupFailure(error); }
    else { logging.LogFatal(error); }
    return 1;
}
finally { logging?.Dispose(); }
