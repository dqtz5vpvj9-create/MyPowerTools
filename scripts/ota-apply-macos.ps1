<#
.SYNOPSIS
    Applies a full macOS OTA package: verify, stage, swap the app bundle, relaunch, health check,
    and roll back to the timestamped backup when anything fails.

.DESCRIPTION
    This is the macOS counterpart of the Windows `Invoke-FullApply` path in ota-update.ps1.
    macOS ships full bundles only, so there is no delta transaction here.

    The bundle swap deliberately delegates to the *new* package's own install-macos.ps1 instead of
    copying files itself. The installer is the one place that knows the layout of the version it
    shipped with, so an update that moves a host into a nested helper .app still writes LaunchAgent
    plists that point at the right executables. This script owns everything the installer does not:
    package verification, taking the previous bundle aside as a restorable backup, stopping the
    processes that run out of the bundle, the post-update health check, and the rollback.

    Maintenance mode on macOS is `launchctl bootout` of the two user agents. There is no registry
    and no scheduled task; the agents are KeepAlive, so leaving them loaded would let launchd
    restart Runner against a half-replaced bundle.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedPackageSha256,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$AppBundlePath,
    [Parameter(Mandatory = $true)][string]$DataRoot,
    [Parameter(Mandatory = $true)][string]$StateRoot,
    [int]$HealthTimeoutSeconds = 90,
    [int]$KeepBackupCount = 2,
    [switch]$KeepBackup,
    [switch]$NoRelaunch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (-not $IsMacOS) {
    throw 'ota-apply-macos.ps1 runs on macOS only.'
}

$agentLabels = @('com.mypowertools.runner', 'com.mypowertools.servicemanager')

function Invoke-Quiet {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList 2>&1 | Out-Null
    return $LASTEXITCODE
}

function Invoke-Required {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Activity
    )

    & $FilePath @ArgumentList | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "$Activity failed with exit code $LASTEXITCODE"
    }
}

function Get-CurrentUserId {
    $value = (& /usr/bin/id '-u').Trim()
    if ($LASTEXITCODE -ne 0 -or -not ($value -match '^\d+$')) {
        throw 'Could not determine the current macOS user id.'
    }
    return $value
}

function Read-BundleShortVersion {
    param([Parameter(Mandatory = $true)][string]$BundlePath)

    $plist = Join-Path $BundlePath 'Contents/Info.plist'
    if (-not (Test-Path -LiteralPath $plist -PathType Leaf)) {
        throw "App bundle has no Contents/Info.plist: $BundlePath"
    }
    $value = (& /usr/bin/defaults 'read' (Join-Path $BundlePath 'Contents/Info') 'CFBundleShortVersionString').Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) {
        throw "Could not read CFBundleShortVersionString from $plist"
    }
    return $value
}

function Get-BundleProcesses {
    param([Parameter(Mandatory = $true)][string]$BundlePath)

    # ps -o comm= prints the full executable path on macOS, which is what identifies a process as
    # "running out of the bundle". Process.MainModule is not implemented on macOS, so Get-Process
    # cannot answer this question.
    $prefix = $BundlePath.TrimEnd('/') + '/'
    $records = [Collections.Generic.List[object]]::new()
    foreach ($line in @(& /bin/ps '-A' '-o' 'pid=,comm=')) {
        if ($line -notmatch '^\s*(\d+)\s+(.+)$') {
            continue
        }
        $processId = [int]$Matches[1]
        $executable = $Matches[2].Trim()
        if ($processId -eq $PID -or -not $executable.StartsWith($prefix, [StringComparison]::Ordinal)) {
            continue
        }
        $records.Add([pscustomobject]@{
            processId = $processId
            path = $executable
        })
    }
    return $records
}

function Stop-BundleProcesses {
    param([Parameter(Mandatory = $true)][string]$BundlePath)

    $stopped = @(Get-BundleProcesses -BundlePath $BundlePath)
    foreach ($record in $stopped) {
        [void](Invoke-Quiet -FilePath '/bin/kill' -ArgumentList @('-TERM', [string]$record.processId))
    }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-BundleProcesses -BundlePath $BundlePath).Count -eq 0) {
            break
        }
        Start-Sleep -Milliseconds 250
    }
    foreach ($record in @(Get-BundleProcesses -BundlePath $BundlePath)) {
        [void](Invoke-Quiet -FilePath '/bin/kill' -ArgumentList @('-KILL', [string]$record.processId))
    }
    return $stopped
}

function Get-LoadedAgents {
    param([Parameter(Mandatory = $true)][string]$UserId)

    $loaded = [Collections.Generic.List[string]]::new()
    foreach ($label in $agentLabels) {
        if ((Invoke-Quiet -FilePath '/bin/launchctl' -ArgumentList @('print', "gui/$UserId/$label")) -eq 0) {
            $loaded.Add($label)
        }
    }
    $global:LASTEXITCODE = 0
    return $loaded
}

function Stop-Agents {
    param([Parameter(Mandatory = $true)][string]$UserId)

    foreach ($label in $agentLabels) {
        [void](Invoke-Quiet -FilePath '/bin/launchctl' -ArgumentList @('bootout', "gui/$UserId/$label"))
    }
    $global:LASTEXITCODE = 0
}

function Start-Agents {
    param(
        [Parameter(Mandatory = $true)][string]$UserId,
        [Parameter(Mandatory = $true)][string]$LaunchAgentsRoot,
        [Parameter(Mandatory = $true)][string[]]$Labels
    )

    foreach ($label in $Labels) {
        $plist = Join-Path $LaunchAgentsRoot "$label.plist"
        if (-not (Test-Path -LiteralPath $plist -PathType Leaf)) {
            continue
        }
        [void](Invoke-Quiet -FilePath '/bin/launchctl' -ArgumentList @('bootout', "gui/$UserId/$label"))
        [void](Invoke-Quiet -FilePath '/bin/launchctl' -ArgumentList @('bootstrap', "gui/$UserId", $plist))
        [void](Invoke-Quiet -FilePath '/bin/launchctl' -ArgumentList @('kickstart', '-k', "gui/$UserId/$label"))
    }
    $global:LASTEXITCODE = 0
}

function Test-RunnerEndpoint {
    param([Parameter(Mandatory = $true)][int]$TimeoutSeconds)

    # IpcEndpoint.RunnerDefault: a Unix domain socket named after the HostControl endpoint under
    # the per-user temporary directory that Path.GetTempPath() resolves to.
    $socketPath = Join-Path ([IO.Path]::GetTempPath()) 'mypowertools.runner.hostcontrol.sock'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastError = ''
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $socketPath) {
            $socket = [Net.Sockets.Socket]::new(
                [Net.Sockets.AddressFamily]::Unix,
                [Net.Sockets.SocketType]::Stream,
                [Net.Sockets.ProtocolType]::Unspecified)
            try {
                $socket.Connect([Net.Sockets.UnixDomainSocketEndPoint]::new($socketPath))
                return [pscustomobject]@{
                    ok = $true
                    endpoint = $socketPath
                    detail = 'connected'
                }
            }
            catch {
                $lastError = $_.Exception.Message
            }
            finally {
                $socket.Dispose()
            }
        }
        else {
            $lastError = 'socket file has not appeared'
        }
        Start-Sleep -Milliseconds 500
    }

    return [pscustomobject]@{
        ok = $false
        endpoint = $socketPath
        detail = $lastError
    }
}

function Remove-OldBackups {
    param(
        [Parameter(Mandatory = $true)][string]$ApplicationsRoot,
        [Parameter(Mandatory = $true)][int]$Keep
    )

    $backups = @(
        Get-ChildItem -LiteralPath $ApplicationsRoot -Directory -Filter 'MyPowerTools.backup.*.app' -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending
    )
    for ($index = $Keep; $index -lt $backups.Count; $index++) {
        Remove-Item -LiteralPath $backups[$index].FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$packageFull = [IO.Path]::GetFullPath($PackagePath)
$appFull = [IO.Path]::GetFullPath($AppBundlePath).TrimEnd('/')
$dataRootFull = [IO.Path]::GetFullPath($DataRoot)
$stateRootFull = [IO.Path]::GetFullPath($StateRoot)
$applicationsRoot = Split-Path -Parent $appFull
if ([string]::IsNullOrWhiteSpace($applicationsRoot)) {
    throw "Could not resolve the parent directory of $appFull"
}
if (-not (Test-Path -LiteralPath $packageFull -PathType Leaf)) {
    throw "OTA package does not exist: $packageFull"
}
New-Item -ItemType Directory -Path $stateRootFull, $applicationsRoot -Force | Out-Null

$actualPackageHash = (Get-FileHash -LiteralPath $packageFull -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualPackageHash -ne $ExpectedPackageSha256.ToLowerInvariant()) {
    throw "OTA package SHA-256 mismatch. expected=$ExpectedPackageSha256 actual=$actualPackageHash"
}

$userId = Get-CurrentUserId
$launchAgentsRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) 'Library/LaunchAgents'
$transactionId = [Guid]::NewGuid().ToString('N')
$transactionRoot = Join-Path $stateRootFull "transactions/$transactionId"
$stageRoot = Join-Path $transactionRoot 'staged'
$plistBackupRoot = Join-Path $transactionRoot 'launch-agents'
New-Item -ItemType Directory -Path $stageRoot, $plistBackupRoot -Force | Out-Null

# ditto, not [IO.Compression.ZipFile]: publish-macos.ps1 creates the package with `ditto -c -k`,
# which stores symlinks, extended attributes and the executable bit. Extracting with the managed
# zip reader turns symlinks into text files and drops the mode bits, producing a bundle whose code
# signature no longer verifies and which Gatekeeper refuses to launch.
Invoke-Required -FilePath '/usr/bin/ditto' -ArgumentList @('-x', '-k', $packageFull, $stageRoot) -Activity 'ditto extract OTA package'
$stagedApp = @(Get-ChildItem -LiteralPath $stageRoot -Directory -Filter '*.app') | Select-Object -First 1
if ($null -eq $stagedApp) {
    throw "OTA package does not contain an .app bundle: $packageFull"
}
$stagedAppPath = $stagedApp.FullName

$stagedVersion = Read-BundleShortVersion -BundlePath $stagedAppPath
if ($stagedVersion -ne $ExpectedVersion) {
    throw "OTA package contains version $stagedVersion but the feed promised $ExpectedVersion."
}

# A bundle whose signature does not verify will be killed by Gatekeeper on launch, and by then the
# old bundle is already gone. Refusing here keeps the failure inside the staging directory.
Invoke-Required -FilePath '/usr/bin/codesign' -ArgumentList @('--verify', '--deep', '--strict', $stagedAppPath) -Activity 'codesign verification of the staged bundle'

$stagedInstaller = Join-Path $stagedAppPath 'Contents/Resources/scripts/install-macos.ps1'
if (-not (Test-Path -LiteralPath $stagedInstaller -PathType Leaf)) {
    throw "OTA package does not ship Contents/Resources/scripts/install-macos.ps1: $stagedAppPath"
}

$loadedAgents = @(Get-LoadedAgents -UserId $userId)
foreach ($label in $agentLabels) {
    $plist = Join-Path $launchAgentsRoot "$label.plist"
    if (Test-Path -LiteralPath $plist -PathType Leaf) {
        Copy-Item -LiteralPath $plist -Destination (Join-Path $plistBackupRoot "$label.plist") -Force
    }
}

$maintenanceState = [ordered]@{
    schemaVersion = 1
    transactionId = $transactionId
    loadedAgents = $loadedAgents
    appBundlePath = $appFull
    enteredAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$maintenanceFile = Join-Path $stateRootFull 'maintenance-mode.macos.json'
[IO.File]::WriteAllText($maintenanceFile, ($maintenanceState | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

$backupApp = ''
$installed = $false
$stoppedProcesses = @()
try {
    Stop-Agents -UserId $userId
    $stoppedProcesses = @(Stop-BundleProcesses -BundlePath $appFull)

    if (Test-Path -LiteralPath $appFull -PathType Container) {
        $backupApp = Join-Path $applicationsRoot ("MyPowerTools.backup.{0}.app" -f (Get-Date -Format 'yyyyMMddHHmmss'))
        if (Test-Path -LiteralPath $backupApp) {
            Remove-Item -LiteralPath $backupApp -Recurse -Force
        }
        Move-Item -LiteralPath $appFull -Destination $backupApp
    }

    # The new package installs itself: ditto into place, chmod the executables, write and bootstrap
    # the LaunchAgents for its own layout, and register the bundle with Launch Services.
    # -SkipOtaState: on the OTA path ota-update.ps1 owns installed-release.json and writes the
    # downloaded release manifest, so the installer must not regenerate either.
    & $stagedInstaller `
        -SourceApp $stagedAppPath `
        -ApplicationsRoot $applicationsRoot `
        -DataRoot $dataRootFull `
        -SkipOtaState | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $appFull 'Contents/Info.plist') -PathType Leaf)) {
        throw "The staged installer did not produce an app bundle at $appFull"
    }
    $installed = $true

    if (-not $NoRelaunch) {
        [void](Invoke-Quiet -FilePath '/usr/bin/open' -ArgumentList @('-a', $appFull))
    }

    $health = Test-RunnerEndpoint -TimeoutSeconds $HealthTimeoutSeconds
    if (-not [bool]$health.ok) {
        throw "The Runner did not answer on $($health.endpoint) within $HealthTimeoutSeconds seconds ($($health.detail))."
    }

    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    if (-not $KeepBackup) {
        Remove-OldBackups -ApplicationsRoot $applicationsRoot -Keep $KeepBackupCount
    }
    Remove-Item -LiteralPath $transactionRoot -Recurse -Force -ErrorAction SilentlyContinue

    [ordered]@{
        success = $true
        transactionId = $transactionId
        packageSha256 = $actualPackageHash
        targetVersion = $ExpectedVersion
        appBundlePath = $appFull
        backupPath = $backupApp
        stoppedProcesses = @($stoppedProcesses)
        restartedAgents = $loadedAgents
        relaunched = (-not $NoRelaunch.IsPresent)
        health = $health
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json -Depth 6
}
catch {
    $applyError = $_
    $rolledBack = $false
    $rollbackError = ''
    if (-not [string]::IsNullOrWhiteSpace($backupApp) -and (Test-Path -LiteralPath $backupApp -PathType Container)) {
        try {
            Stop-Agents -UserId $userId
            if ($installed) {
                [void](Stop-BundleProcesses -BundlePath $appFull)
            }
            if (Test-Path -LiteralPath $appFull -PathType Container) {
                Remove-Item -LiteralPath $appFull -Recurse -Force
            }
            Move-Item -LiteralPath $backupApp -Destination $appFull

            # The staged installer may already have rewritten the plists for the new layout, whose
            # executables no longer exist after the restore. Put the previous ones back.
            foreach ($label in $agentLabels) {
                $saved = Join-Path $plistBackupRoot "$label.plist"
                if (Test-Path -LiteralPath $saved -PathType Leaf) {
                    Copy-Item -LiteralPath $saved -Destination (Join-Path $launchAgentsRoot "$label.plist") -Force
                }
            }

            $lsregister = '/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister'
            if (Test-Path -LiteralPath $lsregister -PathType Leaf) {
                [void](Invoke-Quiet -FilePath $lsregister -ArgumentList @('-f', $appFull))
            }
            Start-Agents -UserId $userId -LaunchAgentsRoot $launchAgentsRoot -Labels $loadedAgents
            if (-not $NoRelaunch) {
                [void](Invoke-Quiet -FilePath '/usr/bin/open' -ArgumentList @('-a', $appFull))
            }
            $rolledBack = $true
        }
        catch {
            $rollbackError = [string]$_.Exception.Message
        }
    }

    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    if ($rolledBack) {
        Remove-Item -LiteralPath $transactionRoot -Recurse -Force -ErrorAction SilentlyContinue
        throw "macOS OTA apply failed and the previous bundle was restored: $($applyError.Exception.Message)"
    }

    # Keep the transaction root: it holds the saved LaunchAgent plists and the staged bundle that a
    # manual recovery needs.
    $marker = Join-Path $transactionRoot 'ROLLBACK-FAILED.txt'
    [IO.File]::WriteAllText(
        $marker,
        "macOS OTA apply failed at $([DateTimeOffset]::UtcNow.ToString('O')).`nbackup=$backupApp`nerror=$($applyError.Exception.Message)`nrollback=$rollbackError`n",
        [Text.UTF8Encoding]::new($false))
    throw $applyError
}
