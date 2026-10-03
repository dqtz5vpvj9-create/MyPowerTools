<#
.SYNOPSIS
    Builds, signs and zips "MyPowerTools Installer.app", the double-clickable macOS online
    installer.

.DESCRIPTION
    Publishes src/MyPowerTools.Installer.Mac self-contained and single-file for osx-<arch>
    (the user has no .NET installed), assembles the .app bundle under
    artifacts/publish/macos-installer-<arch>/, code-signs it (ad-hoc by default), optionally
    notarizes and staples it, and zips it with ditto to
    artifacts/publish/macos-installer-<arch>/MyPowerTools-Installer-macos-<arch>.zip (plus a
    .sha256 sidecar).

    Notarization runs only when a real signing identity is used and MPT_NOTARY_APPLE_ID,
    MPT_NOTARY_TEAM_ID and MPT_NOTARY_PASSWORD (an app-specific password) are all set.
    An ad-hoc signature cannot be notarized.

    The installer is released on its own; the in-app updater uses `mpt ota`, which shares the
    same MacNativeInstaller engine, so MyPowerTools.app does not embed this bundle.
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
    [switch]$SkipCodeSign,
    [switch]$SkipNotarize,
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
$runtimeIdentifier = "osx-$Architecture"
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $artifactsRoot "publish/macos-installer-$Architecture"
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (-not $OutputRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay under $artifactsRoot"
}

$bundleName = 'MyPowerTools Installer.app'
$executableName = 'MyPowerToolsInstaller'
$projectPath = Join-Path $repoRoot 'src/MyPowerTools.Installer.Mac/MyPowerTools.Installer.Mac.csproj'
$plistSource = Join-Path $repoRoot 'packaging/macos/Installer.Info.plist'
$entitlements = Join-Path $repoRoot 'packaging/macos/MyPowerTools.entitlements'
$appBundle = Join-Path $OutputRoot $bundleName
$contentsRoot = Join-Path $appBundle 'Contents'
$macRoot = Join-Path $contentsRoot 'MacOS'
$resourcesRoot = Join-Path $contentsRoot 'Resources'
# Single-run staging inside the declared output directory; removed before the script returns.
$stageRoot = Join-Path $OutputRoot 'stage'
$zipPath = Join-Path $OutputRoot "MyPowerTools-Installer-macos-$Architecture.zip"

function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Activity
    )

    & $FilePath @ArgumentList
    # macOS can transiently reject a just-written file while inspecting it; retry codesign only.
    if ($FilePath -eq '/usr/bin/codesign') {
        for ($attempt = 1; $LASTEXITCODE -ne 0 -and $attempt -lt 3; $attempt++) {
            Start-Sleep -Milliseconds 250
            & $FilePath @ArgumentList
        }
    }
    if ($LASTEXITCODE -ne 0) {
        throw "$Activity failed with exit code $LASTEXITCODE"
    }
}

function Copy-StampedPlist {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$ProductVersion
    )

    $text = Get-Content -LiteralPath $Source -Raw
    foreach ($versionKey in @('CFBundleShortVersionString', 'CFBundleVersion')) {
        $text = [regex]::Replace(
            $text,
            "(<key>$versionKey</key>\s*<string>)[^<]*(</string>)",
            ('${1}' + $ProductVersion + '${2}'))
    }
    if ($text -notmatch "<string>$([regex]::Escape($ProductVersion))</string>") {
        throw "Could not stamp the product version into $Destination."
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
    [IO.File]::WriteAllText($Destination, $text, [Text.UTF8Encoding]::new($false))
}

function New-ProductIcns {
    <# Same rendering path as scripts/publish-macos-base.ps1: svg -> 1024 png -> iconset -> icns. #>
    param(
        [Parameter(Mandatory = $true)][string]$WorkRoot,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $svg = Join-Path $repoRoot 'assets/MyPowerTools.svg'
    $iconPng = Join-Path $WorkRoot 'MyPowerTools-1024.png'
    & sips '-s' 'format' 'png' $svg '--out' $iconPng 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $iconPng -PathType Leaf)) {
        $quickLookOutput = Join-Path $WorkRoot 'MyPowerTools.svg.png'
        Invoke-Native -FilePath '/usr/bin/qlmanage' -ArgumentList @(
            '-t', '-s', '1024', '-o', $WorkRoot, $svg
        ) -Activity 'render MyPowerTools.svg'
        if (-not (Test-Path -LiteralPath $quickLookOutput -PathType Leaf)) {
            throw "Quick Look did not produce the expected icon preview: $quickLookOutput"
        }
        Move-Item -LiteralPath $quickLookOutput -Destination $iconPng -Force
    }

    $iconset = Join-Path $WorkRoot 'MyPowerTools.iconset'
    New-Item -ItemType Directory -Path $iconset -Force | Out-Null
    foreach ($icon in @(
        @{ Size = 16; Name = 'icon_16x16.png' },
        @{ Size = 32; Name = 'icon_16x16@2x.png' },
        @{ Size = 32; Name = 'icon_32x32.png' },
        @{ Size = 64; Name = 'icon_32x32@2x.png' },
        @{ Size = 128; Name = 'icon_128x128.png' },
        @{ Size = 256; Name = 'icon_128x128@2x.png' },
        @{ Size = 256; Name = 'icon_256x256.png' },
        @{ Size = 512; Name = 'icon_256x256@2x.png' },
        @{ Size = 512; Name = 'icon_512x512.png' },
        @{ Size = 1024; Name = 'icon_512x512@2x.png' }
    )) {
        Invoke-Native -FilePath 'sips' -ArgumentList @(
            '-z', [string]$icon.Size, [string]$icon.Size,
            $iconPng,
            '--out', (Join-Path $iconset $icon.Name)
        ) -Activity "create $($icon.Name)"
    }
    Invoke-Native -FilePath 'iconutil' -ArgumentList @(
        '-c', 'icns', $iconset, '-o', $Destination
    ) -Activity 'create MyPowerTools.icns'
}

function New-BundleArchive {
    param(
        [Parameter(Mandatory = $true)][string]$BundlePath,
        [Parameter(Mandatory = $true)][string]$ArchivePath
    )

    Remove-Item -LiteralPath $ArchivePath -Force -ErrorAction SilentlyContinue
    if ($IsMacOS) {
        # ditto keeps the bundle's symlinks, extended attributes and the stapled ticket intact.
        Invoke-Native -FilePath '/usr/bin/ditto' -ArgumentList @(
            '-c', '-k', '--keepParent', $BundlePath, $ArchivePath
        ) -Activity 'zip installer bundle'
    }
    else {
        Compress-Archive -Path $BundlePath -DestinationPath $ArchivePath -CompressionLevel Optimal
    }
}

# --- version -----------------------------------------------------------------------------
$productVersion = $Version
if ([string]::IsNullOrWhiteSpace($productVersion)) {
    $versionInfo = & (Join-Path $PSScriptRoot 'get-product-version.ps1') `
        -RepoRoot $repoRoot `
        -PreferGitTag | ConvertFrom-Json
    $productVersion = [string]$versionInfo.version
}
if ($productVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "The installer version is invalid: '$productVersion'."
}

# --- publish -----------------------------------------------------------------------------
foreach ($required in @($projectPath, $plistSource, $entitlements)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required installer input is missing: $required"
    }
}
if (Test-Path -LiteralPath $appBundle) {
    Remove-Item -LiteralPath $appBundle -Recurse -Force
}
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
$publishOutput = Join-Path $stageRoot 'publish'

# Self-contained single file: the person double-clicking this has no .NET runtime. Native
# libraries (Avalonia, Skia, HarfBuzz) ride inside the executable and self-extract on first
# run; compression roughly halves the on-disk size, which matters because the same bundle is
# also embedded in MyPowerTools.app. No trimming: the engine uses reflection-based JSON.
Invoke-Native -FilePath 'dotnet' -ArgumentList @(
    'publish', $projectPath,
    '--configuration', $Configuration,
    '--runtime', $runtimeIdentifier,
    '--self-contained', 'true',
    '--output', $publishOutput,
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:Version=$productVersion"
) -Activity 'publish macOS installer'

$publishedExecutable = Join-Path $publishOutput $executableName
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "dotnet publish did not produce the installer executable: $publishedExecutable"
}

# --- assemble the bundle -----------------------------------------------------------------
New-Item -ItemType Directory -Path $macRoot -Force | Out-Null
New-Item -ItemType Directory -Path $resourcesRoot -Force | Out-Null
Get-ChildItem -LiteralPath $publishOutput -File |
    Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $macRoot -Force }
Copy-StampedPlist -Source $plistSource -Destination (Join-Path $contentsRoot 'Info.plist') -ProductVersion $productVersion
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/macos/PkgInfo') -Destination (Join-Path $contentsRoot 'PkgInfo') -Force

$bundleExecutable = Join-Path $macRoot $executableName
if ($IsMacOS) {
    Invoke-Native -FilePath '/bin/chmod' -ArgumentList @('+x', $bundleExecutable) -Activity 'chmod installer executable'
    New-ProductIcns -WorkRoot $stageRoot -Destination (Join-Path $resourcesRoot 'MyPowerTools.icns')
}
else {
    Write-Warning 'Not running on macOS: the installer bundle has no .icns icon and is not executable-marked.'
}

# --- sign --------------------------------------------------------------------------------
$adHoc = $CodeSignIdentity -eq '-'
$signed = $false
if (-not $SkipCodeSign) {
    if (-not $IsMacOS) {
        throw 'codesign requires macOS. Use -SkipCodeSign for managed cross-publish validation.'
    }
    $signArguments = @('--force', '--deep', '--sign', $CodeSignIdentity)
    if ($adHoc) {
        $signArguments += '--timestamp=none'
    }
    else {
        # Hardened runtime is what notarization requires; .NET needs JIT and unsigned
        # executable memory, and the self-extracted native libraries are not signed by our team.
        $signArguments += @('--timestamp', '--options', 'runtime', '--entitlements', $entitlements)
    }
    $signArguments += $appBundle
    Invoke-Native -FilePath '/usr/bin/codesign' -ArgumentList $signArguments -Activity 'codesign installer bundle'
    Invoke-Native -FilePath '/usr/bin/codesign' -ArgumentList @(
        '--verify', '--deep', '--strict', $appBundle
    ) -Activity 'codesign verification of installer bundle'
    $signed = $true
}

# --- notarize (optional) -----------------------------------------------------------------
$notarized = $false
$notaryAppleId = $env:MPT_NOTARY_APPLE_ID
$notaryTeamId = $env:MPT_NOTARY_TEAM_ID
$notaryPassword = $env:MPT_NOTARY_PASSWORD
$notaryConfigured = -not [string]::IsNullOrWhiteSpace($notaryAppleId) -and
    -not [string]::IsNullOrWhiteSpace($notaryTeamId) -and
    -not [string]::IsNullOrWhiteSpace($notaryPassword)
if ($notaryConfigured -and -not $SkipNotarize) {
    if (-not $signed -or $adHoc) {
        Write-Warning 'Notarization credentials are set, but the installer is ad-hoc signed or unsigned; skipping notarization. Pass -CodeSignIdentity "Developer ID Application: ..." to notarize.'
    }
    else {
        $submission = Join-Path $stageRoot 'notarize.zip'
        New-BundleArchive -BundlePath $appBundle -ArchivePath $submission
        Invoke-Native -FilePath 'xcrun' -ArgumentList @(
            'notarytool', 'submit', $submission,
            '--apple-id', $notaryAppleId,
            '--team-id', $notaryTeamId,
            '--password', $notaryPassword,
            '--wait'
        ) -Activity 'notarize installer'
        Invoke-Native -FilePath 'xcrun' -ArgumentList @('stapler', 'staple', $appBundle) -Activity 'staple installer'
        Invoke-Native -FilePath 'xcrun' -ArgumentList @('stapler', 'validate', $appBundle) -Activity 'validate stapled ticket'
        $notarized = $true
    }
}

# --- archive -----------------------------------------------------------------------------
$zipHash = $null
if (-not $SkipArchive) {
    New-BundleArchive -BundlePath $appBundle -ArchivePath $zipPath
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $(Split-Path -Leaf $zipPath)" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII
}

Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue

$executableBytes = (Get-Item -LiteralPath $bundleExecutable).Length
[ordered]@{
    appBundle = $appBundle
    runtimeIdentifier = $runtimeIdentifier
    version = $productVersion
    executableBytes = $executableBytes
    signed = $signed
    adHoc = ($signed -and $adHoc)
    notarized = $notarized
    archive = $(if ($SkipArchive) { $null } else { $zipPath })
    archiveSha256 = $zipHash
} | ConvertTo-Json -Depth 3
