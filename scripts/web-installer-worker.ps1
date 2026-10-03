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

function Initialize-ProcessImageReader {
    # Process.MainModule needs PROCESS_VM_READ, which a per-user process never gets for a
    # program running as administrator. QueryFullProcessImageName only needs
    # PROCESS_QUERY_LIMITED_INFORMATION, so elevated MyPowerTools processes are found too.
    try {
        Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace Mpt {
    public static class ProcessImage {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        public static string Get(int processId) {
            IntPtr process = OpenProcess(0x1000, false, processId);
            if (process == IntPtr.Zero) { return null; }
            try {
                StringBuilder name = new StringBuilder(32768);
                int size = name.Capacity;
                return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString(0, size) : null;
            }
            finally { CloseHandle(process); }
        }
    }
}
'@
        return $true
    }
    catch {
        Write-InstallerLog "无法加载进程路径读取组件，改用基本方式：$($_.Exception.Message)"
        return $false
    }
}

$script:ProcessImageReaderReady = $null

function Get-ProcessImagePath {
    param([Parameter(Mandatory = $true)][Diagnostics.Process]$Process)

    if ($null -eq $script:ProcessImageReaderReady) {
        $script:ProcessImageReaderReady = Initialize-ProcessImageReader
    }
    $path = $null
    if ($script:ProcessImageReaderReady) {
        try { $path = [Mpt.ProcessImage]::Get($Process.Id) } catch {}
    }
    if (-not $path) {
        try { $path = $Process.MainModule.FileName } catch {}
    }
    return $path
}

function Get-ProcessesUnderRoot {
    param([Parameter(Mandatory = $true)][string]$RootPrefix)

    foreach ($process in Get-Process -ErrorAction SilentlyContinue) {
        try {
            if ($process.Id -eq $PID) { continue }
            $path = Get-ProcessImagePath -Process $process
            if ($path -and $path.StartsWith($RootPrefix, [StringComparison]::OrdinalIgnoreCase) -and
                -not ([IO.Path]::GetFileName($path) -like 'unins*')) {
                [pscustomobject]@{ Id = $process.Id; Name = $process.ProcessName; Path = $path }
            }
        }
        finally {
            $process.Dispose()
        }
    }
}

function Stop-ScheduledTasksUnderRoot {
    param([Parameter(Mandatory = $true)][string]$RootPrefix)

    # Tasks whose action runs something from the install directory (SmartBird services, the
    # OTA check) could start a program in the middle of the update. End running instances;
    # their triggers are daily or at sign-in, so they do not fire during the install.
    try {
        foreach ($task in @(Get-ScheduledTask -ErrorAction Stop)) {
            $hits = @($task.Actions | Where-Object {
                ([string]$_.Execute + ' ' + [string]$_.Arguments).IndexOf(
                    $RootPrefix.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase) -ge 0
            })
            if ($hits.Count -gt 0 -and [string]$task.State -eq 'Running') {
                try {
                    Stop-ScheduledTask -InputObject $task -ErrorAction Stop
                    Write-InstallerLog "已停止计划任务：$($task.TaskName)"
                }
                catch {
                    Write-InstallerLog "无法停止计划任务 $($task.TaskName)：$($_.Exception.Message)"
                }
            }
        }
    }
    catch {
        Write-InstallerLog "无法读取计划任务列表：$($_.Exception.Message)"
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

    $rootPrefix = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\'
    Stop-ScheduledTasksUnderRoot -RootPrefix $rootPrefix

    # Anything else whose program file lives under the install directory: service units,
    # python/pythonw and adb from Runtimes, helpers added by later versions. Supervisors are
    # stopped first so they cannot restart their children; a few rounds catch respawns.
    $order = @{ 'MyPowerTools.ServiceManager' = 0; 'MyPowerTools.Runner' = 1; 'MyPowerTools.Shell.Avalonia' = 2 }
    for ($round = 1; $round -le 3; $round++) {
        $targets = @(Get-ProcessesUnderRoot -RootPrefix $rootPrefix |
            Sort-Object { if ($order.ContainsKey($_.Name)) { $order[$_.Name] } else { 10 } })
        if ($targets.Count -eq 0) { break }
        foreach ($target in $targets) {
            try {
                Stop-Process -Id $target.Id -Force -ErrorAction Stop
                Write-InstallerLog "已关闭：$($target.Name)（PID $($target.Id)）"
            }
            catch {
                Write-InstallerLog "无法关闭：$($target.Name)（PID $($target.Id)）：$($_.Exception.Message)"
            }
        }
        foreach ($target in $targets) {
            Wait-Process -Id $target.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
    }

    # What is left runs as administrator (a per-user process cannot end it). That never blocks
    # the install - running program files can still be moved aside - but the installer may
    # offer a single UAC prompt to end them, so report them instead of failing.
    $survivors = @(Get-ProcessesUnderRoot -RootPrefix $rootPrefix)
    $elevatedPath = "$ResultPath.elevated"
    Remove-Item -LiteralPath $elevatedPath -Force -ErrorAction SilentlyContinue
    if ($survivors.Count -gt 0) {
        $lines = @($survivors | ForEach-Object { "$($_.Id)`t$($_.Name)" })
        [IO.File]::WriteAllLines($elevatedPath, [string[]]$lines, [Text.UTF8Encoding]::new($false))
        Write-InstallerLog "仍在运行（可能以管理员身份运行）：$(@($survivors | ForEach-Object { $_.Name }) -join ', ')"
        return
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
