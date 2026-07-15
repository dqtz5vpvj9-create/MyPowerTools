<#
.SYNOPSIS
  Verifies the RemoteNotifications.Service unit: it starts, answers its named-pipe
  readiness probe, injects and persists a unique test message to history, survives a
  ServiceManager restart with the same PID (re-adoption), and the persisted history
  survives a worker restart. This proves the core Batch-1 promise: the polling,
  persistence and banner path is owned by an independent process whose life is
  decoupled from the Shell and the Runner.

  Uses isolated temp resources and a unique pipe name. Never touches the user's running
  remote-notifications.core or the real notification settings/registry.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $MyInvocation.MyCommand.Path).Directory.Parent.FullName
$runId = [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
$dataRoot = Join-Path $env:TEMP "mpt-remote-notifications-verify-$runId"
$deployRoot = Join-Path $dataRoot 'deploy'
$unitsDir = Join-Path $deployRoot 'units'
$unitId = "remote-notifications.service-$runId"
$pipeName = "remote-notifications-core-$runId"
$smEndpoint = "mypowertools.servicemanager.remote-notifications-$runId"
$smInstanceName = "MyPowerTools.ServiceManager.RemoteNotifications.$runId"
$resultPath = Join-Path (Join-Path $repoRoot 'artifacts') "remote-notifications-verify-$runId.json"
$smProject = Join-Path $repoRoot 'src\MyPowerTools.ServiceManager\MyPowerTools.ServiceManager.csproj'
$gateProject = Join-Path $repoRoot 'tests\architecture-gate\ArchitectureGate.csproj'
$serviceProject = Join-Path $repoRoot 'tools\remote-notifications\current-integration\src\RemoteNotifications.Service\RemoteNotifications.Service.csproj'
$toolDataRoot = Join-Path $dataRoot 'tool-data'
$historyPath = Join-Path $toolDataRoot 'history.json'

$smProcess = $null
$records = @()

function Add-Record($id, $passed, $detail) {
    $script:records += [pscustomobject]@{ Id = $id; Passed = $passed; Detail = $detail }
    $color = if ($passed) { 'Green' } else { 'Red' }
    Write-Host ("  [{0}] {1}: {2}" -f ($(if($passed){'PASS'}else{'FAIL'})), $id, $detail) -ForegroundColor $color
}

function Start-ServiceManager {
    param([string]$DataRoot, [string]$DeployRoot, [string]$LogPath)
    $env:MPT_DATA_ROOT = $DataRoot
    $p = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','-c','Release','--project',$smProject,'--','--data-root',$DataRoot,'--deploy-root',$DeployRoot,'--endpoint-address',$smEndpoint,'--instance-name',$smInstanceName) -WindowStyle Hidden -PassThru -RedirectStandardOutput $LogPath -RedirectStandardError (Join-Path $DataRoot 'sm.err')
    Start-Sleep -Seconds 6
    return $p
}

function Stop-ServiceManagerGraceful {
    param($Process, [string]$DataRoot, [int]$TimeoutSeconds = 20)
    $env:MPT_DATA_ROOT = $DataRoot
    & dotnet run --no-build -c Release --project $gateProject -- --mode shutdown --data-root $DataRoot --endpoint-address $smEndpoint 2>&1 | Out-Null
    if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

function Get-UnitPid {
    param([string]$PipeName)
    $proc = Get-CimInstance Win32_Process -Filter "Name='RemoteNotifications.Service.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$PipeName*" } |
        Sort-Object ProcessId | Select-Object -First 1
    if ($proc) { return [string]$proc.ProcessId }
    return $null
}

function Send-FramedCommand {
    param([string]$PipeName, [string]$CommandJson, [int]$TimeoutMs = 5000)
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.',$PipeName,[System.IO.Pipes.PipeDirection]::InOut,[System.IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect($TimeoutMs)
        $json = [System.Text.Encoding]::UTF8.GetBytes($CommandJson)
        $header = [BitConverter]::GetBytes([int32]$json.Length)
        $pipe.Write($header,0,4)
        $pipe.Write($json,0,$json.Length)
        $pipe.Flush()
        $h = New-Object byte[] 4
        $read = 0
        while ($read -lt 4) { $n = $pipe.Read($h, $read, 4-$read); if ($n -eq 0) { break }; $read += $n }
        if ($read -lt 4) { $pipe.Dispose(); return $null }
        $len = [BitConverter]::ToInt32($h,0)
        $payload = New-Object byte[] $len
        $read = 0
        while ($read -lt $len) { $n = $pipe.Read($payload, $read, $len-$read); if ($n -eq 0) { break }; $read += $n }
        $pipe.Dispose()
        return [System.Text.Encoding]::UTF8.GetString($payload,0,$read)
    } catch {
        return $null
    }
}

function Read-HistoryMessages {
    try {
        if (-not (Test-Path -LiteralPath $historyPath -PathType Leaf)) { return @() }
        $state = Get-Content -LiteralPath $historyPath -Raw | ConvertFrom-Json
        return @($state.Messages)
    } catch { return @() }
}

try {
    Write-Host "==> RemoteNotifications.Service liveness verification runId=$runId" -ForegroundColor Cyan
    Write-Host "==> dataRoot=$dataRoot"
    New-Item -ItemType Directory -Force -Path $dataRoot,$deployRoot,$unitsDir,$toolDataRoot | Out-Null
    @{
        protocol = 'http'
        host = '127.0.0.1'
        port = 9
        channel = "verify-$runId"
        pollIntervalSeconds = 3600
        privateKeyPath = (Join-Path $dataRoot 'missing-test-key')
        keepWindowsBanners = $false
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $toolDataRoot 'settings.json') -Encoding UTF8

    Write-Host "==> Building + publishing RemoteNotifications.Service..." -ForegroundColor Cyan
    & dotnet build $smProject -c Release --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ServiceManager build failed" }
    & dotnet build $gateProject -c Release --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "architecture-gate build failed" }
    & dotnet publish $serviceProject -c Release -o (Join-Path $dataRoot 'service') --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "RemoteNotifications.Service publish failed" }
    $serviceExe = Join-Path $dataRoot 'service\RemoteNotifications.Service.exe'
    Add-Record "build-service-exe" (Test-Path $serviceExe) "exe at $serviceExe"

    $manifest = @{
        id = $unitId
        toolId = "remote-notifications"
        displayName = "Remote Notifications Service"
        exec = $serviceExe
        arguments = @("--pipe", $pipeName, "--heartbeat-file", (Join-Path $dataRoot 'rn.heartbeat'), "--instance-token", "verify-$runId")
        workingDirectory = ""
        environment = @{
            "RemoteNotifications__Transport" = "pipe"
            "MPT_TOOL_DATA_ROOT" = $toolDataRoot
            "MPT_REMOTE_NOTIFICATIONS_SKIP_LEGACY_IMPORT" = "1"
        }
        autostart = $true
        restartPolicy = @{ maxRestarts = 5; backoffMs = 2000 }
        readiness = @{ kind = "pipe"; address = $pipeName; timeoutMs = 8000 }
        stopTimeoutMs = 5000
        dataRoots = @($toolDataRoot)
        dependsOn = @()
        instanceToken = "verify-$runId"
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $unitsDir "$unitId.json") -Encoding UTF8

    Write-Host "==> Launching ServiceManager (autostarts the unit)..." -ForegroundColor Cyan
    $smProcess = Start-ServiceManager -DataRoot $dataRoot -DeployRoot $deployRoot -LogPath (Join-Path $dataRoot 'sm1.log')
    Add-Record "sm-running" (-not $smProcess.HasExited) "ServiceManager PID=$($smProcess.Id)"

    Start-Sleep -Seconds 3
    $firstPid = Get-UnitPid -PipeName $pipeName
    Add-Record "unit-autostarted" ($null -ne $firstPid) "first unit PID=$firstPid"

    $pong = Send-FramedCommand -PipeName $pipeName -CommandJson '{"command":"ping"}'
    Add-Record "readiness-ping" ($null -ne $pong -and $pong -match '"ok":true' -and $pong -match 'pong') "pipe $pipeName answered ping"

    # State query returns a status snapshot (connectionState, poll counters).
    $stateResp = Send-FramedCommand -PipeName $pipeName -CommandJson '{"command":"state"}'
    Add-Record "state-snapshot" ($null -ne $stateResp -and $stateResp -match 'connectionState') "state command returned snapshot"

    # Inject a unique test message; it must land in the persisted history registry. The worker
    # returns the generated messageId in the response, so we verify that exact id is present in
    # the persisted history (not merely that the count grew — the history caps at 500 messages,
    # so an inject into a full inbox evicts the oldest instead of growing the count).
    $beforeCount = @(Read-HistoryMessages).Count
    $injectResp = Send-FramedCommand -PipeName $pipeName -CommandJson '{"command":"inject"}'
    $injected = ($null -ne $injectResp) -and ($injectResp -match '"injected":true')
    Add-Record "inject-accepted" $injected "inject response: $($injectResp | Out-String)"

    # Extract the generated messageId from the response so we can confirm the exact record persisted.
    $injectedId = ''
    if ($injectResp -match '"messageId":"([^"]+)"') { $injectedId = $matches[1] }

    Start-Sleep -Seconds 1
    $afterMessages = @(Read-HistoryMessages)
    $afterCount = $afterMessages.Count
    $foundPersisted = $false
    if ($injectedId) {
        foreach ($m in $afterMessages) {
            if ($m.id -eq $injectedId) { $foundPersisted = $true; break }
        }
    }
    Add-Record "message-persisted" ($injected -and $foundPersisted) "injected id=$injectedId persisted; history $beforeCount -> $afterCount"

    # ---- Restart ServiceManager, confirm unit PID unchanged (re-adoption) ----
    Write-Host "==> Restarting ServiceManager to verify re-adoption..." -ForegroundColor Cyan
    Stop-ServiceManagerGraceful -Process $smProcess -DataRoot $dataRoot
    $smProcess = Start-ServiceManager -DataRoot $dataRoot -DeployRoot $deployRoot -LogPath (Join-Path $dataRoot 'sm2.log')
    Start-Sleep -Seconds 3

    $secondPid = Get-UnitPid -PipeName $pipeName
    $samePid = ($null -ne $firstPid) -and ($secondPid -eq $firstPid)
    Add-Record "reattach-same-pid" $samePid "first=$firstPid second=$secondPid (must be equal)"

    $instanceCount = @(Get-CimInstance Win32_Process -Filter "Name='RemoteNotifications.Service.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*$pipeName*" }).Count
    Add-Record "single-instance" ($instanceCount -eq 1) "found $instanceCount instance(s) (expect 1)"

    $pong2 = Send-FramedCommand -PipeName $pipeName -CommandJson '{"command":"ping"}'
    Add-Record "readiness-after-restart" ($null -ne $pong2 -and $pong2 -match 'pong') "pipe still answering after SM restart"

    # History must survive the SM restart (worker kept running; persistence intact).
    $surviveMessages = @(Read-HistoryMessages)
    $surviveCount = $surviveMessages.Count
    Add-Record "history-survives-sm-restart" ($surviveCount -ge $afterCount) "history $afterCount -> $surviveCount after SM restart"

    # Crash the worker itself. ServiceManager must replace it and persisted history must remain.
    if (-not $secondPid) { throw 'Worker PID is missing before crash recovery test.' }
    Stop-Process -Id ([int]$secondPid) -Force
    $thirdPid = $null
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        Start-Sleep -Milliseconds 250
        $candidatePid = Get-UnitPid -PipeName $pipeName
        if ($candidatePid -and $candidatePid -ne $secondPid) {
            $thirdPid = $candidatePid
            break
        }
    }
    Add-Record "worker-crash-recovered" ($null -ne $thirdPid) "crashed=$secondPid recovered=$thirdPid"
    $pong3 = Send-FramedCommand -PipeName $pipeName -CommandJson '{"command":"ping"}'
    Add-Record "readiness-after-worker-recovery" ($null -ne $pong3 -and $pong3 -match 'pong') "replacement worker answered ping"
    $recoveredMessages = @(Read-HistoryMessages)
    Add-Record "history-survives-worker-recovery" ($recoveredMessages.Count -ge $afterCount) "history $afterCount -> $($recoveredMessages.Count) after worker recovery"

    $passed = ($records | Where-Object { -not $_.Passed }).Count -eq 0
    $result = [pscustomobject]@{
        Gate = "RemoteNotifications-Service-Liveness"
        Passed = $passed
        RunId = $runId
        UnitId = $unitId
        FirstPid = $firstPid
        SecondPid = $secondPid
        ThirdPid = $thirdPid
        Records = $records
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -Path $resultPath -Encoding UTF8

    Write-Host ""
    Write-Host "==> RemoteNotifications.Service liveness: $(if($passed){'PASS'}else{'FAIL'})" -ForegroundColor $(if($passed){'Green'}else{'Red'})
    Write-Host "==> Result: $resultPath"
    if (-not $passed) { exit 1 }
    exit 0
}
catch {
    Write-Host "FAIL: $_" -ForegroundColor Red
    $_.ScriptStackTrace | Out-Host
    exit 1
}
finally {
    if ($smProcess -and -not $smProcess.HasExited) {
        try { Stop-Process -Id $smProcess.Id -Force -ErrorAction SilentlyContinue } catch {}
    }
    Get-CimInstance Win32_Process -Filter "Name='RemoteNotifications.Service.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*$pipeName*" } | ForEach-Object {
        try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {}
    }
    if (Test-Path $dataRoot) {
        try { Remove-Item -Recurse -Force $dataRoot -ErrorAction SilentlyContinue } catch {}
    }
}
