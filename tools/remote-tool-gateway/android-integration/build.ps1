<#
.SYNOPSIS
Stages the Android "电脑工具" (remote tool control) module package.

.DESCRIPTION
Builds the phone-side module adapter (MobileToolControl.Android), builds the phone page
(MyPowerTools.MobileToolControl, a separate deliverable in src/), copies the declarative package
documents from package/, and verifies every file listed in manifest/android-package-manifest.json
exists before swapping the result into artifacts/package/mobile-tool-control.

The staged package is what the Android app embeds as APK assets; a failed build therefore never
leaves a half-populated module behind for the app or the release scripts to pick up.

The script never writes <repo>/modules unless -Mirror is passed explicitly, and it always passes
-p:StageRepositoryModule=false so no other tool's stage target runs during this build.

Requirements: the SDK pinned by global.json. There is no third-party runtime dependency: the wire
client uses System.Net.Http from the shared framework.

.EXAMPLE
pwsh tools/remote-tool-gateway/android-integration/build.ps1

.EXAMPLE
# Backend-only staging (the phone page is a separate deliverable).
pwsh tools/remote-tool-gateway/android-integration/build.ps1 -SkipSurface -NoRestore -Configuration Debug

.EXAMPLE
# Offline verification: build the module and its tests into the shared verify directory.
pwsh tools/remote-tool-gateway/android-integration/build.ps1 -MyPowerToolsRepoRoot <repo> -VerifyOnly
#>
[CmdletBinding()]
param(
    [string]$MyPowerToolsRepoRoot = '',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    # Mirror the staged package into <repo>/modules/mobile-tool-control. Off by default: the Android
    # bundle embeds the staged package directly, and the modules/ tree belongs to other tasks.
    [switch]$Mirror,

    [switch]$NoRestore,

    # Backend-only verification: stage the module and the metadata and skip the phone page (which
    # lives in its own deliverable). The resulting package is explicitly incomplete.
    [switch]$SkipSurface,

    # Build the module and its tests only; do not publish a package.
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$integrationRoot = $PSScriptRoot
$repo = if ($MyPowerToolsRepoRoot) {
    [IO.Path]::GetFullPath($MyPowerToolsRepoRoot)
} else {
    # android-integration -> remote-tool-gateway -> tools -> repository root
    [IO.Path]::GetFullPath((Join-Path $integrationRoot '../../..'))
}

$moduleProject = Join-Path $integrationRoot 'src/MobileToolControl.Android/MobileToolControl.Android.csproj'
$testProject = Join-Path $integrationRoot 'tests/MobileToolControl.Android.Tests/MobileToolControl.Android.Tests.csproj'
$surfaceProject = Join-Path $repo 'src/MyPowerTools.MobileToolControl/MyPowerTools.MobileToolControl.csproj'
$template = Join-Path $integrationRoot 'package'
$manifestPath = Join-Path $integrationRoot 'manifest/android-package-manifest.json'
$packageRoot = Join-Path $integrationRoot 'artifacts/package/mobile-tool-control'
$staging = Join-Path $integrationRoot 'artifacts/package.staging'
$surfaceStage = Join-Path $staging 'ui/surface'
$moduleMirror = Join-Path $repo 'modules/mobile-tool-control'

$dotnetCommand = Get-Command 'dotnet' -CommandType Application -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $HOME '.dotnet/dotnet' }
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) {
    throw 'The dotnet host was not found. Add it to PATH, or set DOTNET_ROOT.'
}

if (-not (Test-Path -LiteralPath $moduleProject -PathType Leaf)) {
    throw "Project was not found: $moduleProject"
}

# Explicitly typed: PowerShell unrolls a single-element array literal into a string, which would then
# be splatted character by character.
[string[]]$restoreArgument = @()
if ($NoRestore) { $restoreArgument = @('--no-restore') }

# StageRepositoryModule=false keeps tools that publish into <repo>/modules (input-monitor,
# paste-image) from running their stage target as part of this build.
[string[]]$sharedArguments = @(
    '-p:StageRepositoryModule=false',
    "-p:MyPowerToolsRepoRoot=$repo"
)

if ($VerifyOnly) {
    & $dotnet build $moduleProject -c $Configuration --nologo @restoreArgument @sharedArguments
    if ($LASTEXITCODE -ne 0) { throw 'MobileToolControl.Android build failed.' }
    & $dotnet test $testProject -c $Configuration --nologo @restoreArgument @sharedArguments
    if ($LASTEXITCODE -ne 0) { throw 'MobileToolControl.Android tests failed.' }
    Write-Output 'MobileToolControl.Android verified (module + tests); no package was staged.'
    return
}

$surfaceAvailable = -not $SkipSurface -and (Test-Path -LiteralPath $surfaceProject -PathType Leaf)
if (-not $SkipSurface -and -not $surfaceAvailable) {
    throw "Project was not found: $surfaceProject. The phone page is a separate deliverable (src/MyPowerTools.MobileToolControl); pass -SkipSurface only for a backend-only verification package."
}

if (-not $surfaceAvailable) {
    Write-Warning 'Staging without the phone page; the resulting package is INCOMPLETE and must not be shipped.'
}

# Only the disposable staging directory is cleared up front; the published package is replaced after
# a successful build, so a failed build never destroys a package a running dev install still uses.
if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $surfaceStage -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $template) {
    Copy-Item -LiteralPath $item.FullName -Destination $staging -Recurse -Force
}

& $dotnet build $moduleProject -c $Configuration --nologo -o $staging @restoreArgument @sharedArguments "-p:CopyLocalLockFileAssemblies=true"
if ($LASTEXITCODE -ne 0) { throw 'MobileToolControl.Android build failed.' }

if ($surfaceAvailable) {
    & $dotnet build $surfaceProject -c $Configuration --nologo -o $surfaceStage @restoreArgument @sharedArguments
    if ($LASTEXITCODE -ne 0) { throw 'MyPowerTools.MobileToolControl build failed.' }
}

# The in-proc module host resolves dependencies from the package directory, not from a deps.json.
Get-ChildItem -LiteralPath $staging -Filter '*.deps.json' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

# The host resolves Abstractions / Platform.Abstractions / AvaloniaSdk from its own load context, and
# the Android app compiles the shared contracts from source. Shipping a second copy next to the
# module would load a duplicate contract assembly, so they are removed from the stage.
#
# Deliberately NON-recursive: the phone page ui/surface/MyPowerTools.MobileToolControl.dll also
# starts with "MyPowerTools." and must stay in the package.
$hostProvided = @(
    'MyPowerTools.Abstractions.dll',
    'MyPowerTools.Platform.Abstractions.dll',
    'MyPowerTools.AvaloniaSdk.dll',
    'Avalonia*.dll'
)
foreach ($pattern in $hostProvided) {
    Get-ChildItem -LiteralPath $staging -Filter $pattern -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
    Get-ChildItem -LiteralPath $surfaceStage -Filter $pattern -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

# Debug symbols are not part of the shipped package.
Get-ChildItem -LiteralPath $staging -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -eq '.pdb' } |
    Remove-Item -Force

# The surface directory ships exactly one file: the factory assembly named by ui/tool.json.
Get-ChildItem -LiteralPath $surfaceStage -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'MyPowerTools.MobileToolControl.dll' } |
    Remove-Item -Force

# Fail before publishing a catalog that points at a missing factory.
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$missing = @()
foreach ($entry in $manifest.files) {
    $candidate = Join-Path $staging ($entry.path -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        continue
    }

    if (-not $entry.required) {
        Write-Warning "Optional package file is absent: $($entry.path)"
        continue
    }

    if (-not $surfaceAvailable -and $entry.path -like 'ui/surface/*') {
        Write-Warning "Skipped surface assembly (backend-only staging): $($entry.path)"
        continue
    }

    $missing += $entry.path
}

if ($missing.Count -gt 0) {
    throw ("The 电脑工具 Android package is incomplete; missing: " + ($missing -join ', '))
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path (Split-Path -Parent $packageRoot) -Force | Out-Null
Move-Item -LiteralPath $staging -Destination $packageRoot

if ($Mirror) {
    if (Test-Path -LiteralPath $moduleMirror) {
        Remove-Item -LiteralPath $moduleMirror -Recurse -Force
    }
    New-Item -ItemType Directory -Path $moduleMirror -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $packageRoot) {
        Copy-Item -LiteralPath $item.FullName -Destination $moduleMirror -Recurse -Force
    }
}

Write-Output "电脑工具 (Android) staged at $packageRoot"
