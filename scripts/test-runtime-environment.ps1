[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'runtime-environment.ps1')

function Assert-Equal {
    param([object]$Actual, [object]$Expected, [string]$Label)
    if (-not [object]::Equals($Actual, $Expected)) {
        throw "$Label failed. Expected '$Expected'; actual '$Actual'."
    }
}

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'
$original = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'User')

$empty = Get-MyPowerToolsLegacyUserDotNetRootMigration -InstallRoot $installRoot -UserValue $null
Assert-Equal $empty.detected $false 'Empty value detection'
Assert-Equal $empty.reason 'none' 'Empty value reason'

$custom = 'D:\Developer\dotnet'
$customResult = Get-MyPowerToolsLegacyUserDotNetRootMigration -InstallRoot $installRoot -UserValue $custom
Assert-Equal $customResult.detected $false 'Custom value detection'
Assert-Equal $customResult.previousValue $custom 'Custom value preservation'
Assert-Equal $customResult.reason 'user-managed-value-preserved' 'Custom value reason'

$invalid = 'Z:\missing\dotnet'
$invalidResult = Get-MyPowerToolsLegacyUserDotNetRootMigration -InstallRoot $installRoot -UserValue $invalid
Assert-Equal $invalidResult.detected $false 'Invalid value detection'
Assert-Equal $invalidResult.previousValue $invalid 'Invalid value preservation'
Assert-Equal $invalidResult.reason 'user-managed-value-preserved' 'Invalid value reason'

$legacy = Join-Path $installRoot 'Runtime\dotnet'
$legacyResult = Get-MyPowerToolsLegacyUserDotNetRootMigration -InstallRoot $installRoot -UserValue $legacy
Assert-Equal $legacyResult.detected $true 'Legacy value detection'
Assert-Equal $legacyResult.reason 'legacy-mypowertools-private-runtime' 'Legacy value reason'

$legacyChild = Get-MyPowerToolsLegacyUserDotNetRootMigration -InstallRoot $installRoot -UserValue (Join-Path $legacy 'shared')
Assert-Equal $legacyChild.detected $true 'Legacy child value detection'

$after = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'User')
Assert-Equal $after $original 'Persistent user environment snapshot'
Write-Output 'Runtime environment migration tests passed.'
