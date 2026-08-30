using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NssmManager.Contracts;

namespace NssmManager.Windows;

public sealed class NssmServiceProcessState
{
    public string Name { get; set; } = "";
    public IntPtr ProcessHandle { get; set; }
    public uint ProcessId { get; set; }
    public uint ExitCode { get; set; }
    public uint StopMethod { get; set; } = NssmProcess.StopMethodConsole | NssmProcess.StopMethodWindow | NssmProcess.StopMethodThreads | NssmProcess.StopMethodTerminate;
    public uint KillConsoleDelay { get; set; } = 1500;
    public uint KillWindowDelay { get; set; } = 1500;
    public uint KillThreadsDelay { get; set; } = 1500;
    public IntPtr StatusHandle { get; set; }
    public long CreationTime { get; set; }
    public long ExitTime { get; set; }
}

public sealed class NssmKillContext
{
    public string Name { get; set; } = "";
    public IntPtr ProcessHandle { get; set; }
    public uint Depth { get; set; }
    public uint ProcessId { get; set; }
    public uint ExitCode { get; set; }
    public uint StopMethod { get; set; }
    public uint KillConsoleDelay { get; set; }
    public uint KillWindowDelay { get; set; }
    public uint KillThreadsDelay { get; set; }
    public IntPtr StatusHandle { get; set; }
    public long CreationTime { get; set; }
    public long ExitTime { get; set; }
    public int Signalled { get; set; }
}

public delegate int NssmWalkFunction(NssmServiceProcessState? service, NssmKillContext context);

/// <summary>Direct managed translation of process.cpp.</summary>
public static class NssmProcess
{
    public const uint StopMethodConsole = 1u << 0;
    public const uint StopMethodWindow = 1u << 1;
    public const uint StopMethodThreads = 1u << 2;
    public const uint StopMethodTerminate = 1u << 3;

    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNoToken = 1008;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorGenFailure = 31;
    private const int ErrorPartialCopy = 299;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessTerminate = 0x0001;
    private const uint Synchronize = 0x00100000;
    private const uint Th32CsSnapProcess = 0x00000002;
    private const uint Th32CsSnapThread = 0x00000004;
    private const uint StillActive = 259;
    private const uint WmClose = 0x0010;
    private const uint WmEndSession = 0x0016;
    private const uint WmQuit = 0x0012;
    private const uint EndSessionCloseApp = 0x00000001;
    private const uint EndSessionCritical = 0x40000000;
    private const uint EndSessionLogoff = 0x80000000;
    private const uint CtrlCEvent = 0;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [NssmUpstreamFunction("src/process.cpp", 5, "HANDLE get_debug_token()", "NssmProcessTests.get_debug_token_returns_owned_handle_or_invalid")]
    public static IntPtr get_debug_token()
    {
        if (!OperatingSystem.IsWindows()) return InvalidHandleValue;
        var token = IntPtr.Zero;
        if (!OpenThreadToken(GetCurrentThread(), TokenAdjustPrivileges | TokenQuery, false, out token))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNoToken)
            {
                _ = ImpersonateSelf(2);
                _ = OpenThreadToken(GetCurrentThread(), TokenAdjustPrivileges | TokenQuery, false, out token);
            }
        }
        if (token == IntPtr.Zero) return InvalidHandleValue;

        if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
        {
            CloseHandle(token);
            return InvalidHandleValue;
        }

        var privileges = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = 0 };
        var size = checked((uint)Marshal.SizeOf<TokenPrivileges>());
        if (!AdjustTokenPrivileges(token, false, ref privileges, size, out var old, out size))
        {
            CloseHandle(token);
            return InvalidHandleValue;
        }

        old.PrivilegeCount = 1;
        old.Luid = luid;
        old.Attributes |= SePrivilegeEnabled;
        if (!AdjustTokenPrivileges(token, false, ref old, size, IntPtr.Zero, IntPtr.Zero))
        {
            CloseHandle(token);
            return InvalidHandleValue;
        }
        return token;
    }

    [NssmUpstreamFunction("src/process.cpp", 46, "void service_kill_t(nssm_service_t *service, kill_t *k)", "NssmProcessTests.service_kill_t_copies_every_stop_field")]
    public static void service_kill_t(NssmServiceProcessState? service, NssmKillContext? context)
    {
        if (service is null || context is null) return;
        context.Name = service.Name;
        context.ProcessHandle = service.ProcessHandle;
        context.Depth = 0;
        context.ProcessId = service.ProcessId;
        context.ExitCode = service.ExitCode;
        context.StopMethod = service.StopMethod;
        context.KillConsoleDelay = service.KillConsoleDelay;
        context.KillWindowDelay = service.KillWindowDelay;
        context.KillThreadsDelay = service.KillThreadsDelay;
        context.StatusHandle = service.StatusHandle;
        context.CreationTime = service.CreationTime;
        context.ExitTime = service.ExitTime;
        context.Signalled = 0;
    }

    [NssmUpstreamFunction("src/process.cpp", 65, "int get_process_creation_time(HANDLE process_handle, FILETIME *ft)", "NssmProcessTests.get_process_times_match_kernel_values")]
    public static int get_process_creation_time(IntPtr processHandle, out long fileTime)
    {
        fileTime = 0;
        if (!GetProcessTimes(processHandle, out var creation, out _, out _, out _)) return 1;
        fileTime = creation.ToLong();
        return 0;
    }

    [NssmUpstreamFunction("src/process.cpp", 78, "int get_process_exit_time(HANDLE process_handle, FILETIME *ft)", "NssmProcessTests.get_process_exit_time_reports_active_process")]
    public static int get_process_exit_time(IntPtr processHandle, out long fileTime)
    {
        fileTime = 0;
        if (!GetProcessTimes(processHandle, out _, out var exit, out _, out _)) return 1;
        fileTime = exit.ToLong();
        return fileTime == 0 ? 2 : 0;
    }

    [NssmUpstreamFunction("src/process.cpp", 92, "int check_parent(kill_t *k, PROCESSENTRY32 *pe, unsigned long ppid)", "NssmProcessTests.check_parent_rejects_wrong_parent")]
    public static int check_parent(NssmKillContext context, ProcessEntry entry, uint parentProcessId)
    {
        if (entry.ParentProcessId != parentProcessId) return 1;
        var process = OpenProcess(ProcessQueryInformation, false, entry.ProcessId);
        if (process == IntPtr.Zero) return 2;
        try
        {
            if (get_process_creation_time(process, out var creationTime) != 0) return 3;
            if (context.CreationTime > creationTime) return 4;
            if (context.ExitTime < creationTime) return 5;
            return 0;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [NssmUpstreamFunction("src/process.cpp", 128, "int CALLBACK kill_window(HWND window, LPARAM arg)", "NssmProcessTests.kill_window_ignores_other_processes")]
    public static int kill_window(IntPtr window, NssmKillContext context)
    {
        if (GetWindowThreadProcessId(window, out var processId) == 0 || processId != context.ProcessId) return 1;
        if (PostMessage(window, WmClose, new IntPtr(unchecked((int)context.ExitCode)), IntPtr.Zero)) context.Signalled |= 1;
        if (PostMessage(window, WmEndSession, new IntPtr(1), new IntPtr(unchecked((int)(EndSessionCloseApp | EndSessionCritical | EndSessionLogoff))))) context.Signalled |= 1;
        return 1;
    }

    [NssmUpstreamFunction("src/process.cpp", 154, "int kill_threads(nssm_service_t *service, kill_t *k)", "NssmProcessTests.kill_threads_returns_signal_result")]
    public static int kill_threads(NssmServiceProcessState? service, NssmKillContext context)
    {
        var result = 0;
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapThread, 0);
        if (snapshot == InvalidHandleValue) return 0;
        try
        {
            var entry = new ThreadEntry { Size = checked((uint)Marshal.SizeOf<ThreadEntry>()) };
            if (!Thread32First(snapshot, ref entry)) return 0;
            while (true)
            {
                if (entry.OwnerProcessId == context.ProcessId && PostThreadMessage(entry.ThreadId, WmQuit, new IntPtr(unchecked((int)context.ExitCode)), IntPtr.Zero)) result |= 1;
                if (Thread32Next(snapshot, ref entry)) continue;
                if (Marshal.GetLastPInvokeError() == ErrorNoMoreFiles) break;
                return result;
            }
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [NssmUpstreamFunction("src/process.cpp", 199, "int kill_threads(kill_t *k)", "NssmProcessTests.kill_threads_returns_signal_result")]
    public static int kill_threads(NssmKillContext context) => kill_threads(null, context);

    [NssmUpstreamFunction("src/process.cpp", 204, "int kill_process(nssm_service_t *service, kill_t *k)", "NssmProcessTests.kill_process_uses_upstream_stop_order")]
    public static int kill_process(NssmServiceProcessState? service, NssmKillContext? context)
    {
        if (context is null) return 1;
        if (GetExitCodeProcess(context.ProcessHandle, out var exitCode) && exitCode != StillActive) return 1;

        if ((context.StopMethod & StopMethodConsole) != 0 && kill_console(context) == 0) return 1;

        if ((context.StopMethod & StopMethodWindow) != 0)
        {
            EnumWindows((window, _) => kill_window(window, context) != 0, IntPtr.Zero);
            if (context.Signalled != 0)
            {
                if (WaitForSingleObject(context.ProcessHandle, context.KillWindowDelay) == 0) return 1;
                context.Signalled = 0;
            }
        }

        if ((context.StopMethod & StopMethodThreads) != 0 && kill_threads(context) != 0)
        {
            if (WaitForSingleObject(context.ProcessHandle, context.KillThreadsDelay) == 0) return 1;
        }

        return (context.StopMethod & StopMethodTerminate) != 0 && TerminateProcess(context.ProcessHandle, context.ExitCode) ? 1 : 0;
    }

    [NssmUpstreamFunction("src/process.cpp", 249, "int kill_process(kill_t *k)", "NssmProcessTests.kill_process_uses_upstream_stop_order")]
    public static int kill_process(NssmKillContext? context) => kill_process(null, context);

    [NssmUpstreamFunction("src/process.cpp", 254, "int kill_console(nssm_service_t *service, kill_t *k)", "NssmProcessTests.kill_console_rejects_null_context")]
    public static int kill_console(NssmServiceProcessState? service, NssmKillContext? context)
    {
        if (context is null) return 1;
        if (!OperatingSystem.IsWindows()) return 4;
        if (!AttachConsole(context.ProcessId))
        {
            return Marshal.GetLastPInvokeError() switch
            {
                ErrorInvalidHandle => 1,
                ErrorGenFailure => 2,
                _ => 3
            };
        }

        var result = 0;
        var ignored = SetConsoleCtrlHandler(IntPtr.Zero, true);
        if (!ignored) result = 4;
        if (result == 0 && !GenerateConsoleCtrlEvent(CtrlCEvent, 0)) result = 5;
        _ = FreeConsole();
        if (WaitForSingleObject(context.ProcessHandle, context.KillConsoleDelay) != 0) result = 6;
        if (ignored) _ = SetConsoleCtrlHandler(IntPtr.Zero, false);
        return result;
    }

    [NssmUpstreamFunction("src/process.cpp", 315, "int kill_console(kill_t *k)", "NssmProcessTests.kill_console_rejects_null_context")]
    public static int kill_console(NssmKillContext? context) => kill_console(null, context);

    [NssmUpstreamFunction("src/process.cpp", 319, "void walk_process_tree(nssm_service_t *service, walk_function_t fn, kill_t *k, unsigned long ppid)", "NssmProcessTests.walk_process_tree_visits_root_first")]
    public static void walk_process_tree(NssmServiceProcessState? service, NssmWalkFunction function, NssmKillContext? context, uint parentProcessId)
    {
        if (context is null || context.ProcessId == 0) return;
        var processId = context.ProcessId;
        var depth = context.Depth;

        var processHandle = OpenProcess(Synchronize | ProcessQueryInformation | ProcessVmRead | ProcessTerminate, false, processId);
        if (processHandle != IntPtr.Zero)
        {
            context.ProcessHandle = processHandle;
            _ = function(service, context);
            CloseHandle(processHandle);
        }

        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return;
        try
        {
            var entry = new ProcessEntryNative { Size = checked((uint)Marshal.SizeOf<ProcessEntryNative>()) };
            if (!Process32First(snapshot, ref entry)) return;
            context.Depth++;
            while (true)
            {
                var translated = ProcessEntry.From(entry);
                if (check_parent(context, translated, processId) == 0)
                {
                    context.ProcessId = translated.ProcessId;
                    walk_process_tree(service, function, context, parentProcessId);
                    context.ProcessId = processId;
                }
                if (Process32Next(snapshot, ref entry)) continue;
                if (Marshal.GetLastPInvokeError() == ErrorNoMoreFiles) break;
                context.Depth = depth;
                return;
            }
            context.Depth = depth;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [NssmUpstreamFunction("src/process.cpp", 399, "void kill_process_tree(kill_t *k, unsigned long ppid)", "NssmProcessTests.kill_process_tree_delegates_to_walker")]
    public static void kill_process_tree(NssmKillContext context, uint parentProcessId) =>
        walk_process_tree(null, kill_process, context, parentProcessId);

    [NssmUpstreamFunction("src/process.cpp", 403, "int print_process(nssm_service_t *service, kill_t *k)", "NssmProcessTests.print_process_uses_eight_column_pid")]
    public static int print_process(NssmServiceProcessState? service, NssmKillContext context, TextWriter? output = null)
    {
        var executable = new StringBuilder(32767);
        var size = checked((uint)executable.Capacity);
        if (!QueryFullProcessImageName(context.ProcessHandle, 0, executable, ref size))
        {
            executable.Clear();
            executable.EnsureCapacity(32767);
            if (GetModuleFileNameEx(context.ProcessHandle, IntPtr.Zero, executable, executable.Capacity) == 0)
            {
                executable.Clear();
                executable.Append(Marshal.GetLastPInvokeError() == ErrorPartialCopy ? "[WOW64]" : "???");
            }
        }

        (output ?? Console.Out).WriteLine($"{context.ProcessId,8} {new string(' ', checked((int)context.Depth))}{executable}");
        return 1;
    }

    [NssmUpstreamFunction("src/process.cpp", 433, "int print_process(kill_t *k)", "NssmProcessTests.print_process_uses_eight_column_pid")]
    public static int print_process(NssmKillContext context, TextWriter? output = null) => print_process(null, context, output);

    public static IReadOnlyList<ProcessEntry> snapshot_processes()
    {
        var result = new List<ProcessEntry>();
        if (!OperatingSystem.IsWindows()) return result;
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return result;
        try
        {
            var entry = new ProcessEntryNative { Size = checked((uint)Marshal.SizeOf<ProcessEntryNative>()) };
            if (!Process32First(snapshot, ref entry)) return result;
            do result.Add(ProcessEntry.From(entry));
            while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public readonly record struct ProcessEntry(uint ProcessId, uint ParentProcessId, string Executable)
    {
        internal static ProcessEntry From(ProcessEntryNative value) => new(value.ProcessId, value.ParentProcessId, value.ExecutableFile ?? "");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public long ToLong() => unchecked((long)(((ulong)High << 32) | Low));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadEntry
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ProcessEntryNative
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string? ExecutableFile;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(IntPtr thread, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateSelf(int impersonationLevel);

    [DllImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, out TokenPrivileges previousState, out uint returnLength);

    [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges, ref TokenPrivileges newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntryNative entry);

    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntryNative entry);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder executableName, ref uint size);

    [DllImport("psapi.dll", EntryPoint = "GetModuleFileNameExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameEx(IntPtr process, IntPtr module, StringBuilder fileName, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);
}
