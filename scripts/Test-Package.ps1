param([string]$PackageVersion, [switch]$RequireBaseRuntime)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path $PSScriptRoot -Parent
if (!$PackageVersion) {
    $PackageVersion = dotnet msbuild (Join-Path $repository 'src/Clc.Logging/Clc.Logging.csproj') -getProperty:PackageVersion
    if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate PackageVersion.' }
}
$package = Join-Path $repository "artifacts/packages/Clc.Logging.$PackageVersion.nupkg"
if (!(Test-Path -LiteralPath $package)) { throw "Pack $package before running this script." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $entries = @($archive.Entries.FullName)
    foreach ($required in @('Clc.Logging.nuspec', 'lib/net10.0/Clc.Logging.dll', 'README.md', 'LICENSE')) {
        if ($required -notin $entries) { throw "Missing package entry: $required" }
    }
    # Only expected package infrastructure and the library's public artifact belong here.
    $allowed = '^(Clc\.Logging\.nuspec|README\.md|LICENSE|\[Content_Types\]\.xml|_rels/\.rels|lib/net10\.0/Clc\.Logging\.dll|package/services/metadata/core-properties/[^/]+\.psmdcp)$'
    foreach ($entry in $entries) {
        if ($entry -notmatch $allowed) { throw "Unexpected package entry: $entry" }
        $stream = $archive.GetEntry($entry).Open()
        $buffer = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($buffer)
            $bytes = $buffer.ToArray()
            foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
                if ($encoding.GetString($bytes) -match '(?i)[a-z]:[\\/]Users[\\/]') { throw "Machine path in package entry: $entry" }
            }
        }
        finally { $stream.Dispose(); $buffer.Dispose() }
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('Clc.Logging.nuspec').Open())
    try { $metadata = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $nuspec = [xml]$metadata
    if ($nuspec.package.metadata.id -cne 'Clc.Logging' -or $nuspec.package.metadata.version -cne $PackageVersion) {
        throw 'Package identity/version does not match the evaluated build version.'
    }
    if ($metadata -match '<frameworkReference\b') { throw 'Clc.Logging must not declare a shared framework requirement.' }
    foreach ($dependency in @('Clc.Configuration', 'Microsoft.Extensions.Hosting.Abstractions', 'Microsoft.Extensions.Logging', 'NLog', 'NLog.Extensions.Logging')) {
        if (!$metadata.Contains("id=`"$dependency`"")) { throw "Missing dependency $dependency" }
    }
    if ($metadata -match '(?i)(?<![a-z])[a-z]:[\\/]|ProjectReference') { throw 'Machine-specific dependency metadata found.' }
}
finally { $archive.Dispose() }

# Use fresh projects and an isolated package cache; Clc.Logging can resolve only
# from this artifact directory. No project reference or sibling package is usable.
$run = Join-Path ([IO.Path]::GetTempPath()) ('Clc.Logging.Package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
try {
    # Copy only the installed base runtime and native host into an isolated dotnet root.
    # Direct invocation of this host cannot resolve the machine's ASP.NET shared runtime.
    $installed = @(dotnet --list-runtimes | ForEach-Object {
        if ($_ -match '^Microsoft.NETCore.App (10\.[0-9]+\.[0-9]+) \[(.+)\]$') {
            [pscustomobject]@{ Version = [version]$Matches[1]; Directory = Join-Path $Matches[2] $Matches[1] }
        }
    } | Sort-Object Version -Descending)
    if ($installed.Count -eq 0) { throw 'Install the .NET 10 base runtime before package verification.' }
    $runtimeDirectory = $installed[0].Directory
    $dotnetRoot = Split-Path (Split-Path (Split-Path $runtimeDirectory -Parent) -Parent) -Parent
    $baseRoot = Join-Path $run 'base-runtime'
    $baseShared = Join-Path $baseRoot 'shared/Microsoft.NETCore.App'
    New-Item -ItemType Directory -Path $baseShared,(Join-Path $baseRoot 'host') -Force | Out-Null
    $hostName = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
    $baseHost = Join-Path $baseRoot $hostName
    Copy-Item -LiteralPath (Join-Path $dotnetRoot $hostName) -Destination $baseHost
    if (!$IsWindows) { & chmod +x $baseHost; if ($LASTEXITCODE -ne 0) { throw 'Could not make the isolated host executable.' } }
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'host/fxr') -Destination (Join-Path $baseRoot 'host') -Recurse
    Copy-Item -LiteralPath $runtimeDirectory -Destination $baseShared -Recurse
    $runtimes = @(& $baseHost --list-runtimes)
    if ($LASTEXITCODE -ne 0 -or $runtimes.Count -ne 1 -or $runtimes[0] -notlike 'Microsoft.NETCore.App *') {
        throw 'Isolated dotnet root must contain only Microsoft.NETCore.App.'
    }
    Write-Output "Isolated runtime inventory: $($runtimes[0])"
    $source = [System.Security.SecurityElement]::Escape((Split-Path $package -Parent))
    @"
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$source" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local"><package pattern="Clc.Logging" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $run 'NuGet.Config')
    Copy-Item -LiteralPath (Join-Path $repository 'Directory.Build.props') -Destination $run
    foreach ($kind in @('Worker', 'Web')) {
        $sample = Join-Path $repository "samples/Clc.Logging.$kind"
        $consumer = Join-Path $run $kind
        New-Item -ItemType Directory -Path $consumer | Out-Null
        Copy-Item -LiteralPath (Join-Path $sample "Clc.Logging.$kind.csproj"), (Join-Path $sample 'Program.cs') -Destination $consumer
        $project = Join-Path $consumer "Clc.Logging.$kind.csproj"
        $dataDirectory = Join-Path $run "$kind-data"
        dotnet restore $project --configfile (Join-Path $run 'NuGet.Config') --packages (Join-Path $run 'packages') "-p:ClcLoggingPackageVersion=$PackageVersion"
        if ($LASTEXITCODE -ne 0) { throw "$kind package restore failed." }
        dotnet build $project -c Release --no-restore "-p:ClcLoggingPackageVersion=$PackageVersion"
        if ($LASTEXITCODE -ne 0) { throw "$kind package build failed." }
        $assembly = Join-Path $consumer "bin/Release/net10.0/Clc.Logging.$kind.dll"
        $runtime = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($assembly, 'runtimeconfig.json')) -Raw | ConvertFrom-Json -AsHashtable
        $frameworks = @($runtime.runtimeOptions['framework']) + @($runtime.runtimeOptions['frameworks']) | Where-Object { $_ }
        $frameworkNames = @($frameworks | ForEach-Object { $_.name })
        $dependencies = Get-Content -LiteralPath ([IO.Path]::ChangeExtension($assembly, 'deps.json')) -Raw | ConvertFrom-Json -AsHashtable
        $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable
        if ($assets.libraries["Clc.Logging/$PackageVersion"].type -ne 'package') { throw 'Consumer did not use the packed artifact.' }
        $cachedLibrary = Join-Path $run "packages/clc.logging/$($PackageVersion.ToLowerInvariant())/lib/net10.0/Clc.Logging.dll"
        $informationalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($cachedLibrary).ProductVersion
        if ($informationalVersion -cnotmatch ('^' + [regex]::Escape($PackageVersion) + '($|\+)')) {
            throw "Packed assembly version '$informationalVersion' does not match '$PackageVersion'."
        }
        $archive = [IO.Compression.ZipFile]::OpenRead($package)
        try {
            $stream = $archive.GetEntry('lib/net10.0/Clc.Logging.dll').Open()
            try { $packedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ((Get-FileHash -LiteralPath $cachedLibrary -Algorithm SHA256).Hash -ne $packedHash) { throw 'Consumer assembly differs from packed library.' }
        }
        finally { $archive.Dispose() }
        if ($kind -eq 'Worker') {
            Write-Output "Worker runtimeconfig frameworks: $($frameworkNames -join ', ')"
            $frameworkReferences = @(@($assets.project.frameworks.Values | ForEach-Object { $_.frameworkReferences.Keys }) +
                @($assets.targets.Values | ForEach-Object { $_.Values | ForEach-Object { $_['frameworkReferences'] } }) |
                Where-Object { $_ } | Sort-Object -Unique)
            Write-Output "Worker restored framework references: $($frameworkReferences -join ', ')"
            if ('Microsoft.AspNetCore.App' -in $frameworkNames) {
                # Verify the known upstream blocker against the actual base-runtime host.
                $failure = & $baseHost $assembly $dataDirectory 2>&1 | Out-String
                if ($LASTEXITCODE -eq 0 -or $failure -notmatch 'Microsoft.AspNetCore.App') { throw 'Expected the upstream ASP.NET runtime requirement to prevent startup.' }
                if ($RequireBaseRuntime) { throw 'Release blocked: Clc.Configuration still requires Microsoft.AspNetCore.App. Update to a published dependency fix before releasing.' }
                Write-Warning 'Worker base-runtime execution is blocked by the published Clc.Configuration dependency; release verification will fail.'
                dotnet $assembly $dataDirectory
            }
            else {
                if ($frameworkNames.Count -ne 1 -or $frameworkNames[0] -ne 'Microsoft.NETCore.App' -or
                    'Microsoft.AspNetCore.App' -in $frameworkReferences -or
                    @($dependencies.libraries.Keys | Where-Object { $_ -like 'Microsoft.AspNetCore.*' }).Count -ne 0) {
                    throw 'Worker must depend only on the base shared framework and Microsoft.Extensions packages.'
                }
                & $baseHost $assembly $dataDirectory
            }
        }
        else {
            if ('Microsoft.AspNetCore.App' -notin $frameworkNames) { throw 'Web SDK consumer lost its ASP.NET framework reference.' }
            dotnet $assembly $dataDirectory
        }
        if ($LASTEXITCODE -ne 0) { throw "$kind package consumer failed." }
        $logs = @(Get-ChildItem -LiteralPath (Join-Path $dataDirectory 'Logs') -Filter '*.jsonl')
        $records = @($logs | Get-Content | ForEach-Object { $_ | ConvertFrom-Json })
        $expectedEventId = if ($kind -eq 'Worker') { 10 } else { 20 }
        $events = @($records | Where-Object eventId -eq $expectedEventId)
        if ($events.Count -ne 1) { throw "$kind expected one structured event, found $($events.Count)." }
        if ($events[0].scope.CorrelationId -ne "$($kind.ToLowerInvariant())-smoke") { throw "$kind scope was not preserved." }
        if ($kind -eq 'Worker' -and 'Worker disposed' -notin $records.message) { throw 'Worker disposal event was lost.' }
        Write-Output "${kind}: verified package dependency, structured output and orderly shutdown."
    }
}
finally {
    # Delete only the exact fresh temporary directory created above, in this shell.
    $resolved = [IO.Path]::GetFullPath($run)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notlike 'Clc.Logging.Package-*') { throw 'Unexpected temporary cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Write-Output 'Package inspection and hosted consumers passed; any base-runtime release blocker is reported above.'
