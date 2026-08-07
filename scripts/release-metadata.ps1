param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArtifactsRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release'),
    [string]$Version = '',
    [string]$Channel = 'stable',
    [string]$DownloadBaseUrl = '',
    [switch]$PreferGitTag
)

$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$ArtifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'get-product-version.ps1'
    $versionParams = @{
        RepoRoot = $RepoRoot
    }
    if ($PreferGitTag) {
        $versionParams.PreferGitTag = $true
    }
    $versionOutput = @(& $versionScript @versionParams | ForEach-Object { [string]$_ })
    $versionObject = ($versionOutput -join [Environment]::NewLine) | ConvertFrom-Json
    $Version = [string]$versionObject.version
    if (-not $PSBoundParameters.ContainsKey('Channel')) {
        $Channel = [string]$versionObject.channel
    }
}

$artifactName = 'MyPowerTools-win-x64.zip'
$zipPath = Join-Path $ArtifactsRoot $artifactName
$metadataPath = Join-Path $ArtifactsRoot 'release-metadata.json'
$scoopRoot = Join-Path $ArtifactsRoot 'package-managers\scoop'
$scoopPath = Join-Path $scoopRoot 'mypowertools.json'
$manifestAssetName = 'MyPowerTools-win-x64.manifest.json'
$manifestPath = Join-Path $ArtifactsRoot $manifestAssetName
$feedAssetName = "channel-$Channel.json"
$feedPath = Join-Path $ArtifactsRoot $feedAssetName
$feedSignatureAssetName = "$feedAssetName.sig"

if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Release zip was not found at $zipPath"
}

New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $scoopRoot -Force | Out-Null

$zipItem = Get-Item -LiteralPath $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$manifestSha256 = $null
$feedSha256 = $null
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
if (Test-Path -LiteralPath $feedPath -PathType Leaf) {
    $feedSha256 = (Get-FileHash -LiteralPath $feedPath -Algorithm SHA256).Hash.ToLowerInvariant()
}
$readmeUrl = if ([string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
    'README.md'
} else {
    $DownloadBaseUrl.TrimEnd('/')
}
$artifactUrl = if ([string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
    $artifactName
} else {
    $base = $DownloadBaseUrl.TrimEnd('/')
    "$base/$artifactName"
}
$generatedAt = [DateTimeOffset]::UtcNow.ToString('O')

$metadata = [ordered]@{
    schemaVersion = '1.1'
    product = 'MyPowerTools'
    version = $Version
    channel = $Channel
    generatedAt = $generatedAt
    artifacts = @(
        [ordered]@{
            rid = 'win-x64'
            type = 'portable-zip'
            path = 'artifacts/release/MyPowerTools-win-x64.zip'
            url = $artifactUrl
            sha256 = $zipHash
            size = $zipItem.Length
            manifest = [ordered]@{
                asset = $manifestAssetName
                path = 'artifacts/release/MyPowerTools-win-x64.manifest.json'
                sha256 = $manifestSha256
            }
            startHere = 'START_HERE.md'
            portableStart = 'Start-MyPowerTools.cmd'
            installScript = 'install-windows.ps1'
            uninstallScript = 'uninstall-windows.ps1'
        }
    )
    update = [ordered]@{
        latestVersion = $Version
        channel = $Channel
        feed = $feedAssetName
        feedSignature = $feedSignatureAssetName
        feedSha256 = $feedSha256
        fullManifest = $manifestAssetName
        releaseNotes = 'RELEASE_NOTES.md'
        requiresProductionSignature = $true
        productionSignatureState = if (Test-Path -LiteralPath "$feedPath.sig") { 'feed-signed' } else { 'unsigned' }
    }
    packageManagers = [ordered]@{
        scoop = 'package-managers/scoop/mypowertools.json'
    }
}

$scoop = [ordered]@{
    version = $Version
    description = 'Personal PowerToys-style tools platform with Runner, Avalonia Shell, modules, brokers, and package validation.'
    homepage = $readmeUrl
    license = 'SEE README'
    architecture = [ordered]@{
        '64bit' = [ordered]@{
            url = $artifactUrl
            hash = $zipHash
        }
    }
    bin = @(
        ,@('Cli/MyPowerTools.Cli.exe', 'mpt')
    )
    shortcuts = @(
        ,@('MyPowerTools.exe', 'MyPowerTools')
    )
    notes = 'Open START_HERE.md after extraction. The installer creates one Start menu shortcut named MyPowerTools; CLI and Runner stay advanced package tools.'
}

$jsonOptions = @{ Depth = 8 }
$metadata | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath $metadataPath -Encoding UTF8
$scoop | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath $scoopPath -Encoding UTF8

Write-Host $metadataPath
Write-Host $scoopPath
