<#
.SYNOPSIS
  Builds every first-party tool (runtime package + dotnet Surface project) and
  collects the outputs under artifacts/tools/<tool-id>/<version>/.

.DESCRIPTION
  This is the suite-level orchestrator. It:

    1. Builds the local SDK bundles (NuGet / npm / protocol / CLI) by delegating
       to scripts/build-sdk.ps1. The per-tool builds consume these contracts.
    2. For each tool, delegates the runtime (.MyPowerTools) build and module
       package staging to that tool's own build.ps1. Each tool's build.ps1 is
       authoritative for the runtime package because it knows the adapter
       project, the module template, the required MSBuild property
       (MyPowerToolsRepoRoot), and the post-build assembly assertion.
    3. For each tool, additionally builds + packs the tool's .Surface project
       (the dotnet-surface companion that defines the tool's UI surface) and
       drops the nupkg next to the runtime package.
    4. Collects the staged runtime package and the Surface nupkg into a clean,
       versioned directory: artifacts/tools/<tool-id>/<version>/
    5. Emits artifacts/tools/source-manifest.json with branch / short hash /
       dirty flag for both the superproject and each tool submodule, plus the
       list of artifacts produced per tool.

  The tool build.ps1 files live in git submodules, so this script does not edit
  them. It calls them with -MyPowerToolsRepoRoot and layers the Surface build on
  top, which is the cleanest contract split.

.PARAMETER Configuration
  Build configuration (Debug / Release). Defaults to Release.

.PARAMETER OutputRoot
  Where the collected artifacts land. Defaults to artifacts/tools under the
  repository root. Must stay under the repository's artifacts directory.

.PARAMETER Version
  Version label used for the collection directory and the manifest. Defaults to
  0.2.0, which matches the tool-release.json of every shipped tool.

.PARAMETER SkipSdk
  Skip the SDK bundle build (use when artifacts/sdk is already up to date).

.PARAMETER ToolId
  Optional tool id. When supplied, only that tool is built. May be specified
  multiple times: -ToolId adb-forwarder -ToolId screenease.

.EXAMPLE
  pwsh scripts/build-all-tools.ps1
.EXAMPLE
  pwsh scripts/build-all-tools.ps1 -ToolId screenease -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputRoot = '',

    [string]$Version = '0.2.0',

    [switch]$SkipSdk,

    [string[]]$ToolId = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $artifactsRoot 'tools'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$allowedPrefix = $artifactsRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $OutputRoot.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot must stay under $artifactsRoot (got '$OutputRoot')."
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Invoke-Native {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [string]$Activity
    )
    if ($Activity) { Write-Host "  > $Activity" -ForegroundColor DarkGray }
    & $FilePath @ArgumentList
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode."
    }
}

function Get-GitValue {
    param(
        [Parameter(Mandatory = $true)][string]$RepoPath,
        [Parameter(Mandatory = $true)][string[]]$GitArgs
    )
    try {
        $out = & git -C $RepoPath @GitArgs 2>$null
        if ($LASTEXITCODE -eq 0 -and $null -ne $out) {
            return ($out -join "`n").Trim()
        }
    } catch {
        # fall through to empty
    }
    return ''
}

function Get-SourceInfo {
    param([string]$Path)
    $branch = Get-GitValue -RepoPath $Path -GitArgs @('rev-parse', '--abbrev-ref', 'HEAD')
    $commit = Get-GitValue -RepoPath $Path -GitArgs @('rev-parse', '--short', 'HEAD')
    $porcelain = Get-GitValue -RepoPath $Path -GitArgs @('status', '--porcelain')
    return [ordered]@{
        branch = $branch
        commit = $commit
        dirty  = -not [string]::IsNullOrWhiteSpace($porcelain)
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Tool package source is missing: $Source"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

# Resolve the dotnet host once. tool build.ps1 scripts resolve it themselves,
# but the Surface pack step here uses the same one for consistency.
$dotnet = (Get-Command 'dotnet' -CommandType Application -ErrorAction Stop).Source

# ---------------------------------------------------------------------------
# Tool registry
#
# RuntimeStagePath is the directory the tool's build.ps1 writes its staged
# runtime package to. Every existing tool writes to artifacts/package under the
# tool root (remote-notifications writes one level deeper under
# android-tools-suite). These mirror scripts/build-tool-packages.ps1.
# ---------------------------------------------------------------------------
$toolRegistry = @(
    @{
        Id                = 'adb-forwarder'
        BuildScript       = 'tools\adb-forwarder\build.ps1'
        SurfaceProject    = 'tools\adb-forwarder\current-integration\src\AdbForwarder.Surface\AdbForwarder.Surface.csproj'
        RuntimeStagePath  = 'tools\adb-forwarder\artifacts\package'
    },
    @{
        Id                = 'doubao-computer-use'
        BuildScript       = 'tools\doubao-computer-use\build.ps1'
        SurfaceProject    = 'tools\doubao-computer-use\current-integration\src\DoubaoAgent.Surface\DoubaoAgent.Surface.csproj'
        RuntimeStagePath  = 'tools\doubao-computer-use\artifacts\package'
    },
    @{
        Id                = 'remote-notifications'
        BuildScript       = 'tools\remote-notifications\build.ps1'
        SurfaceProject    = 'tools\remote-notifications\current-integration\src\RemoteNotifications.Surface\RemoteNotifications.Surface.csproj'
        RuntimeStagePath  = 'tools\remote-notifications\artifacts\package\android-tools-suite'
    },
    @{
        Id                = 'paste-image'
        BuildScript       = 'tools\paste-image\build.ps1'
        SurfaceProject    = 'tools\paste-image\current-integration\src\PasteImage.Surface\PasteImage.Surface.csproj'
        RuntimeStagePath  = 'tools\paste-image\artifacts\package'
    },
    @{
        Id                = 'screenease'
        BuildScript       = 'tools\screenease\build.ps1'
        SurfaceProject    = 'tools\screenease\current-integration\src\ScreenEase.Surface\ScreenEase.Surface.csproj'
        RuntimeStagePath  = 'tools\screenease\artifacts\package'
    },
    @{
        Id                = 'smartbird-thermostat'
        BuildScript       = 'tools\smartbird-thermostat\build.ps1'
        SurfaceProject    = 'tools\smartbird-thermostat\current-integration\src\SmartBird.Surface\SmartBird.Surface.csproj'
        RuntimeStagePath  = 'tools\smartbird-thermostat\artifacts\package'
    }
)

if ($ToolId.Count -gt 0) {
    $wanted = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($id in $ToolId) { [void]$wanted.Add($id) }
    # Wrap in @(...) so a single match stays a 1-element array; otherwise
    # foreach would iterate the hashtable's dictionary entries instead of the
    # single hashtable item.
    $toolRegistry = @($toolRegistry | Where-Object { $wanted.Contains([string]$_['Id']) })
    $knownIds = $toolRegistry | ForEach-Object { [string]$_['Id'] }
    $missing = $ToolId | Where-Object { $knownIds -notcontains $_ }
    if ($missing) {
        throw "Unknown -ToolId value(s): $($missing -join ', '). Known: $($knownIds -join ', ')"
    }
}

# ---------------------------------------------------------------------------
# Prepare output
# ---------------------------------------------------------------------------
if (Test-Path -LiteralPath $OutputRoot) {
    # Wipe the previous collection but keep sibling artifact directories intact.
    Remove-Item -LiteralPath $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

# ---------------------------------------------------------------------------
# 1) SDK bundles
# ---------------------------------------------------------------------------
if (-not $SkipSdk) {
    Write-Host '==> Building SDK bundles (NuGet / npm / protocol / CLI)...' -ForegroundColor Cyan
    $sdkScript = Join-Path $repoRoot 'scripts\build-sdk.ps1'
    if (-not (Test-Path -LiteralPath $sdkScript -PathType Leaf)) {
        throw "SDK build script not found: $sdkScript"
    }
    Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $sdkScript,
        '-Configuration', $Configuration
    ) -Activity 'pwsh scripts/build-sdk.ps1'
} else {
    Write-Host '==> Skipping SDK bundles (-SkipSdk).' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 2 + 3) Per-tool runtime build (delegated) + Surface build/pack
# ---------------------------------------------------------------------------
$perToolManifest = New-Object System.Collections.ArrayList

foreach ($tool in $toolRegistry) {
    # Use indexer access, not dotted syntax: dotted access on a hashtable
    # returns the value but is fragile under ConvertTo-Json in some PowerShell
    # versions (it can wrap a single string value in a one-element array).
    $toolId            = [string]$tool['Id']
    $toolBuildScript   = [string]$tool['BuildScript']
    $toolSurfaceProj   = [string]$tool['SurfaceProject']
    $toolRuntimeStage  = [string]$tool['RuntimeStagePath']

    Write-Host ''
    Write-Host "DBG toolId=[$toolId] toolIdType=$($toolId.GetType().FullName) toolType=$($tool.GetType().FullName) regCount=$($toolRegistry.Count)" -ForegroundColor Yellow
    Write-Host "==> [$toolId] runtime package via build.ps1" -ForegroundColor Cyan

    $buildScriptPath = Join-Path $repoRoot $toolBuildScript
    if (-not (Test-Path -LiteralPath $buildScriptPath -PathType Leaf)) {
        throw "Tool build script missing: $buildScriptPath"
    }
    Invoke-Native -FilePath 'pwsh.exe' -ArgumentList @(
        '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $buildScriptPath,
        '-MyPowerToolsRepoRoot', $repoRoot
    ) -Activity "pwsh $toolBuildScript"

    $runtimeStageFull = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $toolRuntimeStage))
    if (-not (Test-Path -LiteralPath $runtimeStageFull -PathType Container)) {
        throw "[$toolId] runtime package was not staged where expected: $runtimeStageFull"
    }

    # ---- Surface build + pack --------------------------------------------
    $surfaceProjFull = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $toolSurfaceProj))
    $surfaceOutputs  = @()
    if (Test-Path -LiteralPath $surfaceProjFull -PathType Leaf) {
        Write-Host "==> [$toolId] Surface build + pack" -ForegroundColor Cyan
        Invoke-Native -FilePath $dotnet -ArgumentList @(
            'build', $surfaceProjFull,
            '--configuration', $Configuration,
            '--nologo',
            "-p:MyPowerToolsRepoRoot=$repoRoot"
        ) -Activity "dotnet build $toolSurfaceProj"

        # Pack to a per-tool staging folder so we can collect just the nupkg.
        $surfacePackOut = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "tools\$toolId\artifacts\surface"))
        if (Test-Path -LiteralPath $surfacePackOut) {
            Remove-Item -LiteralPath $surfacePackOut -Recurse -Force
        }
        New-Item -ItemType Directory -Path $surfacePackOut -Force | Out-Null
        Invoke-Native -FilePath $dotnet -ArgumentList @(
            'pack', $surfaceProjFull,
            '--configuration', $Configuration,
            '--nologo',
            '--no-build',
            '-o', $surfacePackOut,
            "-p:MyPowerToolsRepoRoot=$repoRoot"
        ) -Activity "dotnet pack $toolSurfaceProj"

        $surfaceOutputs = @(Get-ChildItem -LiteralPath $surfacePackOut -Filter '*.nupkg' |
            ForEach-Object { $_.FullName })
        if ($surfaceOutputs.Count -eq 0) {
            throw "[$toolId] Surface pack produced no .nupkg under $surfacePackOut"
        }
    } else {
        Write-Warning "[$toolId] Surface project not found at $surfaceProjFull; skipping Surface pack."
    }

    # ---- Collect into artifacts/tools/<id>/<version> ---------------------
    $collectDir = Join-Path $OutputRoot $toolId $Version
    if (Test-Path -LiteralPath $collectDir) {
        Remove-Item -LiteralPath $collectDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $collectDir -Force | Out-Null

    $runtimeCollect = Join-Path $collectDir 'runtime'
    Copy-DirectoryContents -Source $runtimeStageFull -Destination $runtimeCollect

    $surfaceCollect = Join-Path $collectDir 'surface'
    if ($surfaceOutputs.Count -gt 0) {
        New-Item -ItemType Directory -Path $surfaceCollect -Force | Out-Null
        foreach ($nupkg in $surfaceOutputs) {
            Copy-Item -LiteralPath $nupkg -Destination $surfaceCollect -Force
        }
    }

    # ---- Source info for this tool submodule -----------------------------
    $submodulePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "tools\$toolId"))
    $srcInfo = Get-SourceInfo -Path $submodulePath

    $artifactsRelative = @('runtime/')
    if ($surfaceOutputs.Count -gt 0) { $artifactsRelative += @('surface/') }

    [void]$perToolManifest.Add([ordered]@{
        toolId    = $toolId
        version   = $Version
        source    = $srcInfo
        artifacts = $artifactsRelative
        output    = "artifacts/tools/$toolId/$Version"
    })

    Write-Host "  OK [$toolId] -> $collectDir" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 4) Source manifest
# ---------------------------------------------------------------------------
$superSource = Get-SourceInfo -Path $repoRoot

$manifest = [ordered]@{
    schemaVersion = 1
    generatedAt   = ([DateTimeOffset]::UtcNow).ToString('O')
    superproject  = $superSource
    tools         = $perToolManifest
}

$manifestPath = Join-Path $OutputRoot 'source-manifest.json'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host ''
Write-Host '==> Source manifest:' -ForegroundColor Cyan
Write-Host "    $manifestPath"
Write-Host "==> Done. Built $($perToolManifest.Count) tool(s) -> $OutputRoot" -ForegroundColor Green
