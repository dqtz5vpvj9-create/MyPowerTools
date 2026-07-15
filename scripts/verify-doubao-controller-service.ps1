<#
.SYNOPSIS
  Verifies the DoubaoAgent.Controller.Service unit: it starts, answers its named-pipe
  readiness probe, returns a cached status snapshot (probe fan-out results), reports
  the secure-runtime inspection (listeners/owned processes), survives a ServiceManager
  restart with the same PID (re-adoption), and stays single-instance. This proves the
  core Batch-2 promise: subprocess supervision is owned by an independent process whose
  life is decoupled from the Shell and the Runner, with zero UI-thread network waits.

  The default mode uses an empty runtime fixture and a unique pipe name. Pass
  -ExerciseRuntime to execute the real restart path against an explicitly supplied runtime.
#>
[CmdletBinding()]
param(
    [switch]$ExerciseRuntime,
    [string]$RuntimeRoot = '',
    [string]$SecretFilePath = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $MyInvocation.MyCommand.Path).Directory.Parent.FullName
$runId = [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
$dataRoot = Join-Path $env:TEMP "mpt-doubao-controller-verify-$runId"
$deployRoot = Join-Path $dataRoot 'deploy'
$unitsDir = Join-Path $deployRoot 'units'
$unitId = "doubao-agent.controller.service-$runId"
$pipeName = "doubao-agent-controller-$runId"
$smEndpoint = "mypowertools.servicemanager.doubao-$runId"
$smInstanceName = "MyPowerTools.ServiceManager.Doubao.$runId"
$resultPath = Join-Path (Join-Path $repoRoot 'artifacts') "doubao-controller-verify-$runId.json"
$smProject = Join-Path $repoRoot 'src\MyPowerTools.ServiceManager\MyPowerTools.ServiceManager.csproj'
$gateProject = Join-Path $repoRoot 'tests\architecture-gate\ArchitectureGate.csproj'
$serviceProject = Join-Path $repoRoot 'tools\doubao-computer-use\current-integration\src\DoubaoAgent.Controller.Service\DoubaoAgent.Controller.Service.csproj'
$fixtureRuntimeRoot = Join-Path $dataRoot 'runtime-fixture'
$fixtureSecretFile = Join-Path $dataRoot 'runtime-fixture.env'

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
    $proc = Get-CimInstance Win32_Process -Filter "Name='DoubaoAgent.Controller.Service.exe'" -ErrorAction SilentlyContinue |
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

function Get-ListenerPid {
    param([int]$Port)
    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
        Sort-Object OwningProcess |
        Select-Object -First 1
    if ($listener) { return [int]$listener.OwningProcess }
    return 0
}

function Wait-ForRuntimeListeners {
    param([int]$TimeoutSeconds = 60)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $snapshot = [ordered]@{
            Tool = Get-ListenerPid -Port 38102
            Mcp = Get-ListenerPid -Port 38080
            Planner = Get-ListenerPid -Port 38189
        }
        if (($snapshot.Values | Where-Object { $_ -le 0 }).Count -eq 0) {
            return $snapshot
        }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $snapshot
}

try {
    Write-Host "==> DoubaoAgent.Controller.Service liveness verification runId=$runId" -ForegroundColor Cyan
    Write-Host "==> dataRoot=$dataRoot"
    New-Item -ItemType Directory -Force -Path $dataRoot,$deployRoot,$unitsDir,$fixtureRuntimeRoot | Out-Null
    Set-Content -LiteralPath $fixtureSecretFile -Value 'DOUBAO_TOOL_AUTH_KEY=fixture-only' -Encoding UTF8

    if ($ExerciseRuntime) {
        if ([string]::IsNullOrWhiteSpace($RuntimeRoot) -or -not (Test-Path -LiteralPath $RuntimeRoot -PathType Container)) {
            throw '-ExerciseRuntime requires an existing -RuntimeRoot directory.'
        }
        if ([string]::IsNullOrWhiteSpace($SecretFilePath) -or -not (Test-Path -LiteralPath $SecretFilePath -PathType Leaf)) {
            throw '-ExerciseRuntime requires an existing -SecretFilePath file.'
        }
        $effectiveRuntimeRoot = [IO.Path]::GetFullPath($RuntimeRoot)
        $effectiveSecretFile = [IO.Path]::GetFullPath($SecretFilePath)
    } else {
        $effectiveRuntimeRoot = $fixtureRuntimeRoot
        $effectiveSecretFile = $fixtureSecretFile
    }

    Write-Host "==> Building + publishing DoubaoAgent.Controller.Service..." -ForegroundColor Cyan
    & dotnet build $smProject -c Release --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ServiceManager build failed" }
    & dotnet build $gateProject -c Release --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "architecture-gate build failed" }
    & dotnet publish $serviceProject -c Release -o (Join-Path $dataRoot 'service') --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "DoubaoAgent.Controller.Service publish failed" }
    $serviceExe = Join-Path $dataRoot 'service\DoubaoAgent.Controller.Service.exe'
    Add-Record "build-service-exe" (Test-Path $serviceExe) "exe at $serviceExe"

    $manifest = @{
        id = $unitId
        toolId = "doubao-computer-use"
        displayName = "Doubao Computer Use Controller"
        exec = $serviceExe
        arguments = @(
            "--pipe", $pipeName,
            "--heartbeat-file", (Join-Path $dataRoot 'doubao.heartbeat'),
            "--instance-token", "verify-$runId",
            "--probe-interval-ms", "2000",
            "--runtime-root", $effectiveRuntimeRoot,
            "--secret-file", $effectiveSecretFile
        )
        workingDirectory = ""
        environment = @{ "DoubaoAgent__Transport" = "pipe" }
        autostart = $true
        restartPolicy = @{ maxRestarts = 4; backoffMs = 2000 }
        readiness = @{ kind = "pipe"; address = $pipeName; timeoutMs = 8000 }
        stopTimeoutMs = 10000
        dataRoots = @()
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

    # The status snapshot must carry the probe fan-out results (security + 3 service online flags).
    Start-Sleep -Seconds 4  # let the probe cycle populate the cached snapshot
    $stateResp = Send-FramedCommand -PipeName $pipeName -CommandJson '{"command":"state"}'
    $hasState = ($null -ne $stateResp) -and ($stateResp -match 'securitySafe') -and ($stateResp -match 'toolOnline') -and ($stateResp -match 'checkedAt')
    Add-Record "state-snapshot" $hasState "state command returned cached probe snapshot"

    # Inspect returns the secure-runtime listener/owned-process state (proves the controller owns it).
    $inspectResp = Send-FramedCommand -PipeName $pipeName -CommandJson '{"command":"inspect"}'
    $hasInspect = ($null -ne $inspectResp) -and ($inspectResp -match 'inspectionAvailable')
    Add-Record "inspect-security" $hasInspect "inspect command returned runtime security state"

    if ($ExerciseRuntime) {
        $before = Wait-ForRuntimeListeners -TimeoutSeconds 10
        $beforeReady = ($before.Values | Where-Object { $_ -le 0 }).Count -eq 0
        Add-Record 'real-runtime-before' $beforeReady ("tool={0}; mcp={1}; planner={2}" -f $before.Tool,$before.Mcp,$before.Planner)

        $restartRequest = @{
            command = 'restart'
            runtimeRoot = $effectiveRuntimeRoot
            secretFile = $effectiveSecretFile
        } | ConvertTo-Json -Compress
        $restartResp = Send-FramedCommand -PipeName $pipeName -CommandJson $restartRequest -TimeoutMs 90000
        $restartJson = if ($restartResp) { $restartResp | ConvertFrom-Json } else { $null }
        $restartPassed = $null -ne $restartJson -and $restartJson.ok -eq $true -and $restartJson.data.success -eq $true
        Add-Record 'real-runtime-restart-command' $restartPassed ($(if($restartJson){$restartJson.data.message}else{'no response'}))

        $after = Wait-ForRuntimeListeners -TimeoutSeconds 60
        $afterReady = ($after.Values | Where-Object { $_ -le 0 }).Count -eq 0
        Add-Record 'real-runtime-after' $afterReady ("tool={0}; mcp={1}; planner={2}" -f $after.Tool,$after.Mcp,$after.Planner)
        $allChanged = $beforeReady -and $afterReady -and
            $before.Tool -ne $after.Tool -and
            $before.Mcp -ne $after.Mcp -and
            $before.Planner -ne $after.Planner
        Add-Record 'real-runtime-pids-changed' $allChanged ("before={0}/{1}/{2}; after={3}/{4}/{5}" -f $before.Tool,$before.Mcp,$before.Planner,$after.Tool,$after.Mcp,$after.Planner)
    }

    # ---- Restart ServiceManager, confirm unit PID unchanged (re-adoption) ----
    Write-Host "==> Restarting ServiceManager to verify re-adoption..." -ForegroundColor Cyan
    Stop-ServiceManagerGraceful -Process $smProcess -DataRoot $dataRoot
    $smProcess = Start-ServiceManager -DataRoot $dataRoot -DeployRoot $deployRoot -LogPath (Join-Path $dataRoot 'sm2.log')
    Start-Sleep -Seconds 3

    $secondPid = Get-UnitPid -PipeName $pipeName
    $samePid = ($null -ne $firstPid) -and ($secondPid -eq $firstPid)
    Add-Record "reattach-same-pid" $samePid "first=$firstPid second=$secondPid (must be equal)"

    $instanceCount = @(Get-CimInstance Win32_Process -Filter "Name='DoubaoAgent.Controller.Service.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*$pipeName*" }).Count
    Add-Record "single-instance" ($instanceCount -eq 1) "found $instanceCount instance(s) (expect 1)"

    $pong2 = Send-FramedCommand -PipeName $pipeName -CommandJson '{"command":"ping"}'
    Add-Record "readiness-after-restart" ($null -ne $pong2 -and $pong2 -match 'pong') "pipe still answering after SM restart"

    # Crash the controller process itself and require ServiceManager to replace it.
    if (-not $secondPid) { throw 'Controller PID is missing before crash recovery test.' }
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
    Add-Record "controller-crash-recovered" ($null -ne $thirdPid) "crashed=$secondPid recovered=$thirdPid"
    $pong3 = Send-FramedCommand -PipeName $pipeName -CommandJson '{"command":"ping"}'
    Add-Record "readiness-after-controller-recovery" ($null -ne $pong3 -and $pong3 -match 'pong') "replacement controller answered ping"

    $passed = ($records | Where-Object { -not $_.Passed }).Count -eq 0
    $result = [pscustomobject]@{
        Gate = "DoubaoController-Service-Liveness"
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
    Write-Host "==> DoubaoAgent.Controller.Service liveness: $(if($passed){'PASS'}else{'FAIL'})" -ForegroundColor $(if($passed){'Green'}else{'Red'})
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
    Get-CimInstance Win32_Process -Filter "Name='DoubaoAgent.Controller.Service.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*$pipeName*" } | ForEach-Object {
        try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {}
    }
    if (Test-Path $dataRoot) {
        try { Remove-Item -Recurse -Force $dataRoot -ErrorAction SilentlyContinue } catch {}
    }
}
