[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Test-MyPowerToolsPrivateDotNetRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $false
    }

    try {
        $root = [IO.Path]::GetFullPath($Path)
    }
    catch {
        return $false
    }

    $fxrRoot = Join-Path $root 'host\fxr'
    $coreRoot = Join-Path $root 'shared\Microsoft.NETCore.App'
    return (Test-Path -LiteralPath $fxrRoot -PathType Container) -and
        (Test-Path -LiteralPath $coreRoot -PathType Container) -and
        $null -ne (Get-ChildItem -LiteralPath $fxrRoot -Recurse -Filter 'hostfxr.dll' -File -ErrorAction SilentlyContinue |
            Select-Object -First 1)
}

function Set-MyPowerToolsProcessDotNetRoot {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $installRootFull = [IO.Path]::GetFullPath($InstallRoot)
    $privateRoot = Join-Path $installRootFull 'Runtime\dotnet'
    if (Test-MyPowerToolsPrivateDotNetRoot -Path $privateRoot) {
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $privateRoot, 'Process')
        return [pscustomobject]@{
            source = 'private'
            root = $privateRoot
        }
    }

    # The product must never inherit an account-wide DOTNET_ROOT. Framework-dependent
    # apphosts resolve the registered Windows installation through their Global search path.
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $null, 'Process')
    return [pscustomobject]@{
        source = 'global'
        root = $null
    }
}

function Get-MyPowerToolsLegacyUserDotNetRootMigration {
    param(
        [Parameter(Mandatory = $true)][string]$InstallRoot,
        [AllowNull()][string]$UserValue
    )

    $value = if ($PSBoundParameters.ContainsKey('UserValue')) {
        $UserValue
    } else {
        [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'User')
    }
    $migration = [ordered]@{
        detected = $false
        cleared = $false
        previousValue = $null
        reason = 'none'
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        return [pscustomobject]$migration
    }

    $migration.previousValue = $value
    try {
        $candidate = [IO.Path]::GetFullPath([Environment]::ExpandEnvironmentVariables($value)).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        $legacyRoot = [IO.Path]::GetFullPath((Join-Path $InstallRoot 'Runtime\dotnet')).TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)
        $legacyPrefix = $legacyRoot + [IO.Path]::DirectorySeparatorChar
        if ($candidate.Equals($legacyRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $candidate.StartsWith($legacyPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            $migration.detected = $true
            $migration.reason = 'legacy-mypowertools-private-runtime'
        }
        else {
            $migration.reason = 'user-managed-value-preserved'
        }
    }
    catch {
        $migration.reason = 'unparseable-user-value-preserved'
    }

    return [pscustomobject]$migration
}

function Clear-MyPowerToolsLegacyUserDotNetRoot {
    param([Parameter(Mandatory = $true)][string]$InstallRoot)

    $migration = Get-MyPowerToolsLegacyUserDotNetRootMigration -InstallRoot $InstallRoot
    if ($migration.detected) {
        # This is the sole permitted persistent DOTNET_ROOT mutation: remove the value
        # written by older MyPowerTools releases. User-managed values are preserved.
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $null, 'User')
        $migration.cleared = $true
        Write-Host "Removed legacy MyPowerTools user DOTNET_ROOT: $($migration.previousValue)"
    }
    return $migration
}
