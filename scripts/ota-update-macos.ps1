<#
.SYNOPSIS
    Checks and applies signed full-bundle MyPowerTools updates on macOS.

.DESCRIPTION
    macOS updates are distributed as architecture-specific, code-signed application bundles.
    This entrypoint verifies the platform channel feed, the full archive, and the post-signing
    manifest, then delegates the transactional bundle swap and rollback to ota-apply-macos.ps1.
    File-level delta packages are rejected on macOS.
#>
[CmdletBinding()]
param(
    [ValidateSet('Check', 'Apply', 'Status')]
    [string]$Command = 'Check',
    [ValidateSet('stable', 'nightly', 'local')]
    [string]$Channel = '',
    [string]$FeedUrl = '',
    [string]$DownloadBaseUrl = '',
    [string]$InstallRoot = '',
    [string]$DataRoot = '',
    [string]$StateRoot = '',
    [string]$PublicKeyHex = '',
    [string]$PublicKeyPath = '',
    [switch]$AllowUnsigned,
    [switch]$Force,
    [string]$CurrentVersion = '',
    [string]$LocalFeedPath = '',
    [string]$LocalPackageRoot = '',
    [switch]$BootstrapReady,
    [switch]$SkipBootstrap,
    [switch]$NoRuntimeRestart,
    [switch]$NoApplyDeletes,
    [switch]$KeepBackup,
    [switch]$FullRecoveryOnHealthFailure,
    [switch]$SkipFullRecoveryOnHealthFailure
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:OtaBoundParameters = $PSBoundParameters
$script:Repository = 'dqtz5vpvj9-create/MyPowerTools'
$script:EmbeddedPublicKeyHex = '0288efe271c9788b64eca7788fb074da696a08c890fb534ddef549a8648a1b4a'
if (-not $IsMacOS) {
    throw 'ota-update-macos.ps1 runs on macOS only.'
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

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected JSON file is missing: $Path"
    }
    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
}

function Compare-OtaVersion {
    param(
        [Parameter(Mandatory = $true)][string]$Left,
        [Parameter(Mandatory = $true)][string]$Right
    )

    if ($Left -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
        $Right -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "Invalid OTA version comparison: '$Left' vs '$Right'."
    }
    $leftParts = $Left.Split('.') | ForEach-Object { [int]$_ }
    $rightParts = $Right.Split('.') | ForEach-Object { [int]$_ }
    for ($index = 0; $index -lt 3; $index++) {
        if ($leftParts[$index] -lt $rightParts[$index]) { return -1 }
        if ($leftParts[$index] -gt $rightParts[$index]) { return 1 }
    }
    return 0
}

function Read-InstalledRelease {
    param([Parameter(Mandatory = $true)][string]$StateRootFull)

    $path = Join-Path $StateRootFull 'installed-release.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }
    try {
        return Read-JsonFile -Path $path
    }
    catch {
        return $null
    }
}

function Read-BundleVersion {
    param([Parameter(Mandatory = $true)][string]$AppBundlePath)

    $infoBase = Join-Path $AppBundlePath 'Contents/Info'
    if (-not (Test-Path -LiteralPath "$infoBase.plist" -PathType Leaf)) {
        return ''
    }
    $value = ((& /usr/bin/defaults 'read' $infoBase 'CFBundleShortVersionString' 2>$null |
        Select-Object -First 1) -as [string])
    $global:LASTEXITCODE = 0
    return $(if ($null -eq $value) { '' } else { $value.Trim() })
}

function Resolve-MacRuntimeIdentifier {
    param(
        [AllowNull()][object]$InstalledRelease,
        [Parameter(Mandatory = $true)][string]$AppBundlePath
    )

    if ($null -ne $InstalledRelease) {
        $recorded = [string]$InstalledRelease.runtimeIdentifier
        if ($recorded -in @('osx-arm64', 'osx-x64')) {
            return $recorded
        }
    }

    $launcher = Join-Path $AppBundlePath 'Contents/MacOS/MyPowerTools'
    if (Test-Path -LiteralPath $launcher -PathType Leaf) {
        $architectures = @(& /usr/bin/lipo '-archs' $launcher 2>$null)
        $global:LASTEXITCODE = 0
        $tokens = @(
            ($architectures -join ' ') -split '\s+' |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        )
        if ($tokens -contains 'arm64' -and $tokens -notcontains 'x86_64') {
            return 'osx-arm64'
        }
        if ($tokens -contains 'x86_64' -and $tokens -notcontains 'arm64') {
            return 'osx-x64'
        }
    }

    $machineArchitecture = (& /usr/bin/uname '-m').Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not determine the macOS machine architecture.'
    }
    return $(if ($machineArchitecture -eq 'arm64') { 'osx-arm64' } else { 'osx-x64' })
}

function Resolve-PublicKey {
    param([Parameter(Mandatory = $true)][string]$StateRootFull)

    if (-not [string]::IsNullOrWhiteSpace($PublicKeyHex)) {
        return $PublicKeyHex.Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($PublicKeyPath)) {
        $path = [IO.Path]::GetFullPath($PublicKeyPath)
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "OTA public key file does not exist: $path"
        }
        return ([IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))).Trim()
    }

    $stateKeyPath = Join-Path $StateRootFull 'ota-signing-public-key.txt'
    if (Test-Path -LiteralPath $stateKeyPath -PathType Leaf) {
        $stateKey = ([IO.File]::ReadAllText($stateKeyPath, [Text.UTF8Encoding]::new($false))).Trim()
        if ($stateKey -match '^[0-9a-fA-F]{64}$') {
            return $stateKey
        }
    }
    return $script:EmbeddedPublicKeyHex
}

function Assert-FeedSignature {
    param(
        [Parameter(Mandatory = $true)][string]$Json,
        [Parameter(Mandatory = $true)][string]$SignatureBase64,
        [Parameter(Mandatory = $true)][string]$PublicKeyHex,
        [Parameter(Mandatory = $true)][bool]$FeedSigned
    )

    if (-not $FeedSigned) {
        if ($AllowUnsigned) {
            return
        }
        throw 'OTA feed is unsigned. Pass -AllowUnsigned only for local or nightly validation.'
    }
    if ($PublicKeyHex -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Signed OTA feed requires a trusted 64-character Ed25519 public key.'
    }
    if ([string]::IsNullOrWhiteSpace($SignatureBase64)) {
        throw 'Signed OTA feed is missing its detached signature.'
    }

    $verifierSource = Join-Path $PSScriptRoot 'ed25519.cs'
    if (-not (Test-Path -LiteralPath $verifierSource -PathType Leaf)) {
        throw "OTA signature verifier is missing: $verifierSource"
    }
    try {
        Add-Type -Path $verifierSource
        $publicKey = [byte[]]::new(32)
        for ($index = 0; $index -lt 32; $index++) {
            $publicKey[$index] = [Convert]::ToByte($PublicKeyHex.Substring($index * 2, 2), 16)
        }
        $valid = [Mpt.Ed25519]::Verify(
            [Text.Encoding]::UTF8.GetBytes($Json),
            [Convert]::FromBase64String($SignatureBase64),
            $publicKey)
    }
    catch {
        throw "OTA feed signature verification failed: $($_.Exception.Message)"
    }
    if (-not $valid) {
        throw 'OTA feed signature verification failed: signature does not match the feed bytes.'
    }
}

function Resolve-ChannelFeedUri {
    param(
        [Parameter(Mandatory = $true)][string]$ChannelName,
        [Parameter(Mandatory = $true)][string]$RuntimeIdentifier
    )

    $assetName = "channel-$ChannelName-$RuntimeIdentifier.json"
    if ($ChannelName -eq 'stable') {
        return "https://github.com/$($script:Repository)/releases/latest/download/$assetName"
    }
    if ($ChannelName -eq 'local') {
        throw 'The local channel requires -LocalFeedPath or -FeedUrl.'
    }

    $headers = @{
        'User-Agent' = 'MyPowerTools-OTA'
        Accept = 'application/vnd.github+json'
    }
    $releases = Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$($script:Repository)/releases?per_page=30" `
        -Headers $headers
    foreach ($release in @($releases)) {
        $asset = @($release.assets) |
            Where-Object { [string]$_.name -eq $assetName } |
            Select-Object -First 1
        if ($null -ne $asset -and
            -not [string]::IsNullOrWhiteSpace([string]$asset.browser_download_url)) {
            return [string]$asset.browser_download_url
        }
    }
    throw "No GitHub release currently publishes $assetName."
}

function Resolve-FeedContent {
    param(
        [Parameter(Mandatory = $true)][string]$StateRootFull,
        [Parameter(Mandatory = $true)][string]$ChannelName,
        [Parameter(Mandatory = $true)][string]$RuntimeIdentifier
    )

    $downloadsRoot = Join-Path $StateRootFull 'downloads'
    New-Item -ItemType Directory -Path $downloadsRoot -Force | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($LocalFeedPath)) {
        $feedPath = [IO.Path]::GetFullPath($LocalFeedPath)
        if (-not (Test-Path -LiteralPath $feedPath -PathType Leaf)) {
            throw "Local OTA feed does not exist: $feedPath"
        }
        $feedJson = [IO.File]::ReadAllText($feedPath, [Text.UTF8Encoding]::new($false))
        $signaturePath = "$feedPath.sig"
        return [pscustomobject]@{
            Json = $feedJson
            Feed = $feedJson | ConvertFrom-Json
            Signature = if (Test-Path -LiteralPath $signaturePath -PathType Leaf) {
                ([IO.File]::ReadAllText($signaturePath, [Text.UTF8Encoding]::new($false))).Trim()
            } else {
                ''
            }
            BaseUrl = if ([string]::IsNullOrWhiteSpace($LocalPackageRoot)) {
                Split-Path -Parent $feedPath
            } else {
                [IO.Path]::GetFullPath($LocalPackageRoot)
            }
        }
    }

    $feedUri = if ([string]::IsNullOrWhiteSpace($FeedUrl)) {
        Resolve-ChannelFeedUri -ChannelName $ChannelName -RuntimeIdentifier $RuntimeIdentifier
    } else {
        $FeedUrl
    }
    $feedPath = Join-Path $downloadsRoot "channel-$ChannelName-$RuntimeIdentifier.json"
    $headers = @{ 'User-Agent' = 'MyPowerTools-OTA' }
    Invoke-WebRequest -Uri $feedUri -OutFile $feedPath -Headers $headers -UseBasicParsing
    $feedJson = [IO.File]::ReadAllText($feedPath, [Text.UTF8Encoding]::new($false))

    $signature = ''
    try {
        $signaturePath = "$feedPath.sig"
        Invoke-WebRequest -Uri "$feedUri.sig" -OutFile $signaturePath -Headers $headers -UseBasicParsing
        $signature = ([IO.File]::ReadAllText($signaturePath, [Text.UTF8Encoding]::new($false))).Trim()
    }
    catch {
        $signature = ''
    }

    $baseUrl = if (-not [string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
        $DownloadBaseUrl.TrimEnd('/')
    } else {
        $feedUri.Substring(0, $feedUri.LastIndexOf('/'))
    }
    return [pscustomobject]@{
        Json = $feedJson
        Feed = $feedJson | ConvertFrom-Json
        Signature = $signature
        BaseUrl = $baseUrl
    }
}

function Resolve-OtaArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$Asset,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$StateRootFull
    )

    if ([IO.Path]::GetFileName($Asset) -ne $Asset) {
        throw "OTA artifact must be a file name without path components: $Asset"
    }
    if ($ExpectedSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw "OTA artifact has an invalid SHA-256 value: $Asset"
    }

    $downloadsRoot = Join-Path $StateRootFull 'downloads'
    New-Item -ItemType Directory -Path $downloadsRoot -Force | Out-Null
    $destination = Join-Path $downloadsRoot $Asset
    if (Test-Path -LiteralPath $BaseUrl -PathType Container) {
        $source = Join-Path $BaseUrl $Asset
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Local OTA artifact is missing: $source"
        }
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    else {
        Invoke-WebRequest `
            -Uri "$($BaseUrl.TrimEnd('/'))/$Asset" `
            -OutFile $destination `
            -Headers @{ 'User-Agent' = 'MyPowerTools-OTA' } `
            -UseBasicParsing
    }

    $actualSha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $ExpectedSha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        throw "OTA artifact SHA-256 mismatch for $Asset. expected=$ExpectedSha256 actual=$actualSha256"
    }
    return $destination
}

function Assert-MacFeed {
    param(
        [Parameter(Mandatory = $true)][object]$Feed,
        [Parameter(Mandatory = $true)][string]$ChannelName,
        [Parameter(Mandatory = $true)][string]$RuntimeIdentifier
    )

    if ([int]$Feed.schemaVersion -ne 1 -or
        [string]$Feed.kind -ne 'mypowertools-ota-channel-feed') {
        throw 'Unsupported OTA channel feed.'
    }
    if ([string]$Feed.channel -ne $ChannelName) {
        throw "OTA feed channel '$($Feed.channel)' does not match requested channel '$ChannelName'."
    }
    if ([string]$Feed.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "OTA feed contains an invalid version '$($Feed.version)'."
    }

    $expectedArchive = "MyPowerTools-$RuntimeIdentifier.zip"
    $expectedManifest = "MyPowerTools-$RuntimeIdentifier.manifest.json"
    if ([string]$Feed.full.asset -ne $expectedArchive) {
        throw "OTA feed archive does not match this Mac: expected $expectedArchive."
    }
    if ([string]$Feed.full.manifestAsset -ne $expectedManifest) {
        throw "OTA feed manifest does not match this Mac: expected $expectedManifest."
    }
    if ([string]$Feed.full.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]$Feed.full.manifestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [long]$Feed.full.size -le 0) {
        throw 'OTA feed contains invalid full-package metadata.'
    }
    if (@($Feed.deltas).Count -ne 0) {
        throw 'macOS OTA feeds must not publish file-level delta packages.'
    }
}

function Assert-MacManifest {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $manifest = Read-JsonFile -Path $Path
    if ([int]$manifest.schemaVersion -ne 1 -or
        [string]$manifest.kind -ne 'mypowertools-ota-file-manifest' -or
        [string]$manifest.product -ne 'MyPowerTools' -or
        [string]$manifest.version -ne $ExpectedVersion) {
        throw 'Downloaded macOS manifest does not match the selected release.'
    }
}

function Invoke-BootstrapRelaunch {
    param([Parameter(Mandatory = $true)][string]$StateRootFull)

    $bootstrapRoot = Join-Path $StateRootFull 'bootstrap-macos'
    New-Item -ItemType Directory -Path $bootstrapRoot -Force | Out-Null
    foreach ($entry in @(
        @{ Source = $PSCommandPath; Name = 'ota-update.ps1' },
        @{ Source = (Join-Path $PSScriptRoot 'ota-apply-macos.ps1'); Name = 'ota-apply-macos.ps1' },
        @{ Source = (Join-Path $PSScriptRoot 'ed25519.cs'); Name = 'ed25519.cs' }
    )) {
        if (-not (Test-Path -LiteralPath $entry.Source -PathType Leaf)) {
            throw "Unable to bootstrap macOS OTA: $($entry.Source) is missing."
        }
        Copy-Item -LiteralPath $entry.Source -Destination (Join-Path $bootstrapRoot $entry.Name) -Force
    }

    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($key in $script:OtaBoundParameters.Keys) {
        if ($key -in @('BootstrapReady', 'SkipBootstrap')) {
            continue
        }
        $value = $script:OtaBoundParameters[$key]
        if ($value -is [System.Management.Automation.SwitchParameter]) {
            if ($value.IsPresent) {
                $arguments.Add("-$key")
            }
            continue
        }
        if ($null -ne $value) {
            $arguments.Add("-$key")
            $arguments.Add([string]$value)
        }
    }
    $arguments.Add('-BootstrapReady')

    $powerShell = Join-Path $PSHOME 'pwsh'
    if (-not (Test-Path -LiteralPath $powerShell -PathType Leaf)) {
        $powerShell = (Get-Process -Id $PID).Path
    }
    if ([string]::IsNullOrWhiteSpace($powerShell)) {
        throw 'Could not locate the running PowerShell host for macOS OTA bootstrap.'
    }

    & $powerShell `
        -NoLogo `
        -NoProfile `
        -NonInteractive `
        -File (Join-Path $bootstrapRoot 'ota-update.ps1') `
        @arguments
    exit $LASTEXITCODE
}

$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Join-Path $userProfile 'Applications/MyPowerTools.app'
}
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $userProfile 'Library/Application Support/MyPowerTools'
}
$InstallRootFull = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('/')
$DataRootFull = [IO.Path]::GetFullPath($DataRoot)
if ([string]::IsNullOrWhiteSpace($StateRoot)) {
    $StateRoot = Join-Path $DataRootFull 'ota-state'
}
$StateRootFull = [IO.Path]::GetFullPath($StateRoot)
New-Item -ItemType Directory -Path $StateRootFull -Force | Out-Null

$installedRelease = Read-InstalledRelease -StateRootFull $StateRootFull
$RuntimeIdentifier = Resolve-MacRuntimeIdentifier `
    -InstalledRelease $installedRelease `
    -AppBundlePath $InstallRootFull
if ([string]::IsNullOrWhiteSpace($CurrentVersion)) {
    if ($null -ne $installedRelease -and
        [string]$installedRelease.version -match '^[0-9]+\.[0-9]+\.[0-9]+$') {
        $CurrentVersion = [string]$installedRelease.version
    }
    else {
        $bundleVersion = Read-BundleVersion -AppBundlePath $InstallRootFull
        $CurrentVersion = $(if ($bundleVersion -match '^[0-9]+\.[0-9]+\.[0-9]+$') {
            $bundleVersion
        } else {
            '0.0.0'
        })
    }
}
if ([string]::IsNullOrWhiteSpace($Channel)) {
    $Channel = $(if ($null -ne $installedRelease -and
        -not [string]::IsNullOrWhiteSpace([string]$installedRelease.channel)) {
        [string]$installedRelease.channel
    } else {
        'stable'
    })
}
if ($CurrentVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Current version is not a valid OTA version: $CurrentVersion"
}

if ($Command -eq 'Status') {
    [ordered]@{
        schemaVersion = 1
        platform = $RuntimeIdentifier
        installed = $installedRelease
        lastCheck = if (Test-Path -LiteralPath (Join-Path $StateRootFull 'last-check.json') -PathType Leaf) {
            Read-JsonFile -Path (Join-Path $StateRootFull 'last-check.json')
        } else { $null }
        lastUpdate = if (Test-Path -LiteralPath (Join-Path $StateRootFull 'last-update.json') -PathType Leaf) {
            Read-JsonFile -Path (Join-Path $StateRootFull 'last-update.json')
        } else { $null }
        health = if (Test-Path -LiteralPath (Join-Path $StateRootFull 'health-check.json') -PathType Leaf) {
            Read-JsonFile -Path (Join-Path $StateRootFull 'health-check.json')
        } else { $null }
    } | ConvertTo-Json -Depth 8
    exit 0
}

$feedContent = Resolve-FeedContent `
    -StateRootFull $StateRootFull `
    -ChannelName $Channel `
    -RuntimeIdentifier $RuntimeIdentifier
$feed = $feedContent.Feed
Assert-MacFeed -Feed $feed -ChannelName $Channel -RuntimeIdentifier $RuntimeIdentifier
Assert-FeedSignature `
    -Json $feedContent.Json `
    -SignatureBase64 $feedContent.Signature `
    -PublicKeyHex (Resolve-PublicKey -StateRootFull $StateRootFull) `
    -FeedSigned ([bool]$feed.signing.signed)

$versionCompare = Compare-OtaVersion -Left ([string]$feed.version) -Right $CurrentVersion
$available = $versionCompare -gt 0
$reason = if ($versionCompare -eq 0) {
    'up-to-date'
} elseif ($versionCompare -lt 0) {
    'downgrade-blocked'
} else {
    'update-available'
}
if ($Force -and $versionCompare -le 0) {
    $available = $true
    $reason = 'forced'
}

$check = [ordered]@{
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    channel = $Channel
    platform = $RuntimeIdentifier
    currentVersion = $CurrentVersion
    latestVersion = [string]$feed.version
    available = $available
    reason = $reason
    signed = [bool]$feed.signing.signed
    package = if ($available) {
        [ordered]@{
            kind = 'full'
            asset = [string]$feed.full.asset
            sha256 = [string]$feed.full.sha256
            size = [long]$feed.full.size
            manifestAsset = [string]$feed.full.manifestAsset
            manifestSha256 = [string]$feed.full.manifestSha256
        }
    } else {
        $null
    }
}
Write-Utf8TextFile `
    -Path (Join-Path $StateRootFull 'last-check.json') `
    -Value ($check | ConvertTo-Json -Depth 6)
if ($Command -eq 'Check' -or -not $available) {
    $check | ConvertTo-Json -Depth 6
    exit 0
}

if (-not $BootstrapReady -and -not $SkipBootstrap) {
    Invoke-BootstrapRelaunch -StateRootFull $StateRootFull
}

$lockStream = $null
try {
    try {
        $lockStream = [IO.File]::Open(
            (Join-Path $StateRootFull 'ota-update.lock'),
            [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::None)
    }
    catch [IO.IOException] {
        throw 'Another MyPowerTools OTA update is already running.'
    }

    $packagePath = Resolve-OtaArtifact `
        -BaseUrl $feedContent.BaseUrl `
        -Asset ([string]$feed.full.asset) `
        -ExpectedSha256 ([string]$feed.full.sha256) `
        -StateRootFull $StateRootFull
    $manifestPath = Resolve-OtaArtifact `
        -BaseUrl $feedContent.BaseUrl `
        -Asset ([string]$feed.full.manifestAsset) `
        -ExpectedSha256 ([string]$feed.full.manifestSha256) `
        -StateRootFull $StateRootFull
    Assert-MacManifest -Path $manifestPath -ExpectedVersion ([string]$feed.version)

    $applyScript = Join-Path $PSScriptRoot 'ota-apply-macos.ps1'
    if (-not (Test-Path -LiteralPath $applyScript -PathType Leaf)) {
        throw "macOS OTA apply script is missing: $applyScript"
    }
    $applyParameters = @{
        PackagePath = $packagePath
        ExpectedPackageSha256 = [string]$feed.full.sha256
        ExpectedVersion = [string]$feed.version
        AppBundlePath = $InstallRootFull
        DataRoot = $DataRootFull
        StateRoot = $StateRootFull
    }
    if ($KeepBackup) {
        $applyParameters.KeepBackup = $true
    }
    if ($NoRuntimeRestart) {
        $applyParameters.NoRelaunch = $true
    }

    $applyOutput = @(& $applyScript @applyParameters | ForEach-Object { [string]$_ })
    $applyResult = ($applyOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if (-not [bool]$applyResult.success) {
        throw 'macOS OTA apply reported failure.'
    }

    $installedManifestPath = Join-Path $StateRootFull 'installed-files.manifest.json'
    Copy-Item -LiteralPath $manifestPath -Destination $installedManifestPath -Force
    $trustedPublicKey = Resolve-PublicKey -StateRootFull $StateRootFull
    if ($trustedPublicKey -match '^[0-9a-fA-F]{64}$') {
        Write-Utf8TextFile `
            -Path (Join-Path $StateRootFull 'ota-signing-public-key.txt') `
            -Value $trustedPublicKey
    }

    $release = [ordered]@{
        schemaVersion = 1
        product = 'MyPowerTools'
        version = [string]$feed.version
        channel = $Channel
        installedAt = [DateTimeOffset]::UtcNow.ToString('O')
        installDir = $InstallRootFull
        dataRoot = $DataRootFull
        repository = "https://github.com/$($script:Repository)"
        manifestPath = 'installed-files.manifest.json'
        manifestSha256 = (Get-FileHash -LiteralPath $installedManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        packageKind = 'full'
        distributionMode = 'full'
        runtimeIdentifier = $RuntimeIdentifier
    }
    Write-Utf8TextFile `
        -Path (Join-Path $StateRootFull 'installed-release.json') `
        -Value ($release | ConvertTo-Json -Depth 5)
    if ($null -ne $applyResult.health) {
        Write-Utf8TextFile `
            -Path (Join-Path $StateRootFull 'health-check.json') `
            -Value ($applyResult.health | ConvertTo-Json -Depth 6)
    }

    $result = [ordered]@{
        success = $true
        channel = $Channel
        platform = $RuntimeIdentifier
        fromVersion = $CurrentVersion
        toVersion = [string]$feed.version
        packageKind = 'full'
        packageSha256 = [string]$feed.full.sha256
        manifestSha256 = [string]$feed.full.manifestSha256
        apply = $applyResult
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Write-Utf8TextFile `
        -Path (Join-Path $StateRootFull 'last-update.json') `
        -Value ($result | ConvertTo-Json -Depth 8)
    $result | ConvertTo-Json -Depth 8
}
catch {
    $failure = [ordered]@{
        success = $false
        channel = $Channel
        platform = $RuntimeIdentifier
        fromVersion = $CurrentVersion
        latestVersion = [string]$feed.version
        error = [string]$_.Exception.Message
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    try {
        Write-Utf8TextFile `
            -Path (Join-Path $StateRootFull 'last-update.json') `
            -Value ($failure | ConvertTo-Json -Depth 6)
    }
    catch {
    }
    $failure | ConvertTo-Json -Depth 6
    exit 1
}
finally {
    if ($null -ne $lockStream) {
        $lockStream.Dispose()
    }
}
