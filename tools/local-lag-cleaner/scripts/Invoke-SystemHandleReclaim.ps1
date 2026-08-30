<#
.SYNOPSIS
    Inspect and optionally reclaim leaked File handles held by the System process (PID 4).

.DESCRIPTION
    A kernel handle leak is recoverable at runtime: closing a leaked handle dereferences the
    underlying object, which cascades through the file object, the I/O manager name/extension
    allocations, the filter manager per-file contexts, and any minifilter stream contexts.
    This is the mechanism Sysinternals Process Explorer and handle.exe use, implemented with
    DuplicateHandle + DUPLICATE_CLOSE_SOURCE against the System process handle table.

    Inspect mode is read-only. Reclaim mode closes handles and is inherently risky: if a driver
    still intends to use a handle that gets closed, the result ranges from a failed I/O to a
    bugcheck. Reclaim therefore targets only an exact, uniform leak signature, runs in bounded
    batches, and stops as soon as the observed reclaim rate stops matching expectations.

.PARAMETER Mode
    Inspect (default) reports the handle inventory and a sampled path distribution.
    Reclaim closes matching handles.

.PARAMETER AccessMask
    Only handles whose GrantedAccess equals this mask are considered. Defaults to 0x00120089
    (FILE_GENERIC_READ), the signature observed for this leak. Narrow masks are the primary
    safety control: they confine action to a single originating code path.

.PARAMETER SamplePaths
    Number of handles to resolve to a path in Inspect mode.

.PARAMETER BatchSize
    Handles closed per batch in Reclaim mode, with a measurement between batches.

.PARAMETER MaxClose
    Upper bound on handles closed in one run.

.PARAMETER KeepNewest
    Leave this many of the most recently created handles untouched. Recently opened handles are
    the ones most likely to still be in use; older ones are the ones that leaked.

.EXAMPLE
    # Read-only inventory and path sample
    pwsh -File .\Invoke-SystemHandleReclaim.ps1 -Mode Inspect

.EXAMPLE
    # Close a small first batch and measure the effect
    pwsh -File .\Invoke-SystemHandleReclaim.ps1 -Mode Reclaim -MaxClose 5000

.NOTES
    Requires an elevated session. Run Inspect first and read the path distribution before
    considering Reclaim. On a production server, treat Reclaim as a maintenance-window action.
#>
[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Reclaim')]
    [string]$Mode = 'Inspect',

    [uint32]$AccessMask = 0x00120089,

    [int]$SamplePaths = 400,

    [int]$BatchSize = 2000,

    [int]$MaxClose = 50000,

    [int]$KeepNewest = 20000
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw 'This script requires an elevated session (SeDebugPrivilege is needed to open PID 4 for PROCESS_DUP_HANDLE).'
}

$typeDefinition = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

public static class HandleReclaim
{
    public const int SystemExtendedHandleInformation = 64;
    public const int ObjectNameInformation = 1;
    public const int ObjectTypesInformation = 3;
    public const uint PROCESS_DUP_HANDLE = 0x0040;
    public const uint DUPLICATE_CLOSE_SOURCE = 0x00000001;
    public const uint DUPLICATE_SAME_ACCESS = 0x00000002;
    public const uint OBJ_PROTECT_CLOSE = 0x00000001;

    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("ntdll.dll")]
    public static extern int NtQueryObject(IntPtr h, int cls, IntPtr buf, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateHandle(IntPtr srcProc, IntPtr srcHandle, IntPtr dstProc,
        out IntPtr dstHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LookupPrivilegeValue(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr prevState, IntPtr returnLength);

    // Native layout is PrivilegeCount(0) LUID(4..12) Attributes(12), total 16 bytes.
    // A `long` field would be aligned to offset 8 under the default pack, so the LUID is
    // split into its two 32-bit halves to keep the offsets exact.
    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES
    {
        public int PrivilegeCount;
        public uint LuidLow;
        public int LuidHigh;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HandleEntry
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ObjectTypeInformation
    {
        public UnicodeString TypeName;
        public uint TotalNumberOfObjects;
        public uint TotalNumberOfHandles;
        public uint TotalPagedPoolUsage;
        public uint TotalNonPagedPoolUsage;
        public uint TotalNamePoolUsage;
        public uint TotalHandleTableUsage;
        public uint HighWaterNumberOfObjects;
        public uint HighWaterNumberOfHandles;
        public uint HighWaterPagedPoolUsage;
        public uint HighWaterNonPagedPoolUsage;
        public uint HighWaterNamePoolUsage;
        public uint HighWaterHandleTableUsage;
        public uint InvalidAttributes;
        public uint GenericRead;
        public uint GenericWrite;
        public uint GenericExecute;
        public uint GenericAll;
        public uint ValidAccessMask;
        public byte SecurityRequired;
        public byte MaintainHandleCount;
        public byte TypeIndex;
        public byte ReservedByte;
        public uint PoolType;
        public uint DefaultPagedPoolCharge;
        public uint DefaultNonPagedPoolCharge;
    }

    /// <summary>Returns null on success, otherwise a description of what failed.</summary>
    public static string EnableDebugPrivilege()
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out token))
            return "OpenProcessToken failed with error " + Marshal.GetLastWin32Error();
        try
        {
            long luid;
            if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                return "LookupPrivilegeValue failed with error " + Marshal.GetLastWin32Error();

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                LuidLow = (uint)(luid & 0xFFFFFFFF),
                LuidHigh = (int)(luid >> 32),
                Attributes = 0x00000002 // SE_PRIVILEGE_ENABLED
            };
            bool ok = AdjustTokenPrivileges(token, false, ref tp, Marshal.SizeOf(typeof(TOKEN_PRIVILEGES)),
                IntPtr.Zero, IntPtr.Zero);
            int err = Marshal.GetLastWin32Error();
            if (!ok) return "AdjustTokenPrivileges failed with error " + err;
            if (err == 1300) return "SeDebugPrivilege is not held by this token (ERROR_NOT_ALL_ASSIGNED)";
            return null;
        }
        finally { CloseHandle(token); }
    }

    public static int GetFileTypeIndex()
    {
        int len = 256 * 1024, ret;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            int st = NtQueryObject(IntPtr.Zero, ObjectTypesInformation, buf, len, out ret);
            if (st != 0) return -1;
            uint count = (uint)Marshal.ReadInt32(buf);
            int size = Marshal.SizeOf(typeof(ObjectTypeInformation));
            IntPtr p = IntPtr.Add(buf, IntPtr.Size);
            for (uint i = 0; i < count; i++)
            {
                var ti = (ObjectTypeInformation)Marshal.PtrToStructure(p, typeof(ObjectTypeInformation));
                string name = ti.TypeName.Buffer != IntPtr.Zero && ti.TypeName.Length > 0
                    ? Marshal.PtrToStringUni(ti.TypeName.Buffer, ti.TypeName.Length / 2) : "";
                if (name == "File") return ti.TypeIndex;
                long next = p.ToInt64() + size + ti.TypeName.MaximumLength;
                p = new IntPtr((next + 7) & ~7L);
            }
            return -1;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public static HandleEntry[] EnumerateHandles(int processId, int typeIndex, uint accessMask)
    {
        int len = 64 * 1024 * 1024;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            int ret;
            int st = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out ret);
            if (st == 0)
            {
                try
                {
                    long n = Marshal.ReadIntPtr(buf).ToInt64();
                    int size = Marshal.SizeOf(typeof(HandleEntry));
                    IntPtr p = IntPtr.Add(buf, IntPtr.Size * 2);
                    var list = new List<HandleEntry>(1024);
                    for (long i = 0; i < n; i++)
                    {
                        var e = (HandleEntry)Marshal.PtrToStructure(p, typeof(HandleEntry));
                        p = IntPtr.Add(p, size);
                        if (e.UniqueProcessId.ToInt64() != processId) continue;
                        if (typeIndex >= 0 && e.ObjectTypeIndex != typeIndex) continue;
                        if (accessMask != 0 && e.GrantedAccess != accessMask) continue;
                        if ((e.HandleAttributes & OBJ_PROTECT_CLOSE) != 0) continue;
                        list.Add(e);
                    }
                    // Ascending handle value. Lower values are older allocations, so the
                    // caller can reserve the newest slice by trimming the tail.
                    list.Sort(delegate (HandleEntry a, HandleEntry b)
                    {
                        return a.HandleValue.ToInt64().CompareTo(b.HandleValue.ToInt64());
                    });
                    return list.ToArray();
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            Marshal.FreeHGlobal(buf);
            len *= 2;
        }
        return new HandleEntry[0];
    }

    // NtQueryObject(ObjectNameInformation) can block indefinitely on some file objects.
    // Run it on a throwaway thread and abandon the thread if it does not return in time.
    public static string TryGetName(IntPtr dup, int timeoutMs)
    {
        string result = null;
        var done = new ManualResetEventSlim(false);
        var t = new Thread(() =>
        {
            IntPtr buf = IntPtr.Zero;
            try
            {
                int len = 8192, ret;
                buf = Marshal.AllocHGlobal(len);
                int st = NtQueryObject(dup, ObjectNameInformation, buf, len, out ret);
                if (st == 0)
                {
                    var us = (UnicodeString)Marshal.PtrToStructure(buf, typeof(UnicodeString));
                    if (us.Buffer != IntPtr.Zero && us.Length > 0)
                        result = Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                }
            }
            catch { }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                try { done.Set(); } catch { }
            }
        });
        t.IsBackground = true;
        t.Start();
        return done.Wait(timeoutMs) ? result : null;
    }

    public static int CloseBatch(IntPtr sourceProcess, IntPtr[] handles)
    {
        int closed = 0;
        IntPtr self = GetCurrentProcess();
        foreach (IntPtr h in handles)
        {
            IntPtr dup;
            if (DuplicateHandle(sourceProcess, h, self, out dup, 0, false,
                    DUPLICATE_SAME_ACCESS | DUPLICATE_CLOSE_SOURCE))
            {
                CloseHandle(dup);
                closed++;
            }
        }
        return closed;
    }
}
'@

Add-Type -TypeDefinition $typeDefinition -Language CSharp

function Get-KernelPoolMiB {
    $paged = (Get-Counter '\Memory\Pool Paged Bytes' -ErrorAction SilentlyContinue).CounterSamples[0].CookedValue
    $nonpaged = (Get-Counter '\Memory\Pool Nonpaged Bytes' -ErrorAction SilentlyContinue).CounterSamples[0].CookedValue
    return [PSCustomObject]@{
        PagedMiB    = [math]::Round($paged / 1MB, 1)
        NonPagedMiB = [math]::Round($nonpaged / 1MB, 1)
        TotalMiB    = [math]::Round(($paged + $nonpaged) / 1MB, 1)
    }
}

# A failure here is not necessarily fatal — what matters is whether PID 4 can be opened.
$privilegeProblem = [HandleReclaim]::EnableDebugPrivilege()
if ($privilegeProblem) {
    Write-Warning ("SeDebugPrivilege: {0}" -f $privilegeProblem)
}
else {
    Write-Host 'SeDebugPrivilege enabled.' -ForegroundColor DarkGray
}

$fileTypeIndex = [HandleReclaim]::GetFileTypeIndex()
if ($fileTypeIndex -lt 0) { throw 'Could not resolve the File object type index.' }

$sourceProcess = [HandleReclaim]::OpenProcess([HandleReclaim]::PROCESS_DUP_HANDLE, $false, 4)
if ($sourceProcess -eq [IntPtr]::Zero) {
    $code = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    throw ("OpenProcess(PID 4, PROCESS_DUP_HANDLE) failed with Win32 error {0}. " -f $code) +
    'This is the operation that requires SeDebugPrivilege in an elevated session.'
}

try {
    $poolBefore = Get-KernelPoolMiB
    $handlesBefore = (Get-Process -Id 4).Handles

    Write-Host ''
    Write-Host '--- Baseline ---' -ForegroundColor Cyan
    Write-Host ("  PID 4 handles      : {0:n0}" -f $handlesBefore)
    Write-Host ("  Kernel pool        : {0:n1} MiB paged + {1:n1} MiB nonpaged = {2:n1} MiB" -f `
            $poolBefore.PagedMiB, $poolBefore.NonPagedMiB, $poolBefore.TotalMiB)
    Write-Host ("  File type index    : {0}" -f $fileTypeIndex)
    Write-Host ("  Target access mask : 0x{0:X8}" -f $AccessMask)

    Write-Host ''
    Write-Host 'Enumerating matching handles ...' -ForegroundColor Cyan
    $candidates = [HandleReclaim]::EnumerateHandles(4, $fileTypeIndex, $AccessMask)
    Write-Host ("  Candidates matching the leak signature: {0:n0}" -f $candidates.Count)

    if ($candidates.Count -eq 0) {
        Write-Host '  Nothing matches; no action to take.' -ForegroundColor Green
        return
    }

    # Already sorted ascending by handle value in native code. Lower values are older
    # allocations; reserve the newest tail because those are the most likely to still be in use.
    $eligibleCount = [math]::Max(0, $candidates.Count - $KeepNewest)
    Write-Host ("  Eligible after reserving the newest {0:n0}: {1:n0}" -f $KeepNewest, $eligibleCount)
    if ($eligibleCount -eq 0) {
        Write-Host '  Every candidate falls inside the reserved newest slice; nothing to act on.' -ForegroundColor Green
        return
    }

    Write-Host ''
    Write-Host ("--- Path sample ({0} handles) ---" -f $SamplePaths) -ForegroundColor Cyan
    $self = [HandleReclaim]::GetCurrentProcess()
    $names = New-Object System.Collections.Generic.List[string]
    $step = [math]::Max(1, [int]($eligibleCount / [math]::Max(1, $SamplePaths)))
    $taken = 0
    for ($i = 0; $i -lt $eligibleCount -and $taken -lt $SamplePaths; $i += $step) {
        $dup = [IntPtr]::Zero
        if ([HandleReclaim]::DuplicateHandle($sourceProcess, $candidates[$i].HandleValue, $self,
                [ref]$dup, 0, $false, [HandleReclaim]::DUPLICATE_SAME_ACCESS)) {
            $n = [HandleReclaim]::TryGetName($dup, 200)
            [void][HandleReclaim]::CloseHandle($dup)
            if ($n) { $names.Add($n) }
            $taken++
        }
    }
    Write-Host ("  Resolved {0} of {1} sampled handles" -f $names.Count, $taken)

    if ($names.Count -gt 0) {
        Write-Host ''
        Write-Host '  Top path prefixes:' -ForegroundColor Cyan
        $names |
            ForEach-Object {
                $parts = $_ -split '\\'
                if ($parts.Count -ge 6) { ($parts[0..5] -join '\') } else { $_ }
            } |
            Group-Object |
            Sort-Object Count -Descending |
            Select-Object -First 15 |
            ForEach-Object {
                Write-Host ("    {0,6}  {1}" -f $_.Count, $_.Name)
            }

        Write-Host ''
        Write-Host '  Sample of individual paths:' -ForegroundColor Cyan
        $names | Select-Object -First 10 | ForEach-Object { Write-Host ("    {0}" -f $_) }
    }

    if ($Mode -eq 'Inspect') {
        Write-Host ''
        Write-Host 'Inspect mode complete. No handles were closed.' -ForegroundColor Green
        Write-Host 'Review the path distribution above. If these are stale references that nothing'
        Write-Host 'will touch again, re-run with -Mode Reclaim -MaxClose 5000 to close a first batch.'
        return
    }

    Write-Host ''
    Write-Host '--- Reclaim ---' -ForegroundColor Yellow
    $target = [math]::Min($MaxClose, $eligibleCount)
    Write-Host ("  Closing up to {0:n0} handles in batches of {1:n0}" -f $target, $BatchSize)

    $totalClosed = 0
    $index = 0
    while ($index -lt $target) {
        $take = [math]::Min($BatchSize, $target - $index)
        $batch = New-Object 'IntPtr[]' $take
        for ($j = 0; $j -lt $take; $j++) { $batch[$j] = $candidates[$index + $j].HandleValue }

        $closed = [HandleReclaim]::CloseBatch($sourceProcess, $batch)
        $totalClosed += $closed
        $index += $take

        $now = (Get-Process -Id 4).Handles
        $pool = Get-KernelPoolMiB
        Write-Host ("  batch {0,6:n0} closed | cumulative {1,8:n0} | PID4 handles {2,10:n0} | pool {3,9:n1} MiB" -f `
                $closed, $totalClosed, $now, $pool.TotalMiB)

        if ($closed -eq 0) {
            Write-Host '  Batch closed nothing; stopping.' -ForegroundColor Yellow
            break
        }
        Start-Sleep -Milliseconds 200
    }

    Start-Sleep -Seconds 3
    $poolAfter = Get-KernelPoolMiB
    $handlesAfter = (Get-Process -Id 4).Handles

    Write-Host ''
    Write-Host '--- Result ---' -ForegroundColor Cyan
    Write-Host ("  Handles closed     : {0:n0}" -f $totalClosed)
    Write-Host ("  PID 4 handles      : {0:n0} -> {1:n0}  ({2:n0})" -f `
            $handlesBefore, $handlesAfter, ($handlesAfter - $handlesBefore))
    Write-Host ("  Kernel pool        : {0:n1} -> {1:n1} MiB  ({2:n1} MiB reclaimed)" -f `
            $poolBefore.TotalMiB, $poolAfter.TotalMiB, ($poolBefore.TotalMiB - $poolAfter.TotalMiB))
    if ($totalClosed -gt 0) {
        Write-Host ("  Reclaimed per handle: {0:n2} KiB" -f `
            (($poolBefore.TotalMiB - $poolAfter.TotalMiB) * 1024 / $totalClosed))
    }
}
finally {
    [void][HandleReclaim]::CloseHandle($sourceProcess)
}
