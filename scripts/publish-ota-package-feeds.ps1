[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ModulePackagesRoot = '',
    [ValidateSet('stable', 'nightly', 'local')][string]$Channel = 'stable',
    [string]$SigningKeyPath = '',
    [string]$SigningKeyBase64 = '',
    [switch]$AllowUnsigned,
    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
if ([string]::IsNullOrWhiteSpace($ModulePackagesRoot)) {
    $ModulePackagesRoot = Join-Path $RepoRoot 'artifacts\release\module-packages'
}
$ModulePackagesRoot = [IO.Path]::GetFullPath($ModulePackagesRoot)
if (-not (Test-Path -LiteralPath $ModulePackagesRoot -PathType Container)) {
    throw "Module packages root does not exist: $ModulePackagesRoot"
}
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot 'artifacts\release\packages'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

$feedScript = Join-Path $PSScriptRoot 'new-ota-package-feed.ps1'
$results = [Collections.Generic.List[object]]::new()
foreach ($packageDir in Get-ChildItem -LiteralPath $ModulePackagesRoot -Directory | Sort-Object Name) {
    $moduleJsonPath = Join-Path $packageDir.FullName 'module.json'
    if (-not (Test-Path -LiteralPath $moduleJsonPath -PathType Leaf)) {
        continue
    }
    $module = Get-Content -LiteralPath $moduleJsonPath -Raw | ConvertFrom-Json
    $packageId = [string]$module.id
    if ([string]::IsNullOrWhiteSpace($packageId)) {
        throw "Module package has no id: $($packageDir.FullName)"
    }

    $packageOutput = Join-Path $OutputRoot $packageId
    $feedParams = @{
        PackageId = $packageId
        PackageDir = $packageDir.FullName
        Channel = $Channel
        OutputDir = $packageOutput
        AllowUnsigned = $AllowUnsigned
    }
    if (-not [string]::IsNullOrWhiteSpace($SigningKeyPath)) {
        $feedParams.SigningKeyPath = $SigningKeyPath
    }
    if (-not [string]::IsNullOrWhiteSpace($SigningKeyBase64)) {
        $feedParams.SigningKeyBase64 = $SigningKeyBase64
    }
    $resultLines = @(& $feedScript @feedParams | ForEach-Object { [string]$_ })
    $resultObject = ($resultLines -join [Environment]::NewLine) | ConvertFrom-Json
    $results.Add([ordered]@{
        packageId = $packageId
        version = [string]$resultObject.Version
        feed = "packages/$packageId/channel-package-$packageId.json"
        archive = "packages/$packageId/$([IO.Path]::GetFileName([string]$resultObject.ArchivePath))"
        sha256 = [string]$resultObject.ArchiveSha256
        signed = [bool]$resultObject.Signed
    })
}

if ($results.Count -eq 0) {
    throw 'No module packages were found for OTA package feeds.'
}

$manifest = [ordered]@{
    schemaVersion = 1
    kind = 'mypowertools-ota-packages-manifest'
    channel = $Channel
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    packages = $results.ToArray()
}
$manifestPath = Join-Path $OutputRoot 'packages-manifest.json'
[IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    ManifestPath = $manifestPath
    OutputRoot = $OutputRoot
    PackageCount = $results.Count
    Packages = $results.ToArray()
} | ConvertTo-Json -Depth 6
