<#
.SYNOPSIS
  Compatibility entry point for the Remote Notifications macOS production gate.
#>
[CmdletBinding()]
param(
    [string]$AppBundle = ''
)

$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'verify-remote-notifications-macos-production.ps1'
& $script -AppBundle $AppBundle
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
