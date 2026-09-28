<#
.SYNOPSIS
Builds the shared MyPowerTools Android development APK from the standalone
Android solution (MyPowerTools.Android.slnx).

.DESCRIPTION
This script is deliberately independent from the Windows/macOS product builds:
nothing in MyPowerTools.slnx or the desktop installers references the Android
solution, so a normal desktop build never needs the Android workload.

What it does, in order:

  1. Packs the local SDK contract packages (Platform.Abstractions, ToolSdk,
     AvaloniaSdk) into the repo-local NuGet feed at artifacts/sdk/nuget. On a
     fresh checkout that feed is empty, so a checkout cannot silently consume a
     package the repository no longer produces.
  2. Drops those package ids from the repo-scoped package cache
     (artifacts/sdk/global-packages) because local development packages reuse
     their version number.
  3. Stages every Android tool-module package in $moduleStages by delegating to that
     tool's build.ps1: File Transfer, Remote Notifications and Remote Commands.
     The Android app embeds these staged packages as assets.
  4. Builds MyPowerTools.Android.slnx. The default build is universal
     (android-arm64 + android-x64) and uses the experimental CoreCLR Android
     runtime, which keeps the existing in-proc module host lifecycle.

Requirements: the SDK pinned by global.json, the Android workload
(`dotnet workload install android --skip-manifest-update`), an Android SDK with
platforms + build-tools, and JDK 17. AndroidSdkDirectory / JavaSdkDirectory
default to ANDROID_HOME / ANDROID_SDK_ROOT / JAVA_HOME.

The produced APK is a development preview signed with the local Android debug
key. It is not a store-signed production artifact.

.EXAMPLE
pwsh scripts/build-android.ps1 -AndroidSdkDirectory /android/sdk -JavaSdkDirectory /usr/lib/jvm/java-17-openjdk-amd64

.EXAMPLE
# Smaller single-ABI developer build.
pwsh scripts/build-android.ps1 -RuntimeIdentifier android-arm64
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$AndroidSdkDirectory = '',

    [string]$JavaSdkDirectory = '',

    [ValidateSet('', 'android-arm64', 'android-x64')]
    [string]$RuntimeIdentifier = '',

    # Skips the phone-side unit tests. Packaging runs pass this; release and acceptance runs leave
    # the tests on, which is the default because the tests take seconds and guard secret handling.
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nugetFeed = Join-Path $repositoryRoot 'artifacts/sdk/nuget'
$configurationDir = $Configuration.ToLowerInvariant()

# SDK contract packages the standalone Android solution consumes as packages.
# scripts/build-sdk.ps1 is the authoritative producer list for the whole repository;
# this subset is what the Android solution and its tool modules actually restore, and
# every entry must be packed so a fresh checkout cannot silently reuse an old nupkg.
$sdkContractProjects = @(
    'MyPowerTools.Platform.Abstractions',
    'MyPowerTools.Abstractions',
    'MyPowerTools.AvaloniaSdk'
)

# Android tool-module packages embedded into the APK. Each entry builds its module
# package first; the Android app project then embeds the staged files as assets.
# Reserve a slot for a new Android tool module by appending an entry here once its
# build script and stage path exist. Optional = $true means "may not have landed in
# this checkout yet": such an entry is skipped with a warning instead of failing, so
# the preview workflow keeps working while a module is still being developed.
# A finished module is REQUIRED (Optional = $false): a missing stage fails the build
# instead of silently shipping an APK without that tool.
#
# The product build never passes module-specific skip switches (-SkipSurface,
# backend-only modes). Android-only modules use -NoMirror to stay out of the desktop
# catalog. RequireFiles is the host-side guard: a module
# filter that drops a host-provided assembly must never drop its own surface, and
# a package missing any listed file fails here rather than producing a catalog entry
# that points at an assembly the APK does not contain.
$moduleStages = @(
    [pscustomobject]@{
        ToolId       = 'file-transfer'
        BuildScript  = 'tools/file-transfer/build.ps1'
        Stage        = 'tools/file-transfer/artifacts/package'
        Manifest     = 'module.json'
        RequireFiles = @('ui/surface/FileTransfer.Surface.dll')
        Optional     = $false
    },
    [pscustomobject]@{
        ToolId       = 'remote-notifications-android'
        BuildScript  = 'tools/remote-notifications/android-integration/build.ps1'
        Stage        = 'tools/remote-notifications/android-integration/artifacts/package/remote-notifications-android'
        Manifest     = 'module.json'
        RequireFiles = @('ui/surface/MyPowerTools.MobileNotifications.dll')
        Optional     = $false
    },
    [pscustomobject]@{
        ToolId       = 'remote-commands-android'
        BuildScript  = 'tools/remote-commands/android-integration/build.ps1'
        Stage        = 'tools/remote-commands/android-integration/artifacts/package/remote-commands-android'
        Manifest     = 'module.json'
        RequireFiles = @(
            'RemoteCommands.Android.dll',
            'Renci.SshNet.dll',
            'BouncyCastle.Cryptography.dll',
            'ui/surface/MyPowerTools.MobileRemoteCommands.dll'
        )
        Optional     = $false
    },
    [pscustomobject]@{
        # Remote Tool Gateway (G2): phone-side module adapter plus its own page. Android-only; its
        # build script only mirrors when -Mirror is passed explicitly, so nothing lands in
        # <repo>/modules for this stage.
        ToolId       = 'mobile-tool-control'
        BuildScript  = 'tools/remote-tool-gateway/android-integration/build.ps1'
        Stage        = 'tools/remote-tool-gateway/android-integration/artifacts/package/mobile-tool-control'
        Manifest     = 'module.json'
        RequireFiles = @(
            'MobileToolControl.Android.dll',
            'ui/surface/MyPowerTools.MobileToolControl.dll'
        )
        Optional     = $false
    }
)

function Resolve-Dotnet {
    $command = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    foreach ($candidate in @(
            $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet' }),
            (Join-Path $HOME '.dotnet/dotnet'))) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return $candidate
        }
    }
    throw 'The dotnet host was not found. Add it to PATH, or set DOTNET_ROOT.'
}

function Resolve-Directory {
    param(
        [string]$Explicit,
        [string[]]$EnvironmentNames,
        [string]$Label,
        [string[]]$RequiredChildren
    )
    $candidate = $Explicit
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        foreach ($name in $EnvironmentNames) {
            $value = [Environment]::GetEnvironmentVariable($name)
            if (-not [string]::IsNullOrWhiteSpace($value)) { $candidate = $value; break }
        }
    }
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw "$Label was not provided and none of $($EnvironmentNames -join ', ') is set."
    }
    $resolved = [IO.Path]::GetFullPath($candidate)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "$Label does not exist: $resolved"
    }
    foreach ($child in $RequiredChildren) {
        if (-not (Test-Path -LiteralPath (Join-Path $resolved $child))) {
            throw "$Label at '$resolved' is missing '$child'."
        }
    }
    return $resolved
}

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [string]$Activity
    )
    if ($Activity) { Write-Host "  > $Activity" -ForegroundColor DarkGray }
    & $dotnet @ArgumentList '-p:StageRepositoryModule=false'
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($ArgumentList[0]) failed with exit code $LASTEXITCODE. If the failure mentions the Android workload, run: dotnet workload install android --skip-manifest-update"
    }
}

$dotnet = Resolve-Dotnet
$pwshCommand = Get-Command 'pwsh' -CommandType Application -ErrorAction Stop | Select-Object -First 1
$androidSdk = Resolve-Directory -Explicit $AndroidSdkDirectory -EnvironmentNames @('ANDROID_HOME', 'ANDROID_SDK_ROOT') `
    -Label 'Android SDK directory' -RequiredChildren @('platforms', 'build-tools')
$javaSdk = Resolve-Directory -Explicit $JavaSdkDirectory -EnvironmentNames @('JAVA_HOME') `
    -Label 'JDK directory' -RequiredChildren @('bin')

# The tool build scripts resolve `dotnet` from PATH themselves; make the host we
# resolved visible to them too so the module stage works on machines where the SDK
# is not on PATH.
$env:PATH = "$(Split-Path -Parent $dotnet)$([IO.Path]::PathSeparator)$env:PATH"
$env:DOTNET_NOLOGO = 'true'

Push-Location $repositoryRoot
try {
    New-Item -ItemType Directory -Path $nugetFeed -Force | Out-Null

    Write-Host '==> Packing local SDK contract packages' -ForegroundColor Cyan
    foreach ($project in $sdkContractProjects) {
        Invoke-Dotnet -Activity "dotnet pack $project" -ArgumentList @(
            'pack', "src/$project/$project.csproj", '--configuration', $Configuration,
            '--output', $nugetFeed, '--nologo')
    }

    # These local development packages can be rebuilt at the same version, so the
    # repo-scoped cache must not keep an older extraction of the same version.
    foreach ($packageId in @('mypowertools.platform.abstractions', 'mypowertools.toolsdk', 'mypowertools.avaloniasdk')) {
        $packageCache = Join-Path $repositoryRoot "artifacts/sdk/global-packages/$packageId"
        if (Test-Path -LiteralPath $packageCache) {
            Remove-Item -LiteralPath $packageCache -Recurse -Force
        }
    }

    Write-Host '==> Staging Android tool-module packages' -ForegroundColor Cyan
    foreach ($module in $moduleStages) {
        $buildScript = Join-Path $repositoryRoot $module.BuildScript
        if (-not (Test-Path -LiteralPath $buildScript -PathType Leaf)) {
            if ($module.Optional) {
                Write-Warning "[$($module.ToolId)] Android module has not landed in this checkout; skipping $($module.BuildScript)."
                continue
            }
            throw "[$($module.ToolId)] Android module build script is missing: $buildScript"
        }

        $buildArguments = @(
            '-NoLogo', '-NoProfile', '-NonInteractive',
            '-File', $buildScript,
            '-MyPowerToolsRepoRoot', $repositoryRoot)
        # Older tool build contracts expose no configuration switch.
        if ((Get-Command -Name $buildScript).Parameters.ContainsKey('Configuration')) {
            $buildArguments += @('-Configuration', $Configuration)
        }
        # Android-only packages are embedded directly from their stage; keep them out of
        # the desktop modules catalog where their desktop counterparts already exist. Pass the switch
        # only to a build script that exposes it: some opt in with -Mirror instead, and forwarding an
        # unknown parameter would fail the stage.
        $buildScriptParameters = (Get-Command -Name $buildScript).Parameters
        if ($buildScriptParameters.ContainsKey('NoMirror')) {
            $buildArguments += '-NoMirror'
        }
        Write-Host "  > $($module.ToolId)" -ForegroundColor DarkGray
        & $pwshCommand.Source @buildArguments
        if ($LASTEXITCODE -ne 0) {
            throw "$($module.BuildScript) failed with exit code $LASTEXITCODE."
        }

        $stage = Join-Path $repositoryRoot $module.Stage
        $manifest = Join-Path $stage $module.Manifest
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) {
            throw "[$($module.ToolId)] module stage is incomplete: $manifest is missing."
        }
        if ($module.PSObject.Properties['RequireFiles']) {
            foreach ($requiredFile in @($module.RequireFiles)) {
                $requiredPath = Join-Path $stage $requiredFile
                if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
                    throw "[$($module.ToolId)] module stage is incomplete: $requiredFile is missing under $stage. A host-provided-assembly filter must not drop the tool surface or its runtime dependencies."
                }
            }
        }
    }

    Write-Host '==> Building the Android solution' -ForegroundColor Cyan
    if ($RuntimeIdentifier) {
        # A RID cannot be applied to the net10.0 projects in the solution, so a
        # single-ABI build targets the application project directly.
        Invoke-Dotnet -Activity "dotnet build MyPowerTools.Android ($RuntimeIdentifier)" -ArgumentList @(
            'build', 'src/MyPowerTools.Android/MyPowerTools.Android.csproj',
            '--configuration', $Configuration, '--nologo', '-m:1',
            "--runtime=$RuntimeIdentifier",
            "-p:AndroidSdkDirectory=$androidSdk",
            "-p:JavaSdkDirectory=$javaSdk")
    }
    else {
        # Universal arm64 + x64 APK; also proves the standalone Android solution
        # stays buildable on its own.
        Invoke-Dotnet -Activity 'dotnet build MyPowerTools.Android.slnx' -ArgumentList @(
            'build', 'MyPowerTools.Android.slnx',
            '--configuration', $Configuration, '--nologo', '-m:1',
            "-p:AndroidSdkDirectory=$androidSdk",
            "-p:JavaSdkDirectory=$javaSdk")
    }

    Write-Host '==> Running the Android phone-side tests' -ForegroundColor Cyan
    # The Android-free rules of the phone host (pairing-code classification and secret hygiene, the
    # QR decoder, shared-file names) are linked into a plain net10.0 test project so they run on a
    # build machine. They are gated behind -SkipTests because the packaging workflow only needs the
    # APK; the release/acceptance run leaves them on, so a hosted CI job can call this script
    # directly instead of discovering the project path on its own.
    $androidTestProject = 'src/MyPowerTools.Android/tests/MyPowerTools.Android.Tests/MyPowerTools.Android.Tests.csproj'
    if ($SkipTests) {
        Write-Host '  skipped (-SkipTests)' -ForegroundColor DarkGray
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $androidTestProject) -PathType Leaf)) {
        throw "The Android test project is missing: $androidTestProject"
    }
    else {
        Invoke-Dotnet -Activity 'dotnet test MyPowerTools.Android.Tests' -ArgumentList @(
            'test', $androidTestProject,
            '--configuration', $Configuration, '--nologo', '-m:1')
    }

    $outputName = $configurationDir
    if ($RuntimeIdentifier) { $outputName += "_$RuntimeIdentifier" }
    $apkPath = Join-Path $repositoryRoot "artifacts/build/bin/MyPowerTools.Android/$outputName/com.mypowertools.android-Signed.apk"
    if (-not (Test-Path -LiteralPath $apkPath -PathType Leaf)) {
        throw "Android APK was not produced: $apkPath"
    }

    $apk = Get-Item -LiteralPath $apkPath
    $runtimeLabel = if ($RuntimeIdentifier) { $RuntimeIdentifier } else { 'android-arm64;android-x64 (universal CoreCLR preview)' }
    Write-Host ''
    Write-Host "Android development preview: $($apk.FullName)"
    Write-Host ("  configuration : {0}" -f $Configuration)
    Write-Host ("  runtime       : {0}" -f $runtimeLabel)
    Write-Host ("  size          : {0:N1} MB" -f ($apk.Length / 1MB))
    Write-Host '  signing       : local Android debug key (not a production release)'
}
finally {
    Pop-Location
}
