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
$feed = Join-Path $repo 'artifacts/sdk/nuget'
New-Item -ItemType Directory -Force -Path $feed, $ResultsDirectory | Out-Null
$cache = Join-Path $repo 'artifacts/sdk/global-packages'
if (Test-Path $cache) {
    Get-ChildItem $cache -Directory -Filter 'mypowertools.*' | Remove-Item -Recurse -Force
}
# Build the same SDK package set as build-sdk.ps1; CLI/npm packaging is not needed
# for these focused Surface tests. The existing full CI remains unchanged.
$projects = @(
    'MyPowerTools.Platform.Abstractions', 'MyPowerTools.Abstractions',
    'MyPowerTools.Protocol', 'MyPowerTools.Ipc.Shared', 'MyPowerTools.Packaging',
    'MyPowerTools.Platform.Windows', 'MyPowerTools.Broker', 'MyPowerTools.UI.Primitives',
    'MyPowerTools.UI', 'MyPowerTools.HostControl.Contracts', 'MyPowerTools.HostControl.Client',
    'MyPowerTools.ServiceManager.Client', 'MyPowerTools.AvaloniaSdk'
)
foreach ($project in $projects) {
    & dotnet pack (Join-Path $repo "src/$project/$project.csproj") --configuration $Configuration --output $feed --maxcpucount
    if ($LASTEXITCODE -ne 0) { throw "SDK pack failed: $project" }
}
& dotnet test (Join-Path $repo 'tests/PersonalUx.Tests/PersonalUx.Tests.csproj') --configuration $Configuration --logger 'trx;LogFileName=personal-ux.trx' --results-directory $ResultsDirectory -p:BuildWindowsWebToolHost=false
if ($LASTEXITCODE -ne 0) { throw 'Personal UX regression tests failed.' }
