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
    [switch]$FullRecoveryOnHealthFailure,
    [switch]$SkipFullRecoveryOnHealthFailure
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
        $stateKey = (Read-Utf8TextFile -Path $stateKeyPath).Trim()
        if (Test-ValidHexKey -Value $stateKey) {
            return $stateKey
        }
        Write-Warning "OTA public key file is not a 64-character hex key; using the embedded key instead: $stateKeyPath"
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
        $feedUri = Resolve-ChannelFeedUri -ChannelName $ChannelName
    }
    $feedDestination = Join-Path $downloadsDir ("channel-$ChannelName.json")
    $feedHeaders = @{ 'User-Agent' = 'MyPowerTools-OTA' }
    Invoke-WebRequest -Uri $feedUri -OutFile $feedDestination -UseBasicParsing -Headers $feedHeaders
    $feedJson = Read-Utf8TextFile -Path $feedDestination
    $feed = $feedJson | ConvertFrom-Json

    $signatureUri = "$feedUri.sig"
    $feedSignature = ''
    try {
        $signatureDestination = "$feedDestination.sig"
        Invoke-WebRequest -Uri $signatureUri -OutFile $signatureDestination -UseBasicParsing -Headers $feedHeaders
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

function Resolve-ChannelFeedUri {
    param([Parameter(Mandatory = $true)][string]$ChannelName)

    $repo = 'dqtz5vpvj9-create/MyPowerTools'
    $feedSuffix = if ($script:OtaDistributionMode -eq 'web') { '-web' } else { '' }
    if ($ChannelName -eq 'stable') {
        return "https://github.com/$repo/releases/latest/download/channel-stable$feedSuffix.json"
    }

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(2)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('MyPowerTools-OTA')
    $client.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
    try {
        $releasesJson = $client.GetStringAsync(
            "https://api.github.com/repos/$repo/releases?per_page=30"
        ).GetAwaiter().GetResult()
    } finally {
        $client.Dispose()
        $handler.Dispose()
    }

    $releases = $releasesJson | ConvertFrom-Json
    $assetName = "channel-$ChannelName$feedSuffix.json"
    foreach ($release in @($releases)) {
        $asset = @($release.assets) |
            Where-Object { [string]$_.name -eq $assetName } |
            Select-Object -First 1
        if ($null -ne $asset -and -not [string]::IsNullOrWhiteSpace([string]$asset.browser_download_url)) {
            return [string]$asset.browser_download_url
        }
    }

    throw "No GitHub release currently publishes $assetName. Nightly/prerelease feeds are not on /releases/latest."
}

function Invoke-OtaDownload {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Asset
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(20)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('MyPowerTools-OTA')
    try {
        $response = $client.GetAsync(
            $Uri,
            [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead
        ).GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Download failed: $($response.StatusCode) for $Uri"
        }
        $totalBytes = $response.Content.Headers.ContentLength
        $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = [System.IO.File]::Open(
            $Destination,
            [System.IO.FileMode]::Create,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None
        )
        try {
            $buffer = New-Object byte[] 81920
            $received = [long]0
            while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $output.Write($buffer, 0, $read)
                $received += $read
                $percent = -1
                if ($totalBytes -gt 0) {
                    $percent = [int][Math]::Floor(($received * 100.0) / $totalBytes)
                }
                $progress = [ordered]@{
                    event = 'download-progress'
                    file = $Asset
                    received = $received
                    total = $totalBytes
                    percent = $percent
                }
                [Console]::Error.WriteLine(($progress | ConvertTo-Json -Compress))
            }
        } finally {
            $output.Dispose()
            $input.Dispose()
        }
    } finally {
        $client.Dispose()
        $handler.Dispose()
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
        Invoke-OtaDownload -Uri "$BaseUrl/$Asset" -Destination $destination -Asset $Asset
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
        distributionMode = if ($installed -and [string]$installed.distributionMode -eq 'web') { 'web' } else { 'full' }
    }
    Write-Utf8TextFile -Path (Join-Path $StateRootFull 'installed-release.json') -Value (
        $release | ConvertTo-Json -Depth 5)
}

function Clear-StaleOtaState {
    param(
        [Parameter(Mandatory = $true)][string]$StateRootFull,
        [Parameter(Mandatory = $true)][string[]]$KeepVersions
    )

    $transactionsDir = Join-Path $StateRootFull 'transactions'
    if (Test-Path -LiteralPath $transactionsDir -PathType Container) {
        $cutoff = (Get-Date).AddDays(-7)
        foreach ($transaction in @(Get-ChildItem -LiteralPath $transactionsDir -Directory -ErrorAction SilentlyContinue)) {
            if ($transaction.LastWriteTime -ge $cutoff) {
                continue
            }
            # A failed rollback keeps its transaction root for manual recovery.
            if (Test-Path -LiteralPath (Join-Path $transaction.FullName 'ROLLBACK-FAILED.txt') -PathType Leaf) {
                continue
            }
            Remove-Item -LiteralPath $transaction.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $downloadsDir = Join-Path $StateRootFull 'downloads'
    if (Test-Path -LiteralPath $downloadsDir -PathType Container) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $downloadsDir -ErrorAction SilentlyContinue)) {
            $entryVersions = @([regex]::Matches($entry.Name, '[0-9]+\.[0-9]+\.[0-9]+') |
                ForEach-Object { $_.Value })
            if ($entryVersions.Count -eq 0) {
                continue
            }
            if (@($entryVersions | Where-Object { $KeepVersions -contains $_ }).Count -gt 0) {
                continue
            }
            Remove-Item -LiteralPath $entry.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
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
    $previousLocation = Get-Location
    try {
        Set-Location -LiteralPath $bootstrapDir
        & $pwsh.Source -NoLogo -NoProfile -NonInteractive -File $bootstrapScript @relaunchArguments
        exit $LASTEXITCODE
    } finally {
        Set-Location -LiteralPath $previousLocation
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

function Resolve-OtaReopenRestart {
    param([Parameter(Mandatory = $true)][string]$StateRoot)

    $startShell = $true
    $startRunner = $true
    $taskNames = @()
    $planPath = Join-Path $StateRoot 'reopen-plan.json'
    if (Test-Path -LiteralPath $planPath -PathType Leaf) {
        $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
        $ids = @($plan.targets | ForEach-Object { [string]$_.id })
        $startShell = $ids -contains 'shell'
        $startRunner = ($ids -contains 'runner') -or ($ids -contains 'service-manager')
        if ($ids -contains 'smartbird') {
            $taskNames += 'SmartBirdThermostat'
        }
        if ($ids -contains 'energy') {
            $taskNames += 'EnergyServer'
        }
    }

    return [pscustomobject]@{
        StartShell = $startShell
        StartRunner = $startRunner
        TaskNames = $taskNames
    }
}

function Start-OtaReopenedScheduledTasks {
    param([string[]]$TaskNames)

    foreach ($name in @($TaskNames)) {
        if ([string]::IsNullOrWhiteSpace($name)) {
            continue
        }
        if ($null -eq (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue)) {
            continue
        }
        Start-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    }
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

    $preservedRuntimeComponents = $null
    if ($script:OtaDistributionMode -eq 'web') {
        $existingInstallManifestPath = Join-Path $InstallRootFull 'install.manifest.json'
        if (Test-Path -LiteralPath $existingInstallManifestPath -PathType Leaf) {
            $existingInstallManifest = Get-Content -LiteralPath $existingInstallManifestPath -Raw | ConvertFrom-Json
            $preservedRuntimeComponents = $existingInstallManifest.runtimeComponents
        }
    }

    $extractRoot = Join-Path $StateRootFull "downloads\full-$ExpectedVersion"
    if (Test-Path -LiteralPath $extractRoot -PathType Container) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $extractRoot)

    if ($script:OtaDistributionMode -eq 'web') {
        foreach ($relativePath in @('Runtime', 'Runtimes', 'Tools\AndroidPlatformTools')) {
            $ownedSource = Join-Path $InstallRootFull $relativePath
            if (Test-Path -LiteralPath $ownedSource -PathType Container) {
                $ownedDestination = Join-Path $extractRoot $relativePath
                if (Test-Path -LiteralPath $ownedDestination) {
                    Remove-Item -LiteralPath $ownedDestination -Recurse -Force
                }
                New-Item -ItemType Directory -Path (Split-Path -Parent $ownedDestination) -Force | Out-Null
                Copy-Item -LiteralPath $ownedSource -Destination $ownedDestination -Recurse -Force
            }
        }
    }

    $installScript = Join-Path $extractRoot 'install-windows.ps1'
    if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
        throw "Full package is missing install-windows.ps1: $extractRoot"
    }
    $installParams = @{
        PackageRoot = $extractRoot
        InstallDir = $InstallRootFull
        DataRoot = $DataRootFull
    }
    $reopen = $null
    if ($RestartRuntime) {
        $reopen = Resolve-OtaReopenRestart -StateRoot $StateRootFull
        if ($reopen.StartRunner) {
            $installParams.StartRunner = $true
        }
        else {
            $installParams.NoStartRunner = $true
        }
        if (-not $reopen.StartShell) {
            $installParams.NoOpenApp = $true
        }
    } else {
        $installParams.NoStartRunner = $true
        $installParams.NoOpenApp = $true
    }
    & $installScript @installParams | Out-Null
    if ($script:OtaDistributionMode -eq 'web' -and $null -ne $preservedRuntimeComponents) {
        $newInstallManifestPath = Join-Path $InstallRootFull 'install.manifest.json'
        $newInstallManifest = Get-Content -LiteralPath $newInstallManifestPath -Raw | ConvertFrom-Json
        $newInstallManifest | Add-Member `
            -NotePropertyName runtimeComponents `
            -NotePropertyValue $preservedRuntimeComponents `
            -Force
        $newInstallManifest | ConvertTo-Json -Depth 8 |
            Set-Content -LiteralPath $newInstallManifestPath -Encoding UTF8
    }
    if ($null -ne $reopen) {
        Start-OtaReopenedScheduledTasks -TaskNames $reopen.TaskNames
    }

    $shippedManifest = Join-Path $extractRoot $(if ($script:OtaDistributionMode -eq 'web') {
        'MyPowerTools-core-win-x64.manifest.json'
    } else {
        'MyPowerTools-win-x64.manifest.json'
    })
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

    Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
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
# dev-update.manifest.json. OTA updates the canonical release base and leaves
# the overlay marker in place only when a full directory swap did not occur.
# Apply does not re-run update-windows-dev.ps1; re-apply the overlay separately.
$devOverlayManifestPath = Join-Path $InstallRootFull 'dev-update.manifest.json'
$IsDevOverlay = Test-Path -LiteralPath $devOverlayManifestPath -PathType Leaf
$DevOverlay = if ($IsDevOverlay) {
    Get-Content -Raw -LiteralPath $devOverlayManifestPath | ConvertFrom-Json
} else {
    $null
}

$installedRelease = Read-InstalledRelease -StateRootFull $StateRootFull
$script:OtaDistributionMode = if ($installedRelease -and [string]$installedRelease.distributionMode -eq 'web') {
    'web'
} else {
    'full'
}
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

Clear-StaleOtaState -StateRootFull $StateRootFull -KeepVersions @($CurrentVersion, [string]$feed.version)

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
            note = 'dev 覆盖模式：OTA 会更新系统安装底座，不会自动重新应用本地 Debug 覆盖。升级后如需开发覆盖，请再运行 Start-MyPowerTools-Dev.ps1。'
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
try {
    Set-Location -LiteralPath $StateRootFull
} catch {
}

if ($IsDevOverlay -and -not (Test-Path -LiteralPath (Join-Path $InstallRootFull 'install.manifest.json') -PathType Leaf)) {
    throw '未检测到系统安装版（install.manifest.json 缺失）。dev 覆盖模式无法执行 OTA，请先完整安装 MyPowerTools 后再试。'
}

$maintenanceFile = Join-Path $StateRootFull 'maintenance-mode.json'
$mutexOwned = $false
try {
    $updateMutex = [Threading.Mutex]::new($true, 'Global\MyPowerTools.OtaUpdate', [ref]$mutexOwned)
} catch [UnauthorizedAccessException] {
    # Creating a global object needs SeCreateGlobalPrivilege, which a standard
    # user does not have; the session namespace still covers the scheduled task
    # and the Shell, which both run in the interactive session.
    $updateMutex = [Threading.Mutex]::new($true, 'Local\MyPowerTools.OtaUpdate', [ref]$mutexOwned)
}
if (-not $mutexOwned) {
    try {
        $mutexOwned = $updateMutex.WaitOne(0)
    } catch [Threading.AbandonedMutexException] {
        # The previous owner was killed mid-update; the wait still succeeded.
        $mutexOwned = $true
    }
    if (-not $mutexOwned) {
        $updateMutex.Dispose()
        throw 'Another MyPowerTools OTA update is already running.'
    }
}
# Restore maintenance-mode state left behind by a killed prior run. This runs
# under the mutex so it can never undo the maintenance mode of a live update.
if (Test-Path -LiteralPath $maintenanceFile) {
    try {
        $saved = Get-Content -LiteralPath $maintenanceFile -Raw | ConvertFrom-Json
        $runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        if ($saved.savedAutostart) {
            foreach ($prop in $saved.savedAutostart.PSObject.Properties) {
                if (-not [string]::IsNullOrWhiteSpace($prop.Value)) {
                    if (-not (Test-Path -LiteralPath $runKeyPath)) { New-Item -Path $runKeyPath -Force | Out-Null }
                    Set-ItemProperty -LiteralPath $runKeyPath -Name $prop.Name -Value $prop.Value
                }
            }
        }
        if ($saved.otaCheckTaskDisabled) {
            Enable-ScheduledTask -TaskName 'MyPowerTools OTA Check' -ErrorAction SilentlyContinue | Out-Null
        }
        Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    } catch {}
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
    $otaCheckTaskDisabled = $null -ne (
        Get-ScheduledTask -TaskName 'MyPowerTools OTA Check' -ErrorAction SilentlyContinue)

    # Persist maintenance-mode state to disk before anything is removed so a
    # killed process leaves the autostart entries recoverable.
    $maintenanceState = [ordered]@{
        savedAutostart = $savedAutostartValues
        otaCheckTaskDisabled = $otaCheckTaskDisabled
        enteredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $maintenanceState | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $maintenanceFile -Encoding utf8

    foreach ($name in $autostartNames) {
        Remove-ItemProperty -LiteralPath $runKeyPath -Name $name -ErrorAction SilentlyContinue
    }
    if ($otaCheckTaskDisabled) {
        Disable-ScheduledTask -TaskName 'MyPowerTools OTA Check' -ErrorAction SilentlyContinue | Out-Null
    }

    if (-not $BootstrapReady -and -not $SkipBootstrap) {
        if ($mutexOwned) {
            try { $updateMutex.ReleaseMutex() } catch {}
            $mutexOwned = $false
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
        $packageKind = $(if ($script:OtaDistributionMode -eq 'web') { 'core' } else { 'full' })
    }

    $repository = if ($null -ne $installedRelease -and
        -not [string]::IsNullOrWhiteSpace([string]$installedRelease.repository)) {
        [string]$installedRelease.repository
    } else {
        'https://github.com/dqtz5vpvj9-create/MyPowerTools'
    }

    $health = Test-OtaHealth `
        -InstallRootFull $InstallRootFull `
        -StateRootFull $StateRootFull `
        -ExpectedVersion ([string]$feed.version)
    $shouldRecover = -not $SkipFullRecoveryOnHealthFailure.IsPresent
    if (-not [bool]$health.ok -and $decision.Kind -eq 'delta' -and $shouldRecover) {
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
        $packageKind = $(if ($script:OtaDistributionMode -eq 'web') { 'core-recovery' } else { 'full-recovery' })
        $health = Test-OtaHealth `
            -InstallRootFull $InstallRootFull `
            -StateRootFull $StateRootFull `
            -ExpectedVersion ([string]$feed.version)
    }
    if (-not [bool]$health.ok) {
        throw "OTA health check failed after update ($($health.failedCount) failed checks)."
    }

    Update-InstalledRelease `
        -StateRootFull $StateRootFull `
        -Version ([string]$feed.version) `
        -ChannelName $Channel `
        -PackageKind $packageKind `
        -InstallRootFull $InstallRootFull `
        -DataRootFull $DataRootFull `
        -Repository $repository

    $devOverlayResult = if ($IsDevOverlay) {
        [ordered]@{
            reapplied = $false
            skipped = $true
            note = 'OTA 已更新系统安装底座，未自动重新应用 dev 覆盖。需要开发覆盖时请运行 Start-MyPowerTools-Dev.ps1。'
        }
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
catch {
    $applyError = $_
    $failure = [ordered]@{
        success = $false
        error = [string]$applyError.Exception.Message
        channel = $Channel
        fromVersion = $CurrentVersion
        latestVersion = if ($null -ne $feed) { [string]$feed.version } else { '' }
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    try {
        Write-Utf8TextFile -Path (Join-Path $StateRootFull 'last-update.json') -Value (
            $failure | ConvertTo-Json -Depth 6)
    } catch {
    }
    # If delta apply failed, attempt full recovery before giving up.
    if ($decision.Kind -eq 'delta' -and -not $SkipFullRecoveryOnHealthFailure.IsPresent -and $null -ne $feed) {
        try {
            $fullDecision = [pscustomobject]@{
                FeedVersion = [string]$feed.version
                FullAsset = [string]$feed.full.asset
                FullSha256 = [string]$feed.full.sha256
            }
            Invoke-FullRecovery -Decision $fullDecision -BaseUrl $feedContent.BaseUrl -InstallRootFull $InstallRootFull -DataRootFull $DataRootFull -StateRootFull $StateRootFull
            # Re-check health after recovery
            $health = Test-OtaHealth -InstallRootFull $InstallRootFull -StateRootFull $StateRootFull -ExpectedVersion ([string]$feed.version)
            if ([bool]$health.ok) {
                # Recovery succeeded - update the result
                $failure.success = $true
                $failure.Remove('error')
                $failure['packageKind'] = 'full-recovery-after-delta-failure'
                $failure['health'] = $health
                Write-Utf8TextFile -Path (Join-Path $StateRootFull 'last-update.json') -Value ($failure | ConvertTo-Json -Depth 6)
                $failure | ConvertTo-Json -Depth 6
                exit 0
            }
        } catch {
            # Full recovery also failed; fall through to original error
        }
    }
    $failure | ConvertTo-Json -Depth 6
    exit 1
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
    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    if ($mutexOwned) {
        try { $updateMutex.ReleaseMutex() } catch {}
    }
    $updateMutex.Dispose()
}
