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

function Invoke-MacOSInstallCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [switch]$AllowFailure
    )
    # An absent launchd job is expected. Keep native exit handling independent of the
    # caller's PowerShell profile, including PSNativeCommandUseErrorActionPreference.
    $PSNativeCommandUseErrorActionPreference = $false
    $output = @(& $FilePath @ArgumentList 2>&1 | ForEach-Object { [string]$_ })
    $exitCode = $LASTEXITCODE
    $global:LASTEXITCODE = 0
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "$FilePath $($ArgumentList -join ' ') failed with exit code ${exitCode}: $($output -join '; ')"
    }
    return [pscustomobject]@{ ExitCode = $exitCode; Output = ($output -join "`n") }
}

function Get-MacOSPhysicalDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    Push-Location -LiteralPath $Path
    try {
        return (Invoke-MacOSInstallCommand '/bin/pwd' @('-P')).Output.TrimEnd("`r", "`n")
    }
    finally { Pop-Location }
}

function Get-MacOSPhysicalDestination {
    param([Parameter(Mandatory = $true)][string]$Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    $suffix = [Collections.Generic.List[string]]::new()
    while (-not (Test-Path -LiteralPath $cursor -PathType Container)) {
        if (Test-Path -LiteralPath $cursor) { throw "Destination ancestor is not a directory: $cursor" }
        $suffix.Insert(0, (Split-Path -Leaf $cursor))
        $parent = Split-Path -Parent $cursor
        if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $cursor) {
            throw "Could not resolve destination: $Path"
        }
        $cursor = $parent
    }
    $resolved = Get-MacOSPhysicalDirectory $cursor
    foreach ($part in $suffix) { $resolved = Join-Path $resolved $part }
    return $resolved
}

function Select-AppBundleProcessIds {
    param(
        [Parameter(Mandatory = $true)][string]$AppBundlePath,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$ProcessLines,
        [int]$ExcludedProcessId = $PID
    )
    $prefix = [IO.Path]::GetFullPath($AppBundlePath).TrimEnd('/') + '/'
    $ids = [Collections.Generic.HashSet[int]]::new()
    foreach ($line in $ProcessLines) {
        if ($line -notmatch '^\s*(\d+)\s+(.+)$') { continue }
        $processId = [int]$Matches[1]
        # Input is ps comm (executable), never args/command. A path in an editor,
        # updater, terminal or diagnostic command's arguments does not confer ownership.
        $executable = $Matches[2]
        if ($processId -gt 1 -and $processId -ne $ExcludedProcessId -and
            $executable.StartsWith($prefix, [StringComparison]::Ordinal)) {
            [void]$ids.Add($processId)
        }
    }
    return @($ids | Sort-Object)
}

function Get-AppBundleProcessIds {
    param([Parameter(Mandatory = $true)][string]$AppBundlePath)
    $listing = Invoke-MacOSInstallCommand '/bin/ps' @('-ww', '-u', $userId, '-o', 'pid=,comm=')
    return @(Select-AppBundleProcessIds -AppBundlePath $AppBundlePath -ProcessLines ($listing.Output -split "`n"))
}

function Stop-AppBundleProcesses {
    param([Parameter(Mandatory = $true)][string]$AppBundlePath)
    $processIds = @(Get-AppBundleProcessIds -AppBundlePath $AppBundlePath)
    if ($processIds.Count -eq 0) { return }
    Write-Host "Stopping $($processIds.Count) process(es) running from $AppBundlePath"
    foreach ($signal in @('-TERM', '-KILL')) {
        # Re-enumerate before escalation; do not retain stale PIDs from the TERM pass.
        foreach ($processId in @(Get-AppBundleProcessIds -AppBundlePath $AppBundlePath)) {
            [void](Invoke-MacOSInstallCommand '/bin/kill' @($signal, [string]$processId) -AllowFailure)
        }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 100
            $remaining = @(Get-AppBundleProcessIds -AppBundlePath $AppBundlePath)
        } while ($remaining.Count -gt 0 -and [DateTimeOffset]::UtcNow -lt $deadline)
        if ($remaining.Count -eq 0) { return }
    }
    throw "Could not stop process(es) running from the existing app bundle: $($remaining -join ', ')"
}

function Assert-MacOSInstallBundle {
    param([Parameter(Mandatory = $true)][string]$AppBundlePath)
    $plist = Join-Path $AppBundlePath 'Contents/Info.plist'
    [void](Invoke-MacOSInstallCommand '/usr/bin/plutil' @('-lint', $plist))
    $identifier = (Invoke-MacOSInstallCommand '/usr/bin/plutil' @('-extract', 'CFBundleIdentifier', 'raw', '-o', '-', $plist)).Output.Trim()
    if ($identifier -ne 'com.mypowertools.desktop') {
        throw "Unexpected application bundle identifier: $identifier"
    }
    $version = (Invoke-MacOSInstallCommand '/usr/bin/plutil' @('-extract', 'CFBundleShortVersionString', 'raw', '-o', '-', $plist)).Output.Trim()
    if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "Invalid CFBundleShortVersionString: $version"
    }
    foreach ($relative in @(
        'Contents/MacOS/MyPowerTools',
        'Contents/MacOS/Helpers/MyPowerTools Shell.app/Contents/MacOS/MyPowerTools.Shell.Avalonia',
        'Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner',
        'Contents/MacOS/Helpers/MyPowerTools ServiceManager.app/Contents/MacOS/MyPowerTools.ServiceManager',
        'Contents/MacOS/Helpers/MyPowerTools Remote Notifications.app/Contents/MacOS/RemoteNotifications.Service'
    )) {
        $executable = Join-Path $AppBundlePath $relative
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Required macOS executable is missing: $executable"
        }
        [void](Invoke-MacOSInstallCommand '/bin/test' @('-x', $executable))
    }
    foreach ($relative in @('Contents/MacOS/modules', 'Contents/MacOS/ServiceUnits')) {
        if (-not (Test-Path -LiteralPath (Join-Path $AppBundlePath $relative) -PathType Container)) {
            throw "Required macOS payload directory is missing: $relative"
        }
    }
    # Verify the copied tree before stopping anything. Do not repair a damaged signed
    # payload with chmod or modify its contents after signature verification.
    [void](Invoke-MacOSInstallCommand '/usr/bin/codesign' @('--verify', '--deep', '--strict', $AppBundlePath))
}

function Invoke-MacOSBundleSwap {
    param(
        [Parameter(Mandatory = $true)][string]$StagedApp,
        [Parameter(Mandatory = $true)][string]$TargetApp,
        [Parameter(Mandatory = $true)][string]$BackupApp,
        [scriptblock]$Prepare = {},
        [scriptblock]$Activate = {},
        [scriptblock]$Deactivate = {},
        [scriptblock]$Restore = {}
    )
    if (-not (Test-Path -LiteralPath $StagedApp -PathType Container)) {
        throw "Staged application does not exist: $StagedApp"
    }
    if (Test-Path -LiteralPath $BackupApp) { throw "Backup already exists: $BackupApp" }
    $oldMoved = $false
    $newMoved = $false
    $activationStarted = $false
    try {
        & $Prepare
        if (Test-Path -LiteralPath $TargetApp) {
            Move-Item -LiteralPath $TargetApp -Destination $BackupApp -ErrorAction Stop
            $oldMoved = $true
        }
        Move-Item -LiteralPath $StagedApp -Destination $TargetApp -ErrorAction Stop
        $newMoved = $true
        $activationStarted = $true
        & $Activate
    }
    catch {
        $installFailure = $_
        try {
            # A failed bootstrap can still leave workers alive. Stop them before moving
            # the new tree away, then restore the old tree before restarting old agents.
            if ($activationStarted) { & $Deactivate }
            if ($newMoved) {
                Move-Item -LiteralPath $TargetApp -Destination $StagedApp -ErrorAction Stop
            }
            if ($oldMoved) {
                Move-Item -LiteralPath $BackupApp -Destination $TargetApp -ErrorAction Stop
            }
            & $Restore
        }
        catch {
            $failure = [InvalidOperationException]::new(
                "Installation failed: $($installFailure.Exception.Message). Rollback incomplete: $($_.Exception.Message). Recovery paths: backup=$BackupApp; staged=$StagedApp; target=$TargetApp",
                $installFailure.Exception)
            $failure.Data['MptRollbackIncomplete'] = $true
            throw $failure
        }
        throw $installFailure
    }
}

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
    $temporaryPlist = "$plistPath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporaryPlist, $plist, [Text.UTF8Encoding]::new($false))
        [void](Invoke-MacOSInstallCommand '/usr/bin/plutil' @('-lint', $temporaryPlist))
        [IO.File]::Move($temporaryPlist, $plistPath, $true)
        [void](Invoke-MacOSInstallCommand '/bin/launchctl' @('bootstrap', "gui/$userId", $plistPath))
    }
    finally { Remove-Item -LiteralPath $temporaryPlist -Force -ErrorAction SilentlyContinue }
}

function Register-AppBundles {
    $lsregister = '/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister'
    if (-not (Test-Path -LiteralPath $lsregister -PathType Leaf)) { return }
    foreach ($bundle in @($targetApp) + @(Get-ChildItem -LiteralPath $helpersRoot -Directory -Filter '*.app' -ErrorAction SilentlyContinue | ForEach-Object FullName)) {
        if (-not (Test-Path -LiteralPath $bundle -PathType Container)) { continue }
        $result = Invoke-MacOSInstallCommand $lsregister @('-f', $bundle) -AllowFailure
        if ($result.ExitCode -ne 0) {
            Write-Warning "lsregister did not register ${bundle}: $($result.Output)"
        }
    }
}

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$userProfile = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
$userId = (Invoke-MacOSInstallCommand '/usr/bin/id' @('-u')).Output.Trim()
if ($userId -notmatch '^\d+$' -or $userId -eq '0') {
    throw 'Run the macOS user installer as the signed-in user, without sudo.'
}
if ([string]::IsNullOrWhiteSpace($SourceApp)) {
    $machineArchitecture = (Invoke-MacOSInstallCommand '/usr/bin/uname' @('-m')).Output.Trim()
    $publishArchitecture = if ($machineArchitecture -eq 'arm64') { 'arm64' } else { 'x64' }
    $SourceApp = Join-Path $repoRoot "artifacts/publish/macos-$publishArchitecture/MyPowerTools.app"
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceApp 'Contents/Info.plist') -PathType Leaf)) {
    throw "Source app bundle is invalid: $SourceApp"
}
$SourceApp = Get-MacOSPhysicalDirectory $SourceApp
if ([string]::IsNullOrWhiteSpace($ApplicationsRoot)) { $ApplicationsRoot = Join-Path $userProfile 'Applications' }
if ([string]::IsNullOrWhiteSpace($DataRoot)) { $DataRoot = Join-Path $userProfile 'Library/Application Support/MyPowerTools' }
$ApplicationsRoot = Get-MacOSPhysicalDestination $ApplicationsRoot
$DataRoot = Get-MacOSPhysicalDestination $DataRoot
$targetApp = Join-Path $ApplicationsRoot 'MyPowerTools.app'
if ($ApplicationsRoot.Equals($SourceApp, [StringComparison]::OrdinalIgnoreCase) -or
    $ApplicationsRoot.StartsWith($SourceApp.TrimEnd('/') + '/', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ApplicationsRoot cannot be inside the source bundle.'
}
if ($DataRoot.Equals($targetApp, [StringComparison]::OrdinalIgnoreCase) -or
    $DataRoot.StartsWith($targetApp + '/', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'DataRoot cannot be inside the application bundle.'
}
foreach ($protectedPath in @($targetApp, (Join-Path $ApplicationsRoot '.mypowertools-install.lock'))) {
    $item = Get-Item -LiteralPath $protectedPath -Force -ErrorAction SilentlyContinue
    if ($null -ne $item -and $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace a symbolic link: $protectedPath"
    }
}
if ((Test-Path -LiteralPath $targetApp) -and -not (Test-Path -LiteralPath $targetApp -PathType Container)) {
    throw "The application destination is not a directory: $targetApp"
}

New-Item -ItemType Directory -Path $ApplicationsRoot -Force | Out-Null
$lockPath = Join-Path $ApplicationsRoot '.mypowertools-install.lock'
try {
    $installLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}
catch { throw "Cannot acquire macOS installation lock at ${lockPath}: $($_.Exception.Message)" }
# Keep the lock inode after closing it. Unlinking a lock permits two later installers
# to lock different inodes under the same name. Only the open handle represents ownership.
$stageRoot = Join-Path $ApplicationsRoot ('.mypowertools-install.' + [Guid]::NewGuid().ToString('N'))
$stageApp = Join-Path $stageRoot 'MyPowerTools.app'
$backupApp = Join-Path $ApplicationsRoot ('MyPowerTools.backup.' + (Get-Date -Format 'yyyyMMddHHmmssfff') + '.' + [Guid]::NewGuid().ToString('N') + '.app')
$preserveStage = $false
try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    [void](Invoke-MacOSInstallCommand '/bin/chmod' @('700', $stageRoot))
    [void](Invoke-MacOSInstallCommand '/bin/chmod' @('600', $lockPath))
    # Staging is a sibling on the destination filesystem. Copy/validation errors never
    # rename the current app; installing from the current app itself is also safe.
    [void](Invoke-MacOSInstallCommand '/usr/bin/ditto' @($SourceApp, $stageApp))
    Assert-MacOSInstallBundle $stageApp
    New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null

    $macRoot = Join-Path $targetApp 'Contents/MacOS'
    $helpersRoot = Join-Path $macRoot 'Helpers'
    $runnerExecutable = Join-Path $helpersRoot 'MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner'
    $serviceManagerExecutable = Join-Path $helpersRoot 'MyPowerTools ServiceManager.app/Contents/MacOS/MyPowerTools.ServiceManager'
    $launchAgentsRoot = Join-Path $userProfile 'Library/LaunchAgents'
    $logsRoot = Join-Path $userProfile 'Library/Logs/MyPowerTools'
    $launchAgentStates = @()
    if (-not $SkipLaunchAgents) {
        New-Item -ItemType Directory -Path $launchAgentsRoot, $logsRoot -Force | Out-Null
        foreach ($label in @('com.mypowertools.servicemanager', 'com.mypowertools.runner')) {
            $plistPath = Join-Path $launchAgentsRoot "$label.plist"
            $plistItem = Get-Item -LiteralPath $plistPath -Force -ErrorAction SilentlyContinue
            if ($null -ne $plistItem -and $plistItem.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
                throw "Refusing to replace a symbolic-link LaunchAgent: $plistPath"
            }
            $exists = Test-Path -LiteralPath $plistPath -PathType Leaf
            $loaded = (Invoke-MacOSInstallCommand '/bin/launchctl' @('print', "gui/$userId/$label") -AllowFailure).ExitCode -eq 0
            if ($loaded -and -not $exists) { throw "Cannot preserve loaded LaunchAgent without its plist: $label" }
            if ($exists) {
                $program = (Invoke-MacOSInstallCommand '/usr/bin/plutil' @('-extract', 'ProgramArguments.0', 'raw', '-o', '-', $plistPath)).Output.Trim()
                if (-not $program.StartsWith($targetApp + '/', [StringComparison]::Ordinal)) {
                    throw "LaunchAgent $label belongs to another installation at $program. Use -SkipLaunchAgents for an isolated installation."
                }
            }
            $launchAgentStates += [pscustomobject]@{
                Label = $label; Path = $plistPath; Existed = $exists; Loaded = $loaded
                Bytes = $(if ($exists) { [IO.File]::ReadAllBytes($plistPath) } else { $null })
            }
        }
    }

    Invoke-MacOSBundleSwap -StagedApp $stageApp -TargetApp $targetApp -BackupApp $backupApp -Prepare {
        foreach ($state in $launchAgentStates) {
            if ($state.Loaded) {
                [void](Invoke-MacOSInstallCommand '/bin/launchctl' @('bootout', "gui/$userId/$($state.Label)"))
            }
        }
        Stop-AppBundleProcesses $targetApp
    } -Activate {
        # Register identities before starting UserNotifications consumers.
        Register-AppBundles
        if (-not $SkipLaunchAgents) {
            Write-LaunchAgent 'com.mypowertools.servicemanager' @(
                $serviceManagerExecutable, '--data-root', $DataRoot,
                '--deploy-root', (Join-Path $macRoot 'ServiceUnits')) $macRoot
            Write-LaunchAgent 'com.mypowertools.runner' @(
                $runnerExecutable, '--modules', (Join-Path $macRoot 'modules'), '--data-root', $DataRoot) $macRoot
        }
    } -Deactivate {
        foreach ($state in $launchAgentStates) {
            $loaded = Invoke-MacOSInstallCommand '/bin/launchctl' @('print', "gui/$userId/$($state.Label)") -AllowFailure
            if ($loaded.ExitCode -eq 0) {
                [void](Invoke-MacOSInstallCommand '/bin/launchctl' @('bootout', "gui/$userId/$($state.Label)"))
            }
        }
        Stop-AppBundleProcesses $targetApp
    } -Restore {
        foreach ($state in $launchAgentStates) {
            if ($state.Existed) { [IO.File]::WriteAllBytes($state.Path, [byte[]]$state.Bytes) }
            else { Remove-Item -LiteralPath $state.Path -Force -ErrorAction SilentlyContinue }
        }
        Register-AppBundles
        foreach ($state in $launchAgentStates) {
            if ($state.Loaded) {
                $loaded = Invoke-MacOSInstallCommand '/bin/launchctl' @('print', "gui/$userId/$($state.Label)") -AllowFailure
                if ($loaded.ExitCode -ne 0) {
                    [void](Invoke-MacOSInstallCommand '/bin/launchctl' @('bootstrap', "gui/$userId", $state.Path))
                }
            }
        }
    }
    if (Test-Path -LiteralPath $backupApp) { Write-Host "Previous app retained at $backupApp" }
    Write-Host $targetApp
}
catch {
    $preserveStage = $_.Exception.Data.Contains('MptRollbackIncomplete')
    throw
}
finally {
    if (-not $preserveStage) { Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue }
    $installLock.Dispose()
}
