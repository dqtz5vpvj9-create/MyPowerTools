<#
.SYNOPSIS
  Builds all first-party tools (runtime + Surface packages) and collects them to artifacts/.

.DESCRIPTION
  1. Builds local NuGet/npm/protocol SDK bundles (delegates to build-sdk.ps1)
  2. Builds each tool's .MyPowerTools runtime project and .Surface dotnet-surface project
  3. Collects outputs to artifacts/tools/<tool-id>/<version>/
  4. Generates a source manifest with dirty/branch/hash info
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $MyInvocation.MyCommand.Path).Directory.Parent.FullName
$artifactsDir = Join-Path $repoRoot 'artifacts' 'tools'
New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null

$tools = @(
    @{ Id = 'adb-forwarder'; RuntimeProj = 'tools\adb-forwarder\current-integration\src\AdbForwarder.MyPowerTools\AdbForwarder.MyPowerTools.csproj'; SurfaceProj = 'tools\adb-forwarder\current-integration\src\AdbForwarder.Surface\AdbForwarder.Surface.csproj' }
    @{ Id = 'remote-notifications'; RuntimeProj = 'tools\remote-notifications\current-integration\src\AndroidTools.MyPowerTools\AndroidTools.MyPowerTools.csproj'; SurfaceProj = 'tools\remote-notifications\current-integration\src\RemoteNotifications.Surface\RemoteNotifications.Surface.csproj' }
    @{ Id = 'screenease'; RuntimeProj = 'tools\screenease\current-integration\src\ScreenEase.MyPowerTools\ScreenEase.MyPowerTools.csproj'; SurfaceProj = 'tools\screenease\current-integration\src\ScreenEase.Surface\ScreenEase.Surface.csproj' }
    @{ Id = 'doubao-agent'; RuntimeProj = 'tools\doubao-computer-use\current-integration\src\DoubaoAgent.MyPowerTools\DoubaoAgent.MyPowerTools.csproj'; SurfaceProj = 'tools\doubao-computer-use\current-integration\src\DoubaoAgent.Surface\DoubaoAgent.Surface.csproj' }
    @{ Id = 'smartbird-thermostat'; RuntimeProj = 'tools\smartbird-thermostat\current-integration\src\SmartBirdThermostat.MyPowerTools\SmartBirdThermostat.MyPowerTools.csproj'; SurfaceProj = 'tools\smartbird-thermostat\current-integration\src\SmartBird.Surface\SmartBird.Surface.csproj' }
)

Write-Host "==> Building SDK bundles..." -ForegroundColor Cyan
$sdkScript = Join-Path $repoRoot 'scripts' 'build-sdk.ps1'
if (Test-Path $sdkScript) {
    & $sdkScript
    if ($LASTEXITCODE -ne 0) { throw "SDK build failed" }
}

$manifest = @()
foreach ($tool in $tools) {
    $toolId = $tool.Id
    $version = '0.2.0'
    $outDir = Join-Path $artifactsDir $toolId $version
    Write-Host "==> Building $toolId..." -ForegroundColor Cyan

    # Build runtime project
    $runtimeProj = Join-Path $repoRoot $tool.RuntimeProj
    if (Test-Path $runtimeProj) {
        & dotnet build $runtimeProj -c $Configuration --nologo -v quiet 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "$toolId runtime build failed"; continue }
    }

    # Build Surface project
    $surfaceProj = Join-Path $repoRoot $tool.SurfaceProj
    if (Test-Path $surfaceProj) {
        & dotnet build $surfaceProj -c $Configuration --nologo -v quiet 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "$toolId Surface build failed"; continue }
        # Publish Surface to artifacts
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        & dotnet publish $surfaceProj -c $Configuration -o $outDir --nologo -v quiet 2>&1 | Out-Null
    }

    # Record source info
    $submodulePath = "tools/$toolId"
    $branch = try { (git -C (Join-Path $repoRoot $submodulePath) rev-parse --abbrev-ref HEAD 2>$null) } catch { 'unknown' }
    $hash = try { (git -C (Join-Path $repoRoot $submodulePath) rev-parse --short HEAD 2>$null) } catch { 'unknown' }
    $dirty = try { (git -C (Join-Path $repoRoot $submodulePath) status --porcelain 2>$null) } catch { '' }
    $manifest += [pscustomobject]@{
        toolId = $toolId
        version = $version
        branch = if ($branch) { $branch.Trim() } else { 'unknown' }
        commit = if ($hash) { $hash.Trim() } else { 'unknown' }
        dirty = -not [string]::IsNullOrWhiteSpace($dirty)
    }

    Write-Host "  OK $toolId -> $outDir" -ForegroundColor Green
}

# Write source manifest
$manifestPath = Join-Path $artifactsDir 'source-manifest.json'
$manifest | ConvertTo-Json -Depth 3 | Set-Content -Path $manifestPath -Encoding UTF8
Write-Host "==> Source manifest: $manifestPath" -ForegroundColor Cyan
Write-Host "==> Done. $($manifest.Count) tool(s) built." -ForegroundColor Green
