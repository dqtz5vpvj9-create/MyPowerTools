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
  Optional version override for every selected tool. When omitted, each tool's
  declared release version is used.

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

    [string]$Version = '',

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

function Copy-SurfaceOutput {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$AssemblyName
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Surface output is missing: $Source"
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($extension in @('*.dll', '*.pdb', '*.deps.json')) {
        Get-ChildItem -LiteralPath $Source -File -Filter $extension |
            Copy-Item -Destination $Destination -Force
    }
    $expectedAssembly = Join-Path $Destination $AssemblyName
    if (-not (Test-Path -LiteralPath $expectedAssembly -PathType Leaf)) {
        throw "Surface assembly was not staged: $expectedAssembly"
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
# Using [pscustomobject] (not hashtable) so that property access is type-safe
# and ConvertTo-Json serializes values deterministically (hashtable iteration
# under strict mode can wrap single string values in one-element arrays).
$toolRegistry = @(
    [pscustomobject]@{
        Id               = 'adb-forwarder'
        Version          = '0.2.0'
        BuildScript      = 'tools\adb-forwarder\build.ps1'
        SurfaceProject   = 'tools\adb-forwarder\current-integration\src\AdbForwarder.Surface\AdbForwarder.Surface.csproj'
        SurfaceAssembly  = 'AdbForwarder.Surface.dll'
        SurfaceTarget    = 'ui\surface'
        RuntimeStagePath = 'tools\adb-forwarder\artifacts\package'
    },
    [pscustomobject]@{
        Id               = 'doubao-computer-use'
        Version          = '0.3.0'
        BuildScript      = 'tools\doubao-computer-use\build.ps1'
        SurfaceProject   = 'tools\doubao-computer-use\current-integration\src\DoubaoAgent.Surface\DoubaoAgent.Surface.csproj'
        SurfaceAssembly  = 'DoubaoAgent.Surface.dll'
        SurfaceTarget    = 'ui\surface'
        RuntimeStagePath = 'tools\doubao-computer-use\artifacts\package'
    },
    [pscustomobject]@{
        Id               = 'remote-notifications'
        Version          = '0.2.0'
        BuildScript      = 'tools\remote-notifications\build.ps1'
        SurfaceProject   = 'tools\remote-notifications\current-integration\src\RemoteNotifications.Surface\RemoteNotifications.Surface.csproj'
        SurfaceAssembly  = 'RemoteNotifications.Surface.dll'
        SurfaceTarget    = 'modules\notifications\ui\surface'
        RuntimeStagePath = 'tools\remote-notifications\artifacts\package\android-tools-suite'
    },
    [pscustomobject]@{
        Id               = 'screenease'
        Version          = '0.2.0'
        BuildScript      = 'tools\screenease\build.ps1'
        SurfaceProject   = 'tools\screenease\current-integration\src\ScreenEase.Surface\ScreenEase.Surface.csproj'
        SurfaceAssembly  = 'ScreenEase.Surface.dll'
        SurfaceTarget    = 'ui\surface'
        RuntimeStagePath = 'tools\screenease\artifacts\package'
    },
    [pscustomobject]@{
        Id               = 'smartbird-thermostat'
        Version          = '0.2.0'
        BuildScript      = 'tools\smartbird-thermostat\build.ps1'
        SurfaceProject   = 'tools\smartbird-thermostat\current-integration\src\SmartBird.Surface\SmartBird.Surface.csproj'
        SurfaceAssembly  = 'SmartBird.Surface.dll'
        SurfaceTarget    = 'ui\surface'
        RuntimeStagePath = 'tools\smartbird-thermostat\artifacts\package'
    }
)

if ($ToolId.Count -gt 0) {
    $wanted = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($id in $ToolId) { [void]$wanted.Add($id) }
    # Wrap in @(...) so a single match stays a 1-element array.
    $toolRegistry = @($toolRegistry | Where-Object { $wanted.Contains($_.Id) })
    $knownIds = @($toolRegistry | ForEach-Object { $_.Id })
    $missing = @($ToolId | Where-Object { $knownIds -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "Unknown -ToolId value(s): $($missing -join ', '). Known: $($knownIds -join ', ')"
    }
}

# ---------------------------------------------------------------------------
# Prepare output
# ---------------------------------------------------------------------------
if (Test-Path -LiteralPath $OutputRoot) {
    if ($ToolId.Count -eq 0) {
        # A full suite build produces a fresh, complete collection.
        Remove-Item -LiteralPath $OutputRoot -Recurse -Force
    } else {
        # A targeted developer build replaces only the selected tool outputs.
        foreach ($tool in $toolRegistry) {
            $selectedOutput = Join-Path $OutputRoot $tool.Id
            if (Test-Path -LiteralPath $selectedOutput) {
                Remove-Item -LiteralPath $selectedOutput -Recurse -Force
            }
        }
    }
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
    $currentToolId    = [string]$tool.Id
    $toolBuildScript  = $tool.BuildScript
    $toolSurfaceProj  = $tool.SurfaceProject
    $toolRuntimeStage = $tool.RuntimeStagePath
    $toolVersion      = if ([string]::IsNullOrWhiteSpace($Version)) { $tool.Version } else { $Version }

    Write-Host ''
    Write-Host "==> [$currentToolId] runtime package via build.ps1" -ForegroundColor Cyan

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
        throw "[$currentToolId] runtime package was not staged where expected: $runtimeStageFull"
    }

    # ---- Surface build + pack --------------------------------------------
    $surfaceProjFull = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $toolSurfaceProj))
    $surfaceOutputs  = @()
    if (Test-Path -LiteralPath $surfaceProjFull -PathType Leaf) {
        Write-Host "==> [$currentToolId] Surface build + pack" -ForegroundColor Cyan
        # Local SDK packages deliberately keep a stable development version. Force
        # NuGet to re-evaluate their dependency graph after each SDK rebuild so a
        # changed package cannot be hidden by a stale project.assets.json file.
        Invoke-Native -FilePath $dotnet -ArgumentList @(
            'restore', $surfaceProjFull,
            '--force-evaluate',
            '--nologo',
            "-p:MyPowerToolsRepoRoot=$repoRoot"
        ) -Activity "dotnet restore --force-evaluate $toolSurfaceProj"
        Invoke-Native -FilePath $dotnet -ArgumentList @(
            'build', $surfaceProjFull,
            '--configuration', $Configuration,
            '--no-restore',
            '--nologo',
            "-p:MyPowerToolsRepoRoot=$repoRoot"
        ) -Activity "dotnet build $toolSurfaceProj"

        # Pack to a per-tool staging folder so we can collect just the nupkg.
        $surfacePackOut = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "tools\$currentToolId\artifacts\surface"))
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
            throw "[$currentToolId] Surface pack produced no .nupkg under $surfacePackOut"
        }

        # The runtime package is what Runner discovers. Stage the actual Surface
        # assembly next to tool.json so the Shell can load the declared factory.
        $surfaceBuildOut = Join-Path (Split-Path -Parent $surfaceProjFull) "bin\$Configuration\net10.0"
        $surfaceRuntimeOut = Join-Path $runtimeStageFull $tool.SurfaceTarget
        Copy-SurfaceOutput -Source $surfaceBuildOut -Destination $surfaceRuntimeOut -AssemblyName $tool.SurfaceAssembly

        # Surface staging changes package contents, so refresh the local package
        # hash/signature documents before collecting or installing the package.
        $cliExe = Join-Path $repoRoot 'artifacts\sdk\cli\MyPowerTools.Cli.exe'
        if (-not (Test-Path -LiteralPath $cliExe -PathType Leaf)) {
            throw "SDK CLI is missing: $cliExe"
        }
        Invoke-Native -FilePath $cliExe -ArgumentList @(
            'package', 'sign-local', $runtimeStageFull
        ) -Activity "mpt package sign-local $currentToolId"
    } else {
        Write-Warning "[$currentToolId] Surface project not found at $surfaceProjFull; skipping Surface pack."
    }

    # ---- Collect into artifacts/tools/<id>/<version> ---------------------
    $collectDir = Join-Path $OutputRoot $currentToolId $toolVersion
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

    $archiveZip = Join-Path $collectDir "$currentToolId-$toolVersion.zip"
    $packagePath = Join-Path $collectDir "$currentToolId-$toolVersion.mptpkg"
    Compress-Archive -Path (Join-Path $runtimeCollect '*') -DestinationPath $archiveZip -CompressionLevel Optimal
    Move-Item -LiteralPath $archiveZip -Destination $packagePath -Force

    # ---- Source info for this tool submodule -----------------------------
    $submodulePath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "tools\$currentToolId"))
    $srcInfo = Get-SourceInfo -Path $submodulePath

    $artifactsRelative = @('runtime/', (Split-Path -Leaf $packagePath))
    if ($surfaceOutputs.Count -gt 0) { $artifactsRelative += @('surface/') }

    [void]$perToolManifest.Add([ordered]@{
        toolId    = $currentToolId
        version   = $toolVersion
        source    = $srcInfo
        artifacts = $artifactsRelative
        output    = "artifacts/tools/$currentToolId/$toolVersion"
    })

    Write-Host "  OK [$currentToolId] -> $collectDir" -ForegroundColor Green
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
