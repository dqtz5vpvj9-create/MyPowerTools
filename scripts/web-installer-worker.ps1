[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Quiesce', 'Finalize')]
    [string]$Phase,
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot,
    [Parameter(Mandatory = $true)]
    [string]$DataRoot,
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [Parameter(Mandatory = $true)]
    [string]$ResultPath,
    [switch]$InstallSmartBird
)

$ErrorActionPreference = 'Stop'
[Environment]::SetEnvironmentVariable('DOTNET_ROOT', $null, 'Process')

function Write-InstallerLog {
    param([Parameter(Mandatory = $true)][string]$Message)

    $line = '[{0:HH:mm:ss}] {1}' -f [DateTime]::Now, $Message
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function ConvertTo-WindowsCommandLineArgument {
    param([AllowEmptyString()][string]$Value)

    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [Text.StringBuilder]::new('"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $backslashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (($backslashes * 2) + 1)))
            [void]$builder.Append('"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) {
            [void]$builder.Append(('\' * $backslashes))
            $backslashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($backslashes -gt 0) {
        [void]$builder.Append(('\' * ($backslashes * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Start-GracefulClient {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
        Write-InstallerLog "$Description：组件尚未安装，跳过。"
        return
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = Split-Path -Parent $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = ($ArgumentList |
        ForEach-Object { ConvertTo-WindowsCommandLineArgument -Value $_ }) -join ' '
    try {
        $process = [Diagnostics.Process]::Start($startInfo)
        if ($null -ne $process) {
            $process.Dispose()
        }
        Write-InstallerLog "$Description：已发送关闭请求。"
    }
    catch {
        Write-InstallerLog "$Description：关闭请求启动失败，稍后执行强制关闭。$($_.Exception.Message)"
    }
}

function Invoke-LoggedPowerShell {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][hashtable]$Parameters,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        throw "$Description 脚本缺失：$ScriptPath"
    }

    Write-InstallerLog "$Description：开始。"
    & $ScriptPath @Parameters 2>&1 |
        ForEach-Object { Write-InstallerLog ([string]$_) }
    if (-not $?) {
        throw "$Description 执行失败。"
    }
    Write-InstallerLog "$Description：完成。"
}

function Invoke-NativeLogged {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList,
        [ValidateRange(1, 60)][int]$TimeoutSeconds = 15,
        [switch]$SuppressOutput
    )

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = ($ArgumentList |
        ForEach-Object { ConvertTo-WindowsCommandLineArgument -Value $_ }) -join ' '
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $outputTask = $process.StandardOutput.ReadToEndAsync()
        $errorTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            try { $process.Kill() } catch {}
            throw "Native process timed out after $TimeoutSeconds seconds: $FilePath"
        }
        $output = [string]$outputTask.GetAwaiter().GetResult()
        $errorOutput = [string]$errorTask.GetAwaiter().GetResult()
        if (-not $SuppressOutput) {
            foreach ($line in @($output, $errorOutput) -split "`r?`n" |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
                Write-InstallerLog $line
            }
        }
        return [int]$process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-Quiesce {
    $shell = Join-Path $InstallRoot 'Shell\MyPowerTools.Shell.Avalonia.exe'
    $cli = Join-Path $InstallRoot 'Cli\MyPowerTools.Cli.exe'

    Write-InstallerLog '正在请求 MyPowerTools 组件正常退出。'
    Start-GracefulClient -FilePath $shell -ArgumentList @('--shutdown-shell') -Description 'Shell'
    Start-GracefulClient -FilePath $shell -ArgumentList @(
        '--smoke', '--timeout-ms', '5000', '--quit-runner',
        '--modules', (Join-Path $InstallRoot 'modules'),
        '--data-root', $DataRoot
    ) -Description 'Runner'
    if (Test-Path -LiteralPath (Join-Path $InstallRoot 'Runtimes\Doubao') -PathType Container) {
        Start-GracefulClient -FilePath $shell -ArgumentList @(
            '--doubao-runtime', 'stop',
            '--doubao-runtime-root', (Join-Path $InstallRoot 'Runtimes\Doubao'),
            '--doubao-data-root', (Join-Path $DataRoot 'Doubao')
        ) -Description 'Doubao Runtime'
    }
    Start-GracefulClient -FilePath $cli -ArgumentList @('service', 'quiesce') -Description 'ServiceManager'
    foreach ($unitId in @(
        'remote-notifications.service',
        'screenease.service',
        'adb-forwarder.service',
        'ddns.service',
        'doubao-agent.controller.service'
    )) {
        Start-GracefulClient -FilePath $cli -ArgumentList @('service', 'stop', $unitId) -Description $unitId
    }
    Start-GracefulClient -FilePath $cli -ArgumentList @('service', 'shutdown') -Description '旧版 ServiceManager'

    $scheduledTaskExitCode = Invoke-NativeLogged `
        -FilePath "$env:SystemRoot\System32\schtasks.exe" `
        -ArgumentList @('/End', '/TN', '\MyPowerTools WinSpace Shift') `
        -SuppressOutput
    Write-InstallerLog "WinSpace Shift 任务停止命令退出码：$scheduledTaskExitCode。"
    Start-Sleep -Milliseconds 1500

    Write-InstallerLog '正在清理仍未退出的 MyPowerTools 进程。'
    $images = @(
        'MyPowerTools.Shell.Avalonia.exe',
        'MyPowerTools.Runner.exe',
        'MyPowerTools.ServiceManager.exe',
        'MyPowerTools.WebToolHost.exe',
        'MyPowerTools.InputRemapHost.exe',
        'MyPowerTools.Broker.exe',
        'MyPowerTools.ElevatedBroker.exe',
        'MyPowerTools.Cli.exe',
        'AdbForwarder.Service.exe',
        'DoubaoAgent.Controller.Service.exe',
        'RemoteNotifications.Service.exe',
        'ScreenEase.Service.exe'
    )
    $taskkillArguments = @('/F', '/T')
    foreach ($image in $images) {
        $taskkillArguments += @('/IM', $image)
    }
    $taskkillExitCode = Invoke-NativeLogged `
        -FilePath "$env:SystemRoot\System32\taskkill.exe" `
        -ArgumentList $taskkillArguments `
        -SuppressOutput
    Write-InstallerLog "强制进程清理命令退出码：$taskkillExitCode。"

    $survivors = [System.Collections.Generic.List[string]]::new()
    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        $path = $null
        try { $path = $process.MainModule.FileName } catch {}
        if ($path -and $path.StartsWith(
            ([IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'),
            [StringComparison]::OrdinalIgnoreCase)) {
            try {
                Stop-Process -Id $process.Id -Force -ErrorAction Stop
                Wait-Process -Id $process.Id -Timeout 3 -ErrorAction SilentlyContinue
            }
            catch {
                $survivors.Add("$($process.ProcessName) (PID $($process.Id))")
            }
        }
        $process.Dispose()
    }
    if ($survivors.Count -gt 0) {
        throw "以下 MyPowerTools 进程无法关闭：$($survivors -join ', ')"
    }
    Write-InstallerLog '所有已安装组件已退出，可以安全更新文件。'
}

function Invoke-Finalize {
    Invoke-LoggedPowerShell `
        -ScriptPath (Join-Path $InstallRoot 'configure-user-services.ps1') `
        -Parameters @{
            Mode = 'Install'
            InstallRoot = $InstallRoot
            DataRoot = $DataRoot
        } `
        -Description '注册 MyPowerTools 后台服务'

    if ($InstallSmartBird) {
        $smartBirdRoot = Join-Path $InstallRoot 'Runtimes\SmartBird'
        $pythonPath = Join-Path $InstallRoot 'Runtimes\Python312\python.exe'
        $smartBirdDataRoot = Join-Path $DataRoot 'SmartBird'
        Invoke-LoggedPowerShell `
            -ScriptPath (Join-Path $smartBirdRoot 'scripts\install-smartbird-thermostat-task.ps1') `
            -Parameters @{
                Mode = 'Install'
                RepoRoot = $smartBirdRoot
                PythonPath = $pythonPath
                DataRoot = $smartBirdDataRoot
            } `
            -Description '注册 SmartBird 温控任务'
        Invoke-LoggedPowerShell `
            -ScriptPath (Join-Path $smartBirdRoot 'scripts\install-energy-server-task.ps1') `
            -Parameters @{
                Mode = 'Install'
                RepoRoot = $smartBirdRoot
                PythonPath = $pythonPath
                DataRoot = $smartBirdDataRoot
                SettingsFile = (Join-Path $smartBirdDataRoot 'settings.json')
            } `
            -Description '注册 SmartBird 能耗服务'
    }
}

$resultTempPath = "$ResultPath.tmp"
Remove-Item -LiteralPath $ResultPath, $resultTempPath -Force -ErrorAction SilentlyContinue
try {
    if ($Phase -eq 'Quiesce') {
        Invoke-Quiesce
    } else {
        Invoke-Finalize
    }
    Set-Content -LiteralPath $resultTempPath -Value '0' -Encoding Ascii
}
catch {
    Write-InstallerLog "错误：$($_.Exception.Message)"
    Set-Content -LiteralPath $resultTempPath -Value '1' -Encoding Ascii
}
finally {
    Move-Item -LiteralPath $resultTempPath -Destination $ResultPath -Force
}
