[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ManagedExecutable,
    [string] $EvidencePath,
    [switch] $ConfirmIsolatedScmMutation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'The nssm-manager service-mode smoke test requires Windows.' }
if (-not $ConfirmIsolatedScmMutation) { throw 'Pass -ConfirmIsolatedScmMutation to create and delete one isolated temporary service.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this smoke test from an elevated terminal.' }

$ManagedExecutable = (Resolve-Path -LiteralPath $ManagedExecutable).Path
$serviceName = 'NssmManagerSmoke_' + [Guid]::NewGuid().ToString('N')
$steps = [Collections.Generic.List[object]]::new()

function Invoke-CapturedProcess {
    param([string[]] $Arguments)
    $startInfo = [Diagnostics.ProcessStartInfo]::new($ManagedExecutable)
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $startInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($argument in $Arguments) { [void] $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return [ordered]@{ arguments = $Arguments; exitCode = $process.ExitCode; stdout = $stdout; stderr = $stderr }
    }
    finally { $process.Dispose() }
}

function Wait-ServiceState {
    param([string] $Expected, [int] $TimeoutSeconds = 30)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
        if ($null -ne $service -and $service.State -eq $Expected) { return $service }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Service '$serviceName' did not reach state '$Expected' within $TimeoutSeconds seconds."
}

function Remove-SmokeService {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) { return }
    if ($service.Status -ne [ServiceProcess.ServiceControllerStatus]::Stopped) { & sc.exe stop $serviceName 2>$null | Out-Null }
    & sc.exe delete $serviceName 2>$null | Out-Null
    for ($index = 0; $index -lt 100; $index++) {
        if ($null -eq (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 100
    }
    throw "Temporary service '$serviceName' could not be removed."
}

$document = $null
try {
    Remove-SmokeService
    $install = Invoke-CapturedProcess @('install', $serviceName, $env:ComSpec, '/d', '/s', '/c', 'ping -t 127.0.0.1 >NUL')
    $steps.Add($install)
    if ($install.exitCode -ne 0) { throw "Install failed with exit code $($install.exitCode): $($install.stderr)" }

    $start = Invoke-CapturedProcess @('start', $serviceName)
    $steps.Add($start)
    if ($start.exitCode -ne 0) { throw "Start failed with exit code $($start.exitCode): $($start.stderr)" }
    $running = Wait-ServiceState 'Running'
    if ([uint32] $running.ProcessId -eq 0) { throw 'SCM reported a running service without a host PID.' }
    $hostProcess = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$($running.ProcessId)"
    $hostPath = [IO.Path]::GetFullPath([string] $hostProcess.ExecutablePath)
    if (-not [IO.Path]::GetFileName($hostPath).Equals('nssm-manager.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw "SCM started unexpected executable '$hostPath'."
    }

    $pause = Invoke-CapturedProcess @('pause', $serviceName)
    $steps.Add($pause)
    if ($pause.exitCode -ne 1) { throw "A running NSSM service must reject PAUSE with exit code 1; actual exit code was $($pause.exitCode)." }
    [void] (Wait-ServiceState 'Running')

    $rotate = Invoke-CapturedProcess @('rotate', $serviceName)
    $steps.Add($rotate)
    if ($rotate.exitCode -ne 0) { throw "Rotate failed with exit code $($rotate.exitCode): $($rotate.stderr)" }

    $processes = Invoke-CapturedProcess @('processes', $serviceName)
    $steps.Add($processes)
    if ($processes.exitCode -ne 0 -or $processes.stdout -notmatch '(?im)cmd\.exe') { throw 'The supervised cmd.exe child was not present in the reported process tree.' }

    $stop = Invoke-CapturedProcess @('stop', $serviceName)
    $steps.Add($stop)
    if ($stop.exitCode -ne 0) { throw "Stop failed with exit code $($stop.exitCode): $($stop.stderr)" }
    [void] (Wait-ServiceState 'Stopped')

    $setParameters = Invoke-CapturedProcess @('set', $serviceName, 'AppParameters', '/d', '/s', '/c', 'exit 1')
    $steps.Add($setParameters)
    if ($setParameters.exitCode -ne 0) { throw "AppParameters update failed with exit code $($setParameters.exitCode): $($setParameters.stderr)" }
    $setThrottle = Invoke-CapturedProcess @('set', $serviceName, 'AppThrottle', '10000')
    $steps.Add($setThrottle)
    if ($setThrottle.exitCode -ne 0) { throw "AppThrottle update failed with exit code $($setThrottle.exitCode): $($setThrottle.stderr)" }

    & sc.exe start $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "SCM start for the throttle scenario failed with exit code $LASTEXITCODE." }
    [void] (Wait-ServiceState 'Paused')
    $continue = Invoke-CapturedProcess @('continue', $serviceName)
    $steps.Add($continue)
    if ($continue.exitCode -notin @(0, 1)) { throw "Continue failed with exit code $($continue.exitCode): $($continue.stderr)" }
    if ($continue.exitCode -eq 1 -and $continue.stderr -notmatch 'SERVICE_START_PENDING.+CONTINUE') {
        throw "Continue returned an unexpected failure: $($continue.stderr)"
    }
    [void] (Wait-ServiceState 'Paused')

    $stopThrottled = Invoke-CapturedProcess @('stop', $serviceName)
    $steps.Add($stopThrottled)
    if ($stopThrottled.exitCode -ne 0) { throw "Throttled stop failed with exit code $($stopThrottled.exitCode): $($stopThrottled.stderr)" }
    $stopped = Wait-ServiceState 'Stopped'

    $remove = Invoke-CapturedProcess @('remove', $serviceName, 'confirm')
    $steps.Add($remove)
    if ($remove.exitCode -ne 0) { throw "Remove failed with exit code $($remove.exitCode): $($remove.stderr)" }

    $document = [ordered]@{
        schemaVersion = 1
        generatedAt = [DateTimeOffset]::UtcNow
        baseline = '2.24-101-g897c7ad'
        serviceName = $serviceName
        passed = $true
        hostProcessId = [uint32] $running.ProcessId
        hostExecutable = $hostPath
        finalState = [string] $stopped.State
        steps = $steps.ToArray()
    }
}
catch {
    $document = [ordered]@{
        schemaVersion = 1
        generatedAt = [DateTimeOffset]::UtcNow
        baseline = '2.24-101-g897c7ad'
        serviceName = $serviceName
        passed = $false
        error = $_.Exception.ToString()
        steps = $steps.ToArray()
    }
    throw
}
finally {
    Remove-SmokeService
    if ($null -ne $document -and -not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        $fullEvidencePath = [IO.Path]::GetFullPath($EvidencePath)
        [IO.Directory]::CreateDirectory((Split-Path -Parent $fullEvidencePath)) | Out-Null
        $document | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $fullEvidencePath -Encoding UTF8
    }
}

$document | Select-Object baseline,serviceName,passed,hostProcessId,hostExecutable,finalState | Format-List
