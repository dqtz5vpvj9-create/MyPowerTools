[CmdletBinding()]
param(
    [string]$SourceApp = '',
    [string]$ApplicationsRoot = '',
    [string]$DataRoot = '',
    [switch]$SkipLaunchAgents
)

$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) {
    throw 'MyPowerTools macOS installation must run on macOS.'
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
if ([string]::IsNullOrWhiteSpace($SourceApp)) {
    $machineArchitecture = (& /usr/bin/uname '-m').Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not determine the macOS machine architecture.'
    }
    $publishArchitecture = if ($machineArchitecture -eq 'arm64') { 'arm64' } else { 'x64' }
    $SourceApp = Join-Path $repoRoot "artifacts/publish/macos-$publishArchitecture/MyPowerTools.app"
}
$SourceApp = [System.IO.Path]::GetFullPath($SourceApp)
if (-not (Test-Path -LiteralPath (Join-Path $SourceApp 'Contents/Info.plist') -PathType Leaf)) {
    throw "Source app bundle is invalid: $SourceApp"
}

if ([string]::IsNullOrWhiteSpace($ApplicationsRoot)) {
    $ApplicationsRoot = Join-Path $userProfile 'Applications'
}
$ApplicationsRoot = [System.IO.Path]::GetFullPath($ApplicationsRoot)
if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $userProfile 'Library/Application Support/MyPowerTools'
}
$DataRoot = [System.IO.Path]::GetFullPath($DataRoot)
$targetApp = Join-Path $ApplicationsRoot 'MyPowerTools.app'
if (-not ([System.IO.Path]::GetFullPath($targetApp)).StartsWith(
        $ApplicationsRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::Ordinal)) {
    throw 'The resolved app target left ApplicationsRoot.'
}

$userId = (& /usr/bin/id '-u').Trim()
if ($LASTEXITCODE -ne 0 -or -not ($userId -match '^\d+$')) {
    throw 'Could not determine the current macOS user id.'
}

New-Item -ItemType Directory -Path $ApplicationsRoot, $DataRoot -Force | Out-Null

# The agents are installed with KeepAlive, so a reinstall has to take them out of the GUI
# domain first. Otherwise a running Runner or ServiceManager keeps executing from the bundle
# that is being replaced, and launchd restarts it against a half-copied tree.
foreach ($label in @('com.mypowertools.runner', 'com.mypowertools.servicemanager')) {
    & /bin/launchctl 'bootout' "gui/$userId/$label" 2>$null
}
$global:LASTEXITCODE = 0

if (Test-Path -LiteralPath $targetApp) {
    $backupApp = Join-Path $ApplicationsRoot ("MyPowerTools.backup.{0}.app" -f (Get-Date -Format 'yyyyMMddHHmmss'))
    Move-Item -LiteralPath $targetApp -Destination $backupApp
    Write-Host "Previous app moved to $backupApp"
}

# ditto, not Copy-Item: the bundle carries code signatures, symlinks and executable bits that
# a plain recursive copy does not preserve, and Gatekeeper rejects what it produces.
& /usr/bin/ditto $SourceApp $targetApp
if ($LASTEXITCODE -ne 0) {
    throw "ditto failed to install the app bundle to $targetApp"
}

$macRoot = Join-Path $targetApp 'Contents/MacOS'
# Background hosts run from nested helper bundles so that NSBundle.mainBundle resolves and the
# processes carry their own identifiers. docs/MACOS_RELEASE.md records the layout contract.
$helpersRoot = Join-Path $macRoot 'Helpers'
$shellExecutable = Join-Path $helpersRoot 'MyPowerTools Shell.app/Contents/MacOS/MyPowerTools.Shell.Avalonia'
$runnerExecutable = Join-Path $helpersRoot 'MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner'
$serviceManagerExecutable = Join-Path $helpersRoot 'MyPowerTools ServiceManager.app/Contents/MacOS/MyPowerTools.ServiceManager'
foreach ($executable in @(
    (Join-Path $macRoot 'MyPowerTools'),
    $shellExecutable,
    $runnerExecutable,
    $serviceManagerExecutable,
    (Join-Path $macRoot 'RemoteNotifications.Service'),
    (Join-Path $macRoot 'modules/android-tools-suite/macos/arm64/MPTAndroidTools.Runtime'),
    (Join-Path $macRoot 'modules/android-tools-suite/macos/x64/MPTAndroidTools.Runtime')
)) {
    if (Test-Path -LiteralPath $executable -PathType Leaf) {
        & /bin/chmod '+x' $executable
        if ($LASTEXITCODE -ne 0) {
            throw "chmod failed for $executable"
        }
    }
}

if (-not $SkipLaunchAgents) {
    $launchAgentsRoot = Join-Path $userProfile 'Library/LaunchAgents'
    $logsRoot = Join-Path $userProfile 'Library/Logs/MyPowerTools'
    New-Item -ItemType Directory -Path $launchAgentsRoot, $logsRoot -Force | Out-Null

    function Write-LaunchAgent {
        param(
            [Parameter(Mandatory = $true)][string]$Label,
            [Parameter(Mandatory = $true)][string[]]$ProgramArguments,
            [Parameter(Mandatory = $true)][string]$WorkingDirectory
        )
        $escapedArguments = $ProgramArguments |
            ForEach-Object { '<string>{0}</string>' -f [Security.SecurityElement]::Escape($_) }
        $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key><string>$Label</string>
  <key>ProgramArguments</key><array>$($escapedArguments -join '')</array>
  <key>WorkingDirectory</key><string>$([Security.SecurityElement]::Escape($WorkingDirectory))</string>
  <key>RunAtLoad</key><true/>
  <key>KeepAlive</key><true/>
  <key>ProcessType</key><string>Background</string>
  <key>StandardOutPath</key><string>$([Security.SecurityElement]::Escape((Join-Path $logsRoot "$Label.log")))</string>
  <key>StandardErrorPath</key><string>$([Security.SecurityElement]::Escape((Join-Path $logsRoot "$Label.error.log")))</string>
</dict>
</plist>
"@
        $plistPath = Join-Path $launchAgentsRoot "$Label.plist"
        [System.IO.File]::WriteAllText($plistPath, $plist)
        & /bin/launchctl 'bootout' "gui/$userId/$Label" 2>$null
        & /bin/launchctl 'bootstrap' "gui/$userId" $plistPath
        if ($LASTEXITCODE -ne 0) {
            throw "launchctl bootstrap failed for $Label"
        }
    }

    # The launchd labels match the helper bundle identifiers, and the agents execute the helper
    # apphosts directly: launching through the compatibility links in Contents/MacOS/<Host>/
    # would leave it to path resolution whether the process ends up with a bundle identity.
    Write-LaunchAgent -Label 'com.mypowertools.servicemanager' -WorkingDirectory $macRoot -ProgramArguments @(
        $serviceManagerExecutable,
        '--data-root', $DataRoot,
        '--deploy-root', (Join-Path $macRoot 'ServiceUnits')
    )
    Write-LaunchAgent -Label 'com.mypowertools.runner' -WorkingDirectory $macRoot -ProgramArguments @(
        $runnerExecutable,
        '--modules', (Join-Path $macRoot 'modules'),
        '--data-root', $DataRoot
    )
}

# ~/Applications is not always scanned eagerly, and the bundle declares the mypowertools://
# scheme that notification activation and the launcher's tool activation both rely on. The
# helper bundles are registered by name as well: a Launch Services record for
# com.mypowertools.runner is what makes UNUserNotificationCenter usable in the Runner.
$lsregister = '/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister'
if (Test-Path -LiteralPath $lsregister -PathType Leaf) {
    foreach ($bundle in @(
        $targetApp,
        (Join-Path $helpersRoot 'MyPowerTools Shell.app'),
        (Join-Path $helpersRoot 'MyPowerTools Runner.app'),
        (Join-Path $helpersRoot 'MyPowerTools ServiceManager.app')
    )) {
        if (-not (Test-Path -LiteralPath $bundle -PathType Container)) {
            continue
        }
        & $lsregister '-f' $bundle
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "lsregister did not register $bundle; notification activation and the mypowertools:// scheme may not resolve until the app is opened from Finder."
        }
        $global:LASTEXITCODE = 0
    }
}

Write-Host $targetApp
