# Read-only visible-Home probe. Run with Windows PowerShell 5.1 in the desktop session.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExpectedShellPath,
    [Parameter(Mandatory = $true)][string]$TracePath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$ScreenshotPath,
    [Parameter(Mandatory = $true)][string]$StartedAfterUtc,
    [ValidateRange(1, 86400)][int]$TimeoutSeconds = 1900,
    [string]$RejectedProcessIds = ''
)
$ErrorActionPreference = 'Stop'
$diagnostics = New-Object 'System.Collections.Generic.List[string]'
$result = [ordered]@{
    status = 'BLOCKED'; rule = 'fresh-live-home-visible-responsive-positive-tool-count'
    observedTimestampTicks = $null; stopwatchFrequency = [Diagnostics.Stopwatch]::Frequency
    observedUtc = $null; processId = $null; process = $null; window = $null
    tracePath = $TracePath; tracePhases = @(); traceLines = @(); uiaInventory = @()
    screenshotPath = $ScreenshotPath; screenshotCaptured = $false; lastFailure = $null
}
function Add-Diagnostic([string]$Message) {
    if (-not $diagnostics.Contains($Message)) { $diagnostics.Add($Message) }
}
function Save-Result {
    $result.diagnostics = @($diagnostics.ToArray())
    $result.snapshotUtc = [DateTime]::UtcNow.ToString('o')
    $fullPath = [IO.Path]::GetFullPath($OutputPath); $temporaryPath = $fullPath + '.tmp'
    $parent = Split-Path -Parent $fullPath
    [void][IO.Directory]::CreateDirectory($parent)
    [IO.File]::WriteAllText($temporaryPath, ($result | ConvertTo-Json -Depth 10), [Text.Encoding]::UTF8)
    if ([IO.File]::Exists($fullPath)) { [IO.File]::Replace($temporaryPath, $fullPath, $null) }
    else { [IO.File]::Move($temporaryPath, $fullPath) }
}
try {
    $started = [DateTimeOffset]::Parse($StartedAfterUtc, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
    $expected = [IO.Path]::GetFullPath($ExpectedShellPath)
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $rejected = @($RejectedProcessIds -split ',' | Where-Object { $_.Trim() } | ForEach-Object { [int]$_.Trim() })
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DevHomeProbeNative {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsHungAppWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
}
'@
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $lastSavedFailure = $null; $lastSaveSeconds = -5
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $delay = 500
        try {
            if (-not (Test-Path -LiteralPath $TracePath -PathType Leaf)) { throw 'Waiting for startup trace.' }
            $delay = 250
            $traceFile = Get-Item -LiteralPath $TracePath
            if ($traceFile.CreationTimeUtc -lt $started -or $traceFile.LastWriteTimeUtc -lt $started) {
                throw 'Startup trace is not fresh: creation and last-write must be at or after StartedAfterUtc.'
            }
            $result.traceCreatedUtc = $traceFile.CreationTimeUtc.ToString('o')
            $result.traceModifiedUtc = $traceFile.LastWriteTimeUtc.ToString('o')
            $result.traceLines = @([IO.File]::ReadAllLines($TracePath))
            $result.tracePhases = @($result.traceLines | ForEach-Object {
                $parts = $_ -split "`t", 2
                if ($parts.Count -eq 2) { $parts[1].Trim() }
            })
            if ($result.tracePhases -notcontains 'home-live-complete') { throw 'Waiting for home-live-complete.' }
            $candidates = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($expected)) -ErrorAction SilentlyContinue |
                Where-Object { $_.SessionId -eq $sessionId -and $_.Id -notin $rejected } |
                Sort-Object StartTime -Descending)
            $matched = $false
            foreach ($process in $candidates) {
                try {
                    if ($process.StartTime.ToUniversalTime() -lt $started) { continue }
                    if (-not [string]::Equals($process.Path, $expected, [StringComparison]::OrdinalIgnoreCase)) { continue }
                    $matched = $true
                    $result.processId = $process.Id
                    $cim = Get-CimInstance Win32_Process -Filter ('ProcessId = {0}' -f $process.Id)
                    if (-not $cim -or [string]::IsNullOrWhiteSpace($cim.CommandLine)) { throw 'Process command line unavailable.' }
                    if (-not [string]::Equals($cim.ExecutablePath, $expected, [StringComparison]::OrdinalIgnoreCase)) {
                        throw 'CIM executable path does not match the expected Shell path.'
                    }
                    $result.process = [ordered]@{ path = $process.Path; commandLine = $cim.CommandLine
                        sessionId = $process.SessionId; startedUtc = $process.StartTime.ToUniversalTime().ToString('o') }
                    $resident = $result.tracePhases -contains 'resident-hidden' -or $cim.CommandLine -match '(?i)(?:^|[\s"])--prewarm(?:[\s"]|$)'
                    $result.residentPresentationRequired = $resident
                    if ($resident -and $result.tracePhases -notcontains 'resident-presented') { throw 'Waiting for resident-presented.' }
                    $process.Refresh()
                    $handle = $process.MainWindowHandle
                    $result.window = [ordered]@{ handle = $handle.ToInt64(); handleHex = ('0x{0:X}' -f $handle.ToInt64())
                        title = $process.MainWindowTitle; visible = $false; hung = $null; wmNullSucceeded = $null }
                    if ($handle -eq [IntPtr]::Zero) { throw 'Shell has no main window handle.' }
                    $rect = New-Object DevHomeProbeNative+RECT
                    $result.window.visible = [DevHomeProbeNative]::IsWindowVisible($handle)
                    if (-not $result.window.visible) { throw 'Shell window is not visible.' }
                    if (-not [DevHomeProbeNative]::GetWindowRect($handle, [ref]$rect) -or
                        $rect.Right -le $rect.Left -or $rect.Bottom -le $rect.Top) { throw 'Shell window rectangle is invalid.' }
                    $result.window.left = $rect.Left; $result.window.top = $rect.Top
                    $result.window.width = $rect.Right - $rect.Left; $result.window.height = $rect.Bottom - $rect.Top
                    $root = [Windows.Automation.AutomationElement]::FromHandle($handle)
                    $nodes = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
                    $inventory = New-Object 'System.Collections.Generic.List[object]'
                    $names = New-Object 'System.Collections.Generic.List[string]'
                    foreach ($node in $nodes) {
                        $current = $node.Current
                        $bounds = $current.BoundingRectangle
                        $visible = -not $current.IsOffscreen -and -not $bounds.IsEmpty -and $bounds.Width -gt 0 -and $bounds.Height -gt 0 -and
                            $bounds.Right -gt $rect.Left -and $bounds.Left -lt $rect.Right -and $bounds.Bottom -gt $rect.Top -and $bounds.Top -lt $rect.Bottom
                        $inventory.Add([ordered]@{ name = $current.Name; automationId = $current.AutomationId
                            controlType = $current.ControlType.ProgrammaticName; className = $current.ClassName
                            processId = $current.ProcessId; isOffscreen = $current.IsOffscreen; visible = [bool]$visible
                            rectangle = @{ left = $bounds.Left; top = $bounds.Top; width = $bounds.Width; height = $bounds.Height } })
                        if ($visible -and -not [string]::IsNullOrWhiteSpace($current.Name)) { $names.Add($current.Name.Trim()) }
                    }
                    $result.uiaInventory = @($inventory.ToArray())
                    $result.visibleTexts = @($names.ToArray())
                    if (-not @($names | Where-Object { $_ -match '\bHome\b' }).Count) { throw 'Visible Home text missing.' }
                    if (-not @($names | Where-Object { $_ -like '*Open your toolkit*' }).Count) { throw 'Visible Open your toolkit text missing.' }
                    if (-not @($names | Where-Object { $_ -match '^[1-9][0-9]* tools registered$|^1 tool is ready to open$' }).Count) {
                        throw 'Visible positive registered tool count missing.'
                    }
                    if (@($names | Where-Object { $_ -match 'Loading your toolkit|Home could not be loaded|No tools are registered' }).Count) {
                        throw 'Visible loading, failure, or empty-tool state remains.'
                    }
                    $process.Refresh()
                    [uint32]$ownerPid = 0
                    [void][DevHomeProbeNative]::GetWindowThreadProcessId($handle, [ref]$ownerPid)
                    if ($process.HasExited -or $process.MainWindowHandle -ne $handle -or $ownerPid -ne $process.Id -or
                        -not [DevHomeProbeNative]::IsWindowVisible($handle)) { throw 'Window/process changed during observation.' }
                    if (-not [DevHomeProbeNative]::GetWindowRect($handle, [ref]$rect) -or
                        $rect.Right -le $rect.Left -or $rect.Bottom -le $rect.Top) { throw 'Final window rectangle is invalid.' }
                    $result.window.hung = [DevHomeProbeNative]::IsHungAppWindow($handle)
                    if ($result.window.hung) { throw 'Shell window is hung.' }
                    [UIntPtr]$reply = [UIntPtr]::Zero
                    $result.window.wmNullSucceeded = $false
                    if ([DevHomeProbeNative]::SendMessageTimeout($handle, 0, [UIntPtr]::Zero, [IntPtr]::Zero, 3, 1000, [ref]$reply) -eq [IntPtr]::Zero) {
                        throw 'WM_NULL responsiveness check failed or timed out.'
                    }
                    $result.observedTimestampTicks = [Diagnostics.Stopwatch]::GetTimestamp()
                    $result.observedUtc = [DateTime]::UtcNow.ToString('o')
                    $result.window = [ordered]@{ handle = $handle.ToInt64(); handleHex = ('0x{0:X}' -f $handle.ToInt64())
                        ownerProcessId = $ownerPid; title = $process.MainWindowTitle; visible = $true; hung = $false; wmNullSucceeded = $true
                        left = $rect.Left; top = $rect.Top; width = $rect.Right - $rect.Left; height = $rect.Bottom - $rect.Top }
                    $bitmap = $null; $graphics = $null; $result.screenshotCaptured = $false
                    try {
                        Add-Type -AssemblyName System.Drawing
                        [void][IO.Directory]::CreateDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($ScreenshotPath))))
                        $bitmap = New-Object Drawing.Bitmap ($result.window.width), ($result.window.height)
                        $graphics = [Drawing.Graphics]::FromImage($bitmap)
                        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size, [Drawing.CopyPixelOperation]::SourceCopy)
                        $bitmap.Save($ScreenshotPath, [Drawing.Imaging.ImageFormat]::Png)
                        $result.screenshotCaptured = $true
                    } catch { Add-Diagnostic ('Optional screenshot failed: ' + $_.Exception.Message) }
                    finally {
                        try { if ($graphics) { $graphics.Dispose() }; if ($bitmap) { $bitmap.Dispose() } }
                        catch { Add-Diagnostic ('Optional screenshot cleanup failed: ' + $_.Exception.Message) }
                    }
                    $result.status = 'ready'; $result.lastFailure = $null
                    try { Save-Result } catch { Add-Diagnostic ('Ready JSON write failed: ' + $_.Exception.Message); throw }
                    exit 0
                } catch { $result.lastFailure = 'PID {0}: {1}' -f $process.Id, $_.Exception.Message }
            }
            if (-not $matched) { throw 'No fresh Shell process matches path, session, and rejected-PID guards.' }
        } catch { $result.lastFailure = $_.Exception.Message }
        $result.status = 'BLOCKED'
        $result.observedTimestampTicks = $null; $result.observedUtc = $null
        if ($result.lastFailure -ne $lastSavedFailure -or $timer.Elapsed.TotalSeconds - $lastSaveSeconds -ge 5) {
            $lastSavedFailure = $result.lastFailure; $lastSaveSeconds = $timer.Elapsed.TotalSeconds
            try { Save-Result } catch { Add-Diagnostic ('Progress JSON write failed: ' + $_.Exception.Message) }
        }
        Start-Sleep -Milliseconds $delay
    }
    $result.timeoutSeconds = $TimeoutSeconds
} catch { $result.lastFailure = 'Probe initialization failed: ' + $_.Exception.Message }
$result.status = 'BLOCKED'
$result.finishedUtc = [DateTime]::UtcNow.ToString('o')
try { Save-Result } catch { [Console]::Error.WriteLine('BLOCKED: final JSON write failed: ' + $_.Exception.Message) }
exit 1
