$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository 'src/Clc.Logging/Clc.Logging.csproj'

# A stable package cannot depend on today's prerelease Configuration package.
# Verify that rejection; once the dependency is stable, consume both real artifacts.
foreach ($version in @('9.8.7', '9.8.7-smoke.4')) {
    $output = dotnet pack $project -c Release --no-restore -o (Join-Path $repository 'artifacts/packages') "-p:Version=$version" "-p:PackageVersion=$version" 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    Write-Output $output
    if ($exitCode -ne 0) {
        if ($version -eq '9.8.7' -and $output -match 'NU5104' -and $output -notmatch ': error (?!NU5104)' -and
            $output -match 'Clc.Configuration \[[^\]]+-[^\]]+\]') {
            Write-Warning 'Stable packing is correctly blocked by the prerelease Clc.Configuration dependency. A stable dependency is required for a stable release.'
            continue
        }
        throw "Could not pack release-version fixture $version."
    }
    & (Join-Path $PSScriptRoot 'Test-Package.ps1') -PackageVersion $version
}
