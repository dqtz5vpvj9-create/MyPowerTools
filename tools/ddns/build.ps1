[CmdletBinding()]
param(
    [string]$MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$packageRoot = Join-Path $PSScriptRoot 'artifacts/package'
$packageBin = Join-Path $packageRoot 'bin'
$serviceBin = Join-Path $PSScriptRoot 'service-units/ddns.service/bin'
New-Item -ItemType Directory -Path $packageBin, $serviceBin -Force | Out-Null
foreach ($target in @($packageBin, $serviceBin)) {
    foreach ($file in @('ddns.ps1', 'ddns-config.example.json')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $target $file) -Force
    }
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'module.json') -Destination (Join-Path $packageRoot 'module.json') -Force
Write-Host "DDNS scripts staged: $packageRoot ($Configuration)"
