<#
.SYNOPSIS
    Builds, signs and packages the MyPowerTools macOS application and its OTA feed.

.DESCRIPTION
    publish-macos-base.ps1 builds the application layout. This wrapper adds the CLI and OTA
    scripts before the final code-signing pass, emits the post-signing file manifest, creates the
    osx-arm64 or osx-x64 archive, and publishes a platform-specific signed channel feed.
#>
[CmdletBinding()]
param(
    [ValidateSet('arm64', 'x64')]
    [string]$Architecture = 'arm64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '',
    [string]$OutputRoot = '',
    [string]$CodeSignIdentity = '-',
    [ValidateSet('stable', 'nightly', 'local')]
    [string]$Channel = 'local',
    [string]$SigningKeyPath = '',
    [string]$SigningKeyBase64 = '',
    [switch]$AllowUnsigned,
    [switch]$SkipNativeBuild,
    [switch]$SkipCodeSign
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$runtimeIdentifier = "osx-$Architecture"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $artifactsRoot "publish/macos-$Architecture"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$appBundle = Join-Path $OutputRoot 'MyPowerTools.app'
$contentsRoot = Join-Path $appBundle 'Contents'
$macRoot = Join-Path $contentsRoot 'MacOS'
$resourcesRoot = Join-Path $contentsRoot 'Resources'
$helpersRoot = Join-Path $macRoot 'Helpers'
$releaseRoot = Join-Path $artifactsRoot 'release'

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Activity
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$Activity failed with exit code $LASTEXITCODE"
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required macOS package file is missing: $Source"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Set-BundleVersion {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$ProductVersion
    )

    $plistPath = Join-Path $BundlePath 'Contents/Info.plist'
    if (-not (Test-Path -LiteralPath $plistPath -PathType Leaf)) {
        throw "Bundle Info.plist is missing: $plistPath"
    }
    $text = Get-Content -LiteralPath $plistPath -Raw
    foreach ($versionKey in @('CFBundleShortVersionString', 'CFBundleVersion')) {
        $pattern = "(<key>$versionKey</key>\s*<string>)[^<]*(</string>)"
        $text = [regex]::Replace($text, $pattern, ('${1}' + $ProductVersion + '${2}'))
    }
    if ($text -notmatch "<string>$([regex]::Escape($ProductVersion))</string>") {
        throw "Could not stamp version $ProductVersion into $plistPath"
    }
    [IO.File]::WriteAllText($plistPath, $text, [Text.UTF8Encoding]::new($false))
}

function Get-SignableFiles {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$ExcludePrefix = ''
    )

    return @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        -not $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -and
        ($ExcludePrefix.Length -eq 0 -or
            -not $_.FullName.StartsWith($ExcludePrefix, [StringComparison]::Ordinal))
    })
}

function Invoke-CodeSignPasses {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][string]$Identity,
        [Parameter(Mandatory = $true)][string]$EntitlementsPath
    )

    foreach ($candidate in $Files) {
        $fileDescription = (& /usr/bin/file '-b' $candidate.FullName 2>$null) -join ' '
        if ($fileDescription.Contains('Mach-O', [StringComparison]::Ordinal)) {
            continue
        }
        Invoke-Native -FilePath '/usr/bin/codesign' -ArgumentList @(
            '--force', '--sign', $Identity, '--timestamp=none', $candidate.FullName
        ) -Activity "codesign data file $($candidate.FullName)"
    }

    foreach ($candidate in $Files) {
        $fileDescription = (& /usr/bin/file '-b' $candidate.FullName 2>$null) -join ' '
        if (-not $fileDescription.Contains('Mach-O', [StringComparison]::Ordinal)) {
            continue
        }

        & /usr/bin/codesign '--remove-signature' $candidate.FullName 2>$null
        $global:LASTEXITCODE = 0
        $signArguments = @('--force', '--sign', $Identity, '--timestamp=none')
        if (-not $fileDescription.Contains('shared library', [StringComparison]::OrdinalIgnoreCase)) {
            $signArguments += @('--options', 'runtime', '--entitlements', $EntitlementsPath)
        }
        $signArguments += $candidate.FullName
        Invoke-Native -FilePath '/usr/bin/codesign' -ArgumentList $signArguments -Activity "codesign $($candidate.FullName)"
    }
}

function Sign-AppBundle {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$Identity
    )

    $entitlements = Join-Path $repoRoot 'packaging/macos/MyPowerTools.entitlements'
    if (-not (Test-Path -LiteralPath $entitlements -PathType Leaf)) {
        throw "macOS entitlements file is missing: $entitlements"
    }

    Get-ChildItem -LiteralPath $BundlePath -Recurse -File -Filter '*.dll' |
        ForEach-Object {
            Invoke-Native -FilePath '/bin/chmod' -ArgumentList @('-x', $_.FullName) -Activity "chmod -x $($_.FullName)"
        }

    $helperBundles = @(
        Get-ChildItem -LiteralPath $helpersRoot -Directory -Filter '*.app' -ErrorAction SilentlyContinue |
            Sort-Object Name
    )
    foreach ($helperBundle in $helperBundles) {
        Invoke-CodeSignPasses `
            -Files (Get-SignableFiles -Root $helperBundle.FullName) `
            -Identity $Identity `
            -EntitlementsPath $entitlements
        Invoke-Native -FilePath '/usr/bin/codesign' -ArgumentList @(
            '--force', '--sign', $Identity,
            '--options', 'runtime',
            '--entitlements', $entitlements,
            '--timestamp=none',
            $helperBundle.FullName
        ) -Activity "codesign $($helperBundle.Name)"
    }

    Invoke-CodeSignPasses `
        -Files (Get-SignableFiles `
            -Root $macRoot `
            -ExcludePrefix ($helpersRoot + [IO.Path]::DirectorySeparatorChar)) `
        -Identity $Identity `
        -EntitlementsPath $entitlements
    Invoke-Native -FilePath '/usr/bin/codesign' -ArgumentList @(
        '--force', '--sign', $Identity,
        '--options', 'runtime',
        '--entitlements', $entitlements,
        '--timestamp=none',
        $BundlePath
    ) -Activity 'codesign MyPowerTools.app'
    Invoke-Native -FilePath '/usr/bin/codesign' -ArgumentList @(
        '--verify', '--deep', '--strict', $BundlePath
    ) -Activity 'codesign verification'
}

$baseScript = Join-Path $PSScriptRoot 'publish-macos-base.ps1'
if (-not (Test-Path -LiteralPath $baseScript -PathType Leaf)) {
    throw "Base macOS publisher is missing: $baseScript"
}
$baseParameters = @{
    Architecture = $Architecture
    Configuration = $Configuration
    OutputRoot = $OutputRoot
    CodeSignIdentity = $CodeSignIdentity
    SkipCodeSign = $true
}
if ($SkipNativeBuild) {
    $baseParameters.SkipNativeBuild = $true
}
& $baseScript @baseParameters
if (-not (Test-Path -LiteralPath (Join-Path $appBundle 'Contents/Info.plist') -PathType Leaf)) {
    throw "Base macOS publisher did not create a valid app bundle: $appBundle"
}

$productVersion = $Version
if ([string]::IsNullOrWhiteSpace($productVersion)) {
    $versionInfo = & (Join-Path $PSScriptRoot 'get-product-version.ps1') `
        -RepoRoot $repoRoot `
        -PreferGitTag | ConvertFrom-Json
    $productVersion = [string]$versionInfo.version
}
if ($productVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "The macOS release version is invalid: '$productVersion'."
}
Set-BundleVersion -BundlePath $appBundle -ProductVersion $productVersion
foreach ($helperBundle in @(Get-ChildItem -LiteralPath $helpersRoot -Directory -Filter '*.app')) {
    Set-BundleVersion -BundlePath $helperBundle.FullName -ProductVersion $productVersion
}

$cliOutput = Join-Path $macRoot 'Cli'
if (Test-Path -LiteralPath $cliOutput) {
    Remove-Item -LiteralPath $cliOutput -Recurse -Force
}
Invoke-Native -FilePath 'dotnet' -ArgumentList @(
    'publish',
    (Join-Path $repoRoot 'src/MyPowerTools.Cli/MyPowerTools.Cli.csproj'),
    '--configuration', $Configuration,
    '--runtime', $runtimeIdentifier,
    '--self-contained', 'true',
    '--output', $cliOutput,
    '--nologo',
    '-p:PublishSingleFile=false',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
) -Activity 'publish macOS CLI'

$scriptsDestination = Join-Path $resourcesRoot 'scripts'
New-Item -ItemType Directory -Path $scriptsDestination -Force | Out-Null
$scriptMap = [ordered]@{
    'ota-update-macos.ps1' = 'ota-update.ps1'
    'ota-apply-macos.ps1' = 'ota-apply-macos.ps1'
    'install-macos.ps1' = 'install-macos.ps1'
    'install-macos-base.ps1' = 'install-macos-base.ps1'
    'uninstall-macos.ps1' = 'uninstall-macos.ps1'
    'new-ota-file-manifest.ps1' = 'new-ota-file-manifest.ps1'
    'ed25519.cs' = 'ed25519.cs'
}
foreach ($entry in $scriptMap.GetEnumerator()) {
    Copy-RequiredFile `
        -Source (Join-Path $PSScriptRoot $entry.Key) `
        -Destination (Join-Path $scriptsDestination $entry.Value)
}

$publicKeySource = Join-Path $repoRoot 'ota-history/ota-signing-public-key.txt'
$bundledPublicKey = Join-Path $resourcesRoot 'ota-signing-public-key.txt'
Copy-RequiredFile -Source $publicKeySource -Destination $bundledPublicKey

if ($IsMacOS) {
    $cliExecutable = Join-Path $cliOutput 'MyPowerTools.Cli'
    if (Test-Path -LiteralPath $cliExecutable -PathType Leaf) {
        Invoke-Native -FilePath '/bin/chmod' -ArgumentList @('+x', $cliExecutable) -Activity 'chmod macOS CLI'
    }
}

if (-not $SkipCodeSign) {
    if (-not $IsMacOS) {
        throw 'codesign requires macOS. Use -SkipCodeSign for managed cross-publish validation.'
    }
    Sign-AppBundle -BundlePath $appBundle -Identity $CodeSignIdentity
}

New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
$manifestPath = Join-Path $releaseRoot "MyPowerTools-$runtimeIdentifier.manifest.json"
$manifestScript = Join-Path $PSScriptRoot 'new-ota-file-manifest.ps1'
[void](& $manifestScript `
    -Root $appBundle `
    -OutputPath $manifestPath `
    -Version $productVersion)

$zipName = "MyPowerTools-$runtimeIdentifier.zip"
$zipPath = Join-Path $releaseRoot $zipName
Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
if ($IsMacOS) {
    Invoke-Native -FilePath '/usr/bin/ditto' -ArgumentList @(
        '-c', '-k', '--keepParent', $appBundle, $zipPath
    ) -Activity 'create macOS release archive'
}
else {
    Compress-Archive -Path $appBundle -DestinationPath $zipPath -CompressionLevel Optimal
}
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$zipHash  $zipName" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII

# Keep the historical archive aliases until the release workflow is updated everywhere. The OTA
# feed always names the canonical RID asset above.
$legacyZipName = "MyPowerTools-macos-$Architecture.zip"
$legacyZipPath = Join-Path $releaseRoot $legacyZipName
Copy-Item -LiteralPath $zipPath -Destination $legacyZipPath -Force
"$zipHash  $legacyZipName" | Set-Content -LiteralPath "$legacyZipPath.sha256" -Encoding ASCII

$feedPath = Join-Path $releaseRoot "channel-$Channel-$runtimeIdentifier.json"
$feedParameters = @{
    Version = $productVersion
    Channel = $Channel
    FullZipPath = $zipPath
    FullManifestPath = $manifestPath
    DeltaPackages = @()
    OutputPath = $feedPath
    FullAssetName = $zipName
    PublicKeyOutputPath = Join-Path $releaseRoot "ota-signing-public-key-$Channel-$runtimeIdentifier.txt"
}
if (-not [string]::IsNullOrWhiteSpace($SigningKeyPath)) {
    $feedParameters.SigningKeyPath = $SigningKeyPath
}
if (-not [string]::IsNullOrWhiteSpace($SigningKeyBase64)) {
    $feedParameters.SigningKeyBase64 = $SigningKeyBase64
}
if ([string]::IsNullOrWhiteSpace($SigningKeyPath) -and
    [string]::IsNullOrWhiteSpace($SigningKeyBase64)) {
    if ($Channel -eq 'stable' -and -not $AllowUnsigned) {
        throw 'Stable macOS OTA feeds require a signing key. Pass -AllowUnsigned only for local validation.'
    }
    $feedParameters.AllowUnsigned = $true
}
elseif ($AllowUnsigned) {
    $feedParameters.AllowUnsigned = $true
}

$feedResult = & (Join-Path $PSScriptRoot 'new-ota-channel-feed.ps1') @feedParameters |
    ConvertFrom-Json
if ([bool]$feedResult.Signed) {
    $bundledKey = (Get-Content -LiteralPath $bundledPublicKey -Raw).Trim()
    $generatedKey = (Get-Content -LiteralPath $feedResult.PublicKeyPath -Raw).Trim()
    if ($bundledKey -ne $generatedKey) {
        throw 'The macOS feed signing key does not match the public key sealed into the app bundle.'
    }
}

[ordered]@{
    appBundle = $appBundle
    runtimeIdentifier = $runtimeIdentifier
    version = $productVersion
    channel = $Channel
    archive = $zipPath
    archiveSha256 = $zipHash
    legacyArchive = $legacyZipPath
    manifest = $manifestPath
    feed = $feedResult.FeedPath
    signature = $feedResult.SignaturePath
    signed = [bool]$feedResult.Signed
} | ConvertTo-Json -Depth 5
