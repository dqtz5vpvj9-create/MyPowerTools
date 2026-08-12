[CmdletBinding()]
param(
    [ValidateSet('Check', 'Apply', 'Status')]
    [string]$Command = 'Check',
    [ValidateSet('stable', 'nightly', 'local')]
    [string]$Channel = '',
    [string]$FeedUrl = '',
    [string]$DownloadBaseUrl = '',
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
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
    [switch]$FullRecoveryOnHealthFailure
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:OtaBoundParameters = $PSBoundParameters

# Production public key (64 hex chars of the raw Ed25519 public key). CI writes
# the generated key into the release package and the installer copies it to the
# OTA state directory; the embedded value below is the fallback used when the
# state file is unavailable. Keep this in sync with the CI signing key.
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

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Expected JSON file is missing: $Path"
    }
    return (Read-Utf8TextFile -Path $Path | ConvertFrom-Json)
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
        return $script:EmbeddedPublicKeyHex.Trim()
    }

    return ''
}

function Test-ValidHexKey {
    param([Parameter(Mandatory = $true)][string]$Value)

    return $Value -match '^[0-9a-fA-F]{64}$'
}

function Resolve-FeedContent {
    param(
        [Parameter(Mandatory = $true)][string]$StateRootFull,
        [Parameter(Mandatory = $true)][string]$ChannelName
    )

    $downloadsDir = Join-Path $StateRootFull 'downloads'
    New-Item -ItemType Directory -Path $downloadsDir -Force | Out-Null

    if (-not [string]::IsNullOrWhiteSpace($LocalFeedPath)) {
        $feedFull = [IO.Path]::GetFullPath($LocalFeedPath)
        if (-not (Test-Path -LiteralPath $feedFull -PathType Leaf)) {
            throw "Local OTA feed does not exist: $feedFull"
        }
        $feedJson = Read-Utf8TextFile -Path $feedFull
        $feed = $feedJson | ConvertFrom-Json
        $signaturePath = "$feedFull.sig"
        $feedSignature = if (Test-Path -LiteralPath $signaturePath -PathType Leaf) {
            (Read-Utf8TextFile -Path $signaturePath).Trim()
        } else {
            ''
        }
        return [pscustomobject]@{
            Json = $feedJson
            Feed = $feed
            Signature = $feedSignature
            BaseUrl = if ([string]::IsNullOrWhiteSpace($LocalPackageRoot)) {
                [IO.Path]::GetDirectoryName($feedFull)
            } else {
                [IO.Path]::GetFullPath($LocalPackageRoot)
            }
        }
    }

    $feedUri = $FeedUrl
    if ([string]::IsNullOrWhiteSpace($feedUri)) {
        $feedUri = "https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/latest/download/channel-$ChannelName.json"
    }
    $feedDestination = Join-Path $downloadsDir ("channel-$ChannelName.json")
    Invoke-WebRequest -Uri $feedUri -OutFile $feedDestination -UseBasicParsing
    $feedJson = Read-Utf8TextFile -Path $feedDestination
    $feed = $feedJson | ConvertFrom-Json

    $signatureUri = "$feedUri.sig"
    $feedSignature = ''
    try {
        $signatureDestination = "$feedDestination.sig"
        Invoke-WebRequest -Uri $signatureUri -OutFile $signatureDestination -UseBasicParsing
        $feedSignature = (Read-Utf8TextFile -Path $signatureDestination).Trim()
    }
    catch {
        $feedSignature = ''
    }

    $baseUri = if (-not [string]::IsNullOrWhiteSpace($DownloadBaseUrl)) {
        $DownloadBaseUrl.TrimEnd('/')
    } else {
        $feedUri.Substring(0, $feedUri.LastIndexOf('/'))
    }
    return [pscustomobject]@{
        Json = $feedJson
        Feed = $feed
        Signature = $feedSignature
        BaseUrl = $baseUri
    }
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
        throw 'OTA feed is unsigned. Pass -AllowUnsigned only for local or nightly development feeds.'
    }

    if (-not (Test-ValidHexKey -Value $PublicKeyHex)) {
        throw 'OTA feed requires a 64-character Ed25519 public key (hex) but none is configured.'
    }
    if ([string]::IsNullOrWhiteSpace($SignatureBase64)) {
        throw 'OTA feed is missing its Ed25519 signature.'
    }

    try {
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
    }
    catch {
        throw "OTA feed signature verification failed: $($_.Exception.Message)"
    }
    if (-not $valid) {
        throw 'OTA feed signature verification failed: signature does not match the feed bytes.'
    }
}

function Read-InstalledRelease {
    param([Parameter(Mandatory = $true)][string]$StateRootFull)

    $path = Join-Path $StateRootFull 'installed-release.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        return $null
    }
    try {
        return (Read-JsonFile -Path $path)
    }
    catch {
        return $null
    }
}

function Select-OtaPackage {
    param(
        [Parameter(Mandatory = $true)][object]$Feed,
        [Parameter(Mandatory = $true)][string]$StateRootFull,
        [Parameter(Mandatory = $true)][string]$CurrentVersion
    )

    $installedManifestPath = Join-Path $StateRootFull 'installed-files.manifest.json'
    $installedManifestSha256 = ''
    if (Test-Path -LiteralPath $installedManifestPath -PathType Leaf) {
        $installedManifestSha256 = (
            Get-FileHash -LiteralPath $installedManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    $matchingDelta = $null
    foreach ($delta in @($Feed.deltas)) {
        if (-not [string]::IsNullOrWhiteSpace($installedManifestSha256) -and
            ([string]$delta.fromManifestSha256).ToLowerInvariant() -eq $installedManifestSha256 -and
            [string]$delta.fromVersion -eq $CurrentVersion) {
            $matchingDelta = $delta
            break
        }
    }
    if ($null -eq $matchingDelta -and -not [string]::IsNullOrWhiteSpace($installedManifestSha256)) {
        foreach ($delta in @($Feed.deltas)) {
            if (([string]$delta.fromManifestSha256).ToLowerInvariant() -eq $installedManifestSha256) {
                $matchingDelta = $delta
                break
            }
        }
    }

    if ($null -ne $matchingDelta) {
        return [pscustomobject]@{
            Kind = 'delta'
            Asset = [string]$matchingDelta.asset
            Sha256 = ([string]$matchingDelta.sha256).ToLowerInvariant()
            Size = [long]$matchingDelta.size
            FromVersion = [string]$matchingDelta.fromVersion
            FromManifestSha256 = ([string]$matchingDelta.fromManifestSha256).ToLowerInvariant()
        }
    }

    return [pscustomobject]@{
        Kind = 'full'
        Asset = [string]$Feed.full.asset
        Sha256 = ([string]$Feed.full.sha256).ToLowerInvariant()
        Size = [long]$Feed.full.size
        FromVersion = $CurrentVersion
        FromManifestSha256 = $installedManifestSha256
    }
}

function Resolve-PackageFile {
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
            throw "Local OTA package is missing: $localAsset"
        }
        Copy-Item -LiteralPath $localAsset -Destination $destination -Force
    } else {
        Invoke-WebRequest -Uri "$BaseUrl/$Asset" -OutFile $destination -UseBasicParsing
    }

    $actualHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) {
        Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
        throw "Downloaded OTA package SHA-256 mismatch for $Asset. expected=$ExpectedSha256 actual=$actualHash"
    }
    return $destination
}

function Test-OtaHealth {
    param(
        [Parameter(Mandatory = $true)][string]$InstallRootFull,
        [Parameter(Mandatory = $true)][string]$StateRootFull,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $criticalPaths = @(
        'Runner\MyPowerTools.Runner.exe',
        'Shell\MyPowerTools.Shell.Avalonia.exe',
        'Cli\MyPowerTools.Cli.exe',
        'ServiceManager\MyPowerTools.ServiceManager.exe',
        'MyPowerTools.exe',
        'install-windows.ps1',
        'invoke-ota-update.ps1',
        'ota-update.ps1',
        'configure-user-services.ps1',
        'start-user-runtime.ps1',
        'new-ota-file-manifest.ps1',
        'new-ota-delta-package.ps1'
    )
    $results = [Collections.Generic.List[object]]::new()
    $manifestPath = Join-Path $StateRootFull 'installed-files.manifest.json'
    $fileMap = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
        $manifest = Read-JsonFile -Path $manifestPath
        foreach ($record in @($manifest.files)) {
            if (-not $fileMap.ContainsKey([string]$record.path)) {
                $fileMap.Add([string]$record.path, $record)
            }
        }
    }

    foreach ($relative in $criticalPaths) {
        $target = Join-Path $InstallRootFull ($relative.Replace('/', '\'))
        $ok = Test-Path -LiteralPath $target -PathType Leaf
        $detail = 'missing'
        if ($ok) {
            $record = $null
            if ($fileMap.TryGetValue($relative.Replace('\', '/'), [ref]$record)) {
                $item = Get-Item -LiteralPath $target -Force
                $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
                $ok = [long]$item.Length -eq [long]$record.length -and $hash -eq [string]$record.sha256
                $detail = if ($ok) { 'hash-ok' } else { 'hash-mismatch' }
            } else {
                $detail = 'present-not-in-manifest'
            }
        }
        $results.Add([ordered]@{
            path = $relative
            ok = $ok
            detail = $detail
        })
    }

    $installManifest = Join-Path $InstallRootFull 'install.manifest.json'
    $installManifestOk = Test-Path -LiteralPath $installManifest -PathType Leaf
    $results.Add([ordered]@{
        path = 'install.manifest.json'
        ok = $installManifestOk
        detail = if ($installManifestOk) { 'present' } else { 'missing' }
    })

    $failed = @($results | Where-Object { -not $_.ok })
    $health = [ordered]@{
        checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        expectedVersion = $ExpectedVersion
        ok = $failed.Count -eq 0
        failedCount = $failed.Count
        checks = $results.ToArray()
    }
    Write-Utf8TextFile -Path (Join-Path $StateRootFull 'health-check.json') -Value (
        $health | ConvertTo-Json -Depth 6)
    return $health
}

function Update-InstalledRelease {
    param(
        [Parameter(Mandatory = $true)][string]$StateRootFull,
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$ChannelName,
        [Parameter(Mandatory = $true)][string]$PackageKind,
        [Parameter(Mandatory = $true)][string]$InstallRootFull,
        [Parameter(Mandatory = $true)][string]$DataRootFull,
        [string]$Repository = ''
    )

    $installed = Read-InstalledRelease -StateRootFull $StateRootFull
    $manifestPath = Join-Path $StateRootFull 'installed-files.manifest.json'
    $release = [ordered]@{
        schemaVersion = 1
        product = if ($installed -and -not [string]::IsNullOrWhiteSpace([string]$installed.product)) {
            [string]$installed.product
        } else { 'MyPowerTools' }
        version = $Version
        channel = $ChannelName
        installedAt = (Get-Date).ToString('O')
        installDir = $InstallRootFull
        dataRoot = $DataRootFull
        repository = $Repository
        manifestPath = 'installed-files.manifest.json'
        manifestSha256 = if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
            (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        } else { '' }
        packageKind = $PackageKind
    }
    Write-Utf8TextFile -Path (Join-Path $StateRootFull 'installed-release.json') -Value (
        $release | ConvertTo-Json -Depth 5)
}

function Invoke-BootstrapRelaunch {
    param(
        [Parameter(Mandatory = $true)][string]$InstallRootFull,
        [Parameter(Mandatory = $true)][string]$StateRootFull
    )

    $bootstrapDir = Join-Path $StateRootFull 'bootstrap'
    New-Item -ItemType Directory -Path $bootstrapDir -Force | Out-Null
    $requiredScripts = @(
        'ota-update.ps1',
        'ed25519.cs',
        'new-ota-file-manifest.ps1',
        'new-ota-delta-package.ps1',
        'invoke-ota-update.ps1'
    )
    foreach ($scriptName in $requiredScripts) {
        $candidates = @(
            (Join-Path $InstallRootFull $scriptName),
            (Join-Path $PSScriptRoot $scriptName)
        ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
        if (-not $candidates) {
            throw "Unable to bootstrap update scripts: $scriptName was not found in the install or source layout."
        }
        Copy-Item -LiteralPath $candidates[0] -Destination (Join-Path $bootstrapDir $scriptName) -Force
    }

    $relaunchArguments = [Collections.Generic.List[string]]::new()
    foreach ($key in $script:OtaBoundParameters.Keys) {
        if ($key -eq 'BootstrapReady' -or $key -eq 'SkipBootstrap') {
            continue
        }
        $value = $script:OtaBoundParameters[$key]
        if ($value -is [switch]) {
            if ($value.IsPresent) {
                $relaunchArguments.Add("-$key")
            }
            continue
        }
        if ($null -ne $value) {
            $relaunchArguments.Add("-$key")
            $relaunchArguments.Add([string]$value)
        }
    }
    $relaunchArguments.Add('-BootstrapReady')

    $pwsh = Get-Command 'pwsh.exe' -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $bootstrapScript = Join-Path $bootstrapDir 'ota-update.ps1'
    & $pwsh.Source -NoLogo -NoProfile -NonInteractive -File $bootstrapScript @relaunchArguments
    exit $LASTEXITCODE
}

function Invoke-DevOverlayReapply {
    param(
        [Parameter(Mandatory = $true)][pscustomobject]$Overlay,
        [Parameter(Mandatory = $true)][string]$InstallRootFull
    )

    $repoRoot = [string]$Overlay.repositoryRoot
    $configuration = [string]$Overlay.configuration
    if ([string]::IsNullOrWhiteSpace($configuration)) {
        $configuration = 'Debug'
    }
    $devScript = Join-Path $repoRoot 'scripts\update-windows-dev.ps1'
    if (-not (Test-Path -LiteralPath $devScript -PathType Leaf)) {
        return [ordered]@{
            reapplied = $false
            note = "dev overlay source not found: $devScript"
        }
    }

    $pwsh = Get-Command 'pwsh.exe' -CommandType Application -ErrorAction Stop |
        Select-Object -First 1
    $devOutput = & $pwsh.Source -NoLogo -NoProfile -NonInteractive -File $devScript `
        -Scope Core -Configuration $configuration -NoOpenShell
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        return [ordered]@{
            reapplied = $true
            note = "dev overlay reapplied from $repoRoot after base update"
        }
    }
    return [ordered]@{
        reapplied = $false
        note = "dev overlay reapply failed with exit code $exitCode"
    }
}

function Invoke-DeltaApply {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256,
        [Parameter(Mandatory = $true)][string]$InstallRootFull,
        [Parameter(Mandatory = $true)][string]$StateRootFull,
        [Parameter(Mandatory = $true)][string]$DataRootFull,
        [switch]$ApplyDeletes,
        [switch]$RestartRuntime,
        [switch]$KeepBackup,
        [switch]$SkipDriftCheck
    )

    $targetManifestPath = Join-Path $StateRootFull 'installed-files.manifest.json'
    if (-not (Test-Path -LiteralPath $targetManifestPath -PathType Leaf)) {
        throw "OTA target manifest is missing: $targetManifestPath"
    }
    $applyParams = @{
        PackagePath = $PackagePath
        ExpectedPackageSha256 = $ExpectedSha256
        TargetRoot = $InstallRootFull
        TargetManifestPath = $targetManifestPath
        StateRoot = $StateRootFull
        RuntimeDataRoot = $DataRootFull
    }
    if ($ApplyDeletes) {
        $applyParams.ApplyDeletes = $true
    }
    if ($RestartRuntime) {
        $applyParams.StopTargetProcesses = $true
        $applyParams.RestartRuntime = $true
    }
    if ($KeepBackup) {
        $applyParams.KeepBackup = $true
    }
    if ($SkipDriftCheck) {
        $applyParams.SkipDriftCheck = $true
    }

    $applyScript = Join-Path $PSScriptRoot 'invoke-ota-update.ps1'
    $applyOutput = @(& $applyScript @applyParams | ForEach-Object { [string]$_ })
    $applyResult = ($applyOutput -join [Environment]::NewLine) | ConvertFrom-Json
    if (-not [bool]$applyResult.success) {
        throw 'OTA delta apply reported failure.'
    }

    $desiredManifest = Join-Path $StateRootFull 'desired-source-manifest.json'
    if (-not (Test-Path -LiteralPath $desiredManifest -PathType Leaf)) {
        throw 'OTA delta apply did not persist the new source manifest.'
    }
    Copy-Item -LiteralPath $desiredManifest -Destination $targetManifestPath -Force
    return $applyResult
}

function Invoke-FullApply {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$InstallRootFull,
        [Parameter(Mandatory = $true)][string]$DataRootFull,
        [Parameter(Mandatory = $true)][string]$StateRootFull,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion,
        [switch]$RestartRuntime
    )

    $extractRoot = Join-Path $StateRootFull "downloads\full-$ExpectedVersion"
    if (Test-Path -LiteralPath $extractRoot -PathType Container) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $extractRoot)

    $installScript = Join-Path $extractRoot 'install-windows.ps1'
    if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
        throw "Full package is missing install-windows.ps1: $extractRoot"
    }
    $installParams = @{
        PackageRoot = $extractRoot
        InstallDir = $InstallRootFull
        DataRoot = $DataRootFull
        NoOpenApp = $true
    }
    if ($RestartRuntime) {
        $installParams.StartRunner = $true
    } else {
        $installParams.NoStartRunner = $true
    }
    & $installScript @installParams | Out-Null

    $shippedManifest = Join-Path $extractRoot 'MyPowerTools-win-x64.manifest.json'
    if (Test-Path -LiteralPath $shippedManifest -PathType Leaf) {
        Copy-Item -LiteralPath $shippedManifest -Destination (
            Join-Path $StateRootFull 'installed-files.manifest.json') -Force
    } else {
        $manifestScript = Join-Path $extractRoot 'new-ota-file-manifest.ps1'
        if (Test-Path -LiteralPath $manifestScript -PathType Leaf) {
            [void](& $manifestScript `
                -Root $InstallRootFull `
                -OutputPath (Join-Path $StateRootFull 'installed-files.manifest.json') `
                -Version $ExpectedVersion)
        }
    }
}

function Invoke-FullRecovery {
    param(
        [Parameter(Mandatory = $true)][object]$Decision,
        [Parameter(Mandatory = $true)][string]$BaseUrl,
        [Parameter(Mandatory = $true)][string]$InstallRootFull,
        [Parameter(Mandatory = $true)][string]$DataRootFull,
        [Parameter(Mandatory = $true)][string]$StateRootFull
    )

    $fullPackage = Resolve-PackageFile `
        -BaseUrl $BaseUrl `
        -Asset ([string]$Decision.FullAsset) `
        -ExpectedSha256 ([string]$Decision.FullSha256) `
        -StateRootFull $StateRootFull
    Invoke-FullApply `
        -PackagePath $fullPackage `
        -InstallRootFull $InstallRootFull `
        -DataRootFull $DataRootFull `
        -StateRootFull $StateRootFull `
        -ExpectedVersion ([string]$Decision.FeedVersion) `
        -RestartRuntime:(-not $NoRuntimeRestart.IsPresent)
}

$InstallRootFull = [IO.Path]::GetFullPath($InstallRoot)
$DataRootFull = [IO.Path]::GetFullPath($DataRoot)
if ([string]::IsNullOrWhiteSpace($StateRoot)) {
    $StateRoot = Join-Path $DataRootFull 'ota-state'
}
$StateRootFull = [IO.Path]::GetFullPath($StateRoot)
New-Item -ItemType Directory -Path $StateRootFull -Force | Out-Null

# Dev overlay detection: update-windows-dev.ps1 marks the install root with
# dev-update.manifest.json. OTA never overwrites the overlay binaries in place;
# it updates the canonical release base and then reapplies the overlay.
$devOverlayManifestPath = Join-Path $InstallRootFull 'dev-update.manifest.json'
$IsDevOverlay = Test-Path -LiteralPath $devOverlayManifestPath -PathType Leaf
$DevOverlay = if ($IsDevOverlay) {
    Get-Content -Raw -LiteralPath $devOverlayManifestPath | ConvertFrom-Json
} else {
    $null
}

$installedRelease = Read-InstalledRelease -StateRootFull $StateRootFull
if ([string]::IsNullOrWhiteSpace($CurrentVersion)) {
    if ($null -ne $installedRelease -and
        -not [string]::IsNullOrWhiteSpace([string]$installedRelease.version)) {
        $CurrentVersion = [string]$installedRelease.version
    } else {
        $CurrentVersion = '0.0.0'
    }
}
if ([string]::IsNullOrWhiteSpace($Channel)) {
    if ($null -ne $installedRelease -and
        -not [string]::IsNullOrWhiteSpace([string]$installedRelease.channel)) {
        $Channel = [string]$installedRelease.channel
    } else {
        $Channel = 'stable'
    }
}
if ($CurrentVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Current version is not a valid OTA version: $CurrentVersion"
}

if ($Command -eq 'Status') {
    $status = [ordered]@{
        schemaVersion = 1
        mode = if ($IsDevOverlay) { 'dev-overlay' } else { 'installed' }
        installed = if ($null -ne $installedRelease) {
            [ordered]@{
                product = [string]$installedRelease.product
                version = [string]$installedRelease.version
                channel = [string]$installedRelease.channel
                installedAt = [string]$installedRelease.installedAt
                manifestSha256 = [string]$installedRelease.manifestSha256
            }
        } else { $null }
        lastCheck = if (Test-Path -LiteralPath (Join-Path $StateRootFull 'last-check.json')) {
            Read-JsonFile -Path (Join-Path $StateRootFull 'last-check.json')
        } else { $null }
        lastUpdate = if (Test-Path -LiteralPath (Join-Path $StateRootFull 'last-update.json')) {
            Read-JsonFile -Path (Join-Path $StateRootFull 'last-update.json')
        } else { $null }
        health = if (Test-Path -LiteralPath (Join-Path $StateRootFull 'health-check.json')) {
            Read-JsonFile -Path (Join-Path $StateRootFull 'health-check.json')
        } else { $null }
    }
    $status | ConvertTo-Json -Depth 8
    exit 0
}

$feedContent = Resolve-FeedContent -StateRootFull $StateRootFull -ChannelName $Channel
$feed = $feedContent.Feed
if ([int]$feed.schemaVersion -ne 1 -or
    [string]$feed.kind -ne 'mypowertools-ota-channel-feed') {
    throw 'Unsupported OTA channel feed.'
}
if ([string]$feed.channel -ne $Channel) {
    throw "OTA feed channel '$($feed.channel)' does not match requested channel '$Channel'."
}
if ([string]$feed.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "OTA feed contains an invalid version '$($feed.version)'."
}

$feedSigned = [bool]$feed.signing.signed
if ($feedSigned) {
    $publicKey = Resolve-PublicKey -StateRootFull $StateRootFull
    Assert-FeedSignature `
        -Json $feedContent.Json `
        -SignatureBase64 $feedContent.Signature `
        -PublicKeyHex $publicKey `
        -FeedSigned $true
} else {
    if (-not $AllowUnsigned) {
        throw 'OTA feed is unsigned. Pass -AllowUnsigned only for local or nightly development feeds.'
    }
}

$versionCompare = Compare-OtaVersion -Left ([string]$feed.version) -Right $CurrentVersion
$available = $versionCompare -gt 0
$reason = if ($versionCompare -eq 0) { 'up-to-date' } elseif ($versionCompare -lt 0) { 'downgrade-blocked' } else { 'update-available' }
if ($Force -and $versionCompare -le 0) {
    $available = $true
    $reason = 'forced'
}

$decision = $null
if ($available) {
    $decision = Select-OtaPackage -Feed $feed -StateRootFull $StateRootFull -CurrentVersion $CurrentVersion
}

$check = [ordered]@{
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    channel = $Channel
    mode = if ($IsDevOverlay) { 'dev-overlay' } else { 'installed' }
    currentVersion = $CurrentVersion
    latestVersion = [string]$feed.version
    available = $available
    reason = $reason
    signed = $feedSigned
    package = if ($null -ne $decision) {
        [ordered]@{
            kind = $decision.Kind
            asset = $decision.Asset
            sha256 = $decision.Sha256
            size = $decision.Size
            fromVersion = $decision.FromVersion
        }
    } else {
        $null
    }
    devOverlay = if ($IsDevOverlay) {
        [ordered]@{
            repositoryRoot = [string]$DevOverlay.repositoryRoot
            configuration = [string]$DevOverlay.configuration
            note = 'dev 覆盖模式：OTA 不直接更新覆盖层；如检测到系统安装版，将更新系统安装并重新应用 dev 覆盖。'
        }
    } else {
        $null
    }
}
Write-Utf8TextFile -Path (Join-Path $StateRootFull 'last-check.json') -Value (
    $check | ConvertTo-Json -Depth 5)

if ($Command -eq 'Check' -or -not $available) {
    $check | ConvertTo-Json -Depth 5
    exit 0
}

# Apply path
if ($IsDevOverlay -and -not (Test-Path -LiteralPath (Join-Path $InstallRootFull 'install.manifest.json') -PathType Leaf)) {
    throw '未检测到系统安装版（install.manifest.json 缺失）。dev 覆盖模式无法执行 OTA，请先完整安装 MyPowerTools 后再试。'
}

# Apply path
$mutexCreated = $false
$updateMutex = [Threading.Mutex]::new($false, 'MyPowerTools.OtaUpdate', [ref]$mutexCreated)
if (-not $mutexCreated) {
    if (-not $updateMutex.WaitOne(5000)) {
        $updateMutex.Dispose()
        throw 'Another MyPowerTools OTA update is already running.'
    }
}
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$autostartNames = @('MyPowerTools', 'MyPowerTools.ServiceManager')
$savedAutostartValues = @{}
foreach ($name in $autostartNames) {
    $value = (Get-ItemProperty -LiteralPath $runKeyPath -Name $name -ErrorAction SilentlyContinue).$name
    if ($null -ne $value -and -not [string]::IsNullOrWhiteSpace([string]$value)) {
        $savedAutostartValues[$name] = [string]$value
    }
}
$otaCheckTaskDisabled = $false
try {
    # Maintenance mode: stop auto-relaunch vectors so the transaction is not
    # interrupted by Runner/ServiceManager or the daily OTA check task.
    foreach ($name in $autostartNames) {
        Remove-ItemProperty -LiteralPath $runKeyPath -Name $name -ErrorAction SilentlyContinue
    }
    if (Get-ScheduledTask -TaskName 'MyPowerTools OTA Check' -ErrorAction SilentlyContinue) {
        Disable-ScheduledTask -TaskName 'MyPowerTools OTA Check' -ErrorAction SilentlyContinue | Out-Null
        $otaCheckTaskDisabled = $true
    }

    if (-not $BootstrapReady -and -not $SkipBootstrap) {
        if ($mutexCreated) {
            try { $updateMutex.ReleaseMutex() } catch {}
            $mutexCreated = $false
        }
        Invoke-BootstrapRelaunch -InstallRootFull $InstallRootFull -StateRootFull $StateRootFull
    }

    $packagePath = Resolve-PackageFile `
        -BaseUrl $feedContent.BaseUrl `
        -Asset $decision.Asset `
        -ExpectedSha256 $decision.Sha256 `
        -StateRootFull $StateRootFull

    $restartRuntime = -not $NoRuntimeRestart.IsPresent
    $applyDeletes = -not $NoApplyDeletes.IsPresent
    $applyResult = $null
    $packageKind = $decision.Kind
    if ($decision.Kind -eq 'delta') {
        $applyResult = Invoke-DeltaApply `
            -PackagePath $packagePath `
            -ExpectedSha256 $decision.Sha256 `
            -InstallRootFull $InstallRootFull `
            -StateRootFull $StateRootFull `
            -DataRootFull $DataRootFull `
            -ApplyDeletes:$applyDeletes `
            -RestartRuntime:$restartRuntime `
            -KeepBackup:$KeepBackup.IsPresent `
            -SkipDriftCheck:$IsDevOverlay
    } else {
        Invoke-FullApply `
            -PackagePath $packagePath `
            -InstallRootFull $InstallRootFull `
            -DataRootFull $DataRootFull `
            -StateRootFull $StateRootFull `
            -ExpectedVersion ([string]$feed.version) `
            -RestartRuntime:$restartRuntime
        $packageKind = 'full'
    }

    $repository = if ($null -ne $installedRelease -and
        -not [string]::IsNullOrWhiteSpace([string]$installedRelease.repository)) {
        [string]$installedRelease.repository
    } else {
        'https://github.com/dqtz5vpvj9-create/MyPowerTools'
    }
    Update-InstalledRelease `
        -StateRootFull $StateRootFull `
        -Version ([string]$feed.version) `
        -ChannelName $Channel `
        -PackageKind $packageKind `
        -InstallRootFull $InstallRootFull `
        -DataRootFull $DataRootFull `
        -Repository $repository

    $health = Test-OtaHealth `
        -InstallRootFull $InstallRootFull `
        -StateRootFull $StateRootFull `
        -ExpectedVersion ([string]$feed.version)
    if (-not [bool]$health.ok -and $decision.Kind -eq 'delta' -and $FullRecoveryOnHealthFailure.IsPresent) {
        $fullDecision = [pscustomobject]@{
            FeedVersion = [string]$feed.version
            FullAsset = [string]$feed.full.asset
            FullSha256 = [string]$feed.full.sha256
        }
        Invoke-FullRecovery `
            -Decision $fullDecision `
            -BaseUrl $feedContent.BaseUrl `
            -InstallRootFull $InstallRootFull `
            -DataRootFull $DataRootFull `
            -StateRootFull $StateRootFull
        $packageKind = 'full-recovery'
        Update-InstalledRelease `
            -StateRootFull $StateRootFull `
            -Version ([string]$feed.version) `
            -ChannelName $Channel `
            -PackageKind $packageKind `
            -InstallRootFull $InstallRootFull `
            -DataRootFull $DataRootFull `
            -Repository $repository
        $health = Test-OtaHealth `
            -InstallRootFull $InstallRootFull `
            -StateRootFull $StateRootFull `
            -ExpectedVersion ([string]$feed.version)
    }
    if (-not [bool]$health.ok) {
        throw "OTA health check failed after update ($($health.failedCount) failed checks)."
    }

    $devOverlayResult = if ($IsDevOverlay) {
        Invoke-DevOverlayReapply -Overlay $DevOverlay -InstallRootFull $InstallRootFull
    } else {
        $null
    }

    $updateResult = [ordered]@{
        success = $true
        channel = $Channel
        fromVersion = $CurrentVersion
        toVersion = [string]$feed.version
        packageKind = $packageKind
        packageSha256 = $decision.Sha256
        health = $health
        delta = $applyResult
        devOverlay = $devOverlayResult
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    Write-Utf8TextFile -Path (Join-Path $StateRootFull 'last-update.json') -Value (
        $updateResult | ConvertTo-Json -Depth 8)
    $updateResult | ConvertTo-Json -Depth 8
}
finally {
    foreach ($name in $savedAutostartValues.Keys) {
        try {
            if (-not (Test-Path -LiteralPath $runKeyPath)) {
                New-Item -Path $runKeyPath -Force | Out-Null
            }
            Set-ItemProperty -LiteralPath $runKeyPath -Name $name -Value $savedAutostartValues[$name]
        } catch {}
    }
    if ($otaCheckTaskDisabled) {
        try { Enable-ScheduledTask -TaskName 'MyPowerTools OTA Check' -ErrorAction SilentlyContinue | Out-Null } catch {}
    }
    if ($mutexCreated) {
        try { $updateMutex.ReleaseMutex() } catch {}
    }
    $updateMutex.Dispose()
}
