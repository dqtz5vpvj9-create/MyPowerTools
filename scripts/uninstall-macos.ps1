[CmdletBinding()]
param(
    [string]$ApplicationsRoot = '',
    [string]$DataRoot = '',
    [switch]$RemoveData,
    [switch]$RemoveBackups,
    [switch]$Force,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) {
    throw 'MyPowerTools macOS uninstallation must run on macOS.'
}

$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
if ([string]::IsNullOrWhiteSpace($ApplicationsRoot)) {
    $ApplicationsRoot = Join-Path $userProfile 'Applications'
}
$ApplicationsRoot = [System.IO.Path]::GetFullPath($ApplicationsRoot)
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $userProfile 'Library/Application Support/MyPowerTools'
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)

function Assert-SafeMyPowerToolsPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$Force
    )

    $full = [System.IO.Path]::GetFullPath($Path)
    if ($full -eq [System.IO.Path]::GetPathRoot($full) -or $full -eq $userProfile) {
        throw "Refusing to remove $full"
    }

    $leaf = Split-Path -Leaf $full
    if (-not $leaf.StartsWith('MyPowerTools', [System.StringComparison]::Ordinal) -and -not $Force) {
        throw "Refusing to remove $full because its leaf name is not a MyPowerTools path. Pass -Force for an explicit custom path."
    }
}

function Remove-Target {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [switch]$Force
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    Assert-SafeMyPowerToolsPath -Path $Path -Force:$Force
    if ($DryRun) {
        Write-Host "Would remove $Path"
        return
    }

    Remove-Item -LiteralPath $Path -Recurse -Force
    Write-Host "Removed $Path"
}

$userId = (& /usr/bin/id '-u').Trim()
if ($LASTEXITCODE -ne 0 -or -not ($userId -match '^\d+$')) {
    throw 'Could not determine the current macOS user id.'
}

# The agents run with KeepAlive, so they have to leave the GUI domain before their plists and
# the bundle they execute from are removed.
$launchAgentsRoot = Join-Path $userProfile 'Library/LaunchAgents'
foreach ($label in @('com.mypowertools.runner', 'com.mypowertools.servicemanager')) {
    if ($DryRun) {
        Write-Host "Would bootout gui/$userId/$label"
    }
    else {
        & /bin/launchctl 'bootout' "gui/$userId/$label" 2>$null
        $global:LASTEXITCODE = 0
    }

    Remove-Target -Path (Join-Path $launchAgentsRoot "$label.plist") -Force
}

# Per-module autostart agents registered through the platform pack share one label prefix.
if (Test-Path -LiteralPath $launchAgentsRoot) {
    foreach ($plist in Get-ChildItem -LiteralPath $launchAgentsRoot -Filter 'com.mypowertools.autostart.*.plist' -File -ErrorAction SilentlyContinue) {
        $label = [System.IO.Path]::GetFileNameWithoutExtension($plist.Name)
        if ($DryRun) {
            Write-Host "Would bootout gui/$userId/$label"
        }
        else {
            & /bin/launchctl 'bootout' "gui/$userId/$label" 2>$null
            $global:LASTEXITCODE = 0
        }

        Remove-Target -Path $plist.FullName -Force
    }
}

# The app and its nested helper bundles each hold a Launch Services record. Removing the
# directory alone leaves com.mypowertools.runner and com.mypowertools.shell in the database,
# which keeps stale mypowertools:// and notification-client entries alive until the next
# system rebuild.
$installedApp = Join-Path $ApplicationsRoot 'MyPowerTools.app'
$lsregister = '/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister'
if ((Test-Path -LiteralPath $installedApp -PathType Container) -and
    (Test-Path -LiteralPath $lsregister -PathType Leaf)) {
    $helpersRoot = Join-Path $installedApp 'Contents/MacOS/Helpers'
    foreach ($bundle in @(
        (Join-Path $helpersRoot 'MyPowerTools Shell.app'),
        (Join-Path $helpersRoot 'MyPowerTools Runner.app'),
        (Join-Path $helpersRoot 'MyPowerTools ServiceManager.app'),
        $installedApp
    )) {
        if (-not (Test-Path -LiteralPath $bundle -PathType Container)) {
            continue
        }
        if ($DryRun) {
            Write-Host "Would unregister $bundle from Launch Services"
            continue
        }

        & $lsregister '-u' $bundle 2>$null
        $global:LASTEXITCODE = 0
    }
}

Remove-Target -Path $installedApp

if ($RemoveBackups -and (Test-Path -LiteralPath $ApplicationsRoot)) {
    foreach ($backup in Get-ChildItem -LiteralPath $ApplicationsRoot -Filter 'MyPowerTools.backup.*.app' -ErrorAction SilentlyContinue) {
        Remove-Target -Path $backup.FullName
    }
}

# The Runner recreates this socket on every start; a stale one blocks the next bind.
$socketRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'mypowertools'
if (Test-Path -LiteralPath $socketRoot) {
    Remove-Target -Path $socketRoot -Force
}

if ($RemoveData) {
    Remove-Target -Path $DataRoot
    Remove-Target -Path (Join-Path $userProfile 'Library/Logs/MyPowerTools')
}
else {
    Write-Host "Kept user data at $DataRoot (pass -RemoveData to delete it)."
}

Write-Host 'MyPowerTools macOS uninstall complete.'
