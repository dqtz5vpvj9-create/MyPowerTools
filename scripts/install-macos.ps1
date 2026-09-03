<#
.SYNOPSIS
    Installs MyPowerTools.app and seeds the macOS OTA state.

.DESCRIPTION
    The bundle-copy and LaunchAgent work remains in install-macos-base.ps1. This wrapper adds the
    OTA state contract and accepts -SkipOtaState for transactional OTA replacement, where the
    signed channel manifest is downloaded and persisted by ota-update.ps1 after health succeeds.
#>
[CmdletBinding()]
param(
    [string]$SourceApp = '',
    [string]$ApplicationsRoot = '',
    [string]$DataRoot = '',
    [ValidateSet('stable', 'nightly', 'local')]
    [string]$Channel = 'stable',
    [switch]$SkipLaunchAgents,
    [switch]$SkipOtaState
)

$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) {
    throw 'MyPowerTools macOS installation must run on macOS.'
}

function Write-Utf8TextFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Value
    )

    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Get-OtaRuntimeIdentifier {
    $architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    if ($architecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) {
        return 'osx-arm64'
    }
    if ($architecture -eq [System.Runtime.InteropServices.Architecture]::X64) {
        return 'osx-x64'
    }
    throw "Unsupported macOS process architecture: $architecture"
}

$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$baseScript = Join-Path $PSScriptRoot 'install-macos-base.ps1'
if (-not (Test-Path -LiteralPath $baseScript -PathType Leaf)) {
    throw "Base macOS installer is missing: $baseScript"
}

$baseParameters = @{}
if (-not [string]::IsNullOrWhiteSpace($SourceApp)) {
    $baseParameters.SourceApp = $SourceApp
}
if (-not [string]::IsNullOrWhiteSpace($ApplicationsRoot)) {
    $baseParameters.ApplicationsRoot = $ApplicationsRoot
}
if (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
    $baseParameters.DataRoot = $DataRoot
}
if ($SkipLaunchAgents) {
    $baseParameters.SkipLaunchAgents = $true
}

$baseOutput = @(& $baseScript @baseParameters | ForEach-Object { [string]$_ })
if ($LASTEXITCODE -ne 0) {
    throw "Base macOS installer failed with exit code $LASTEXITCODE"
}

if ([string]::IsNullOrWhiteSpace($ApplicationsRoot)) {
    $ApplicationsRoot = Join-Path $userProfile 'Applications'
}
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $userProfile 'Library/Application Support/MyPowerTools'
}
$ApplicationsRootFull = [IO.Path]::GetFullPath($ApplicationsRoot)
$DataRootFull = [IO.Path]::GetFullPath($DataRoot)
$targetApp = Join-Path $ApplicationsRootFull 'MyPowerTools.app'

if (-not $SkipOtaState) {
    $infoBase = Join-Path $targetApp 'Contents/Info'
    $version = (& /usr/bin/defaults 'read' $infoBase 'CFBundleShortVersionString').Trim()
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "Installed app bundle has an invalid CFBundleShortVersionString: $version"
    }

    $stateRoot = Join-Path $DataRootFull 'ota-state'
    New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
    $manifestPath = Join-Path $stateRoot 'installed-files.manifest.json'
    $manifestScript = Join-Path $PSScriptRoot 'new-ota-file-manifest.ps1'
    if (-not (Test-Path -LiteralPath $manifestScript -PathType Leaf)) {
        throw "OTA manifest generator is missing: $manifestScript"
    }
    [void](& $manifestScript `
        -Root $targetApp `
        -OutputPath $manifestPath `
        -Version $version)

    $publicKeySource = Join-Path (Split-Path -Parent $PSScriptRoot) 'ota-signing-public-key.txt'
    if (Test-Path -LiteralPath $publicKeySource -PathType Leaf) {
        $publicKey = ([IO.File]::ReadAllText($publicKeySource, [Text.UTF8Encoding]::new($false))).Trim()
        if ($publicKey -match '^[0-9a-fA-F]{64}$') {
            Write-Utf8TextFile `
                -Path (Join-Path $stateRoot 'ota-signing-public-key.txt') `
                -Value $publicKey
        }
    }

    $release = [ordered]@{
        schemaVersion = 1
        product = 'MyPowerTools'
        version = $version
        channel = $Channel
        installedAt = [DateTimeOffset]::UtcNow.ToString('O')
        installDir = $targetApp
        dataRoot = $DataRootFull
        repository = 'https://github.com/dqtz5vpvj9-create/MyPowerTools'
        manifestPath = 'installed-files.manifest.json'
        manifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        packageKind = 'full-install'
        distributionMode = 'full'
        runtimeIdentifier = (Get-OtaRuntimeIdentifier)
    }
    Write-Utf8TextFile `
        -Path (Join-Path $stateRoot 'installed-release.json') `
        -Value ($release | ConvertTo-Json -Depth 5)
}

$baseOutput
