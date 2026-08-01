using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalLagCleaner.MyPowerTools;

internal static class WindowsNative
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Th32csSnapProcess = 0x00000002;
    private const int ProcessCommandLineInformation = 60;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint MaximumPowerPlanNameBytes = 64 * 1024;

    public static PerformanceSnapshot ReadPerformance()
    {
        EnsureWindows();
        var info = new PerformanceInformation
        {
            Size = (uint)Marshal.SizeOf<PerformanceInformation>()
        };
        if (!GetPerformanceInfo(ref info, info.Size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetPerformanceInfo failed.");
        }

        var pageSize = (ulong)info.PageSize;
        return new PerformanceSnapshot(
            (ulong)info.CommitTotal * pageSize,
            (ulong)info.CommitLimit * pageSize,
            (ulong)info.PhysicalTotal * pageSize,
            (ulong)info.PhysicalAvailable * pageSize,
            (ulong)info.KernelTotal * pageSize,
            (ulong)info.KernelPaged * pageSize,
            (ulong)info.KernelNonPaged * pageSize,
            info.HandleCount,
            info.ProcessCount,
            info.ThreadCount);
    }

    public static CpuTimes ReadCpuTimes()
    {
        EnsureWindows();
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemTimes failed.");
        }

        return new CpuTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    public static double ReadUptimeDays()
    {
        EnsureWindows();
        return GetTickCount64() / 86_400_000d;
    }

    public static string ReadPowerSource()
    {
        EnsureWindows();
        if (!GetSystemPowerStatus(out var status))
        {
            return "未知";
        }

        return status.AcLineStatus switch
        {
            0 => "电池",
            1 => "交流电",
            _ => "未知"
        };
    }

    public static ActivePowerPlanNativeSnapshot ReadActivePowerPlan()
    {
        EnsureWindows();
        var status = PowerGetActiveScheme(IntPtr.Zero, out var schemePointer);
        if (status != ErrorSuccess || schemePointer == IntPtr.Zero)
        {
            throw new Win32Exception(
                unchecked((int)status),
                $"PowerGetActiveScheme failed with 0x{status:x8}.");
        }

        try
        {
            var schemeGuid = Marshal.PtrToStructure<Guid>(schemePointer);
            uint nameSize = 0;
            var friendlyNameStatus = PowerReadFriendlyName(
                IntPtr.Zero,
                ref schemeGuid,
                IntPtr.Zero,
                IntPtr.Zero,
                null,
                ref nameSize);
            if (friendlyNameStatus is not (ErrorSuccess or ErrorMoreData) ||
                nameSize == 0 ||
                nameSize > MaximumPowerPlanNameBytes)
            {
                return new ActivePowerPlanNativeSnapshot(
                    schemeGuid,
                    "",
                    friendlyNameStatus);
            }

            var nameBuffer = new byte[nameSize];
            friendlyNameStatus = PowerReadFriendlyName(
                IntPtr.Zero,
                ref schemeGuid,
                IntPtr.Zero,
                IntPtr.Zero,
                nameBuffer,
                ref nameSize);
            var friendlyName = friendlyNameStatus == ErrorSuccess
                ? Encoding.Unicode.GetString(nameBuffer, 0, checked((int)nameSize))
                    .TrimEnd('\0')
                : "";
            return new ActivePowerPlanNativeSnapshot(
                schemeGuid,
                friendlyName,
                friendlyNameStatus);
        }
        finally
        {
            _ = LocalFree(schemePointer);
        }
    }

    public static IReadOnlyDictionary<int, NativeProcessEntry> ReadProcessTable()
    {
        EnsureWindows();
        var result = new Dictionary<int, NativeProcessEntry>();
        using var snapshot = new SafeSnapshotHandle(CreateToolhelp32Snapshot(Th32csSnapProcess, 0), ownsHandle: true);
        if (snapshot.IsInvalid)
        {
            return result;
        }

        var entry = new ProcessEntry32
        {
            Size = (uint)Marshal.SizeOf<ProcessEntry32>()
        };
        if (!Process32First(snapshot, ref entry))
        {
            return result;
        }

        do
        {
            result[unchecked((int)entry.ProcessId)] = new NativeProcessEntry(
                unchecked((int)entry.ParentProcessId),
                unchecked((int)entry.Threads));
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (Process32Next(snapshot, ref entry));

        return result;
    }

    public static IReadOnlyDictionary<int, string> ReadKnownServiceProcessRoles()
    {
        EnsureWindows();
        var result = new Dictionary<int, string>();
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return result;
        }

        try
        {
            foreach (var service in new[]
            {
                (Name: "TermService", Role: "Remote Desktop Services"),
                (Name: "DoSvc", Role: "Delivery Optimization"),
                (Name: "NVDisplay.ContainerLocalSystem", Role: "NVIDIA Display Container")
            })
            {
                var serviceHandle = OpenService(manager, service.Name, ServiceQueryStatus);
                if (serviceHandle == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (QueryServiceStatusEx(
                            serviceHandle,
                            ScStatusProcessInfo,
                            out var status,
                            Marshal.SizeOf<ServiceStatusProcess>(),
                            out _) &&
                        status.ProcessId > 0)
                    {
                        result[unchecked((int)status.ProcessId)] = service.Role;
                    }
                }
                finally
                {
                    _ = CloseServiceHandle(serviceHandle);
                }
            }
        }
        finally
        {
            _ = CloseServiceHandle(manager);
        }

        return result;
    }

    public static string TryReadCommandLine(int processId)
    {
        EnsureWindows();
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            return "";
        }

        _ = NtQueryInformationProcess(
            process,
            ProcessCommandLineInformation,
            IntPtr.Zero,
            0,
            out var required);
        if (required <= Marshal.SizeOf<UnicodeString>() || required > 1024 * 1024)
        {
            return "";
        }

        var buffer = Marshal.AllocHGlobal(required);
        try
        {
            var status = NtQueryInformationProcess(
                process,
                ProcessCommandLineInformation,
                buffer,
                required,
                out _);
            if (status < 0)
            {
                return "";
            }

            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            return value.Buffer == IntPtr.Zero || value.Length == 0
                ? ""
                : Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char)) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static string TryReadImagePath(int processId)
    {
        EnsureWindows();
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            return "";
        }

        var capacity = 32_768;
        var builder = new StringBuilder(capacity);
        return QueryFullProcessImageName(process, 0, builder, ref capacity)
            ? builder.ToString()
            : "";
    }

    public static ProcessIoSnapshot TryReadProcessIo(int processId)
    {
        EnsureWindows();
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid || !GetProcessIoCounters(process, out var counters))
        {
            return ProcessIoSnapshot.Unavailable;
        }

        return new ProcessIoSnapshot(
            counters.ReadOperationCount,
            counters.WriteOperationCount,
            counters.OtherOperationCount,
            counters.ReadTransferCount,
            counters.WriteTransferCount,
            counters.OtherTransferCount,
            true);
    }

    private static ulong ToUInt64(FileTime value)
    {
        return ((ulong)value.HighDateTime << 32) | value.LowDateTime;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Local Lag Cleaner supports Windows only.");
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(
        ref PerformanceInformation performanceInformation,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus systemPowerStatus);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(
        IntPtr userRootPowerKey,
        out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        [Out] byte[]? buffer,
        ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        SafeProcessHandle processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        SafeProcessHandle processHandle,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(
        SafeProcessHandle processHandle,
        out IoCounters ioCounters);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(
        SafeSnapshotHandle snapshot,
        ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(
        SafeSnapshotHandle snapshot,
        ref ProcessEntry32 entry);

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonPaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    private sealed class SafeSnapshotHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeSnapshotHandle(IntPtr preexistingHandle, bool ownsHandle)
            : base(ownsHandle)
        {
            SetHandle(preexistingHandle);
        }

        protected override bool ReleaseHandle()
        {
            return CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(
        IntPtr serviceControlManager,
        string serviceName,
        uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        int bufferSize,
        out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }
}

internal sealed record PerformanceSnapshot(
    ulong CommitTotalBytes,
    ulong CommitLimitBytes,
    ulong PhysicalTotalBytes,
    ulong PhysicalAvailableBytes,
    ulong KernelTotalBytes,
    ulong KernelPagedBytes,
    ulong KernelNonPagedBytes,
    uint HandleCount,
    uint ProcessCount,
    uint ThreadCount);

internal sealed record CpuTimes(ulong Idle, ulong Kernel, ulong User);

internal sealed record ActivePowerPlanNativeSnapshot(
    Guid SchemeGuid,
    string FriendlyName,
    uint FriendlyNameStatus);

internal sealed record NativeProcessEntry(int ParentProcessId, int ThreadCount);

internal sealed record ProcessIoSnapshot(
    ulong ReadOperations,
    ulong WriteOperations,
    ulong OtherOperations,
    ulong ReadBytes,
    ulong WriteBytes,
    ulong OtherBytes,
    bool Available)
{
    public static ProcessIoSnapshot Unavailable { get; } = new(0, 0, 0, 0, 0, 0, false);
}
