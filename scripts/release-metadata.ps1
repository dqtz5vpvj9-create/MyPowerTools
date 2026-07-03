param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ArtifactsRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release'),
    [string]$Version = '0.2.0',
    [string]$DownloadBaseUrl = ''
)

$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)
$ArtifactsRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
$artifactName = 'MyPowerTools-win-x64.zip'
$zipPath = Join-Path $ArtifactsRoot $artifactName
$metadataPath = Join-Path $ArtifactsRoot 'release-metadata.json'
$scoopRoot = Join-Path $ArtifactsRoot 'package-managers\scoop'
$scoopPath = Join-Path $scoopRoot 'mypowertools.json'

if (-not (Test-Path -LiteralPath $zipPath)) {
    throw "Release zip was not found at $zipPath"
}

New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null
New-Item -ItemType Directory -Path $scoopRoot -Force | Out-Null

$zipItem = Get-Item -LiteralPath $zipPath
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
$readmePath = Join-Path $RepoRoot 'README.md'
$readmeUrl = if (Test-Path -LiteralPath $readmePath) {
    ([System.Uri]::new($readmePath)).AbsoluteUri
} else {
    ([System.Uri]::new($RepoRoot)).AbsoluteUri
}
$artifactUrl = if ([string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
    ([System.Uri]::new($zipPath)).AbsoluteUri
} else {
    $base = $DownloadBaseUrl.TrimEnd('/')
    "$base/$artifactName"
}
$generatedAt = [DateTimeOffset]::UtcNow.ToString('O')

$metadata = [ordered]@{
    schemaVersion = '1.0'
    product = 'MyPowerTools'
    version = $Version
    channel = if ([string]::IsNullOrWhiteSpace($DownloadBaseUrl)) { 'local-portable' } else { 'portable' }
    generatedAt = $generatedAt
    artifacts = @(
        [ordered]@{
            rid = 'win-x64'
            type = 'portable-zip'
            path = 'artifacts/release/MyPowerTools-win-x64.zip'
            url = $artifactUrl
            sha256 = $zipHash
            size = $zipItem.Length
            installScript = 'install-windows.ps1'
            uninstallScript = 'uninstall-windows.ps1'
        }
    )
    update = [ordered]@{
        latestVersion = $Version
        feed = 'release-metadata.json'
        releaseNotes = 'RELEASE_NOTES.md'
        requiresProductionSignature = $true
        productionSignatureState = 'external'
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
        ,@('Shell/MyPowerTools.Shell.Avalonia.exe', 'MyPowerTools')
    )
    notes = 'Run install-windows.ps1 from the extracted package for shortcuts, autostart, and Runner launch options.'
}

$jsonOptions = @{ Depth = 8 }
$metadata | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath $metadataPath -Encoding UTF8
$scoop | ConvertTo-Json @jsonOptions | Set-Content -LiteralPath $scoopPath -Encoding UTF8

Write-Host $metadataPath
Write-Host $scoopPath
