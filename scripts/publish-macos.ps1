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

function Test-MachOFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $description = (& /usr/bin/file '-b' $Path 2>$null) -join ' '
    return $description.Contains('Mach-O', [StringComparison]::Ordinal)
}

function Sign-MachOFile {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$File,
        [Parameter(Mandatory = $true)][string]$Identity,
        [Parameter(Mandatory = $true)][string]$EntitlementsPath
    )

    if (-not (Test-MachOFile -Path $File.FullName)) {
        return
    }

    & /usr/bin/codesign '--remove-signature' $File.FullName 2>$null
    $global:LASTEXITCODE = 0
    $arguments = @('--force', '--sign', $Identity, '--timestamp=none')
    $isLibrary = $File.Extension -in @('.dylib', '.so')
    if (-not $isLibrary) {
        $arguments += @('--options', 'runtime', '--entitlements', $EntitlementsPath)
    }
    $arguments += $File.FullName
    Invoke-Native -FilePath '/usr/bin/codesign' -ArgumentList $arguments -Activity "codesign $($File.FullName)"
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
            Sort-Object { $_.FullName.Length } -Descending
    )
    foreach ($helperBundle in $helperBundles) {
        Get-ChildItem -LiteralPath $helperBundle.FullName -Recurse -File |
            Where-Object { -not $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) } |
            Sort-Object { $_.FullName.Length } -Descending |
            ForEach-Object {
                Sign-MachOFile -File $_ -Identity $Identity -EntitlementsPath $entitlements
            }
        Invoke-Native -FilePath '/usr/bin/codesign' -ArgumentList @(
            '--force', '--sign', $Identity,
            '--options', 'runtime',
            '--entitlements', $entitlements,
            '--timestamp=none',
            $helperBundle.FullName
        ) -Activity "codesign $($helperBundle.Name)"
    }

    Get-ChildItem -LiteralPath $BundlePath -Recurse -File |
        Where-Object {
            -not $_.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -and
            -not $_.FullName.StartsWith(
                $helpersRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::Ordinal)
        } |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object {
            Sign-MachOFile -File $_ -Identity $Identity -EntitlementsPath $entitlements
        }

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
if ($LASTEXITCODE -ne 0) {
    throw "Base macOS publisher failed with exit code $LASTEXITCODE"
}
if (-not (Test-Path -LiteralPath (Join-Path $appBundle 'Contents/Info.plist') -PathType Leaf)) {
    throw "Base macOS publisher did not create a valid app bundle: $appBundle"
}

$productVersion = [string](Get-Content -LiteralPath (Join-Path $repoRoot 'version.json') -Raw |
    ConvertFrom-Json).version
if ($productVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "version.json contains an invalid version '$productVersion'."
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
foreach ($legacyName in @(
    "MyPowerTools-macos-$Architecture.zip",
    "MyPowerTools-macos-$Architecture.zip.sha256"
)) {
    Remove-Item -LiteralPath (Join-Path $releaseRoot $legacyName) -Force -ErrorAction SilentlyContinue
}

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
    manifest = $manifestPath
    feed = $feedResult.FeedPath
    signature = $feedResult.SignaturePath
    signed = [bool]$feedResult.Signed
} | ConvertTo-Json -Depth 5
