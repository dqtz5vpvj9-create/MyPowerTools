[CmdletBinding()]
param(
    [string]$ArtifactsRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\release'),
    [ValidateSet('stable', 'nightly', 'local')][string]$Channel = '',
    [string]$ExpectedVersion = '',
    [string]$PublicKeyHex = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    if (-not $Condition) {
        throw "ASSERT: $Message"
    }
}

function Read-Utf8TextFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
}

function Test-Signature {
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][string]$SignatureBase64,
        [Parameter(Mandatory = $true)][string]$PublicKeyHexValue
    )

    Add-Type -Path (Join-Path $PSScriptRoot 'ed25519.cs')
    $signature = [Convert]::FromBase64String($SignatureBase64)
    $publicKey = [byte[]]::new(32)
    for ($index = 0; $index -lt 32; $index++) {
        $publicKey[$index] = [Convert]::ToByte($PublicKeyHexValue.Substring($index * 2, 2), 16)
    }
    return [Mpt.Ed25519]::Verify(
        [Text.Encoding]::UTF8.GetBytes($Json),
        $signature,
        $publicKey)
}

$ArtifactsRoot = [IO.Path]::GetFullPath($ArtifactsRoot)
if (-not (Test-Path -LiteralPath $ArtifactsRoot -PathType Container)) {
    throw "Release artifacts root does not exist: $ArtifactsRoot"
}

$metadataPath = Join-Path $ArtifactsRoot 'release-metadata.json'
$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    $ExpectedVersion = [string]$metadata.version
}
if ([string]::IsNullOrWhiteSpace($Channel)) {
    $Channel = [string]$metadata.channel
}
if ([string]::IsNullOrWhiteSpace($PublicKeyHex)) {
    $PublicKeyHex = '0288efe271c9788b64eca7788fb074da696a08c890fb534ddef549a8648a1b4a'
}

$checks = [Collections.Generic.List[object]]::new()
function Add-Check {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Ok,
        [string]$Detail = ''
    )

    $checks.Add([pscustomobject]@{
        check = $Name
        ok = $Ok
        detail = $Detail
    })
}

$zipPath = Join-Path $ArtifactsRoot 'MyPowerTools-win-x64.zip'
$zipHashPath = "$zipPath.sha256"
$manifestPath = Join-Path $ArtifactsRoot 'MyPowerTools-win-x64.manifest.json'
$feedPath = Join-Path $ArtifactsRoot "channel-$Channel.json"
$feedSigPath = "$feedPath.sig"
$publicKeyPath = Join-Path $ArtifactsRoot 'ota-signing-public-key.txt'

foreach ($path in @($zipPath, $zipHashPath, $manifestPath, $feedPath, $feedSigPath, $publicKeyPath, $metadataPath)) {
    Add-Check -Name "exists:$([IO.Path]::GetFileName($path))" -Ok (Test-Path -LiteralPath $path -PathType Leaf)
}
if ($checks | Where-Object { -not $_.ok }) {
    throw 'Required release artifacts are missing.'
}

$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$zipHashFile = (Read-Utf8TextFile -Path $zipHashPath).Trim()
$zipHashMatches = $zipHashFile -match "^$zipHash\s+MyPowerTools-win-x64\.zip$"
Add-Check -Name 'zip-sha256' -Ok $zipHashMatches -Detail "file=$zipHash"

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifestOk = ([int]$manifest.schemaVersion -eq 1) -and
    ([string]$manifest.kind -eq 'mypowertools-ota-file-manifest') -and
    ([string]$manifest.version -eq $ExpectedVersion) -and
    ([int]$manifest.fileCount -eq @($manifest.files).Count) -and
    ([long]$manifest.totalBytes -eq [long](@($manifest.files) | Measure-Object -Property length -Sum).Sum)
Add-Check -Name 'manifest-consistent' -Ok $manifestOk -Detail "files=$($manifest.fileCount) bytes=$($manifest.totalBytes)"

$feedJson = Read-Utf8TextFile -Path $feedPath
$feed = $feedJson | ConvertFrom-Json
$feedSig = (Read-Utf8TextFile -Path $feedSigPath).Trim()
$feedStructureOk = ([int]$feed.schemaVersion -eq 1) -and
    ([string]$feed.kind -eq 'mypowertools-ota-channel-feed') -and
    ([string]$feed.channel -eq $Channel) -and
    ([string]$feed.version -eq $ExpectedVersion) -and
    ([string]$feed.full.sha256 -eq $zipHash) -and
    ([string]$feed.full.manifestSha256 -eq $manifestHash) -and
    ([string]$feed.signing.publicKeyHex -eq $PublicKeyHex) -and
    ([bool]$feed.signing.signed)
Add-Check -Name 'feed-structure' -Ok $feedStructureOk
Add-Check -Name 'feed-signature' -Ok (Test-Signature `
    -Json $feedJson `
    -SignatureBase64 $feedSig `
    -PublicKeyHexValue $PublicKeyHex)

$publicKeyFile = (Read-Utf8TextFile -Path $publicKeyPath).Trim()
Add-Check -Name 'public-key-match' -Ok ($publicKeyFile -eq $PublicKeyHex)

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipEntries = $null
$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $zipEntries = @($archive.Entries | ForEach-Object FullName)
}
finally {
    $archive.Dispose()
}
foreach ($entry in @(
    'MyPowerTools-win-x64.manifest.json',
    'ota-update.ps1',
    'package-ota-update.ps1',
    'ed25519.cs',
    'install-windows.ps1',
    'ota-signing-public-key.txt',
    'build-provenance.json')) {
    Add-Check -Name "zip-entry:$entry" -Ok ($zipEntries -contains $entry)
}

$metadataFeedOk = ([string]$metadata.update.feed -eq "channel-$Channel.json") -and
    ([string]$metadata.update.feedSignature -eq "channel-$Channel.json.sig") -and
    ([string]$metadata.artifacts[0].sha256 -eq $zipHash) -and
    ([string]$metadata.update.productionSignatureState -eq 'feed-signed') -and
    ([string]$metadata.version -eq $ExpectedVersion)
Add-Check -Name 'release-metadata-consistent' -Ok $metadataFeedOk

$deltaChecksOk = $true
foreach ($delta in @($feed.deltas)) {
    $deltaPath = Join-Path $ArtifactsRoot 'ota' ([string]$delta.asset)
    if (-not (Test-Path -LiteralPath $deltaPath -PathType Leaf)) {
        $deltaChecksOk = $false
        break
    }
    $deltaHash = (Get-FileHash -LiteralPath $deltaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($deltaHash -ne ([string]$delta.sha256).ToLowerInvariant()) {
        $deltaChecksOk = $false
        break
    }
    $deltaArchive = [IO.Compression.ZipFile]::OpenRead($deltaPath)
    try {
        $planEntry = $deltaArchive.Entries | Where-Object { $_.FullName -eq 'ota-plan.json' } |
            Select-Object -First 1
        $reader = [IO.StreamReader]::new($planEntry.Open(), [Text.UTF8Encoding]::new($false))
        try {
            $plan = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $deltaArchive.Dispose()
    }
    if ([string]$plan.sourceVersion -ne $ExpectedVersion -or
        ([string]$plan.sourceManifestSha256).ToLowerInvariant() -ne $manifestHash -or
        ([string]$plan.targetManifestSha256).ToLowerInvariant() -ne ([string]$delta.fromManifestSha256).ToLowerInvariant()) {
        $deltaChecksOk = $false
        break
    }
}
Add-Check -Name 'delta-consistency' -Ok $deltaChecksOk -Detail "count=$(@($feed.deltas).Count)"

$packagesRoot = Join-Path $ArtifactsRoot 'packages'
$packageChecksOk = $true
$packageCount = 0
if (Test-Path -LiteralPath $packagesRoot -PathType Container) {
    foreach ($feedFile in Get-ChildItem -LiteralPath $packagesRoot -Recurse -Filter 'channel-package-*.json' -File) {
        $packageFeedJson = Read-Utf8TextFile -Path $feedFile.FullName
        $packageFeed = $packageFeedJson | ConvertFrom-Json
        $packageSigPath = "$($feedFile.FullName).sig"
        $packageCount++
        if (-not (Test-Path -LiteralPath $packageSigPath -PathType Leaf) -or
            -not [bool]$packageFeed.signing.signed -or
            [string]$packageFeed.signing.publicKeyHex -ne $PublicKeyHex) {
            $packageChecksOk = $false
            break
        }
        $packageSig = (Read-Utf8TextFile -Path $packageSigPath).Trim()
        if (-not (Test-Signature -Json $packageFeedJson -SignatureBase64 $packageSig -PublicKeyHexValue $PublicKeyHex)) {
            $packageChecksOk = $false
            break
        }
        $latestRelease = @($packageFeed.releases | Select-Object -First 1)[0]
        $archivePath = Join-Path (Split-Path -Parent $feedFile.FullName) ([string]$latestRelease.asset)
        if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
            $packageChecksOk = $false
            break
        }
        $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($archiveHash -ne ([string]$latestRelease.sha256).ToLowerInvariant()) {
            $packageChecksOk = $false
            break
        }
    }
}
Add-Check -Name 'package-feeds-consistent' -Ok $packageChecksOk -Detail "packages=$packageCount"

$failed = @($checks | Where-Object { -not $_.ok })
[pscustomobject]@{
    Success = $failed.Count -eq 0
    Version = $ExpectedVersion
    Channel = $Channel
    ZipSha256 = $zipHash
    ManifestSha256 = $manifestHash
    FeedSha256 = (Get-FileHash -LiteralPath $feedPath -Algorithm SHA256).Hash.ToLowerInvariant()
    CheckCount = $checks.Count
    FailedCount = $failed.Count
    Checks = $checks.ToArray()
} | ConvertTo-Json -Depth 6

if ($failed.Count -gt 0) {
    exit 1
}
