[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\MyPowerTools'),
    [string]$DataRoot = (Join-Path $env:LOCALAPPDATA 'MyPowerTools'),
    [string]$WrapperScript = '',
    [string[]]$WrapperArgs = @(),
    [int]$TimeoutSeconds = 150
)

$ErrorActionPreference = 'Stop'

$interactiveUser = [string](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName
if ([string]::IsNullOrWhiteSpace($interactiveUser)) {
    throw 'No interactive desktop user is logged in on this machine.'
}

$wrapperScript = $WrapperScript
if ([string]::IsNullOrWhiteSpace($wrapperScript)) {
    $wrapperScript = Join-Path $PSScriptRoot 'dorm-start-runtime-remote.ps1'
}
if (-not (Test-Path -LiteralPath $wrapperScript -PathType Leaf)) {
    $wrapperScript = Join-Path $InstallRoot 'dorm-start-runtime-remote.ps1'
}
if (-not (Test-Path -LiteralPath $wrapperScript -PathType Leaf)) {
    throw "Runtime wrapper script was not found: $wrapperScript"
}

$taskName = "MyPowerToolsRuntimeStart-$PID-$([Guid]::NewGuid().ToString('N'))"
$resultPath = Join-Path $env:TEMP "mpt-runtime-start-result-$taskName.json"
$consoleLog = Join-Path $env:TEMP "mpt-runtime-task-console-$taskName.log"
$windowsPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$actionArguments = "/c `"`"$windowsPowerShell`" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -File `"$wrapperScript`" -InstallRoot `"$InstallRoot`" -DataRoot `"$DataRoot`" -ResultPath `"$resultPath`""
foreach ($wrapperArg in $WrapperArgs) {
    $actionArguments += " `"$wrapperArg`""
}
$actionArguments += " > `"$consoleLog`" 2>&1`""
$action = New-ScheduledTaskAction `
    -Execute (Join-Path $env:WINDIR 'System32\cmd.exe') `
    -Argument $actionArguments `
    -WorkingDirectory (Split-Path -Parent $wrapperScript)
$principal = New-ScheduledTaskPrincipal `
    -UserId $interactiveUser `
    -LogonType Interactive `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
$registeredAt = Get-Date
$registered = $false

try {
    Register-ScheduledTask `
        -TaskName $taskName `
        -Action $action `
        -Principal $principal `
        -Settings $settings `
        -Description 'One-time MyPowerTools interactive runtime start' `
        -Force | Out-Null
    $registered = $true
    Start-ScheduledTask -TaskName $taskName

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
            $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
            $result | ConvertTo-Json -Depth 5
            if (-not [bool]$result.success) {
                exit 1
            }
            exit 0
        }
        $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        $info = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
        if ($null -ne $task -and $task.State -ne 'Running' -and
            $null -ne $info -and $info.LastRunTime -ge $registeredAt.AddSeconds(-1) -and
            $info.LastTaskResult -ne 267009) {
            if ($info.LastTaskResult -eq 0) {
                break
            }
            Start-Sleep -Seconds 2
            if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
                $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
                $result | ConvertTo-Json -Depth 5
                if (-not [bool]$result.success) {
                    exit 1
                }
                exit 0
            }
            $consoleTail = if (Test-Path -LiteralPath $consoleLog) {
                [IO.File]::ReadAllText($consoleLog, [Text.Encoding]::UTF8)
            } else {
                ''
            }
            throw "Runtime start task failed with result $($info.LastTaskResult). Console: $consoleTail"
        }
        Start-Sleep -Milliseconds 500
    }

    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        throw 'Runtime start timed out without producing a result file.'
    }
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 5
    if (-not [bool]$result.success) {
        exit 1
    }
}
finally {
    if ($registered) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}
