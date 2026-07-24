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

New-Item -ItemType Directory -Path $ApplicationsRoot, $DataRoot -Force | Out-Null
if (Test-Path -LiteralPath $targetApp) {
    $backupApp = Join-Path $ApplicationsRoot ("MyPowerTools.backup.{0}.app" -f (Get-Date -Format 'yyyyMMddHHmmss'))
    Move-Item -LiteralPath $targetApp -Destination $backupApp
    Write-Host "Previous app moved to $backupApp"
}
Copy-Item -LiteralPath $SourceApp -Destination $targetApp -Recurse -Force

$macRoot = Join-Path $targetApp 'Contents/MacOS'
foreach ($executable in @(
    (Join-Path $macRoot 'MyPowerTools'),
    (Join-Path $macRoot 'Shell/MyPowerTools.Shell.Avalonia'),
    (Join-Path $macRoot 'Runner/MyPowerTools.Runner'),
    (Join-Path $macRoot 'ServiceManager/MyPowerTools.ServiceManager'),
    (Join-Path $macRoot 'RemoteNotifications.Service'),
    (Join-Path $macRoot 'modules/android-tools-suite/macos/arm64/powertoold'),
    (Join-Path $macRoot 'modules/android-tools-suite/macos/x64/powertoold')
)) {
    if (Test-Path -LiteralPath $executable -PathType Leaf) {
        & /bin/chmod '+x' $executable
        if ($LASTEXITCODE -ne 0) {
            throw "chmod failed for $executable"
        }
    }
}

if (-not $SkipLaunchAgents) {
    $userId = (& /usr/bin/id '-u').Trim()
    if ($LASTEXITCODE -ne 0 -or -not ($userId -match '^\d+$')) {
        throw 'Could not determine the current macOS user id.'
    }
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

    Write-LaunchAgent -Label 'com.mypowertools.servicemanager' -WorkingDirectory $macRoot -ProgramArguments @(
        (Join-Path $macRoot 'ServiceManager/MyPowerTools.ServiceManager'),
        '--data-root', $DataRoot,
        '--deploy-root', (Join-Path $macRoot 'ServiceUnits')
    )
    Write-LaunchAgent -Label 'com.mypowertools.runner' -WorkingDirectory $macRoot -ProgramArguments @(
        (Join-Path $macRoot 'Runner/MyPowerTools.Runner'),
        '--modules', (Join-Path $macRoot 'modules'),
        '--data-root', $DataRoot
    )
}

Write-Host $targetApp
