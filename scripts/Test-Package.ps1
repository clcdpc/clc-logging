param([string]$PackageVersion = '0.0.0-local')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path $PSScriptRoot -Parent
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
    foreach ($dependency in @('Clc.Configuration', 'NLog', 'NLog.Extensions.Logging')) {
        if (!$metadata.Contains("id=`"$dependency`"")) { throw "Missing dependency $dependency" }
    }
    if (!$metadata.Contains('0.1.0-beta.4')) { throw 'Published Clc.Configuration version is missing.' }
    if ($metadata -match '(?i)(?<![a-z])[a-z]:[\\/]|ProjectReference') { throw 'Machine-specific dependency metadata found.' }
}
finally { $archive.Dispose() }

# Use fresh projects and an isolated package cache; Clc.Logging can resolve only
# from this artifact directory. No project reference or sibling package is usable.
$run = Join-Path ([IO.Path]::GetTempPath()) ('Clc.Logging.Package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
try {
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
        dotnet $assembly $dataDirectory
        if ($LASTEXITCODE -ne 0) { throw "$kind package consumer failed." }
        $logs = @(Get-ChildItem -LiteralPath (Join-Path $dataDirectory 'Logs') -Filter '*.jsonl')
        $records = @($logs | Get-Content | ForEach-Object { $_ | ConvertFrom-Json })
        $expectedEventId = if ($kind -eq 'Worker') { 10 } else { 20 }
        $events = @($records | Where-Object eventId -eq $expectedEventId)
        if ($events.Count -ne 1) { throw "$kind expected one structured event, found $($events.Count)." }
        if ($events[0].scope.CorrelationId -ne "$($kind.ToLowerInvariant())-smoke") { throw "$kind scope was not preserved." }
        if ($kind -eq 'Worker' -and 'Worker disposed' -notin $records.message) { throw 'Worker disposal event was lost.' }
        $assets = Get-Content -LiteralPath (Join-Path $consumer 'obj/project.assets.json') -Raw | ConvertFrom-Json
        if ($assets.libraries."Clc.Logging/$PackageVersion".type -ne 'package') { throw 'Consumer did not use the packed artifact.' }
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
Write-Output 'Package inspection and both package-backed consumers passed.'
