[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageId,
    [ValidateSet('stable', 'nightly', 'local')][string]$Channel = 'stable',
    [string]$FeedUrl = '',
    [string]$DownloadBaseUrl = '',
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$StateRoot = '',
    [string]$PublicKeyHex = '',
    [string]$PublicKeyPath = '',
    [switch]$AllowUnsigned,
    [switch]$Force,
    [string]$LocalFeedPath = '',
    [string]$LocalPackageRoot = '',
    [switch]$NoRuntimeRestart
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:EmbeddedPublicKeyHex = '0288efe271c9788b64eca7788fb074da696a08c890fb534ddef549a8648a1b4a'

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

function Read-Utf8TextFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
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

function Resolve-PublicKey {
    param([Parameter(Mandatory = $true)][string]$StateRootFull)

    if (-not [string]::IsNullOrWhiteSpace($PublicKeyHex)) {
        return $PublicKeyHex.Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($PublicKeyPath)) {
        $keyPath = [IO.Path]::GetFullPath($PublicKeyPath)
        if (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
            throw "OTA public key file does not exist: $keyPath"
        }
        return (Read-Utf8TextFile -Path $keyPath).Trim()
    }
    $stateKeyPath = Join-Path $StateRootFull 'ota-signing-public-key.txt'
    if (Test-Path -LiteralPath $stateKeyPath -PathType Leaf) {
        return (Read-Utf8TextFile -Path $stateKeyPath).Trim()
    }
    if (-not [string]::IsNullOrWhiteSpace($script:EmbeddedPublicKeyHex)) {
        return $script:EmbeddedPublicKeyHex
    }
    return ''
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
        throw 'Package OTA feed is unsigned. Pass -AllowUnsigned only for local or nightly development feeds.'
    }
    if ($PublicKeyHex -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Package OTA feed requires a 64-character Ed25519 public key (hex) but none is configured.'
    }
    if ([string]::IsNullOrWhiteSpace($SignatureBase64)) {
        throw 'Package OTA feed is missing its Ed25519 signature.'
    }
    Add-Type -Path (Join-Path $PSScriptRoot 'ed25519.cs')
    $signature = [Convert]::FromBase64String($SignatureBase64)
    $publicKey = [byte[]]::new(32)
    for ($index = 0; $index -lt 32; $index++) {
        $publicKey[$index] = [Convert]::ToByte($PublicKeyHex.Substring($index * 2, 2), 16)
    }
    $valid = [Mpt.Ed25519]::Verify(
        [Text.Encoding]::UTF8.GetBytes($Json),
        $signature,
        $publicKey)
    if (-not $valid) {
        throw 'Package OTA feed signature verification failed.'
    }
}

function Resolve-Feed {
    param([Parameter(Mandatory = $true)][string]$StateRootFull)

    $downloadsDir = Join-Path $StateRootFull 'downloads'
    New-Item -ItemType Directory -Path $downloadsDir -Force | Out-Null
    if (-not [string]::IsNullOrWhiteSpace($LocalFeedPath)) {
        $feedFull = [IO.Path]::GetFullPath($LocalFeedPath)
        $feedJson = Read-Utf8TextFile -Path $feedFull
        $feed = $feedJson | ConvertFrom-Json
        $sigPath = "$feedFull.sig"
        $feedSignature = if (Test-Path -LiteralPath $sigPath -PathType Leaf) {
            (Read-Utf8TextFile -Path $sigPath).Trim()
        } else { '' }
        $baseUrl = if ([string]::IsNullOrWhiteSpace($LocalPackageRoot)) {
            [IO.Path]::GetDirectoryName($feedFull)
        } else {
            [IO.Path]::GetFullPath($LocalPackageRoot)
        }
        return [pscustomobject]@{
            Json = $feedJson
            Feed = $feed
            Signature = $feedSignature
            BaseUrl = $baseUrl
        }
    }

    $feedUri = $FeedUrl
    if ([string]::IsNullOrWhiteSpace($feedUri)) {
        $feedUri = "https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/latest/download/packages/$PackageId/channel-package-$PackageId.json"
    }
    $feedDestination = Join-Path $downloadsDir "channel-package-$PackageId.json"
    Invoke-WebRequest -Uri $feedUri -OutFile $feedDestination -UseBasicParsing
    $feedJson = Read-Utf8TextFile -Path $feedDestination
    $feed = $feedJson | ConvertFrom-Json
    $feedSignature = ''
    try {
        $sigDestination = "$feedDestination.sig"
        Invoke-WebRequest -Uri "$feedUri.sig" -OutFile $sigDestination -UseBasicParsing
        $feedSignature = (Read-Utf8TextFile -Path $sigDestination).Trim()
    }
    catch {
        $feedSignature = ''
    }
    $baseUrl = if (-not [string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
        $DownloadBaseUrl.TrimEnd('/')
    } else {
        $feedUri.Substring(0, $feedUri.LastIndexOf('/'))
    }
    return [pscustomobject]@{
        Json = $feedJson
        Feed = $feed
        Signature = $feedSignature
        BaseUrl = $baseUrl
    }
}

function Resolve-PackageArchive {
    param(
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$Asset,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$StateRootFull
    )

    $downloadsDir = Join-Path $StateRootFull 'downloads'
    New-Item -ItemType Directory -Path $downloadsDir -Force | Out-Null
    $destination = Join-Path $downloadsDir ([IO.Path]::GetFileName($Asset))
    if (Test-Path -LiteralPath $BaseUrl -PathType Container) {
        $localAsset = Join-Path $BaseUrl $Asset
        if (-not (Test-Path -LiteralPath $localAsset -PathType Leaf)) {
            throw "Local package OTA archive is missing: $localAsset"
        }
        Copy-Item -LiteralPath $localAsset -Destination $destination -Force
    } else {
        Invoke-WebRequest -Uri "$BaseUrl/$Asset" -OutFile $destination -UseBasicParsing
    }
    $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        throw "Package OTA archive SHA-256 mismatch for $Asset. expected=$ExpectedSha256 actual=$actualHash"
    }
    return $destination
}

$InstallRootFull = [IO.Path]::GetFullPath($InstallRoot)
$DataRootFull = [IO.Path]::GetFullPath($DataRoot)
if ([string]::IsNullOrWhiteSpace($StateRoot)) {
    $StateRoot = Join-Path $DataRootFull 'ota-state'
}
$StateRootFull = [IO.Path]::GetFullPath($StateRoot)
New-Item -ItemType Directory -Path $StateRootFull -Force | Out-Null

$installedReleasePath = Join-Path $StateRootFull 'installed-release.json'
$coreVersion = '0.0.0'
if (Test-Path -LiteralPath $installedReleasePath -PathType Leaf) {
    $installedRelease = Get-Content -LiteralPath $installedReleasePath -Raw | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace([string]$installedRelease.version)) {
        $coreVersion = [string]$installedRelease.version
    }
}

$packageRoot = Join-Path $InstallRootFull "modules\$PackageId"
$moduleJsonPath = Join-Path $packageRoot 'module.json'
$packageJsonPath = Join-Path $packageRoot 'package.json'
$installedManifestPath = if (Test-Path -LiteralPath $moduleJsonPath -PathType Leaf) {
    $moduleJsonPath
} elseif (Test-Path -LiteralPath $packageJsonPath -PathType Leaf) {
    $packageJsonPath
} else {
    $null
}
$installedPackageVersion = '0.0.0'
if ($null -ne $installedManifestPath) {
    $installedModule = Get-Content -LiteralPath $installedManifestPath -Raw | ConvertFrom-Json
    $installedPackageVersion = [string]$installedModule.version
}

$feedContent = Resolve-Feed -StateRootFull $StateRootFull
$feed = $feedContent.Feed
if ([int]$feed.schemaVersion -ne 1 -or
    [string]$feed.kind -ne 'mypowertools-ota-package-feed' -or
    [string]$feed.packageId -ne $PackageId) {
    throw "Unsupported or mismatched package OTA feed for '$PackageId'."
}
if ([string]$feed.channel -ne $Channel) {
    throw "Package OTA feed channel '$($feed.channel)' does not match '$Channel'."
}
if ([string]::IsNullOrWhiteSpace([string]$feed.version) -or
    [string]$feed.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Package OTA feed has an invalid version."
}

$feedSigned = [bool]$feed.signing.signed
if ($feedSigned) {
    $publicKey = Resolve-PublicKey -StateRootFull $StateRootFull
    Assert-FeedSignature `
        -Json $feedContent.Json `
        -SignatureBase64 $feedContent.Signature `
        -PublicKeyHex $publicKey `
        -FeedSigned $true
} elseif (-not $AllowUnsigned) {
    throw 'Package OTA feed is unsigned. Pass -AllowUnsigned only for local or nightly development feeds.'
}

$latest = @($feed.releases | Select-Object -First 1)
if ($latest.Count -eq 0) {
    throw "Package OTA feed for '$PackageId' has no releases."
}
$latest = $latest[0]
$targetVersion = [string]$latest.version
if ($targetVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Package OTA release has an invalid version '$targetVersion'."
}

$versionCompare = Compare-OtaVersion -Left $targetVersion -Right $installedPackageVersion
if ($versionCompare -le 0 -and -not $Force) {
    [pscustomobject]@{
        success = $true
        packageId = $PackageId
        installedVersion = $installedPackageVersion
        latestVersion = $targetVersion
        available = $false
        reason = if ($versionCompare -lt 0) { 'downgrade-blocked' } else { 'up-to-date' }
    } | ConvertTo-Json -Depth 4
    exit 0
}

$coreMinimum = [string]$feed.coreMinimumVersion
if ($coreMinimum -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Package OTA feed has an invalid core minimum version '$coreMinimum'."
}
if ((Compare-OtaVersion -Left $coreVersion -Right $coreMinimum) -lt 0) {
    throw "Package '$PackageId' requires core >= $coreMinimum but installed core is $coreVersion."
}

$archivePath = Resolve-PackageArchive `
    -BaseUrl $feedContent.BaseUrl `
    -Asset ([string]$latest.asset) `
    -ExpectedSha256 ([string]$latest.sha256) `
    -StateRootFull $StateRootFull

$extractRoot = Join-Path $StateRootFull "downloads\pkg-$PackageId-$targetVersion"
if (Test-Path -LiteralPath $extractRoot -PathType Container) {
    Remove-Item -LiteralPath $extractRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $extractRoot)

$extractedModuleJson = Join-Path $extractRoot 'module.json'
$extractedPackageJson = Join-Path $extractRoot 'package.json'
$extractedManifestPath = if (Test-Path -LiteralPath $extractedModuleJson -PathType Leaf) {
    $extractedModuleJson
} elseif (Test-Path -LiteralPath $extractedPackageJson -PathType Leaf) {
    $extractedPackageJson
} else {
    $null
}
if ($null -eq $extractedManifestPath) {
    throw "Package OTA archive has neither module.json nor package.json: $extractRoot"
}
$extractedModule = Get-Content -LiteralPath $extractedManifestPath -Raw | ConvertFrom-Json
if ([string]$extractedModule.id -ne $PackageId) {
    throw "Package OTA archive id '$($extractedModule.id)' does not match '$PackageId'."
}

$cli = Join-Path $InstallRootFull 'Cli\MyPowerTools.Cli.exe'
if (-not (Test-Path -LiteralPath $cli -PathType Leaf)) {
    throw "Installed CLI is missing: $cli"
}
& $cli package trust $extractRoot --strict | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Package trust validation failed for '$PackageId' v$targetVersion."
}

if (-not $NoRuntimeRestart) {
    $installPrefix = $InstallRootFull.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        $processPath = $null
        try { $processPath = $process.MainModule.FileName } catch {}
        if ($processPath -and $processPath.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                [void]$process.CloseMainWindow()
                [void]$process.WaitForExit(3000)
            }
            if (-not $process.HasExited) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
            }
            $process.Dispose()
        }
    }
}

$transactionRoot = Join-Path $StateRootFull 'transactions'
New-Item -ItemType Directory -Path $transactionRoot -Force | Out-Null
$transactionId = "pkg-$PackageId-$([Guid]::NewGuid().ToString('N'))"
$backupRoot = Join-Path $transactionRoot $transactionId
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
$packageBackup = Join-Path $backupRoot 'package'
$packageExisted = Test-Path -LiteralPath $packageRoot -PathType Container
$installedOk = $false

try {
    if ($packageExisted) {
        Move-Item -LiteralPath $packageRoot -Destination $packageBackup
    }
    Move-Item -LiteralPath $extractRoot -Destination $packageRoot

    & $cli package trust $packageRoot --strict | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Installed package trust validation failed for '$PackageId'."
    }

    if (-not $NoRuntimeRestart) {
        $runtimeScript = Join-Path $InstallRootFull 'start-user-runtime.ps1'
        if (Test-Path -LiteralPath $runtimeScript -PathType Leaf) {
            & $runtimeScript -InstallRoot $InstallRootFull -DataRoot $DataRootFull -StartRunner | Out-Null
        }
        $configureScript = Join-Path $InstallRootFull 'configure-user-services.ps1'
        if ((Test-Path -LiteralPath (Join-Path $packageRoot 'service-units') -PathType Container) -and
            (Test-Path -LiteralPath $configureScript -PathType Leaf)) {
            & $configureScript -Mode Install -InstallRoot $InstallRootFull -DataRoot $DataRootFull | Out-Null
        }
    }

    $installedOk = $true
    $packagesStatePath = Join-Path $StateRootFull 'installed-packages.json'
    $packagesState = [ordered]@{}
    if (Test-Path -LiteralPath $packagesStatePath -PathType Leaf) {
        try {
            $packagesState = (Get-Content -LiteralPath $packagesStatePath -Raw | ConvertFrom-Json)
        } catch {}
    }
    $packagesState.$PackageId = [ordered]@{
        version = $targetVersion
        channel = $Channel
        installedAt = (Get-Date).ToString('O')
        archiveSha256 = [string]$latest.sha256
        coreVersion = $coreVersion
    }
    $packagesState | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $packagesStatePath -Encoding UTF8

    $result = [ordered]@{
        success = $true
        packageId = $PackageId
        fromVersion = $installedPackageVersion
        toVersion = $targetVersion
        coreVersion = $coreVersion
        channel = $Channel
        archiveSha256 = [string]$latest.sha256
        runtimeRestarted = -not $NoRuntimeRestart.IsPresent
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Write-Utf8TextFile -Path (Join-Path $StateRootFull 'last-package-update.json') -Value (
        $result | ConvertTo-Json -Depth 5)
    $result | ConvertTo-Json -Depth 5
}
catch {
    if (Test-Path -LiteralPath $packageRoot -PathType Container) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($packageExisted -and (Test-Path -LiteralPath $packageBackup -PathType Container)) {
        Move-Item -LiteralPath $packageBackup -Destination $packageRoot
    }
    throw
}
finally {
    if ($installedOk -and (Test-Path -LiteralPath $backupRoot -PathType Container)) {
        $transactionParentFull = [IO.Path]::GetFullPath($transactionRoot).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $backupFull = [IO.Path]::GetFullPath($backupRoot)
        if ($backupFull.StartsWith($transactionParentFull, [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $backupRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
