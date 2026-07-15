<#
.SYNOPSIS
  Builds a self-contained MPT installation package.

.DESCRIPTION
  Produces a portable directory under artifacts/install/<version>/ containing:
  - Shell (MyPowerTools.Shell.Avalonia)
  - Runner (MyPowerTools.Runner)
  - ServiceManager (MyPowerTools.ServiceManager)
  - WebToolHost (MyPowerTools.WebToolHost)
  - SDK runtime dependencies
  - Selected tool packages from artifacts/tools/

  The installer registers the ServiceManager as a login autostart entry, deploys unit
  manifests to the ServiceManager deploy root, and activates default services.
#>
[CmdletBinding()]
param(
    [string]$Version = "0.2.0",
    [string]$RuntimeIdentifier = "win-x64",
    [string[]]$ToolIds = @()
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $MyInvocation.MyCommand.Path).Directory.Parent.FullName
$installDir = Join-Path $repoRoot 'artifacts' 'install' $Version
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

Write-Host "==> Building MPT installation $Version ($RuntimeIdentifier)..." -ForegroundColor Cyan

# Core processes
$projects = @(
    @{ Name = 'Shell';          Proj = 'src\MyPowerTools.Shell.Avalonia\MyPowerTools.Shell.Avalonia.csproj';          Out = 'Shell' }
    @{ Name = 'Runner';         Proj = 'src\MyPowerTools.Runner\MyPowerTools.Runner.csproj';                            Out = 'Runner' }
    @{ Name = 'ServiceManager'; Proj = 'src\MyPowerTools.ServiceManager\MyPowerTools.ServiceManager.csproj';            Out = 'ServiceManager' }
)

foreach ($p in $projects) {
    $outDir = Join-Path $installDir $p.Out
    Write-Host "  Publishing $($p.Name)..." -ForegroundColor Gray
    & dotnet publish (Join-Path $repoRoot $p.Proj) -c Release -r $RuntimeIdentifier -o $outDir --self-contained false --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$($p.Name) publish failed" }
}

# WebToolHost (Windows-specific TFM)
$webHostOut = Join-Path $installDir 'WebToolHost'
Write-Host "  Publishing WebToolHost..." -ForegroundColor Gray
& dotnet publish (Join-Path $repoRoot 'src\MyPowerTools.WebToolHost\MyPowerTools.WebToolHost.csproj') -c Release -o $webHostOut --nologo -v quiet 2>&1 | Out-Null

# Tool packages
$toolsDir = Join-Path $installDir 'tools'
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
$artifactsTools = Join-Path $repoRoot 'artifacts' 'tools'
if (Test-Path $artifactsTools) {
    $toolDirs = if ($ToolIds.Count -gt 0) { $ToolIds } else { (Get-ChildItem $artifactsTools -Directory).Name }
    foreach ($toolId in $toolDirs) {
        $srcDir = Join-Path $artifactsTools $toolId $Version
        if (Test-Path $srcDir) {
            Copy-Item -Path $srcDir -Destination (Join-Path $toolsDir $toolId) -Recurse -Force
            Write-Host "  Included tool: $toolId" -ForegroundColor Gray
        }
    }
}

# Unit manifests directory
$unitsDir = Join-Path $installDir 'ServiceManager' 'units'
New-Item -ItemType Directory -Force -Path $unitsDir | Out-Null

# Installation bootstrap script
$bootstrap = @'
@echo off
setlocal
set INSTALL_DIR=%~dp0
set DATA_DIR=%LOCALAPPDATA%\MyPowerTools

echo Installing MyPowerTools to %INSTALL_DIR%
echo Data directory: %DATA_DIR%

mkdir "%DATA_DIR%\ServiceManager\units" 2>nul

echo Registering ServiceManager autostart...
"%INSTALL_DIR%ServiceManager\MyPowerTools.ServiceManager.exe" --register-autostart --data-root "%DATA_DIR%"

echo Copying unit manifests...
copy /Y "%INSTALL_DIR%ServiceManager\units\*.json" "%DATA_DIR%\ServiceManager\units\" 2>nul

echo Installation complete.
pause
'@
$bootstrapPath = Join-Path $installDir 'install.bat'
Set-Content -Path $bootstrapPath -Value $bootstrap -Encoding ASCII

Write-Host "==> Installation package: $installDir" -ForegroundColor Green
Write-Host "==> Run install.bat to install (registers ServiceManager autostart + deploys units)" -ForegroundColor Green
