param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class Rm
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        IntPtr rgApplications,
        uint nServices,
        string[] rgsServiceNames);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    public static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[] rgAffectedApps,
        ref uint lpdwRebootReasons);
}
'@

$sessionKey = [Guid]::NewGuid().ToString()
$session = [uint32]0
$null = [Rm]::RmStartSession([ref]$session, 0, $sessionKey)
try {
    $files = @()
    if (Test-Path -LiteralPath $Path -PathType Container) {
        $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force | ForEach-Object { $_.FullName })
    } else {
        $files = @($Path)
    }
    if ($files.Count -eq 0) {
        Write-Output 'NO-FILES'
        exit 0
    }
    $null = [Rm]::RmRegisterResources($session, [uint32]$files.Count, [string[]]$files, 0, [IntPtr]::Zero, 0, $null)
    $needed = [uint32]0
    $count = [uint32]0
    $rebootReasons = [uint32]0
    $null = [Rm]::RmGetList($session, [ref]$needed, [ref]$count, $null, [ref]$rebootReasons)
    if ($needed -eq 0) {
        Write-Output 'NO-LOCKERS'
        exit 0
    }
    $apps = New-Object Rm+RM_PROCESS_INFO[] $needed
    $count = $needed
    $null = [Rm]::RmGetList($session, [ref]$needed, [ref]$count, $apps, [ref]$rebootReasons)
    $results = @()
    foreach ($app in $apps) {
        if ($app.dwProcessId -ne 0) {
            $results += [pscustomobject]@{
                ProcessId = $app.dwProcessId
                AppName = $app.strAppName
                Service = $app.strServiceShortName
                SessionId = $app.TSSessionId
                Restartable = $app.bRestartable
            }
        }
    }
    $results | ConvertTo-Json -Compress
}
finally {
    $null = [Rm]::RmEndSession($session)
}
