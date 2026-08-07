[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version,
    [ValidateSet('stable', 'nightly', 'local')][string]$Channel = 'stable',
    [Parameter(Mandatory = $true)][string]$FullZipPath,
    [Parameter(Mandatory = $true)][string]$FullManifestPath,
    [string[]]$DeltaPackages = @(),
    [string]$OutputPath = '',
    [string]$PublishedAtUtc = '',
    [string]$SigningKeyPath = '',
    [string]$SigningKeyBase64 = '',
    [string]$PublicKeyOutputPath = '',
    [switch]$AllowUnsigned,
    [string]$FullAssetName = 'MyPowerTools-win-x64.zip'
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

$fullZipFull = [IO.Path]::GetFullPath($FullZipPath)
$fullManifestFull = [IO.Path]::GetFullPath($FullManifestPath)
if (-not (Test-Path -LiteralPath $fullZipFull -PathType Leaf)) {
    throw "Full package zip does not exist: $fullZipFull"
}
if (-not (Test-Path -LiteralPath $fullManifestFull -PathType Leaf)) {
    throw "Full package manifest does not exist: $fullManifestFull"
}

$manifest = Get-Content -LiteralPath $fullManifestFull -Raw | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1 -or
    [string]$manifest.kind -ne 'mypowertools-ota-file-manifest') {
    throw "Unsupported full package manifest: $fullManifestFull"
}
if ([string]$manifest.version -ne $Version) {
    throw "Full package manifest version '$($manifest.version)' does not match feed version '$Version'."
}

$fullItem = Get-Item -LiteralPath $fullZipFull
$fullZipHash = (Get-FileHash -LiteralPath $fullZipFull -Algorithm SHA256).Hash.ToLowerInvariant()
$fullManifestHash = (Get-FileHash -LiteralPath $fullManifestFull -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestAssetName = [IO.Path]::GetFileName($fullManifestFull)

if ([string]::IsNullOrWhiteSpace($PublishedAtUtc)) {
    $PublishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$deltaRecords = [Collections.Generic.List[object]]::new()
foreach ($deltaPath in $DeltaPackages) {
    $deltaFull = [IO.Path]::GetFullPath($deltaPath)
    if (-not (Test-Path -LiteralPath $deltaFull -PathType Leaf)) {
        throw "Delta package does not exist: $deltaFull"
    }

    $plan = $null
    $archive = [IO.Compression.ZipFile]::OpenRead($deltaFull)
    try {
        $planEntry = $archive.Entries |
            Where-Object { $_.FullName -eq 'ota-plan.json' } |
            Select-Object -First 1
        if ($null -eq $planEntry) {
            throw "Delta package has no ota-plan.json: $deltaFull"
        }
        $reader = [IO.StreamReader]::new(
            $planEntry.Open(),
            [Text.UTF8Encoding]::new($false))
        try {
            $plan = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    if ([int]$plan.schemaVersion -ne 1 -or
        [string]$plan.kind -ne 'mypowertools-ota-delta-plan') {
        throw "Delta package contains an unsupported plan: $deltaFull"
    }
    if ([string]$plan.sourceVersion -ne $Version) {
        throw "Delta package '$([IO.Path]::GetFileName($deltaFull))' targets source version '$($plan.sourceVersion)' but the feed is '$Version'."
    }
    if ($plan.targetVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
        $plan.targetManifestSha256 -notmatch '^[0-9a-f]{64}$') {
        throw "Delta package contains invalid from-version metadata: $deltaFull"
    }

    $deltaItem = Get-Item -LiteralPath $deltaFull
    $deltaRecords.Add([ordered]@{
        fromVersion = [string]$plan.targetVersion
        fromManifestSha256 = ([string]$plan.targetManifestSha256).ToLowerInvariant()
        asset = [IO.Path]::GetFileName($deltaFull)
        sha256 = (Get-FileHash -LiteralPath $deltaFull -Algorithm SHA256).Hash.ToLowerInvariant()
        size = $deltaItem.Length
        sourceManifestSha256 = ([string]$plan.sourceManifestSha256).ToLowerInvariant()
    })
}

$deltaRecordsArray = @($deltaRecords | Sort-Object {
    $parts = $_.fromVersion.Split('.') | ForEach-Object { [int]$_ }
    return $parts[0] * 1000000 + $parts[1] * 1000 + $parts[2]
})

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
    throw 'OTA feed signing requires -SigningKeyPath or -SigningKeyBase64 (or -AllowUnsigned for local/nightly builds).'
}

$publicKeyHex = ''
$signed = $false
if ($null -ne $signingKeyBytes) {
    try {
        Add-Type -Path (Join-Path $PSScriptRoot 'ed25519.cs')
        $publicKeyBytes = [Mpt.Ed25519]::PublicKeyFromPrivate($signingKeyBytes)
        $publicKeyHex = ($publicKeyBytes | ForEach-Object { $_.ToString('x2') }) -join ''
        $signed = $true
    }
    catch {
        throw "Ed25519 feed signing failed: $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path (Split-Path -Parent $fullZipFull) "channel-$Channel.json"
}
$outputFull = [IO.Path]::GetFullPath($OutputPath)

$feed = [ordered]@{
    schemaVersion = 1
    kind = 'mypowertools-ota-channel-feed'
    product = [string]$manifest.product
    channel = $Channel
    version = $Version
    publishedAtUtc = $PublishedAtUtc
    full = [ordered]@{
        asset = $FullAssetName
        sha256 = $fullZipHash
        size = [long]$fullItem.Length
        manifestAsset = $manifestAssetName
        manifestSha256 = $fullManifestHash
    }
    deltas = $deltaRecordsArray
    signing = [ordered]@{
        algorithm = 'ed25519'
        signed = $signed
        publicKeyHex = $publicKeyHex
        signatureAsset = if ($signed) { "$([IO.Path]::GetFileName($outputFull)).sig" } else { '' }
    }
}

$feedJson = $feed | ConvertTo-Json -Depth 8
Write-Utf8TextFile -Path $outputFull -Value $feedJson

$signaturePath = ''
if ($signed) {
    $feedBytes = [Text.Encoding]::UTF8.GetBytes($feedJson)
    $signature = [Mpt.Ed25519]::Sign($feedBytes, $signingKeyBytes)
    $signaturePath = "$outputFull.sig"
    Write-Utf8TextFile -Path $signaturePath -Value ([Convert]::ToBase64String($signature))

    if ([string]::IsNullOrWhiteSpace($PublicKeyOutputPath)) {
        $PublicKeyOutputPath = "$outputFull.public-key.txt"
    }
    $publicKeyFull = [IO.Path]::GetFullPath($PublicKeyOutputPath)
    Write-Utf8TextFile -Path $publicKeyFull -Value $publicKeyHex
} else {
    $publicKeyFull = ''
}

[pscustomobject]@{
    FeedPath = $outputFull
    FeedSha256 = (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash.ToLowerInvariant()
    SignaturePath = $signaturePath
    PublicKeyPath = $publicKeyFull
    Channel = $Channel
    Version = $Version
    Signed = $signed
    FullSha256 = $fullZipHash
    ManifestSha256 = $fullManifestHash
    DeltaCount = $deltaRecordsArray.Count
} | ConvertTo-Json -Depth 5
