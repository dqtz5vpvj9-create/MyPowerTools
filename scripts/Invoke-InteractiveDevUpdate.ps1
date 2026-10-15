[CmdletBinding()]
param(
    [hashtable]$UpdateParameters = @{},
    [switch]$Worker,
    [string]$RequestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Worker) {
    $request = Import-Clixml -LiteralPath $RequestPath
    $exitCode = 1
    try {
        if ((Get-Process -Id $PID).SessionId -eq 0) { throw 'Interactive Dev task started in Session 0.' }
        $entryPoint = Join-Path $PSScriptRoot 'Start-MyPowerTools-Dev.ps1'
        $parameters = $request.Parameters
        & $entryPoint @parameters *> $request.Log
        $exitCode = $LASTEXITCODE
    }
    catch { $_ | Out-String | Add-Content -LiteralPath $request.Log -Encoding utf8 }
    finally { [IO.File]::WriteAllText($request.Result, [string]$exitCode) }
    exit $exitCode
}

$runId = [Guid]::NewGuid().ToString('N')
$runRoot = Join-Path $env:LOCALAPPDATA "MyPowerTools\dev-update-dispatch\$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$RequestPath = Join-Path $runRoot 'request.xml'
$logPath = Join-Path $runRoot 'update.log'
$resultPath = Join-Path $runRoot 'exit-code.txt'
foreach ($key in @('NoRestore', 'NoOpenShell', 'SkipArtifactsCheck')) {
    if ($UpdateParameters.ContainsKey($key)) { $UpdateParameters[$key] = [bool]$UpdateParameters[$key] }
}
@{ Parameters = $UpdateParameters; Log = $logPath; Result = $resultPath } |
    Export-Clixml -LiteralPath $RequestPath
$scheduler = New-Object -ComObject Schedule.Service
$scheduler.Connect()
$folder = $scheduler.GetFolder('\')
$taskName = "MyPowerTools-Dev-Update-$runId"
$registered = $false
try {
    $definition = $scheduler.NewTask(0)
    $definition.RegistrationInfo.Description = 'One-time development update in the logged-in user desktop session.'
    $definition.Principal.UserId = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $definition.Principal.LogonType = 3 # TASK_LOGON_INTERACTIVE_TOKEN; no stored password.
    $definition.Settings.ExecutionTimeLimit = 'PT45M'
    $definition.Settings.DisallowStartIfOnBatteries = $false
    $definition.Settings.StopIfGoingOnBatteries = $false
    $action = $definition.Actions.Create(0)
    $action.Path = Join-Path $env:WINDIR 'System32\conhost.exe'
    $workerPath = $PSCommandPath
    $pwshPath = Join-Path $PSHOME 'pwsh.exe'
    $action.Arguments = "--headless `"$pwshPath`" -NoLogo -NoProfile -NonInteractive -File `"$workerPath`" -Worker -RequestPath `"$RequestPath`""
    $action.WorkingDirectory = Split-Path -Parent $PSScriptRoot
    $task = $folder.RegisterTaskDefinition($taskName, $definition, 6, $definition.Principal.UserId, $null, 3)
    $registered = $true
    $desktopSessions = @(Get-Process -Name explorer -ErrorAction SilentlyContinue |
        Where-Object SessionId -gt 0 | Select-Object -ExpandProperty SessionId -Unique)
    if ($desktopSessions.Count -ne 1) { throw 'A single logged-in desktop session is required for the Dev update.' }
    [void]$task.RunEx($null, 4, $desktopSessions[0], $null) # TASK_RUN_USE_SESSION_ID
    Write-Host "Dev update dispatched to the logged-in desktop. Log: $logPath"
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(45)
    while (-not (Test-Path -LiteralPath $resultPath)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "Interactive Dev update timed out. Log: $logPath" }
        Start-Sleep -Milliseconds 1000
        if ($task.State -eq 3 -and -not (Test-Path -LiteralPath $resultPath)) {
            throw "Interactive Dev task failed with code $($task.LastTaskResult). Log: $logPath"
        }
    }
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath }
    exit ([int](Get-Content -LiteralPath $resultPath -Raw))
}
finally {
    if ($registered) { $folder.DeleteTask($taskName, 0) }
    Remove-Item -LiteralPath $RequestPath -Force -ErrorAction SilentlyContinue
}
