[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Write-Utf8Text {
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

function Read-JsonOutput {
    param([Parameter(Mandatory = $true)][object[]]$Output)

    return (($Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) | ConvertFrom-Json
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

$scriptsRoot = $PSScriptRoot
$manifestScript = Join-Path $scriptsRoot 'new-ota-file-manifest.ps1'
$packageScript = Join-Path $scriptsRoot 'new-ota-delta-package.ps1'
$feedScript = Join-Path $scriptsRoot 'new-ota-channel-feed.ps1'
$updaterScript = Join-Path $scriptsRoot 'ota-update.ps1'
$testId = [Guid]::NewGuid().ToString('N')
$tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempParent "mypowertools-ota-online-test-$testId"))

try {
    $sourceRoot = Join-Path $testRoot 'source'
    $targetRoot = Join-Path $testRoot 'target'
    $metadataRoot = Join-Path $testRoot 'metadata'
    $stateRoot = Join-Path $testRoot 'data\ota-state'
    $dataRoot = Join-Path $testRoot 'data'
    New-Item -ItemType Directory -Path $sourceRoot, $targetRoot, $metadataRoot, $stateRoot -Force | Out-Null

    $shippedScripts = @(
        'ota-update.ps1',
        'ed25519.cs',
        'new-ota-file-manifest.ps1',
        'new-ota-delta-package.ps1',
        'invoke-ota-update.ps1'
    )
    foreach ($scriptName in $shippedScripts) {
        Copy-Item -LiteralPath (Join-Path $scriptsRoot $scriptName) -Destination (
            Join-Path $sourceRoot $scriptName) -Force
        Copy-Item -LiteralPath (Join-Path $scriptsRoot $scriptName) -Destination (
            Join-Path $targetRoot $scriptName) -Force
    }

    $criticalFiles = @(
        'Runner\MyPowerTools.Runner.exe'
        'Shell\MyPowerTools.Shell.Avalonia.exe'
        'Cli\MyPowerTools.Cli.exe'
        'ServiceManager\MyPowerTools.ServiceManager.exe'
        'MyPowerTools.exe'
        'install-windows.ps1'
        'configure-user-services.ps1'
        'start-user-runtime.ps1'
    )
    foreach ($relative in $criticalFiles) {
        Write-Utf8Text -Path (Join-Path $sourceRoot $relative) -Value "source-v2:$relative"
        Write-Utf8Text -Path (Join-Path $targetRoot $relative) -Value "target-v1:$relative"
    }

    Write-Utf8Text -Path (Join-Path $sourceRoot 'same.txt') -Value 'same'
    Write-Utf8Text -Path (Join-Path $sourceRoot 'changed.txt') -Value 'new-value'
    Write-Utf8Text -Path (Join-Path $sourceRoot 'nested\added.txt') -Value 'added'
    Write-Utf8Text -Path (Join-Path $targetRoot 'same.txt') -Value 'same'
    Write-Utf8Text -Path (Join-Path $targetRoot 'changed.txt') -Value 'old-value'
    Write-Utf8Text -Path (Join-Path $targetRoot 'removed.txt') -Value 'remove-me'
    Write-Utf8Text -Path (Join-Path $targetRoot 'install.manifest.json') -Value '{"preserve":true}'

    $sourceManifestPath = Join-Path $metadataRoot 'MyPowerTools-win-x64.manifest.json'
    $targetManifestPath = Join-Path $metadataRoot 'target-manifest.json'
    [void](& $manifestScript -Root $sourceRoot -OutputPath $sourceManifestPath -Version '2.0.0')
    [void](& $manifestScript -Root $targetRoot -OutputPath $targetManifestPath -Version '1.0.0')

    $deltaPath = Join-Path $metadataRoot 'MyPowerTools-1.0.0-to-2.0.0.ota.zip'
    $deltaResult = Read-JsonOutput -Output @(& $packageScript `
        -SourceRoot $sourceRoot `
        -SourceManifestPath $sourceManifestPath `
        -TargetManifestPath $targetManifestPath `
        -OutputPath $deltaPath)
    Assert-True -Condition ($deltaResult.CopyCount -ge 2) -Message 'delta should contain changed files'

    $fullZipPath = Join-Path $metadataRoot 'MyPowerTools-win-x64.zip'
    Copy-Item -LiteralPath $sourceManifestPath -Destination (Join-Path $sourceRoot 'MyPowerTools-win-x64.manifest.json') -Force
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceRoot, $fullZipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

    $keyBytes = [byte[]]::new(32)
    [Security.Cryptography.RandomNumberGenerator]::Fill($keyBytes)
    $keyBase64 = [Convert]::ToBase64String($keyBytes)
    $publicKeyPath = Join-Path $metadataRoot 'ota-signing-public-key.txt'
    $feedPath = Join-Path $metadataRoot 'channel-stable.json'
    $feedResult = Read-JsonOutput -Output @(& $feedScript `
        -Version '2.0.0' `
        -Channel 'stable' `
        -FullZipPath $fullZipPath `
        -FullManifestPath $sourceManifestPath `
        -DeltaPackages @($deltaPath) `
        -OutputPath $feedPath `
        -SigningKeyBase64 $keyBase64 `
        -PublicKeyOutputPath $publicKeyPath)
    Assert-True -Condition ([bool]$feedResult.Signed) -Message 'feed should be signed'
    Assert-True -Condition ((Test-Path -LiteralPath "$feedPath.sig")) -Message 'feed signature file missing'
    Assert-True -Condition ($feedResult.DeltaCount -eq 1) -Message 'feed should reference one delta'

    $installedRelease = [ordered]@{
        schemaVersion = 1
        product = 'MyPowerTools'
        version = '1.0.0'
        channel = 'stable'
        installedAt = (Get-Date).ToString('O')
        installDir = $targetRoot
        dataRoot = $dataRoot
        repository = 'https://github.com/dqtz5vpvj9-create/MyPowerTools'
        manifestPath = 'installed-files.manifest.json'
        manifestSha256 = (Get-FileHash -LiteralPath $targetManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        packageKind = 'full'
    }
    $installedRelease | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (
        Join-Path $stateRoot 'installed-release.json') -Encoding UTF8
    Copy-Item -LiteralPath $targetManifestPath -Destination (
        Join-Path $stateRoot 'installed-files.manifest.json') -Force

    $checkParams = @{
        Command = 'Check'
        Channel = 'stable'
        LocalFeedPath = $feedPath
        LocalPackageRoot = $metadataRoot
        InstallRoot = $targetRoot
        DataRoot = $dataRoot
        StateRoot = $stateRoot
        PublicKeyPath = $publicKeyPath
        CurrentVersion = '1.0.0'
    }
    $check = Read-JsonOutput -Output @(& $updaterScript @checkParams)
    Assert-True -Condition ([bool]$check.available) -Message 'check should report an available update'
    Assert-True -Condition ($check.package.kind -eq 'delta') -Message 'check should select the delta package'
    Assert-True -Condition ([string]$check.package.asset -eq 'MyPowerTools-1.0.0-to-2.0.0.ota.zip') -Message 'check selected wrong delta asset'

    $tamperedFeed = Get-Content -LiteralPath $feedPath -Raw | ConvertFrom-Json
    $tamperedFeed.version = '9.9.9'
    $tamperedPath = Join-Path $metadataRoot 'channel-tampered.json'
    $tamperedFeed | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $tamperedPath -Encoding UTF8
    Copy-Item -LiteralPath "$feedPath.sig" -Destination "$tamperedPath.sig" -Force
    $tamperedRejected = $false
    try {
        $tamperedParams = $checkParams.Clone()
        $tamperedParams.LocalFeedPath = $tamperedPath
        [void](& $updaterScript @tamperedParams)
    }
    catch {
        $tamperedRejected = $_.Exception.Message -like '*signature verification failed*'
    }
    Assert-True -Condition $tamperedRejected -Message 'updater accepted a tampered feed'

    $unsignedFeedPath = Join-Path $metadataRoot 'channel-unsigned.json'
    [void](& $feedScript `
        -Version '2.0.0' `
        -Channel 'stable' `
        -FullZipPath $fullZipPath `
        -FullManifestPath $sourceManifestPath `
        -DeltaPackages @($deltaPath) `
        -OutputPath $unsignedFeedPath `
        -AllowUnsigned)
    $unsignedRejected = $false
    try {
        $unsignedParams = $checkParams.Clone()
        $unsignedParams.LocalFeedPath = $unsignedFeedPath
        $unsignedParams.Remove('PublicKeyPath')
        [void](& $updaterScript @unsignedParams)
    }
    catch {
        $unsignedRejected = $_.Exception.Message -like '*unsigned*'
    }
    Assert-True -Condition $unsignedRejected -Message 'updater accepted an unsigned feed without -AllowUnsigned'
    $unsignedParams = $checkParams.Clone()
    $unsignedParams.LocalFeedPath = $unsignedFeedPath
    $unsignedParams.Remove('PublicKeyPath')
    $unsignedParams.AllowUnsigned = $true
    $unsignedCheck = Read-JsonOutput -Output @(& $updaterScript @unsignedParams)
    Assert-True -Condition ([bool]$unsignedCheck.available) -Message 'unsigned check with -AllowUnsigned should pass'

    $downgradeRelease = Get-Content -LiteralPath (Join-Path $stateRoot 'installed-release.json') -Raw | ConvertFrom-Json
    $downgradeRelease.version = '9.0.0'
    $downgradeRelease | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (
        Join-Path $stateRoot 'installed-release.json') -Encoding UTF8
    $downgradeParams = $checkParams.Clone()
    $downgradeParams.CurrentVersion = '9.0.0'
    $downgrade = Read-JsonOutput -Output @(& $updaterScript @downgradeParams)
    Assert-True -Condition (-not [bool]$downgrade.available) -Message 'downgrade was not blocked'
    Assert-True -Condition ($downgrade.reason -eq 'downgrade-blocked') -Message 'downgrade reason is wrong'
    $downgradeRelease.version = '1.0.0'
    $downgradeRelease | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (
        Join-Path $stateRoot 'installed-release.json') -Encoding UTF8

    Write-Utf8Text -Path (Join-Path $targetRoot 'dev-update.manifest.json') -Value '{"repositoryRoot":"C:\\does-not-exist-ota-overlay","configuration":"Debug"}'
    New-Item -ItemType Directory -Path (Join-Path $targetRoot 'Shell') -Force | Out-Null
    $previousCwd = [IO.Directory]::GetCurrentDirectory()
    $applyParams = @{
        Command = 'Apply'
        Channel = 'stable'
        LocalFeedPath = $feedPath
        LocalPackageRoot = $metadataRoot
        InstallRoot = $targetRoot
        DataRoot = $dataRoot
        StateRoot = $stateRoot
        PublicKeyPath = $publicKeyPath
        CurrentVersion = '1.0.0'
        NoRuntimeRestart = $true
        SkipBootstrap = $true
    }
    try {
        Set-Location -LiteralPath (Join-Path $targetRoot 'Shell')
        $apply = Read-JsonOutput -Output @(& $updaterScript @applyParams)
    }
    finally {
        Set-Location -LiteralPath $previousCwd
    }
    Assert-True -Condition ([bool]$apply.success) -Message 'OTA apply did not succeed'
    Assert-True -Condition ($apply.toVersion -eq '2.0.0') -Message 'OTA apply reported wrong target version'
    Assert-True -Condition ((Get-Content -LiteralPath (Join-Path $targetRoot 'changed.txt') -Raw) -eq 'new-value') -Message 'changed file was not replaced'
    Assert-True -Condition ((Get-Content -LiteralPath (Join-Path $targetRoot 'nested\added.txt') -Raw) -eq 'added') -Message 'added file is missing'
    Assert-True -Condition (-not (Test-Path -LiteralPath (Join-Path $targetRoot 'removed.txt'))) -Message 'removed file still exists'
    Assert-True -Condition ((Get-Content -LiteralPath (Join-Path $targetRoot 'install.manifest.json') -Raw) -eq '{"preserve":true}') -Message 'protected install manifest changed'
    Assert-True -Condition ([bool]$apply.health.ok) -Message 'post-update health check failed'
    Assert-True -Condition ([bool]$apply.devOverlay.skipped) -Message 'OTA apply should not reapply a missing dev overlay'

    $newRelease = Get-Content -LiteralPath (Join-Path $stateRoot 'installed-release.json') -Raw | ConvertFrom-Json
    Assert-True -Condition ($newRelease.version -eq '2.0.0') -Message 'installed-release.json was not updated'
    $installedManifestHash = (Get-FileHash -LiteralPath (Join-Path $stateRoot 'installed-files.manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceManifestHash = (Get-FileHash -LiteralPath $sourceManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Assert-True -Condition ($installedManifestHash -eq $sourceManifestHash) -Message 'installed OTA manifest differs from the release manifest'

    $statusParams = @{
        Command = 'Status'
        InstallRoot = $targetRoot
        DataRoot = $dataRoot
        StateRoot = $stateRoot
    }
    $status = Read-JsonOutput -Output @(& $updaterScript @statusParams)
    Assert-True -Condition ($status.installed.version -eq '2.0.0') -Message 'status reports the wrong installed version'
    Assert-True -Condition ($null -ne $status.health -and [bool]$status.health.ok) -Message 'status does not report health'

    [pscustomobject]@{
        Success = $true
        DeltaSelected = $check.package.kind -eq 'delta'
        TamperedFeedRejected = $tamperedRejected
        UnsignedFeedRejected = $unsignedRejected
        DowngradeBlocked = $downgrade.reason -eq 'downgrade-blocked'
        ApplySucceeded = [bool]$apply.success
        HealthOk = [bool]$apply.health.ok
        InstalledManifestPersisted = $installedManifestHash -eq $sourceManifestHash
    } | ConvertTo-Json -Depth 5
}
finally {
    Set-Location -LiteralPath $tempParent
    $tempPrefix = $tempParent.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($testRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($testRoot) -eq "mypowertools-ota-online-test-$testId" -and
        (Test-Path -LiteralPath $testRoot -PathType Container)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
