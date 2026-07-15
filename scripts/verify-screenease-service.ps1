<#
.SYNOPSIS
  Verifies that ScreenEase state and command execution live in ScreenEase.Service, survive a
  ServiceManager restart, and recover after the worker is killed. The run is fully isolated and
  uses logical display mode, so it never writes the user's Gamma ramp.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = (Get-Item $MyInvocation.MyCommand.Path).Directory.Parent.FullName
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
$dataRoot = Join-Path $env:TEMP "mpt-screenease-verify-$runId"
$deployRoot = Join-Path $dataRoot 'deploy'
$unitsRoot = Join-Path $deployRoot 'units'
$toolDataRoot = Join-Path $dataRoot 'tool-data'
$pipeName = "screenease-core-$runId"
$endpoint = "mypowertools.servicemanager.screenease-$runId"
$instanceName = "MyPowerTools.ServiceManager.ScreenEase.$runId"
$resultPath = Join-Path $repoRoot "artifacts\screenease-service-verify-$runId.json"
$managerProject = Join-Path $repoRoot 'src\MyPowerTools.ServiceManager\MyPowerTools.ServiceManager.csproj'
$gateProject = Join-Path $repoRoot 'tests\architecture-gate\ArchitectureGate.csproj'
$serviceProject = Join-Path $repoRoot 'tools\screenease\current-integration\src\ScreenEase.Service\ScreenEase.Service.csproj'
$manager = $null
$records = @()

function Add-Record([string]$Id, [bool]$Passed, [string]$Detail) {
    $script:records += [pscustomobject]@{ Id = $Id; Passed = $Passed; Detail = $Detail }
    Write-Host ("  [{0}] {1}: {2}" -f $(if ($Passed) { 'PASS' } else { 'FAIL' }), $Id, $Detail) -ForegroundColor $(if ($Passed) { 'Green' } else { 'Red' })
}

function Start-Manager([string]$LogName) {
    $env:MPT_DATA_ROOT = $dataRoot
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @(
        'run', '--no-build', '-c', 'Release', '--project', $managerProject, '--',
        '--data-root', $dataRoot,
        '--deploy-root', $deployRoot,
        '--endpoint-address', $endpoint,
        '--instance-name', $instanceName
    ) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $dataRoot $LogName) -RedirectStandardError (Join-Path $dataRoot "$LogName.err")
    Start-Sleep -Seconds 5
    return $process
}

function Stop-ManagerGracefully($Process) {
    $env:MPT_DATA_ROOT = $dataRoot
    & dotnet run --no-build -c Release --project $gateProject -- --mode shutdown --data-root $dataRoot --endpoint-address $endpoint 2>&1 | Out-Null
    if (-not $Process.WaitForExit(15000)) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 1
}

function Get-WorkerPid {
    $process = Get-CimInstance Win32_Process -Filter "Name='ScreenEase.Service.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$pipeName*" } |
        Sort-Object ProcessId |
        Select-Object -First 1
    if ($process) { return [int]$process.ProcessId }
    return 0
}

function Send-Request([object]$Request, [int]$TimeoutMs = 10000) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', $pipeName, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect($TimeoutMs)
        $payload = [Text.Encoding]::UTF8.GetBytes(($Request | ConvertTo-Json -Depth 12 -Compress))
        $header = [BitConverter]::GetBytes([int32]$payload.Length)
        $pipe.Write($header, 0, 4)
        $pipe.Write($payload, 0, $payload.Length)
        $pipe.Flush()

        $responseHeader = New-Object byte[] 4
        Read-Exactly -Stream $pipe -Buffer $responseHeader
        $length = [BitConverter]::ToInt32($responseHeader, 0)
        if ($length -le 0 -or $length -gt 4194304) { throw "Invalid response length $length" }
        $responsePayload = New-Object byte[] $length
        Read-Exactly -Stream $pipe -Buffer $responsePayload
        return [Text.Encoding]::UTF8.GetString($responsePayload) | ConvertFrom-Json
    }
    finally {
        $pipe.Dispose()
    }
}

function Read-Exactly([IO.Stream]$Stream, [byte[]]$Buffer) {
    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $read = $Stream.Read($Buffer, $offset, $Buffer.Length - $offset)
        if ($read -eq 0) { throw 'Unexpected end of pipe response.' }
        $offset += $read
    }
}

try {
    Write-Host "==> ScreenEase.Service product verification runId=$runId" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $dataRoot, $deployRoot, $unitsRoot, $toolDataRoot | Out-Null

    & dotnet build $managerProject -c Release --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ServiceManager build failed.' }
    & dotnet build $gateProject -c Release --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Architecture gate build failed.' }
    $serviceOutput = Join-Path $dataRoot 'service'
    & dotnet publish $serviceProject -c Release -o $serviceOutput --nologo -v quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'ScreenEase.Service publish failed.' }
    $serviceExe = Join-Path $serviceOutput 'ScreenEase.Service.exe'
    Add-Record 'service-published' (Test-Path -LiteralPath $serviceExe -PathType Leaf) $serviceExe

    $manifest = @{
        id = 'screenease.service'
        toolId = 'screenease'
        displayName = 'ScreenEase Service verification'
        exec = $serviceExe
        arguments = @('--pipe', $pipeName, '--heartbeat-file', (Join-Path $dataRoot 'heartbeat.log'), '--logical-only')
        workingDirectory = $serviceOutput
        environment = @{ MPT_TOOL_DATA_ROOT = $toolDataRoot }
        autostart = $true
        restartPolicy = @{ maxRestarts = 5; backoffMs = 300 }
        readiness = @{ kind = 'pipe'; address = $pipeName; timeoutMs = 8000 }
        stopTimeoutMs = 1000
        dataRoots = @($toolDataRoot)
        dependsOn = @()
        instanceToken = "screenease-verify-$runId"
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $unitsRoot 'screenease.service.json') -Encoding UTF8

    $manager = Start-Manager 'manager-1.log'
    Add-Record 'manager-started' (-not $manager.HasExited) "pid=$($manager.Id)"
    $firstPid = Get-WorkerPid
    Add-Record 'service-autostarted' ($firstPid -gt 0) "pid=$firstPid"

    $ping = Send-Request @{ command = 'ping' }
    Add-Record 'readiness-ping' ($ping.ok -eq $true -and $ping.data.pong -eq $true) "pipe=$pipeName"
    $status = Send-Request @{ command = 'execute'; invocationId = "status-$runId"; commandId = 'screenease.status.summary'; args = @{} }
    Add-Record 'real-module-status' ($status.ok -eq $true -and $status.data.success -eq $true -and $status.data.output -match 'profiles') 'ScreenEaseModule answered through service pipe'

    $beforeSettings = Send-Request @{ command = 'getSettings' }
    $beforeRevision = [uint64]$beforeSettings.data.revision
    $updatedSettings = Send-Request @{
        command = 'updateSettings'
        expectedRevision = $beforeRevision
        patch = @{ advanced = @{ smoothTransitions = $false; transitionDurationMs = 1234 } }
    }
    Add-Record 'settings-updated' ($updatedSettings.ok -eq $true -and [uint64]$updatedSettings.data.revision -eq ($beforeRevision + 1)) "revision=$($updatedSettings.data.revision)"

    $apply = Send-Request @{
        command = 'execute'
        invocationId = "apply-$runId"
        commandId = 'screenease.effect.apply'
        args = @{ colorTemperatureKelvin = 4321; brightnessPercent = 67; displayId = 'all'; hardwareWrite = $false }
    }
    $applyOutput = if ($apply.data.output) { $apply.data.output | ConvertFrom-Json } else { $null }
    Add-Record 'logical-effect-applied' ($apply.ok -eq $true -and $apply.data.success -eq $true -and $applyOutput.effect.colorTemperatureKelvin -eq 4321 -and $applyOutput.effect.brightnessPercent -eq 67) '4321 K / 67% persisted by service runtime'

    Stop-ManagerGracefully $manager
    $manager = Start-Manager 'manager-2.log'
    $secondPid = Get-WorkerPid
    Add-Record 'manager-restart-readopts-worker' ($secondPid -eq $firstPid -and $secondPid -gt 0) "first=$firstPid second=$secondPid"

    Stop-Process -Id $secondPid -Force
    $thirdPid = 0
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        Start-Sleep -Milliseconds 250
        $candidate = Get-WorkerPid
        if ($candidate -gt 0 -and $candidate -ne $secondPid) { $thirdPid = $candidate; break }
    }
    Add-Record 'worker-crash-recovered' ($thirdPid -gt 0) "crashed=$secondPid recovered=$thirdPid"

    $afterSettings = Send-Request @{ command = 'getSettings' }
    $advanced = $afterSettings.data.values.advanced
    Add-Record 'settings-survive-recovery' ([uint64]$afterSettings.data.revision -eq ($beforeRevision + 1) -and $advanced.smoothTransitions -eq $false -and $advanced.transitionDurationMs -eq 1234) "revision=$($afterSettings.data.revision)"
    $effect = Send-Request @{ command = 'execute'; invocationId = "effect-$runId"; commandId = 'screenease.effect.status'; args = @{} }
    $effectOutput = $effect.data.output | ConvertFrom-Json
    Add-Record 'effect-survives-recovery' ($effect.data.success -eq $true -and $effectOutput.colorTemperatureKelvin -eq 4321 -and $effectOutput.brightnessPercent -eq 67) 'service restart restored persisted logical effect'

    $passed = @($records | Where-Object { -not $_.Passed }).Count -eq 0
    [ordered]@{
        gate = 'ScreenEase-Service-Product'
        passed = $passed
        runId = $runId
        firstPid = $firstPid
        secondPid = $secondPid
        thirdPid = $thirdPid
        records = $records
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding UTF8
    Write-Host "==> ScreenEase.Service: $(if ($passed) { 'PASS' } else { 'FAIL' })" -ForegroundColor $(if ($passed) { 'Green' } else { 'Red' })
    Write-Host "==> Result: $resultPath"
    if (-not $passed) { exit 1 }
}
catch {
    Write-Host "FAIL: $_" -ForegroundColor Red
    $_.ScriptStackTrace | Out-Host
    exit 1
}
finally {
    if ($manager -and -not $manager.HasExited) {
        Stop-Process -Id $manager.Id -Force -ErrorAction SilentlyContinue
    }
    Get-CimInstance Win32_Process -Filter "Name='ScreenEase.Service.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like "*$pipeName*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $dataRoot) {
        Remove-Item -LiteralPath $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
