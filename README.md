# Clc.Logging

A lightweight .NET 10 setup library around NLog and Microsoft `ILogger<T>`. It removes repetitive path, file rotation, fallback and host integration code while leaving applications in control of layouts, targets, routing, providers and logging policy. There is no new logger interface, event model or configuration format. Release package versions come from Git tags; local builds default to `0.0.0-local`.

## Standard application

Reference `Clc.Logging`. Web applications can use their Web SDK host; workers also reference `Microsoft.Extensions.Hosting` to construct a host.

```csharp
using Clc.Configuration;
using Clc.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.AddClcConfiguration("clc-my-app");
builder.AddClcLogging();

await using var app = builder.Build();
await app.RunAsync();
```

For a worker, use `Host.CreateApplicationBuilder(args)`, the same two configuration/logging calls, register hosted services, and run `using var host = builder.Build(); await host.RunAsync();`. Workers require only `Microsoft.NETCore.App`; Clc.Logging has no ASP.NET Core framework dependency.

Both use the same `IHostApplicationBuilder` extension. Register once before `Build()`. The host owns this logging instance and disposes it with the host. Applications continue using ordinary `ILogger<T>`, message templates, scopes and `EventId`.

Clc.Configuration owns the application identity and external application configuration. After `AddClcConfiguration` or `AddClcConfigurationWithBootstrap` succeeds, parameterless `AddClcLogging()` retrieves the existing `ClcApplicationPaths`. Supply the directory name only once. Registration before successful initialization throws without invoking the callback or changing services, providers or filters. Logging does not infer identity from assemblies, executables, environment variables, configuration filenames or working directories. No `appsettings.json`, `nlog.config` or environment variable is required.

Explicit registration remains available without Clc.Configuration initialization:

```csharp
builder.AddClcLogging("clc-my-app");
// Or, with previously resolved ClcApplicationPaths:
builder.AddClcLogging(paths);
```

## Plaintext and files

The default uses NLog's built-in `SimpleLayout`:

```text
2026-11-14 17:15:32.481 -05:00 [INFO] MyApp.Services.CatalogService - Catalog refreshed successfully.
```

Entries include local time with milliseconds and UTC offset, uppercase NLog level, logger/category and rendered message. Exceptions use NLog's native rendering, including inner exceptions and stack traces; they may span multiple lines. Structured properties, scopes, event IDs and other metadata remain available to native custom layouts without being forced into ordinary plaintext. There is no package-specific JSON logging or serializer. Applications wanting JSON can supply a native NLog JSON layout.

Files are UTF-8 without a BOM, appended across restarts:

```text
<ApplicationDataDirectory>/Logs/<application-name>-yyyy-MM-dd.log
C:\ProgramData\clc-my-app\Logs\clc-my-app-2026-11-14.log
```

The last application-directory segment supplies the identity. Size rollover adds a sequence suffix such as `clc-my-app-2026-11-14.001.log`. Existing directory and filename text is literal, including `${...}`. `ClcLogging.CreateForDirectory(absoluteApplicationDataDirectory)` supports an already resolved directory and appends `Logs`.

Daily and size rollover, retention and archive limits use NLog's file target. Cleanup occurs on writes, with no background retention job. An individual event may exceed the size limit, and the active file may temporarily exist in addition to the archive cap. Unrelated application filenames are retained.

The package creates `Logs` using existing process permissions. It does not elevate, change ACLs or provision IIS/service identities. Successful conventional writes go to the file. If writing fails, NLog's `FallbackGroupTarget` sends the event to a native stderr `ConsoleTarget`, then retries the file on subsequent writes. Disabled file output goes directly to stderr. File and stderr share the effective layout. Retain stderr in the hosting environment if it should serve as a fallback record; failure of both destinations cannot guarantee persistence.

## Native customization

Set `Layout` to any native NLog layout. Null selects the default plaintext layout. The supplied instance is used directly, with normal NLog initialization and disposal, without wrapping, serialization or text transformation:

```csharp
using NLog.Layouts;

builder.AddClcConfiguration("clc-my-app");
builder.AddClcLogging(options =>
{
    options.Layout = new SimpleLayout("${level} | ${logger} | ${message}");
});
```

An application's existing `Layout` subclass, including an application-owned sanitizing layout, can be assigned the same way.

`ConfigureNLog` receives the actual instance-local `LoggingConfiguration` after conventional targets and rules are assembled and before activation. It runs once during configured-host registration, never during bootstrap. Use native NLog APIs to add, remove or replace targets, change layouts and modify routing or rules:

```csharp
using NLog.Targets;

builder.AddClcLogging(options =>
{
    options.ConfigureNLog = configuration =>
    {
        var file = configuration.FindTargetByName<FileTarget>("file");
        if (file is not null)
        {
            file.KeepFileOpen = false;
        }
        var console = new ConsoleTarget("application-console")
        {
            Layout = "${level} | ${logger} | ${message}",
            StdErr = true
        };
        configuration.AddRule(NLog.LogLevel.Error, NLog.LogLevel.Fatal, console);
    };
});
```

Conventional named targets are `file`, `stderr` and, when files are enabled, `file-with-stderr-fallback`. The default rule accepts Trace through Fatal, with normal `ILogger` filtering applied first. Native rules may further restrict or reroute those events. Removing a target's name alone does not remove references from native wrapper/rule collections; update those references too. To use only custom targets, clear conventional rules and add your own. `File.Enabled = false` does not prevent custom targets from working. Changes stay in this instance's `LogFactory`; the package never sets process-global `LogManager.Configuration`.

## Providers and filters

Registration adds one package-owned NLog provider alongside existing providers. It rejects duplicate Clc.Logging registration on the same builder. Retained providers with overlapping routing can deliver events to multiple destinations. Applications wanting only NLog can explicitly clear providers before registration:

```csharp
builder.Logging.ClearProviders();
builder.AddClcLogging();
```

The provider collection remains under application control after registration as well.

Standard Microsoft `Logging:LogLevel` configuration, provider-specific configuration and `AddFilter` rules remain effective. Existing rules are retained and rules added later work normally. Microsoft's provider specificity, longest category match and last equal-specificity rule determine the result. `None` disables matching categories. Configuration filters use Microsoft's normal reload behavior when the configuration provider supports it; conventional file options are read once at registration.

Without conflicting application policy, defaults are Information generally, Warning for `Microsoft`, Information for `Microsoft.Hosting.Lifetime`, and Warning for `System.Net.Http`. Category defaults fill gaps: explicit general `Default` or broader category rules take precedence, including over more-specific conventional defaults. Provider-specific rules keep native precedence. An application minimum other than Information is left alone and disables category-default insertion. General wildcard category rules also disable category-default insertion, leaving wildcard matching entirely to Microsoft. No provider-specific package defaults or forced minimum/maximum are installed.

`options.LogLevels` contains configured general levels. Callback additions or changed values append native Microsoft filter rules at registration; unchanged entries remain managed by Microsoft's configuration provider. Use `builder.Logging.AddFilter(...)` for advanced policy and provider filters. Clearing the convenience dictionary does not remove application configuration rules.

```csharp
builder.AddClcLogging(options =>
{
    options.LogLevels["MyApp.Diagnostics"] = LogLevel.Debug;
});
builder.Logging.AddFilter("MyApp.Diagnostics.Quiet", LogLevel.None);
```

## File settings

Optional settings belong in the existing external application configuration:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "MyApp.Diagnostics": "Debug"
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

The displayed file settings are the defaults. `Enabled` accepts a Boolean; file size is an integer from 1 KiB through 1 GiB, retention 1–3650 days, archive cap 1–10000 files. Unknown file settings and malformed values fail registration with the setting's name. General log levels must be named Microsoft levels, including `None`. The options callback runs after settings are read and validated; its resulting values are validated again. It can also change `FlushTimeout` from its five-second default to a value greater than zero and at most one minute.

## Early startup and lifetime

Use a caller-owned instance before host construction and configuration loading. Bootstrap uses stderr without creating application directories or replaying events into the configured file. Dispose the host first; the instance remains available for an outer fatal handler until its owner disposes it:

```csharp
using Clc.Configuration;
using Clc.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string applicationDirectoryName = "clc-my-app";
ClcLogging? logging = null;
try
{
    logging = ClcLogging.Create(applicationDirectoryName);
    logging.Logger.LogInformation("Starting application");
    var builder = Host.CreateApplicationBuilder(args);
    builder.AddClcConfiguration(applicationDirectoryName);
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

Caller-owned `Create`, `Create(paths)` and `CreateForDirectory` apply the options callback immediately to bootstrap defaults, then reapply it to fresh configuration-derived options during registration. This callback can execute twice and must not rely on one-time side effects. Assign an application-owned layout in that callback to preserve it in both phases, for example `ClcLogging.Create("clc-my-app", options => options.Layout = applicationSafeLayout)`.

Bootstrap never invokes `ConfigureNLog`. Host-owned registration executes its options callback only once, after configuration loading. Parameterless registration before Clc.Configuration initialization does not invoke it.

Each instance owns its NLog factory and never shuts down process-global NLog. Logging remains available through hosted-service stop and disposal. `Flush()` and `Dispose()` are safe to repeat. Writes are synchronous with file auto-flush; there is no package queue. A flush timeout cannot bound an operating system write already blocked.

`LogFatal()` bypasses Microsoft host/category filters and uses current NLog layouts and routing. Repeated calls with the same exception object are suppressed by that instance. It does not deduplicate exceptions previously logged through `ILogger`, arbitrary layouts or other providers. No automatic unhandled-exception logging is installed.

## Application policy and beta API changes

Applications own redaction, sanitization, credential handling and decisions about what may enter logs. Clc.Logging contains no redaction policy, secret-key list, request inspection or authentication-specific logic. Supply a native layout when output-boundary sanitization is required, including during caller-owned bootstrap. Other retained providers have their own output policies.

`WriteStartupFailure()` is an explicitly invoked, caller-controlled last resort when instance creation fails. It writes raw exception text directly to stderr, has no custom layout and is unsuitable for arbitrary sensitive exceptions without caller preparation.

Intentional earlier-beta changes: `ClcJsonLayout`, `ProcessOutputTarget`, `IncludeProperty` and `TransformText` are removed; conventional files use `.log`; providers and application filters are preserved; caller-owned callbacks run during bootstrap and registration; fatal deduplication covers repeated `LogFatal` calls only. Consumers such as `ssrs-web` can retain their own layout, but must review early messages, other providers and explicit raw startup-failure output under their own policy.

## Local verification

```powershell
dotnet restore Clc.Logging.slnx --locked-mode
dotnet build Clc.Logging.slnx -c Release --no-restore
dotnet test --solution Clc.Logging.slnx -c Release --no-build --no-restore
dotnet pack src/Clc.Logging/Clc.Logging.csproj -c Release --no-build --no-restore -o artifacts/packages
./scripts/Test-Package.ps1
./scripts/Test-ReleaseVersions.ps1
```

MSTest uses isolated temporary directories. `Test-Package.ps1` checks package contents, identity, repository/assembly metadata and hashes, restores fresh Worker and Web consumers from the artifact with an isolated package cache, and verifies plaintext and shutdown events. Worker also exercises a custom native layout, scopes/event properties and a target added through `ConfigureNLog`. Samples use package references, never project references; other dependencies come from NuGet.org.

The shared test-only `samples/ConfigurationFixture.cs` invokes the published Configuration assembly's existing internal initialization seams with temporary paths, then exercises public path retrieval and configuration-first logging registration. It avoids production ProgramData writes and adds no upstream API. Samples stay outside the main solution so clean builds do not depend on a previously packed artifact.

Package verification copies only the installed .NET 10 base runtime and native host into an isolated root, checks its runtime inventory, and runs both default and custom-layout Workers there. An ASP.NET framework dependency in the Worker runtime configuration or restored graph fails verification. `Test-ReleaseVersions.ps1` requires prerelease packing and consumption and verifies stable packing rejects the prerelease Configuration dependency with `NU5104`. Stable Logging packaging requires a suitable stable Configuration dependency. The package script evaluates local `PackageVersion` unless supplied `-PackageVersion <version>`.

## Release workflow

`.github/workflows/build-test-publish.yaml` follows the CLC `build-test-publish` pattern. Pull requests and pushes to `master` build/test on Windows and Linux, verify stable/prerelease package consumers, and pack/consume the evaluated CI version. Tags must be `v<major>.<minor>.<patch>` with an optional SemVer prerelease suffix, such as `v1.2.3-beta.2`; malformed versions, leading zeroes and build metadata are rejected. The tag supplies `Version` and `PackageVersion`; no project or README version edit is needed. Untagged CI uses `0.0.0-ci.<run>.<attempt>`.

Only a valid pushed version tag can publish, after both platforms pass all checks including mandatory base-runtime Worker execution. The publish job downloads the verified Linux artifact, checks its SHA-256 sidecar and tag, then obtains a short-lived NuGet key using `NuGet/login@v1`. An authorized version-tag push and successful validation allow unattended publishing without an additional human approval step. Ordinary `master` pushes and pull requests only validate. The Configuration runtime blocker is resolved by the published dependency; first-prerelease publishing still requires the external setup below.

Before the first release, an administrator must:

1. Confirm `Clc.Logging` package ID/prefix availability and ownership by the `clcdpc` NuGet organization.
2. Add a NuGet trusted publishing policy owned by `clcdpc`, scoped to `Clc.Logging`, with GitHub owner `clcdpc`, repository `clc-logging`, workflow file `build-test-publish.yaml` and environment `nuget-production`. Keep the existing CLC NuGet login identity `mfields` authorized for that organization and policy.
3. Create the GitHub `nuget-production` environment with selected deployment tags matching `v*` and no permitted branches. The workflow enforces the full valid SemVer tag format. Leave required environment reviewers and other manual approval gates disabled. Only the publish job receives `id-token: write`; no long-lived API key is needed.
4. Protect `master` from direct pushes, force pushes and deletion, and require changes through pull requests. Scope these repository rules to `master`; ordinary task and pull request branches need no additional restrictions.

These are required external setup steps; this change does not configure or verify them. See [NuGet trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).

MIT; Central Library Consortium.
