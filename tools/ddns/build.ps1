[CmdletBinding()]
param(
    [string]$MyPowerToolsRepoRoot,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$toolRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$stageRoot = Join-Path $toolRoot 'artifacts\package'

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $stageRoot 'bin') -Force | Out-Null

Copy-Item -LiteralPath (Join-Path $toolRoot 'ddns.ps1') -Destination (Join-Path $stageRoot 'bin\ddns.ps1') -Force
Copy-Item -LiteralPath (Join-Path $toolRoot 'ddns-config.example.json') -Destination (Join-Path $stageRoot 'bin\ddns-config.example.json') -Force

[ordered]@{
    schemaVersion = '1.0'
    id = 'ddns'
    packageId = 'ddns'
    displayName = 'DDNS (Tencent DNSPod)'
    version = '0.1.0'
    moduleSdk = '1.0'
    entrypoints = @()
    capabilities = @('status', 'settings', 'logs')
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stageRoot 'module.json') -Encoding UTF8

Write-Host $stageRoot
