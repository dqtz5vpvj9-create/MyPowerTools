[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $ResultsDirectory = Join-Path $temporaryRoot 'mypowertools-personal-ux-results'
}
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
& (Join-Path $PSScriptRoot 'prepare-focused-test-sdk.ps1') -Configuration $Configuration
# Shortcut fixtures share this host but retain their own namespace and focused entry point.
& dotnet test (Join-Path $repo 'tests/PersonalUx.Tests/PersonalUx.Tests.csproj') --configuration $Configuration --filter 'FullyQualifiedName!~ShortcutCenter.Tests.' --logger 'trx;LogFileName=personal-ux.trx' --results-directory $ResultsDirectory -p:BuildWindowsWebToolHost=false "-p:RestorePackagesPath=$(Join-Path $repo 'artifacts/sdk/global-packages')"
if ($LASTEXITCODE -ne 0) { throw 'Personal UX regression tests failed.' }
