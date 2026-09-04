<#
.SYNOPSIS
  Verifies the installed Remote Notifications production path on macOS.

.DESCRIPTION
  Starts the published ServiceManager helper, the published Remote Notifications
  helper and an isolated signed HTTP feed. The gate validates helper identity,
  native UserNotifications availability, Unix-socket security, signed polling,
  shared persistence, exact-message notification activation, duplicate suppression,
  single ownership, ServiceManager re-adoption and worker crash recovery.
#>
[CmdletBinding()]
param(
    [string]$AppBundle = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsMacOS) { throw 'This verification gate requires macOS.' }

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$machineArchitecture = (& /usr/bin/uname -m).Trim()
$architecture = if ($machineArchitecture -eq 'arm64') { 'arm64' } else { 'x64' }
$runtimeIdentifier = "osx-$architecture"
if ([string]::IsNullOrWhiteSpace($AppBundle)) {
    $AppBundle = Join-Path $repoRoot "artifacts/publish/macos-$architecture/MyPowerTools.app"
}
$AppBundle = [IO.Path]::GetFullPath($AppBundle)

$macRoot = Join-Path $AppBundle 'Contents/MacOS'
$remoteHelper = Join-Path $macRoot 'Helpers/MyPowerTools Remote Notifications.app'
$remoteHelperRoot = Join-Path $remoteHelper 'Contents/MacOS'
$workerExecutable = Join-Path $remoteHelperRoot 'RemoteNotifications.Service'
$nativeLibrary = Join-Path $remoteHelperRoot 'libMptMacNative.dylib'
$remoteHelperPlist = Join-Path $remoteHelper 'Contents/Info.plist'
$managerHelper = Join-Path $macRoot 'Helpers/MyPowerTools ServiceManager.app'
$managerExecutable = Join-Path $managerHelper 'Contents/MacOS/MyPowerTools.ServiceManager'
$shippedManifestPath = Join-Path $macRoot 'ServiceUnits/units/remote-notifications.service.json'

$runId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "mpt-rn-macos-$runId"
$evidenceRoot = Join-Path $repoRoot "artifacts/remote-notifications-macos/$runId"
$dataRoot = Join-Path $tempRoot 'data'
$deployRoot = Join-Path $tempRoot 'deploy'
$unitsRoot = Join-Path $deployRoot 'units'
$toolDataRoot = Join-Path $dataRoot 'state/tools/remote-notifications'
$workerSocket = Join-Path $tempRoot 'remote-notifications.sock'
$managerSocket = Join-Path $tempRoot 'servicemanager.sock'
$heartbeatPath = Join-Path $tempRoot 'remote-notifications.heartbeat'
$notificationRecordPath = Join-Path $tempRoot 'notifications.jsonl'
$feedPath = Join-Path $tempRoot 'feed.json'
$requestLogPath = Join-Path $tempRoot 'requests.jsonl'
$serverScriptPath = Join-Path $tempRoot 'feed_server.py'
$keyPath = Join-Path $tempRoot 'id_ed25519'
$resultPath = Join-Path $evidenceRoot 'result.json'
$gateProject = Join-Path $repoRoot 'tests/architecture-gate/ArchitectureGate.csproj'
$unitId = "remote-notifications.service.$runId"
$instanceName = "MyPowerTools.ServiceManager.RemoteNotifications.$runId"
$records = [Collections.Generic.List[object]]::new()
$managerProcess = $null
$serverProcess = $null
$managerStartCount = 0
$firstPid = $null
$secondPid = $null
$thirdPid = $null
$gateError = $null

function Add-Record {
    param([string]$Id, [bool]$Passed, [string]$Detail)
    $records.Add([pscustomobject]@{ id = $Id; passed = $Passed; detail = $Detail })
    $label = if ($Passed) { 'PASS' } else { 'FAIL' }
    $color = if ($Passed) { 'Green' } else { 'Red' }
    Write-Host "  [$label] ${Id}: $Detail" -ForegroundColor $color
}

function Require-Record {
    param([string]$Id, [bool]$Passed, [string]$Detail)
    Add-Record -Id $Id -Passed $Passed -Detail $Detail
    if (-not $Passed) { throw "Required verification failed: $Id. $Detail" }
}

function Invoke-Native {
    param([string]$FilePath, [string[]]$ArgumentList, [string]$Activity)
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) { throw "$Activity failed with exit code $LASTEXITCODE." }
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSeconds = 20, [int]$DelayMilliseconds = 100)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            if (& $Condition) { return $true }
        } catch { }
        Start-Sleep -Milliseconds $DelayMilliseconds
    }
    return $false
}

function Get-FreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

function Read-Exactly {
    param([IO.Stream]$Stream, [byte[]]$Buffer)
    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $count = $Stream.Read($Buffer, $offset, $Buffer.Length - $offset)
        if ($count -eq 0) { throw 'Unexpected end of stream.' }
        $offset += $count
    }
}

function Send-WorkerCommand {
    param([string]$SocketPath, [hashtable]$Command)
    $socket = [Net.Sockets.Socket]::new(
        [Net.Sockets.AddressFamily]::Unix,
        [Net.Sockets.SocketType]::Stream,
        [Net.Sockets.ProtocolType]::Unspecified)
    try {
        $socket.Connect([Net.Sockets.UnixDomainSocketEndPoint]::new($SocketPath))
        $stream = [Net.Sockets.NetworkStream]::new($socket, $false)
        try {
            $payload = [Text.Encoding]::UTF8.GetBytes(($Command | ConvertTo-Json -Compress -Depth 5))
            $header = [BitConverter]::GetBytes([int32]$payload.Length)
            $stream.Write($header, 0, $header.Length)
            $stream.Write($payload, 0, $payload.Length)
            $stream.Flush()
            $responseHeader = [byte[]]::new(4)
            Read-Exactly -Stream $stream -Buffer $responseHeader
            $length = [BitConverter]::ToInt32($responseHeader, 0)
            if ($length -le 0 -or $length -gt 1048576) {
                throw "Worker returned invalid frame length $length."
            }
            $responsePayload = [byte[]]::new($length)
            Read-Exactly -Stream $stream -Buffer $responsePayload
            return ([Text.Encoding]::UTF8.GetString($responsePayload) | ConvertFrom-Json)
        }
        finally { $stream.Dispose() }
    }
    finally { $socket.Dispose() }
}

function Get-WorkerPid {
    param([string]$SocketPath)
    foreach ($line in @(& /bin/ps -ww -axo pid=,command=)) {
        if ($line -notlike "*$SocketPath*") { continue }
        if ($line -match '^\s*(\d+)\s+') { return [int]$matches[1] }
    }
    return $null
}

function Start-Manager {
    $script:managerStartCount++
    if (Test-Path -LiteralPath $managerSocket) {
        Remove-Item -LiteralPath $managerSocket -Force
    }
    $env:MPT_DATA_ROOT = $dataRoot
    return Start-Process -FilePath $managerExecutable -PassThru `
        -RedirectStandardOutput (Join-Path $tempRoot "manager-$managerStartCount.out.log") `
        -RedirectStandardError (Join-Path $tempRoot "manager-$managerStartCount.err.log") `
        -ArgumentList @(
            '--data-root', $dataRoot,
            '--deploy-root', $deployRoot,
            '--endpoint-address', $managerSocket,
            '--instance-name', $instanceName)
}

function Stop-Manager {
    param([Diagnostics.Process]$Process)
    if ($null -eq $Process -or $Process.HasExited) { return }
    $env:MPT_DATA_ROOT = $dataRoot
    & dotnet run --no-build --configuration Release --project $gateProject -- `
        --mode shutdown --data-root $dataRoot --endpoint-address $managerSocket | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ServiceManager shutdown request failed.' }
    if (-not $Process.WaitForExit(15000)) { throw 'ServiceManager did not exit after shutdown.' }
}

function Write-Feed {
    param([string]$Id, [string]$Body)
    $now = [DateTimeOffset]::UtcNow.ToString('O')
    @{
        notifications = @(@{
            id = $Id
            channel = "verify-$runId"
            message = "[mac-e2e] $Body"
            icon = 'info'
            timestamp = $now
            server_timestamp = $now
            session_id = "session-$runId"
            session_name = 'macOS production verification'
            source_client = 'github-actions'
            content_kind = 'text'
        })
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $feedPath -Encoding utf8
}

function Read-History {
    $historyPath = Join-Path $toolDataRoot 'history.json'
    if (-not (Test-Path -LiteralPath $historyPath -PathType Leaf)) { return @() }
    return @((Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json).messages)
}

function Read-NotificationRecords {
    if (-not (Test-Path -LiteralPath $notificationRecordPath -PathType Leaf)) { return @() }
    return @(Get-Content -LiteralPath $notificationRecordPath |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_ | ConvertFrom-Json })
}

function Save-Evidence {
    param([bool]$Passed, [string]$ErrorMessage = '')
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    [pscustomobject]@{
        gate = 'RemoteNotifications-macOS-production'
        passed = $Passed
        error = $ErrorMessage
        runId = $runId
        runtimeIdentifier = $runtimeIdentifier
        appBundle = $AppBundle
        helperBundle = $remoteHelper
        workerPids = @($firstPid, $secondPid, $thirdPid)
        records = $records
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding utf8

    foreach ($file in Get-ChildItem -LiteralPath $tempRoot -File -ErrorAction SilentlyContinue) {
        if ($file.Name -match '\.(log|jsonl)$' -or $file.Name -eq 'remote-notifications.heartbeat') {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidenceRoot $file.Name) -Force
        }
    }
}

try {
    Write-Host "==> Remote Notifications macOS production gate $runId" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $tempRoot, $dataRoot, $deployRoot, $unitsRoot, $toolDataRoot, $evidenceRoot | Out-Null

    Require-Record 'app-bundle-present' (Test-Path -LiteralPath $AppBundle -PathType Container) $AppBundle
    Require-Record 'remote-helper-present' (Test-Path -LiteralPath $remoteHelper -PathType Container) $remoteHelper
    Require-Record 'worker-published' (Test-Path -LiteralPath $workerExecutable -PathType Leaf) $workerExecutable
    Require-Record 'service-manager-published' (Test-Path -LiteralPath $managerExecutable -PathType Leaf) $managerExecutable
    Require-Record 'shared-native-library-published' (Test-Path -LiteralPath $nativeLibrary -PathType Leaf) $nativeLibrary
    Require-Record 'service-manifest-published' (Test-Path -LiteralPath $shippedManifestPath -PathType Leaf) $shippedManifestPath

    $bundleId = (& /usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' $remoteHelperPlist).Trim()
    Require-Record 'helper-production-identity' ($bundleId -eq 'com.mypowertools.remote-notifications') $bundleId
    $symbols = @(& /usr/bin/nm -gU $nativeLibrary 2>$null)
    Require-Record 'native-authorization-symbol' ([bool]($symbols -match 'mpt_notification_authorization_status')) 'mpt_notification_authorization_status'
    Require-Record 'native-publish-symbol' ([bool]($symbols -match 'mpt_notification_publish')) 'mpt_notification_publish'

    $shippedManifest = Get-Content -LiteralPath $shippedManifestPath -Raw | ConvertFrom-Json
    $manifestDirectory = Split-Path -Parent $shippedManifestPath
    $resolvedExecutable = [IO.Path]::GetFullPath((Join-Path $manifestDirectory ([string]$shippedManifest.exec)))
    $resolvedWorkingDirectory = [IO.Path]::GetFullPath((Join-Path $manifestDirectory ([string]$shippedManifest.workingDirectory)))
    Require-Record 'manifest-resolves-helper-executable' ($resolvedExecutable -eq $workerExecutable) $resolvedExecutable
    Require-Record 'manifest-resolves-helper-directory' ($resolvedWorkingDirectory -eq $remoteHelperRoot) $resolvedWorkingDirectory
    Require-Record 'manifest-shares-surface-data-root' `
        ([string]$shippedManifest.environment.MPT_TOOL_DATA_ROOT -eq '~/Library/Application Support/MyPowerTools/state/tools/remote-notifications') `
        ([string]$shippedManifest.environment.MPT_TOOL_DATA_ROOT)

    & /usr/bin/codesign --verify --strict $remoteHelper
    Require-Record 'helper-signature-valid' ($LASTEXITCODE -eq 0) 'codesign --verify --strict'
    $global:LASTEXITCODE = 0

    $port = Get-FreeTcpPort
    @'
import json
import pathlib
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

port = int(sys.argv[1])
feed_path = pathlib.Path(sys.argv[2])
request_log = pathlib.Path(sys.argv[3])

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        parsed = urlparse(self.path)
        query = parse_qs(parsed.query)
        with request_log.open("a", encoding="utf-8") as handle:
            handle.write(json.dumps({"path": parsed.path, "query": query}) + "\n")
        if parsed.path != "/pull":
            self.send_response(404)
            self.end_headers()
            return
        if not query.get("sig") or not query.get("channel"):
            self.send_response(401)
            self.end_headers()
            return
        body = json.dumps(json.loads(feed_path.read_text(encoding="utf-8-sig"))).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, format, *args):
        pass

ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
'@ | Set-Content -LiteralPath $serverScriptPath -Encoding utf8

    Write-Feed -Id "startup-$runId" -Body 'startup backfill'
    Invoke-Native '/usr/bin/ssh-keygen' @('-q', '-t', 'ed25519', '-N', '', '-f', $keyPath) 'generate Ed25519 key'
    @{
        protocol = 'http'
        host = '127.0.0.1'
        port = $port
        channel = "verify-$runId"
        pollIntervalSeconds = 3600
        privateKeyPath = $keyPath
        keepWindowsBanners = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $toolDataRoot 'settings.json') -Encoding utf8

    $python = (Get-Command python3 -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $serverProcess = Start-Process -FilePath $python -PassThru `
        -RedirectStandardOutput (Join-Path $tempRoot 'feed.out.log') `
        -RedirectStandardError (Join-Path $tempRoot 'feed.err.log') `
        -ArgumentList @('-u', $serverScriptPath, [string]$port, $feedPath, $requestLogPath)
    $feedReady = Wait-Until -TimeoutSeconds 10 -Condition {
        try {
            $client = [Net.Sockets.TcpClient]::new('127.0.0.1', $port)
            $client.Dispose()
            return $true
        } catch { return $false }
    }
    Require-Record 'feed-listening' $feedReady "port=$port"

    Invoke-Native 'dotnet' @('build', $gateProject, '-c', 'Release', '--nologo') 'build ServiceManager control client'
    @{
        id = $unitId
        toolId = 'remote-notifications'
        displayName = 'Remote Notifications macOS verification'
        exec = $workerExecutable
        arguments = @('--socket', $workerSocket, '--heartbeat-file', $heartbeatPath, '--instance-token', "verify-$runId")
        workingDirectory = $remoteHelperRoot
        environment = @{
            RemoteNotifications__Transport = 'unix-domain-socket'
            RemoteNotifications__UnixSocket__Path = $workerSocket
            MPT_TOOL_DATA_ROOT = $toolDataRoot
            MPT_REMOTE_NOTIFICATIONS_SKIP_LEGACY_IMPORT = '1'
            MPT_REMOTE_NOTIFICATIONS_ALLOW_TEST_BACKEND = '1'
            MPT_REMOTE_NOTIFICATIONS_NOTIFICATION_MODE = 'record'
            MPT_REMOTE_NOTIFICATIONS_NOTIFICATION_RECORD_PATH = $notificationRecordPath
        }
        autostart = $true
        restartPolicy = @{ maxRestarts = 5; backoffMs = 500 }
        readiness = @{ kind = 'unix-socket'; address = $workerSocket; timeoutMs = 10000 }
        stopTimeoutMs = 5000
        dataRoots = @($toolDataRoot)
        dependsOn = @()
        instanceToken = "verify-$runId"
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $unitsRoot "$unitId.json") -Encoding utf8

    $managerProcess = Start-Manager
    Require-Record 'service-manager-ready' `
        (Wait-Until -TimeoutSeconds 20 -Condition { Test-Path -LiteralPath $managerSocket }) $managerSocket
    Require-Record 'worker-socket-ready' `
        (Wait-Until -TimeoutSeconds 30 -Condition { Test-Path -LiteralPath $workerSocket }) $workerSocket

    $lockPath = Join-Path $toolDataRoot 'remote-notifications.service.lock'
    Require-Record 'worker-lock-created' `
        (Wait-Until -TimeoutSeconds 10 -Condition { Test-Path -LiteralPath $lockPath -PathType Leaf }) $lockPath
    $socketMode = (& /usr/bin/stat -f '%Lp' $workerSocket).Trim()
    $lockMode = (& /usr/bin/stat -f '%Lp' $lockPath).Trim()
    Add-Record 'worker-socket-owner-only' ($socketMode -eq '600') "mode=$socketMode"
    Add-Record 'worker-lock-owner-only' ($lockMode -eq '600') "mode=$lockMode"

    $pong = Send-WorkerCommand $workerSocket @{ command = 'ping' }
    Require-Record 'worker-ping' ([bool]$pong.ok -and [bool]$pong.data.pong) 'framed Unix-socket command succeeded'
    Add-Record 'startup-backfill-persisted' `
        (Wait-Until -TimeoutSeconds 20 -Condition { @(Read-History | Where-Object { $_.id -eq "startup-$runId" }).Count -eq 1 }) `
        'first signed HTTP response stored'

    $firstPid = Get-WorkerPid $workerSocket
    Require-Record 'worker-autostarted' ($null -ne $firstPid) "pid=$firstPid"

    $savedEnvironment = @{}
    foreach ($name in @(
        'MPT_TOOL_DATA_ROOT',
        'MPT_REMOTE_NOTIFICATIONS_SKIP_LEGACY_IMPORT',
        'MPT_REMOTE_NOTIFICATIONS_ALLOW_TEST_BACKEND',
        'MPT_REMOTE_NOTIFICATIONS_NOTIFICATION_MODE',
        'MPT_REMOTE_NOTIFICATIONS_NOTIFICATION_RECORD_PATH')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
    }
    try {
        $env:MPT_TOOL_DATA_ROOT = $toolDataRoot
        $env:MPT_REMOTE_NOTIFICATIONS_SKIP_LEGACY_IMPORT = '1'
        $env:MPT_REMOTE_NOTIFICATIONS_ALLOW_TEST_BACKEND = '1'
        $env:MPT_REMOTE_NOTIFICATIONS_NOTIFICATION_MODE = 'record'
        $env:MPT_REMOTE_NOTIFICATIONS_NOTIFICATION_RECORD_PATH = $notificationRecordPath
        $duplicate = Start-Process -FilePath $workerExecutable -PassThru `
            -RedirectStandardOutput (Join-Path $tempRoot 'duplicate.out.log') `
            -RedirectStandardError (Join-Path $tempRoot 'duplicate.err.log') `
            -ArgumentList @('--socket', (Join-Path $tempRoot 'duplicate.sock'))
        $duplicateExited = $duplicate.WaitForExit(10000)
        if (-not $duplicateExited) { Stop-Process -Id $duplicate.Id -Force }
        Add-Record 'second-worker-rejected' `
            ($duplicateExited -and $duplicate.ExitCode -eq 17) `
            "exitCode=$(if ($duplicateExited) { $duplicate.ExitCode } else { 'timeout' })"
    }
    finally {
        foreach ($name in $savedEnvironment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name])
        }
    }

    $liveId = "live-$runId"
    Write-Feed -Id $liveId -Body 'live notification delivery'
    $poll = Send-WorkerCommand $workerSocket @{ command = 'poll' }
    Add-Record 'live-message-persisted' (@(Read-History | Where-Object { $_.id -eq $liveId }).Count -eq 1) $liveId
    Add-Record 'poll-state-success' ([bool]$poll.ok -and $poll.data.connectionState -eq 'ok') "state=$($poll.data.connectionState)"
    Add-Record 'notification-provider-recorded' ($poll.data.notificationState -eq 'recorded') "state=$($poll.data.notificationState)"
    Add-Record 'notification-authorization-resolved' `
        (-not [string]::IsNullOrWhiteSpace([string]$poll.data.notificationAuthorization) -and [string]$poll.data.notificationAuthorization -ne 'unknown') `
        "authorization=$($poll.data.notificationAuthorization)"

    $bannerStored = Wait-Until -TimeoutSeconds 10 -Condition {
        @(Read-NotificationRecords | Where-Object { $_.id -eq $liveId }).Count -eq 1
    }
    $notification = @(Read-NotificationRecords | Where-Object { $_.id -eq $liveId } | Select-Object -First 1)
    $activationUri = if ($notification.Count -eq 1) { [string]$notification[0].activationUri } else { '' }
    Add-Record 'service-owned-banner-dispatched' $bannerStored "records=$(@(Read-NotificationRecords).Count)"
    Add-Record 'banner-targets-exact-message' ($bannerStored -and $activationUri -like "*$liveId*") $activationUri

    $recordsBeforeReplay = @(Read-NotificationRecords).Count
    $null = Send-WorkerCommand $workerSocket @{ command = 'poll' }
    $recordsAfterReplay = @(Read-NotificationRecords).Count
    $historyCopies = @(Read-History | Where-Object { $_.id -eq $liveId }).Count
    Add-Record 'duplicate-suppressed' `
        ($recordsBeforeReplay -eq $recordsAfterReplay -and $historyCopies -eq 1) `
        "bannerRecords=$recordsAfterReplay historyCopies=$historyCopies"

    $requests = if (Test-Path -LiteralPath $requestLogPath) {
        @(Get-Content -LiteralPath $requestLogPath | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    } else { @() }
    $signedRequests = @($requests | Where-Object {
        $_.path -eq '/pull' -and $null -ne $_.query.sig -and $null -ne $_.query.channel
    })
    Add-Record 'signed-pull-observed' ($signedRequests.Count -ge 2) "count=$($signedRequests.Count)"

    Stop-Manager $managerProcess
    $managerProcess = Start-Manager
    Add-Record 'manager-restarted' `
        (Wait-Until -TimeoutSeconds 20 -Condition { Test-Path -LiteralPath $managerSocket }) $managerSocket
    $secondPidReady = Wait-Until -TimeoutSeconds 20 -Condition { $null -ne (Get-WorkerPid $workerSocket) }
    $secondPid = if ($secondPidReady) { Get-WorkerPid $workerSocket } else { $null }
    Add-Record 'worker-readopted' ($null -ne $firstPid -and $secondPid -eq $firstPid) "before=$firstPid after=$secondPid"
    $pongAfterManagerRestart = Send-WorkerCommand $workerSocket @{ command = 'ping' }
    Add-Record 'ready-after-manager-restart' ([bool]$pongAfterManagerRestart.ok) 'existing worker reachable'

    if ($null -eq $secondPid) { throw 'Worker PID unavailable before crash recovery test.' }
    $recoveryId = "recovery-$runId"
    Write-Feed -Id $recoveryId -Body 'notification received while worker restarts'
    Stop-Process -Id $secondPid -Force
    $replacementReady = Wait-Until -TimeoutSeconds 30 -DelayMilliseconds 250 -Condition {
        $candidate = Get-WorkerPid $workerSocket
        $null -ne $candidate -and $candidate -ne $secondPid -and (Test-Path -LiteralPath $workerSocket)
    }
    $thirdPid = if ($replacementReady) { Get-WorkerPid $workerSocket } else { $null }
    Add-Record 'worker-crash-recovered' $replacementReady "crashed=$secondPid replacement=$thirdPid"
    $pongAfterCrash = Send-WorkerCommand $workerSocket @{ command = 'ping' }
    Add-Record 'ready-after-worker-recovery' ([bool]$pongAfterCrash.ok) 'replacement worker reachable'
    Add-Record 'history-survives-recovery' (@(Read-History | Where-Object { $_.id -eq $liveId }).Count -eq 1) $liveId

    $recoveryPersisted = Wait-Until -TimeoutSeconds 20 -DelayMilliseconds 250 -Condition {
        @(Read-History | Where-Object { $_.id -eq $recoveryId }).Count -eq 1
    }
    Add-Record 'message-received-after-worker-restart' $recoveryPersisted $recoveryId
    $recoveryBannerStored = Wait-Until -TimeoutSeconds 20 -DelayMilliseconds 250 -Condition {
        @(Read-NotificationRecords | Where-Object { $_.id -eq $recoveryId }).Count -eq 1
    }
    $recoveryNotification = @(
        Read-NotificationRecords |
            Where-Object { $_.id -eq $recoveryId } |
            Select-Object -First 1)
    $recoveryActivationUri = if ($recoveryNotification.Count -eq 1) {
        [string]$recoveryNotification[0].activationUri
    } else { '' }
    Add-Record 'banner-delivered-after-worker-restart' $recoveryBannerStored $recoveryId
    Add-Record 'restart-banner-targets-exact-message' `
        ($recoveryBannerStored -and $recoveryActivationUri -like "*$recoveryId*") `
        $recoveryActivationUri

    $failed = @($records | Where-Object { -not $_.passed })
    Save-Evidence -Passed ($failed.Count -eq 0)
    if ($failed.Count -gt 0) { throw "$($failed.Count) verification record(s) failed." }
    Write-Host '==> Remote Notifications macOS production gate passed.' -ForegroundColor Green
}
catch {
    $gateError = $_
    Save-Evidence -Passed $false -ErrorMessage $_.Exception.Message
}
finally {
    if ($null -ne $managerProcess -and -not $managerProcess.HasExited) {
        try { Stop-Manager $managerProcess }
        catch { Stop-Process -Id $managerProcess.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($null -ne $serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
    }
    $remainingWorker = Get-WorkerPid $workerSocket
    if ($null -ne $remainingWorker) {
        Stop-Process -Id $remainingWorker -Force -ErrorAction SilentlyContinue
    }
    Save-Evidence -Passed ($null -eq $gateError -and @($records | Where-Object { -not $_.passed }).Count -eq 0) `
        -ErrorMessage $(if ($null -eq $gateError) { '' } else { $gateError.Exception.Message })
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host "==> Result: $resultPath"
if ($null -ne $gateError) { throw $gateError }
