[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [Parameter(Mandatory = $true)][string]$StagingDir,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$HashMarkerPath = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Read-Utf8TextFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
}

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "ASSERT: $Message"
    }
}

$ZipPath = [IO.Path]::GetFullPath($ZipPath)
$StagingDir = [IO.Path]::GetFullPath($StagingDir)
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if ([string]::IsNullOrWhiteSpace($HashMarkerPath)) {
    $HashMarkerPath = "$ZipPath.sha256"
}
$HashMarkerPath = [IO.Path]::GetFullPath($HashMarkerPath)

Assert-True -Condition (Test-Path -LiteralPath $ZipPath -PathType Leaf) -Message "ZIP is missing: $ZipPath"
Assert-True -Condition (Test-Path -LiteralPath $HashMarkerPath -PathType Leaf) -Message "SHA-256 marker is missing: $HashMarkerPath"

$expectedHash = (Read-Utf8TextFile -Path $HashMarkerPath).Trim()
$expectedHash = [regex]::Match($expectedHash, '^([0-9a-fA-F]{64})').Groups[1].Value.ToLowerInvariant()
Assert-True -Condition ($expectedHash -match '^[0-9a-f]{64}$') -Message "SHA-256 marker is malformed: $HashMarkerPath"
$actualHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-True -Condition ($actualHash -eq $expectedHash) -Message "ZIP SHA-256 mismatch. expected=$expectedHash actual=$actualHash"

if (Test-Path -LiteralPath $StagingDir -PathType Container) {
    Remove-Item -LiteralPath $StagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $StagingDir)

$installScript = Join-Path $StagingDir 'install-windows.ps1'
$shippedManifest = Join-Path $StagingDir 'MyPowerTools-win-x64.manifest.json'
Assert-True -Condition (Test-Path -LiteralPath $installScript -PathType Leaf) -Message "Staging is missing install-windows.ps1"
Assert-True -Condition (Test-Path -LiteralPath $shippedManifest -PathType Leaf) -Message "Staging is missing MyPowerTools-win-x64.manifest.json"
$manifest = Get-Content -LiteralPath $shippedManifest -Raw | ConvertFrom-Json
$newVersion = [string]$manifest.version
Assert-True -Condition ($newVersion -match '^[0-9]+\.[0-9]+\.[0-9]+$') -Message "Release manifest has an invalid version '$newVersion'"

& $installScript `
    -PackageRoot $StagingDir `
    -InstallDir $InstallDir `
    -DataRoot $DataRoot `
    -NoOpenApp `
    -StartRunner
if ($LASTEXITCODE -ne 0) {
    throw "install-windows.ps1 failed with exit code $LASTEXITCODE."
}

$installManifestPath = Join-Path $InstallDir 'install.manifest.json'
Assert-True -Condition (Test-Path -LiteralPath $installManifestPath -PathType Leaf) -Message "install.manifest.json was not created"
$installManifest = Get-Content -LiteralPath $installManifestPath -Raw | ConvertFrom-Json
Assert-True -Condition ([string]$installManifest.version -eq $newVersion) -Message "Installed version $($installManifest.version) does not match release $newVersion"

$otaStateDir = Join-Path $DataRoot 'ota-state'
$installedReleasePath = Join-Path $otaStateDir 'installed-release.json'
$installedFilesManifest = Join-Path $otaStateDir 'installed-files.manifest.json'
$installedPublicKey = Join-Path $otaStateDir 'ota-signing-public-key.txt'
Assert-True -Condition (Test-Path -LiteralPath $installedReleasePath -PathType Leaf) -Message "installed-release.json was not created"
Assert-True -Condition (Test-Path -LiteralPath $installedFilesManifest -PathType Leaf) -Message "installed-files.manifest.json was not created"
Assert-True -Condition (Test-Path -LiteralPath $installedPublicKey -PathType Leaf) -Message "ota-signing-public-key.txt was not created"
$installedRelease = Get-Content -LiteralPath $installedReleasePath -Raw | ConvertFrom-Json
Assert-True -Condition ([string]$installedRelease.version -eq $newVersion) -Message "installed-release version $($installedRelease.version) does not match release $newVersion"

$installedManifestHash = (Get-FileHash -LiteralPath $installedFilesManifest -Algorithm SHA256).Hash.ToLowerInvariant()
$shippedManifestHash = (Get-FileHash -LiteralPath $shippedManifest -Algorithm SHA256).Hash.ToLowerInvariant()
Assert-True -Condition ($installedManifestHash -eq $shippedManifestHash) -Message "installed OTA manifest differs from the release manifest"

$criticalPaths = @(
    'Runner\MyPowerTools.Runner.exe',
    'Shell\MyPowerTools.Shell.Avalonia.exe',
    'Cli\MyPowerTools.Cli.exe',
    'ServiceManager\MyPowerTools.ServiceManager.exe',
    'MyPowerTools.exe',
    'ota-update.ps1',
    'ed25519.cs',
    'package-ota-update.ps1'
)
$missing = @($criticalPaths | Where-Object {
    -not (Test-Path -LiteralPath (Join-Path $InstallDir $_) -PathType Leaf)
})
Assert-True -Condition ($missing.Count -eq 0) -Message "Critical installed files are missing: $($missing -join ', ')"

[pscustomobject]@{
    success = $true
    host = $env:COMPUTERNAME
    version = $newVersion
    installDir = $InstallDir
    dataRoot = $DataRoot
    zipSha256 = $actualHash
    manifestSha256 = $shippedManifestHash
    otaStateReady = $true
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
} | ConvertTo-Json -Depth 5
