[CmdletBinding()]
param(
    [int] $Seconds = 120,
    [string] $OutputPath = (Join-Path $env:TEMP ('mpt-console-window-diagnostics-{0:yyyyMMdd-HHmmss}.log' -f (Get-Date)))
)

# Diagnoses "something keeps creating console windows" complaints.
#
# Build and test runs must never paint a console window on the user's desktop
# (see AGENTS.md "构建与测试不得弹出命令行窗口", enforced by the Architecture Gate
# rule A1.16-no-console-window-in-build-and-test). When a window does appear, this
# script records which process created it: every new top-level window is logged
# with its class, owning process, visibility, on-screen size, and lifetime, and
# every new process is logged with its parent and command line.
#
# Usage:
#   pwsh -File scripts/diagnose-console-windows.ps1 -Seconds 300
#   # reproduce the problem in another window while it runs, then read the log

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class MptWindowSpy
{
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

    public static List<string> Snapshot()
    {
        var found = new List<string>();
        EnumWindows((window, parameter) =>
        {
            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            var title = new StringBuilder(256);
            GetWindowText(window, title, title.Capacity);
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            string processName = "";
            try { processName = System.Diagnostics.Process.GetProcessById((int)processId).ProcessName; } catch { }
            RECT rect;
            int width = 0, height = 0;
            if (GetWindowRect(window, out rect)) { width = rect.Right - rect.Left; height = rect.Bottom - rect.Top; }
            int cloaked = 0;
            try { DwmGetWindowAttribute(window, 14, out cloaked, 4); } catch { }
            found.Add(((long)window).ToString() + "|" + className + "|" + processId + "|" + processName + "|" +
                      (IsWindowVisible(window) ? "visible" : "hidden") + "|" + cloaked + "|" + width + "x" + height + "|" + title);
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

function Write-ReportLine {
    param([string] $Text)
    Add-Content -LiteralPath $OutputPath -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $Text)
}

$consoleClasses = @('ConsoleWindowClass', 'PseudoConsoleWindow', 'CASCADIA_HOSTING_WINDOW_CLASS')
$processes = @{}
foreach ($process in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue) { $processes["$($process.ProcessId)"] = $true }

$windows = @{}
$suspicious = [Collections.Generic.List[string]]::new()
$snapshot = [MptWindowSpy]::Snapshot()
foreach ($entry in $snapshot) { $windows[($entry -split '\|')[0]] = (Get-Date) }
Write-ReportLine "START seconds=$Seconds windows=$($snapshot.Count) processes=$($processes.Count)"

$lastProcessPoll = [DateTime]::MinValue
$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
while ([DateTime]::UtcNow -lt $deadline) {
    $now = Get-Date
    $snapshot = [MptWindowSpy]::Snapshot()
    $present = @{}
    foreach ($entry in $snapshot) {
        $parts = $entry -split '\|'
        $present[$parts[0]] = $true
        if ($windows.ContainsKey($parts[0])) { continue }
        $windows[$parts[0]] = $now
        $isVisible = $parts[4] -eq 'visible'
        $isCloaked = $parts[5] -ne '0'
        $looksConsole = $consoleClasses -contains $parts[1]
        Write-ReportLine ("WINDOW-NEW class={0} pid={1} process={2} {3} cloaked={4} size={5} title='{6}'" -f $parts[1], $parts[2], $parts[3], $parts[4], $parts[5], $parts[6], $parts[7])
        if ($looksConsole -and $isVisible -and -not $isCloaked) {
            $line = "CONSOLE-WINDOW pid=$($parts[2]) process=$($parts[3]) class=$($parts[1]) size=$($parts[6]) title='$($parts[7])'"
            $suspicious.Add($line)
            Write-ReportLine ("*** $line")
        }
    }
    foreach ($handle in @($windows.Keys)) {
        if ($present.ContainsKey($handle)) { continue }
        Write-ReportLine ("WINDOW-GONE handle=$handle lifetimeMs={0}" -f [int]($now - $windows[$handle]).TotalMilliseconds)
        $windows.Remove($handle)
    }
    if (($now - $lastProcessPoll).TotalMilliseconds -gt 150) {
        $lastProcessPoll = $now
        foreach ($process in Get-CimInstance Win32_Process -ErrorAction SilentlyContinue) {
            $key = "$($process.ProcessId)"
            if ($processes.ContainsKey($key)) { continue }
            $processes[$key] = $true
            $parentText = ''
            try {
                $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ParentProcessId)" -ErrorAction SilentlyContinue
                if ($parent) { $parentText = "$($parent.Name)/$($process.ParentProcessId)" }
            } catch { }
            $commandLine = ($process.CommandLine -replace '\s+', ' ')
            if ($commandLine.Length -gt 160) { $commandLine = $commandLine.Substring(0, 160) }
            Write-ReportLine ("PROCESS-NEW $($process.Name) pid=$key parent=$parentText $commandLine")
        }
    }
    Start-Sleep -Milliseconds 15
}

Write-ReportLine "SUMMARY visibleConsoleWindows=$($suspicious.Count)"
foreach ($line in $suspicious) { Write-ReportLine "SUMMARY $line" }
Write-ReportLine "DONE log=$OutputPath"
Write-Output "Diagnostics written to $OutputPath"
if ($suspicious.Count -gt 0) {
    Write-Output "Visible console windows detected:"
    $suspicious | ForEach-Object { Write-Output "  $_" }
} else {
    Write-Output 'No visible console window was created during the capture.'
}
