[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageId,
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [ValidateSet('stable', 'nightly', 'local')][string]$Channel = 'stable',
    [string]$Version = '',
    [string]$CoreMinimumVersion = '',
    [string]$OutputDir = '',
    [string]$PublishedAtUtc = '',
    [string]$SigningKeyPath = '',
    [string]$SigningKeyBase64 = '',
    [string]$PublicKeyOutputPath = '',
    [switch]$AllowUnsigned,
    [string]$HistoryFeedPath = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Read-Utf8TextFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
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

function ConvertTo-Ed25519KeyBytes {
    param([Parameter(Mandatory = $true)][string]$Value)

    $trimmed = $Value.Trim()
    if ($trimmed -match '^[0-9a-fA-F]{64}$') {
        $bytes = [byte[]]::new(32)
        for ($index = 0; $index -lt 32; $index++) {
            $bytes[$index] = [Convert]::ToByte($trimmed.Substring($index * 2, 2), 16)
        }
        return $bytes
    }
    try {
        $bytes = [Convert]::FromBase64String($trimmed)
        if ($bytes.Length -eq 32) {
            return $bytes
        }
    }
    catch {
    }
    throw 'Ed25519 signing key must be a 32-byte seed encoded as 64 hex characters or base64.'
}

function Compare-OtaVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    $leftParts = $Left.Split('.') | ForEach-Object { [int]$_ }
    $rightParts = $Right.Split('.') | ForEach-Object { [int]$_ }
    for ($index = 0; $index -lt 3; $index++) {
        if ($leftParts[$index] -lt $rightParts[$index]) { return -1 }
        if ($leftParts[$index] -gt $rightParts[$index]) { return 1 }
    }
    return 0
}

$packageDirFull = [IO.Path]::GetFullPath($PackageDir)
$moduleJsonPath = Join-Path $packageDirFull 'module.json'
$packageJsonPath = Join-Path $packageDirFull 'package.json'
$manifestPath = if (Test-Path -LiteralPath $moduleJsonPath -PathType Leaf) {
    $moduleJsonPath
} elseif (Test-Path -LiteralPath $packageJsonPath -PathType Leaf) {
    $packageJsonPath
} else {
    throw "Package directory has neither module.json nor package.json: $packageDirFull"
}
$module = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$moduleId = [string]$module.id
if ($moduleId -ne $PackageId) {
    throw "Package id '$moduleId' does not match requested '$PackageId'."
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [string]$module.version
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Package '$PackageId' has an invalid version '$Version'."
}
if ([string]::IsNullOrWhiteSpace($CoreMinimumVersion)) {
    if ($null -ne $module.host -and -not [string]::IsNullOrWhiteSpace([string]$module.host.minVersion)) {
        $CoreMinimumVersion = [string]$module.host.minVersion
    } elseif ($null -ne $module.minHostVersion -and -not [string]::IsNullOrWhiteSpace([string]$module.minHostVersion)) {
        $CoreMinimumVersion = [string]$module.minHostVersion
    } else {
        $CoreMinimumVersion = '0.0.0'
    }
}
if ($CoreMinimumVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Package '$PackageId' has an invalid core minimum version '$CoreMinimumVersion'."
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path (Split-Path -Parent $packageDirFull) 'package-feeds'
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$outputFull = [IO.Path]::GetFullPath($OutputDir)
$archiveName = "$PackageId-$Version.mptpkg.zip"
$archivePath = Join-Path $outputFull $archiveName

if (Test-Path -LiteralPath $archivePath -PathType Leaf) {
    Remove-Item -LiteralPath $archivePath -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $packageDirFull,
    $archivePath,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)

$archiveItem = Get-Item -LiteralPath $archivePath
$archiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(
    "$archivePath.sha256",
    "$archiveSha256  $archiveName`n",
    [Text.UTF8Encoding]::new($false))

if ([string]::IsNullOrWhiteSpace($PublishedAtUtc)) {
    $PublishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}

$history = @()
if (-not [string]::IsNullOrWhiteSpace($HistoryFeedPath) -and
    (Test-Path -LiteralPath $HistoryFeedPath -PathType Leaf)) {
    $historyFeed = Get-Content -LiteralPath $HistoryFeedPath -Raw | ConvertFrom-Json
    if ([string]$historyFeed.kind -eq 'mypowertools-ota-package-feed') {
        $history = @($historyFeed.releases |
            Where-Object { [string]$_.version -ne $Version })
    }
}
$historySorted = @($history | Sort-Object {
    $parts = ([string]$_.version).Split('.') | ForEach-Object { [int]$_ }
    return $parts[0] * 1000000 + $parts[1] * 1000 + $parts[2]
} -Descending)
$releases = @(
    [ordered]@{
        version = $Version
        asset = $archiveName
        sha256 = $archiveSha256
        size = $archiveItem.Length
        publishedAtUtc = $PublishedAtUtc
    }
) + $historySorted

$signingKeyBytes = $null
if (-not [string]::IsNullOrWhiteSpace($SigningKeyPath)) {
    $signingKeyFull = [IO.Path]::GetFullPath($SigningKeyPath)
    if (-not (Test-Path -LiteralPath $signingKeyFull -PathType Leaf)) {
        throw "Ed25519 signing key file does not exist: $signingKeyFull"
    }
    $SigningKeyBase64 = Read-Utf8TextFile -Path $signingKeyFull
}
if (-not [string]::IsNullOrWhiteSpace($SigningKeyBase64)) {
    $signingKeyBytes = ConvertTo-Ed25519KeyBytes -Value $SigningKeyBase64
}
if ($null -eq $signingKeyBytes -and -not $AllowUnsigned) {
    throw 'Package feed signing requires -SigningKeyPath or -SigningKeyBase64 (or -AllowUnsigned for local/nightly builds).'
}

$publicKeyHex = ''
$signed = $false
if ($null -ne $signingKeyBytes) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ed25519.cs')
    $publicKeyBytes = [Mpt.Ed25519]::PublicKeyFromPrivate($signingKeyBytes)
    $publicKeyHex = ($publicKeyBytes | ForEach-Object { $_.ToString('x2') }) -join ''
    $signed = $true
}

$feedName = "channel-package-$PackageId.json"
$feedPath = Join-Path $outputFull $feedName
$feed = [ordered]@{
    schemaVersion = 1
    kind = 'mypowertools-ota-package-feed'
    packageId = $PackageId
    channel = $Channel
    version = $Version
    coreMinimumVersion = $CoreMinimumVersion
    publishedAtUtc = $PublishedAtUtc
    releases = $releases
    signing = [ordered]@{
        algorithm = 'ed25519'
        signed = $signed
        publicKeyHex = $publicKeyHex
        signatureAsset = if ($signed) { "$feedName.sig" } else { '' }
    }
}
$feedJson = $feed | ConvertTo-Json -Depth 8
Write-Utf8TextFile -Path $feedPath -Value $feedJson

$signaturePath = ''
if ($signed) {
    $feedBytes = [Text.Encoding]::UTF8.GetBytes($feedJson)
    $signature = [Mpt.Ed25519]::Sign($feedBytes, $signingKeyBytes)
    $signaturePath = "$feedPath.sig"
    Write-Utf8TextFile -Path $signaturePath -Value ([Convert]::ToBase64String($signature))
    if ([string]::IsNullOrWhiteSpace($PublicKeyOutputPath)) {
        $PublicKeyOutputPath = "$feedPath.public-key.txt"
    }
    $publicKeyFull = [IO.Path]::GetFullPath($PublicKeyOutputPath)
    Write-Utf8TextFile -Path $publicKeyFull -Value $publicKeyHex
} else {
    $publicKeyFull = ''
}

[pscustomobject]@{
    PackageId = $PackageId
    Version = $Version
    CoreMinimumVersion = $CoreMinimumVersion
    FeedPath = $feedPath
    SignaturePath = $signaturePath
    PublicKeyPath = $publicKeyFull
    ArchivePath = $archivePath
    ArchiveSha256 = $archiveSha256
    Signed = $signed
    ReleaseCount = $releases.Count
} | ConvertTo-Json -Depth 5
