using System.Runtime.InteropServices;
using System.Text;

namespace NssmManager.Windows;

internal static class NativeMethods
{
    internal const uint ScManagerConnect = 0x0001;
    internal const uint ScManagerCreateService = 0x0002;
    internal const uint ScManagerEnumerateService = 0x0004;
    internal const uint ScManagerAllAccess = 0x000F003F;
    internal const uint ServiceQueryConfig = 0x0001;
    internal const uint ServiceChangeConfig = 0x0002;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const uint ServiceEnumerateDependents = 0x0008;
    internal const uint ServiceStart = 0x0010;
    internal const uint ServiceStop = 0x0020;
    internal const uint ServicePauseContinue = 0x0040;
    internal const uint ServiceUserDefinedControl = 0x0100;
    internal const uint Delete = 0x00010000;
    internal const uint ServiceAllAccess = 0x000F01FF;
    internal const uint ServiceWin32OwnProcess = 0x00000010;
    internal const uint ServiceWin32 = 0x00000030;
    internal const uint ServiceInteractiveProcess = 0x00000100;
    internal const uint ServiceErrorNormal = 0x00000001;
    internal const uint ServiceConfigDescription = 1;
    internal const uint ServiceConfigDelayedAutoStartInfo = 3;
    internal const uint ServiceConfigFailureActionsFlag = 4;
    internal const uint ScStatusProcessInfo = 0;
    internal const uint ScEnumProcessInfo = 0;
    internal const uint ServiceStateAll = 3;
    internal const uint ServiceControlStop = 1;
    internal const uint ServiceControlPause = 2;
    internal const uint ServiceControlContinue = 3;
    internal const uint ServiceControlInterrogate = 4;
    internal const uint ServiceControlShutdown = 5;
    internal const uint ServiceControlPowerEvent = 13;
    internal const uint ServiceControlRotate = 128;
    internal const uint ServiceStopped = 1;
    internal const uint ServiceStartPending = 2;
    internal const uint ServiceStopPending = 3;
    internal const uint ServiceRunning = 4;
    internal const uint ServiceContinuePending = 5;
    internal const uint ServicePausePending = 6;
    internal const uint ServicePaused = 7;
    internal const uint ServiceAcceptStop = 1;
    internal const uint ServiceAcceptPauseContinue = 2;
    internal const uint ServiceAcceptShutdown = 4;
    internal const uint ServiceAcceptPowerEvent = 0x40;
    internal const int ErrorFailedServiceControllerConnect = 1063;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int ErrorMoreData = 234;
    internal const int ErrorServiceDoesNotExist = 1060;
    internal const int ErrorInvalidLevel = 124;
    internal const uint WmClose = 0x0010;
    internal const uint WmQuit = 0x0012;
    internal const uint CtrlCEvent = 0;
    internal const uint ThreadSuspendResume = 0x0002;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint TokenAdjustPrivileges = 0x0020;
    internal const uint TokenQuery = 0x0008;
    internal const uint SePrivilegeEnabled = 0x00000002;
    internal const uint ToolhelpSnapshotProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? ServiceName;
        public ServiceMain? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatusProcess
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ServiceDescription { public IntPtr Description; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct QueryServiceConfigData
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceDelayedAutoStartInfo { [MarshalAs(UnmanagedType.Bool)] public bool Delayed; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceFailureActionsFlag { [MarshalAs(UnmanagedType.Bool)] public bool FailureActionsOnNonCrashFailures; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TokenPrivileges { public uint PrivilegeCount; public Luid Luid; public uint Attributes; }

    internal delegate void ServiceMain(uint argumentCount, IntPtr arguments);
    internal delegate uint HandlerEx(uint control, uint eventType, IntPtr eventData, IntPtr context);
    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] table);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, HandlerEx handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateService(IntPtr manager, string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType, uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, IntPtr dependencies, string? account, string? password);

    [DllImport("advapi32.dll", EntryPoint = "CreateServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateServiceWithPassword(IntPtr manager, string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType, uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, IntPtr dependencies, string? account, IntPtr password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath, string? loadOrderGroup, IntPtr tagId, IntPtr dependencies, string? account, string? password, string? displayName);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfigWithPassword(IntPtr service, uint serviceType, uint startType, uint errorControl, string? binaryPath, string? loadOrderGroup, IntPtr tagId, IntPtr dependencies, string? account, IntPtr password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartService(IntPtr service, uint argumentCount, string[]? arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatusEx(IntPtr service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetServiceDisplayName(IntPtr manager, string serviceName, StringBuilder displayName, ref uint displayNameLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetServiceKeyName(IntPtr manager, string displayName, StringBuilder serviceName, ref uint serviceNameLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfig(IntPtr service, IntPtr serviceConfig, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfig2(IntPtr service, uint infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumServicesStatusEx(IntPtr manager, uint infoLevel, uint serviceType, uint serviceState, IntPtr services, uint bufferSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder executableName, ref uint size);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint SuspendThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
