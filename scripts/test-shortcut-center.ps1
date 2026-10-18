[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$ResultsDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $ResultsDirectory = Join-Path $temporaryRoot 'mypowertools-shortcut-results'
}
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
& (Join-Path $PSScriptRoot 'prepare-focused-test-sdk.ps1') -Configuration $Configuration
& dotnet test (Join-Path $repo 'tests/PersonalUx.Tests/PersonalUx.Tests.csproj') --configuration $Configuration --logger 'trx;LogFileName=personal-ux.trx' --results-directory $ResultsDirectory -p:BuildWindowsWebToolHost=false "-p:RestorePackagesPath=$(Join-Path $repo 'artifacts/sdk/global-packages')"
if ($LASTEXITCODE -ne 0) { throw 'Shortcut center and personal UX regression tests failed.' }
