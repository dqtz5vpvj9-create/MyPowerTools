[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$PreferGitTag
)

$ErrorActionPreference = 'Stop'

$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
$versionFilePath = Join-Path $RepoRoot 'version.json'
if (-not (Test-Path -LiteralPath $versionFilePath -PathType Leaf)) {
    throw "Central version file is missing: $versionFilePath"
}

$versionData = Get-Content -LiteralPath $versionFilePath -Raw | ConvertFrom-Json
$product = [string]$versionData.product
$version = [string]$versionData.version
$channel = [string]$versionData.channel
$repository = [string]$versionData.repository
if ([string]::IsNullOrWhiteSpace($product)) {
    $product = 'MyPowerTools'
}
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Central version file contains an invalid version '$version'."
}
if ([string]::IsNullOrWhiteSpace($channel)) {
    $channel = 'stable'
}

$source = 'version.json'
if ($PreferGitTag) {
    $git = Get-Command 'git.exe' -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($git) {
        $tag = (& $git.Source -C $RepoRoot describe --tags --exact-match HEAD 2>$null |
            Select-Object -First 1)
        if ($LASTEXITCODE -eq 0 -and $tag -match '^v([0-9]+\.[0-9]+\.[0-9]+)$') {
            $version = $Matches[1]
            $source = 'git-tag'
        }
    }
}

$result = [ordered]@{
    schemaVersion = 1
    product = $product
    version = $version
    channel = $channel
    repository = $repository
    source = $source
}

$result | ConvertTo-Json -Depth 4
