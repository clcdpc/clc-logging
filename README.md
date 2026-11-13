# Clc.Logging

Opinionated .NET 10 logging for CLC applications: ordinary `ILogger<T>`, NLog underneath, daily JSON Lines files, and stderr when files cannot be written. No `nlog.config` or new logger interface is required. This draft is a local package, version `0.0.0-local`.

## Web and worker setup

Reference `Clc.Logging` in the application. These examples compile with the .NET 10 Web SDK and .NET SDK respectively; the package supplies the shared framework reference needed by the worker.

```csharp
using Clc.Logging;

var builder = WebApplication.CreateBuilder(args);
// Load application configuration before logging registration.
builder.AddClcLogging("my-web");
var app = builder.Build();
app.MapGet("/", (ILoggerFactory factory) =>
{
    factory.CreateLogger("MyWeb").LogInformation("Health check");
    return "ok";
});
await app.RunAsync();
```

```csharp
using Clc.Logging;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.AddClcLogging("my-worker");
// Register the application's BackgroundService/IHostedService here.
using var host = builder.Build();
await host.RunAsync();
```

Both use the same `IHostApplicationBuilder` extension. The builder owns logging and disposes it with the host. Register once, before `Build()`. The package replaces the builder's existing providers and filter rules; add any extra `builder.Logging.AddFilter(...)` calls **after** `AddClcLogging`. Consumers keep using message templates, scopes and `EventId` normally.

`Clc.Configuration` **0.1.0-beta.4**, restored from NuGet.org, supplies path resolution. The package does not load application configuration itself. If the application has already resolved paths, reuse that object:

```csharp
using Clc.Configuration;
using Clc.Logging;

var paths = ClcApplicationPaths.Resolve("my-web");
var builder = WebApplication.CreateBuilder(args);
// For apps using Clc.Configuration, call builder.AddClcConfiguration("my-web") here.
builder.AddClcLogging(paths);
```

The directory's name is the single application identity. Files go to `<ApplicationDataDirectory>/Logs/<name>-yyyy-MM-dd.jsonl`. Size rollover adds a sequence suffix, for example `<name>-yyyy-MM-dd.001.jsonl`. Paths are resolved without writing to ProgramData during bootstrap. `ClcLogging.CreateForDirectory(absoluteApplicationDataDirectory)` also supports an already resolved application data directory or an isolated local fixture; its last path segment supplies the identity. It still appends `Logs`.

## Early startup and fatal failures

Use a caller-owned instance to cover configuration and host construction failures. It writes bootstrap events to stderr until registration reads the application's logging settings. Bootstrap events are not replayed into the file. The same instance stays alive after the host is disposed so the outer catch can report a fatal failure:

```csharp
using Clc.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

ClcLogging? logging = null;
try
{
    logging = ClcLogging.Create("my-worker");
    logging.Logger.LogInformation("Starting application");
    var builder = Host.CreateApplicationBuilder(args);
    // Load the application's configuration here.
    builder.AddClcLogging(logging);
    using var host = builder.Build();
    await host.RunAsync();
    return 0;
}
catch (Exception error)
{
    if (logging is null) { ClcLogging.WriteStartupFailure(error); }
    else { logging.LogFatal(error); }
    return 1;
}
finally { logging?.Dispose(); }
```

For a web app, use `WebApplication.CreateBuilder(args)`, the same registration, `await using var app = builder.Build()`, and `await app.RunAsync()` inside the try. Do not also log/rethrow the exception in an inner catch. `LogFatal` reports an exception once: if this instance already rendered that same exception object (for example, the host logged a failed hosted-service startup), it keeps the original diagnostic. A previously unreported fatal error bypasses category filters. A failure before instance creation uses the static stderr helper. These helpers include exception text; application privacy rules still apply.

Dispose the host before the caller-owned logging instance. Logging remains available through hosted-service stop and disposal. Each instance owns its NLog factory; it never sets process-global NLog configuration or calls global shutdown. `Flush()` and `Dispose()` are idempotent. Writes are synchronous with file auto-flush; there is no queue to lose on shutdown or overload. This trades some logging throughput for simpler lifecycle behavior. Explicit final flush defaults to five seconds; it cannot bound an operating system file write that is already blocked.

## Defaults and settings

Settings are a startup snapshot. Configuration changes require a restart. The optional callback runs during registration, after configuration is read:

```csharp
builder.AddClcLogging("my-web", options =>
{
    options.LogLevels["MyWeb.Diagnostics"] = LogLevel.Debug;
    options.FlushTimeout = TimeSpan.FromSeconds(5);
});
```

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft": "Warning",
      "Microsoft.Hosting.Lifetime": "Information",
      "System.Net.Http": "Warning",
      "MyWeb.Diagnostics": "Debug"
    },
    "File": {
      "Enabled": true,
      "MaxFileSizeBytes": 52428800,
      "RetentionDays": 30,
      "MaxArchiveFiles": 300
    }
  }
}
```

Omitted settings use these defaults. The longest matching category prefix wins, case-insensitively, following Microsoft's category matching. `Default` is the fallback; specific built-in categories remain in effect when only `Default` is changed. All seven Microsoft levels, including `None`, are supported. Explicit framework Debug/Trace settings work because NLog accepts all levels and Microsoft applies the single filter policy. Additional `AddFilter` rules follow Microsoft's normal longest-prefix and last-rule precedence.

Invalid values fail registration with the setting's name. File size must be an integer from 1 KiB through 1 GiB, retention 1–3650 days, archive cap 1–10000 files, and the callback's flush timeout greater than zero and at most one minute. Unknown file settings, numeric/unknown level names, nested scalar values and wildcard categories are rejected. NLog performs daily/size rollover and archive cleanup when writing; there is no background retention job. An individual event can exceed the size limit, and rollover/cleanup can temporarily leave the active file in addition to the archive cap. Existing current files are appended, and unrelated application filenames are retained.

## Output and permissions

File output is UTF-8 without a BOM, one JSON object per line. The stable fields are `timestamp`, `level`, `category`, `eventId`, `eventName`, `message`, `template`, `properties`, `scope`, `scopes`, `traceId`, `spanId` and `exception`. Absent event names, tracing and exceptions are null; the default event ID is zero. `properties` and `scope` hold separate objects, so application keys cannot overwrite core fields. Non-property scope states are in `scopes`. Activity trace/span IDs are captured when present; caller-supplied IDs remain in scope/event properties. Timestamps use local time with an explicit offset calculated for each event, including daylight-saving transitions. Level names are NLog's `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal`.

The package creates `Logs` using the process's existing permissions. It does not elevate, change ACLs or provision service/IIS identities. A successful file write produces only file output. A failed file write uses NLog's fallback group to write an actionable diagnostic and the event to stderr; it retries the file on later writes. Disabled file output writes events directly to stderr. Configure the hosting environment to retain stderr if that is the desired fallback record. Failure of both the filesystem and process output cannot guarantee persistence. Invalid settings still fail startup clearly.

## Application policy

The package does not automatically inspect HTTP requests, headers, cookies, query strings, bodies, configuration or application object properties. Structured scalar values retain their types; other explicitly supplied values use their invariant string representation. Message and exception text can contain secrets. This formatter does not guarantee redaction.

During registration, applications can set `options.IncludeProperty = name => ...` to allowlist structured event/scope property names, and `options.TransformText = text => ...` to transform string values, messages, templates, categories, event names, scope states and exception text before JSON encoding. Property filtering does **not** remove a value already included in the formatted message. Bootstrap uses defaults until registration; keep its messages minimal. A failing formatter/callback produces a JSON diagnostic with a `formattingError` type instead of silently dropping the event. These hooks are narrow application policy, not a general redaction framework.

Migration must preserve each application's existing allowlists, sanitization and authentication/protocol filters. Polaris's OpenIddict suppression and ASAP's specific OIDC-event filter are intentionally absent from shared defaults. There are no request logging middleware, remote sinks or telemetry integrations.

## Local verification

```powershell
dotnet restore Clc.Logging.slnx --locked-mode
dotnet build Clc.Logging.slnx -c Release --no-restore
dotnet test --solution Clc.Logging.slnx -c Release --no-build --no-restore
dotnet pack src/Clc.Logging/Clc.Logging.csproj -c Release --no-build --no-restore -o artifacts/packages
./scripts/Test-Package.ps1
```

The MSTest suite uses isolated temporary directories. `Test-Package.ps1` inspects package contents/metadata, restores both sample consumers into fresh projects with an isolated package cache, runs a real worker and loopback HTTP request, and checks their JSON events and shutdown output. These sample projects have package references, never project references. All dependencies except the local `Clc.Logging` artifact come from NuGet.org. Samples are outside the main solution so a clean library build does not depend on an already packed artifact. No consuming application or service is modified.

MIT; Central Library Consortium.
