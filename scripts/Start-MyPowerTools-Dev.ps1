<#
.SYNOPSIS
Builds and starts the MyPowerTools development overlay on the complete Windows installation.

.DESCRIPTION
This entry point delegates all updates to update-windows-dev.ps1. The running Shell, Runner,
modules, runtimes, service units, broker, and ServiceManager stay in the canonical installed
layout. The repository supplies development build inputs only.

.EXAMPLE
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\Start-MyPowerTools-Dev.ps1

.EXAMPLE
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId paste-image
#>
[CmdletBinding()]
param(
    [ValidateSet('Core', 'Shell', 'Tools')]
    [string]$Scope = 'Core',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string[]]$ToolId = @(),
    [switch]$NoRestore,
    [switch]$NoOpenShell,
    [switch]$SkipArtifactsCheck
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest

try {
    $repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
    $installRoot = [IO.Path]::GetFullPath(
        (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'))
    $updateScript = Join-Path $repositoryRoot 'scripts\update-windows-dev.ps1'
    $requiredInstalledPaths = @(
        (Join-Path $installRoot 'MyPowerTools.exe'),
        (Join-Path $installRoot 'Shell\MyPowerTools.Shell.Avalonia.exe'),
        (Join-Path $installRoot 'Runner\MyPowerTools.Runner.exe'),
        (Join-Path $installRoot 'modules'),
        (Join-Path $installRoot 'Runtimes'),
        (Join-Path $installRoot 'service-units'),
        (Join-Path $installRoot 'ServiceManager'))

    if (-not (Test-Path -LiteralPath $updateScript -PathType Leaf)) {
        throw "Development update script is missing: $updateScript"
    }

    $missingInstalledPaths = @($requiredInstalledPaths | Where-Object {
        -not (Test-Path -LiteralPath $_)
    })
    if ($missingInstalledPaths.Count -gt 0) {
        throw @"
The complete MyPowerTools installation is required before starting the development overlay.
Missing: $($missingInstalledPaths -join ', ')
Run scripts\install-windows.ps1 once, then use this development shortcut for later updates.
"@
    }

    $updateParameters = @{
        Scope = $Scope
        Configuration = $Configuration
        ToolId = $ToolId
        DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools')
        NoRestore = $NoRestore.IsPresent
        NoOpenShell = $NoOpenShell.IsPresent
    }
    & $updateScript @updateParameters

    if (-not $SkipArtifactsCheck) {
        # Advisory only: a disk-hygiene warning must never stop the dev overlay.
        try {
            & (Join-Path $repositoryRoot 'scripts\check-artifacts-governance.ps1')
        }
        catch {
            Write-Warning "Artifacts governance check could not run: $($_.Exception.Message)"
        }
    }
}
catch {
    $failureMessage = $_.Exception.Message
    if ([Console]::IsOutputRedirected) {
        Write-Error $failureMessage
        exit 1
    }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show(
            $failureMessage,
            'MyPowerTools 开发版启动失败',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error)
    }
    catch {
        Write-Error $failureMessage
    }
    exit 1
}
