<#
.SYNOPSIS
  Verifies the ScreenEase.Service unit survives ServiceManager restart (re-adoption)
  and answers its named-pipe readiness probe. This proves the core Batch-1 promise:
  long-running units are independent of the ServiceManager process.

  Uses isolated temp resources. Never touches the user's running ScreenEase.CoreService
  (PID may exist) or the in-proc ScreenEase module.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $MyInvocation.MyCommand.Path).Directory.Parent.FullName
$runId = [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
$dataRoot = Join-Path $env:TEMP "mpt-screenease-verify-$runId"
$deployRoot = Join-Path $dataRoot 'deploy'
$unitsDir = Join-Path $deployRoot 'units'
$unitId = "screenease.service-$runId"
$pipeName = "screenease-core-$runId"
$resultPath = Join-Path (Join-Path $repoRoot 'artifacts') "screenease-verify-$runId.json"
$smProject = Join-Path (Join-Path (Join-Path $repoRoot 'src') 'MyPowerTools.ServiceManager') 'MyPowerTools.ServiceManager.csproj'
$gateProject = Join-Path $repoRoot 'tests\architecture-gate\ArchitectureGate.csproj'
$serviceProject = Join-Path (Join-Path (Join-Path (Join-Path $repoRoot 'tools') 'screenease') 'current-integration') 'src' | ForEach-Object { Join-Path $_ 'ScreenEase.Service' } | ForEach-Object { Join-Path $_ 'ScreenEase.Service.csproj' }

# fix: rebuild the path cleanly
$serviceProject = Join-Path $repoRoot 'tools\screenease\current-integration\src\ScreenEase.Service\ScreenEase.Service.csproj'

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
    $p = Start-Process -FilePath 'dotnet' -ArgumentList @('run','--no-build','--project',$smProject,'--','--data-root',$DataRoot,'--deploy-root',$DeployRoot) -WindowStyle Hidden -PassThru -RedirectStandardOutput $LogPath -RedirectStandardError (Join-Path $DataRoot 'sm.err')
    Start-Sleep -Seconds 6
    return $p
}

function Stop-ServiceManagerGraceful {
    param($Process, [string]$DataRoot, [int]$TimeoutSeconds = 20)
    # Request a graceful shutdown via the gRPC Shutdown RPC (invoked through the architecture-gate
    # driver with --mode shutdown). This runs the host's `finally { engine.DisposeAsync() }`, which
    # detaches from units WITHOUT stopping them, so the unit survives and the next ServiceManager
    # re-adopts it. This mirrors the production shutdown path and avoids PowerShell job-cascade kills.
    $env:MPT_DATA_ROOT = $DataRoot
    & dotnet run --no-build --project $gateProject -- --mode shutdown --data-root $DataRoot 2>&1 | Out-Null
    if (-not $Process.WaitForExit($TimeoutSeconds * 1000)) {
        # RPC graceful stop did not complete in time; force-kill the SM host only as a last resort.
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

function Get-UnitPid {
    param([string]$PipeName)
    # Find the ScreenEase.Service.exe whose command line references our unique pipe name.
    $proc = Get-CimInstance Win32_Process -Filter "Name='ScreenEase.Service.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$PipeName*" } |
        Sort-Object ProcessId | Select-Object -First 1
    if ($proc) { return [string]$proc.ProcessId }
    return $null
}

function Send-Ping {
    param([string]$PipeName, [int]$TimeoutMs = 5000)
    # Chromium-style framed ping over the named pipe. Returns $true if pong received.
    try {
        $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.',$PipeName,[System.IO.Pipes.PipeDirection]::InOut,[System.IO.Pipes.PipeOptions]::Asynchronous)
        $pipe.Connect($TimeoutMs)
        $json = [System.Text.Encoding]::UTF8.GetBytes('{"command":"ping"}')
        $header = [BitConverter]::GetBytes([int32]$json.Length)
        $pipe.Write($header,0,4)
        $pipe.Write($json,0,$json.Length)
        $pipe.Flush()
        # read response header
        $h = New-Object byte[] 4
        $read = 0
        while ($read -lt 4) { $n = $pipe.Read($h, $read, 4-$read); if ($n -eq 0) { break }; $read += $n }
        if ($read -lt 4) { $pipe.Dispose(); return $false }
        $len = [BitConverter]::ToInt32($h,0)
        $payload = New-Object byte[] $len
        $read = 0
        while ($read -lt $len) { $n = $pipe.Read($payload, $read, $len-$read); if ($n -eq 0) { break }; $read += $n }
        $pipe.Dispose()
        $resp = [System.Text.Encoding]::UTF8.GetString($payload,0,$read)
        return $resp -match '"ok":true' -and $resp -match 'pong'
    } catch {
        return $false
    }
}

try {
    Write-Host "==> ScreenEase.Service liveness verification runId=$runId" -ForegroundColor Cyan
    Write-Host "==> dataRoot=$dataRoot"
    New-Item -ItemType Directory -Force -Path $dataRoot,$deployRoot,$unitsDir | Out-Null

    # Build + publish the ScreenEase.Service exe.
    Write-Host "==> Building + publishing ScreenEase.Service..." -ForegroundColor Cyan
    & dotnet build $gateProject --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "architecture-gate build failed" }
    & dotnet build $serviceProject --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ScreenEase.Service build failed" }
    $pubDir = Join-Path $dataRoot 'service'
    & dotnet publish $serviceProject -c Release -o $pubDir --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ScreenEase.Service publish failed" }
    $serviceExe = Join-Path $pubDir 'ScreenEase.Service.exe'
    Add-Record "build-service-exe" (Test-Path $serviceExe) "exe at $serviceExe"

    # Deploy a unit manifest with a UNIQUE pipe name (so we never collide with the user's screenease.core).
    $manifest = @{
        id = $unitId
        toolId = "screenease"
        displayName = "ScreenEase Service"
        exec = $serviceExe
        arguments = @("--pipe", $pipeName, "--heartbeat-file", (Join-Path $dataRoot 'screenease.heartbeat'), "--instance-token", "verify-$runId")
        workingDirectory = ""
        environment = @{ "ScreenEase__Transport" = "pipe" }
        autostart = $true
        restartPolicy = @{ maxRestarts = 5; backoffMs = 2000 }
        readiness = @{ kind = "none"; address = ""; timeoutMs = 5000 }
        stopTimeoutMs = 5000
        dataRoots = @()
        dependsOn = @()
        instanceToken = "verify-$runId"
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $unitsDir "$unitId.json") -Encoding UTF8

    # ---- Launch ServiceManager (autostarts the unit) ----
    Write-Host "==> Launching ServiceManager (autostarts the unit)..." -ForegroundColor Cyan
    $smProcess = Start-ServiceManager -DataRoot $dataRoot -DeployRoot $deployRoot -LogPath (Join-Path $dataRoot 'sm1.log')
    Add-Record "sm-running" (-not $smProcess.HasExited) "ServiceManager PID=$($smProcess.Id)"

    # Query the unit PID directly from the process table (matched by the unique pipe name in its command line).
    Start-Sleep -Seconds 2
    $firstPid = Get-UnitPid -PipeName $pipeName
    Add-Record "unit-autostarted" ($null -ne $firstPid) "first unit PID=$firstPid"

    # Verify readiness via the pipe.
    $pong = Send-Ping -PipeName $pipeName
    Add-Record "readiness-ping" $pong "pipe $pipeName answered ping"

    # ---- THE CORE TEST: restart ServiceManager, confirm unit PID unchanged (re-adoption) ----
    Write-Host "==> Restarting ServiceManager to verify re-adoption..." -ForegroundColor Cyan
    Stop-ServiceManagerGraceful -Process $smProcess -DataRoot $dataRoot
    $smProcess = Start-ServiceManager -DataRoot $dataRoot -DeployRoot $deployRoot -LogPath (Join-Path $dataRoot 'sm2.log')
    Start-Sleep -Seconds 3   # give ReconcileAsync time to re-adopt

    $secondPid = Get-UnitPid -PipeName $pipeName
    $samePid = ($null -ne $firstPid) -and ($secondPid -eq $firstPid)
    Add-Record "reattach-same-pid" $samePid "first=$firstPid second=$secondPid (must be equal)"

    # Confirm exactly one instance exists (no duplicate spawned by the re-adopt path).
    $instanceCount = @(Get-CimInstance Win32_Process -Filter "Name='ScreenEase.Service.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*$pipeName*" }).Count
    Add-Record "single-instance" ($instanceCount -eq 1) "found $instanceCount instance(s) (expect 1)"

    # Unit should still answer ping after manager restart.
    $pong2 = Send-Ping -PipeName $pipeName
    Add-Record "readiness-after-restart" $pong2 "pipe still answering after SM restart"

    $passed = ($records | Where-Object { -not $_.Passed }).Count -eq 0
    $result = [pscustomobject]@{
        Gate = "ScreenEase-Service-Liveness"
        Passed = $passed
        RunId = $runId
        UnitId = $unitId
        FirstPid = $firstPid
        SecondPid = $secondPid
        Records = $records
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -Path $resultPath -Encoding UTF8

    Write-Host ""
    Write-Host "==> ScreenEase.Service liveness: $(if($passed){'PASS'}else{'FAIL'})" -ForegroundColor $(if($passed){'Green'}else{'Red'})
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
    # Kill the unit process we started (by matching our unique pipe / token). Never touch screenease.core proper.
    Get-CimInstance Win32_Process -Filter "Name='ScreenEase.Service.exe'" -ErrorAction SilentlyContinue | Where-Object { $_.CommandLine -like "*$pipeName*" } | ForEach-Object {
        try { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue } catch {}
    }
    if (Test-Path $dataRoot) {
        try { Remove-Item -Recurse -Force $dataRoot -ErrorAction SilentlyContinue } catch {}
    }
}
