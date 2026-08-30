using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NssmManager.Contracts;
using NssmManager.Windows;

namespace NssmManager.Supervisor;

internal static class NativeChildProcess
{
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint IdlePriorityClass = 0x00000040;
    private const uint BelowNormalPriorityClass = 0x00004000;
    private const uint NormalPriorityClass = 0x00000020;
    private const uint AboveNormalPriorityClass = 0x00008000;
    private const uint HighPriorityClass = 0x00000080;
    private const uint RealtimePriorityClass = 0x00000100;

    public static Process Start(NssmServiceConfiguration configuration, string application, string workingDirectory, NativeChildProcessIo io) =>
        Start(configuration, application, workingDirectory, io, BuildEnvironmentDictionary(configuration));

    public static Process Start(NssmServiceConfiguration configuration, string application, string workingDirectory, NativeChildProcessIo io, IReadOnlyDictionary<string, string> environment)
    {
        using var stdin = io.DuplicateStandardInput();
        using var stdout = io.DuplicateStandardOutput();
        using var stderr = io.DuplicateStandardError();
        var startup = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
            Flags = io.HasStandardHandles ? StartfUseStdHandles : 0,
            StandardInput = stdin?.DangerousGetHandle() ?? IntPtr.Zero,
            StandardOutput = stdout?.DangerousGetHandle() ?? IntPtr.Zero,
            StandardError = stderr?.DangerousGetHandle() ?? IntPtr.Zero
        };
        var commandLine = new StringBuilder(Quote(application));
        if (!string.IsNullOrWhiteSpace(configuration.AppParameters)) commandLine.Append(' ').Append(Expand(configuration.AppParameters, environment));
        var affinity = ManagedServiceRuntime.ParseAffinity(configuration.Affinity);
        var flags = Priority(configuration.Priority) |
            (configuration.NoConsole ? 0 : CreateNewConsole) |
            (affinity == 0 ? 0 : CreateSuspended);
        var information = StartProcess(commandLine, workingDirectory, BuildEnvironment(environment), io.HasStandardHandles, flags, startup);
        try
        {
            if (affinity != 0)
            {
                try
                {
                    if (GetProcessAffinityMask(information.Process, out _, out var systemAffinity)) affinity &= systemAffinity;
                    if (affinity != 0 && !SetProcessAffinityMask(information.Process, unchecked((nuint)affinity)))
                        NssmManager.Windows.NssmEvent.log_event(2, NssmManager.Windows.NssmEvent.message_id("NSSM_EVENT_SETPROCESSAFFINITYMASK_FAILED"), configuration.Name, new Win32Exception(Marshal.GetLastWin32Error()).Message);
                }
                finally { _ = ResumeThread(information.Thread); }
            }
            return Process.GetProcessById(checked((int)information.ProcessId));
        }
        finally { CloseHandle(information.Thread); CloseHandle(information.Process); }
    }

    public static Process StartHook(string command, string workingDirectory, IReadOnlyDictionary<string, string?> environment, NativeChildProcessIo? io, bool shareOutputHandles)
    {
        using var stdout = shareOutputHandles ? io?.DuplicateStandardOutput() : null;
        using var stderr = shareOutputHandles ? io?.DuplicateStandardError() : null;
        var useHandles = stdout is not null || stderr is not null;
        var startup = new StartupInfo
        {
            Size = (uint)Marshal.SizeOf<StartupInfo>(),
            Flags = useHandles ? StartfUseStdHandles : 0,
            StandardOutput = stdout?.DangerousGetHandle() ?? IntPtr.Zero,
            StandardError = stderr?.DangerousGetHandle() ?? IntPtr.Zero
        };
        var information = StartProcess(new StringBuilder(command), workingDirectory,
            BuildEnvironment((IEnumerable<KeyValuePair<string, string?>>)environment), useHandles, 0, startup);
        try { return Process.GetProcessById(checked((int)information.ProcessId)); }
        finally { CloseHandle(information.Thread); CloseHandle(information.Process); }
    }

    private static ProcessInformation StartProcess(StringBuilder commandLine, string workingDirectory, string environment, bool inheritHandles, uint flags, StartupInfo? suppliedStartup = null)
    {
        var startup = suppliedStartup ?? new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        var environmentPointer = Marshal.StringToHGlobalUni(environment);
        try
        {
            if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, inheritHandles, flags | CreateUnicodeEnvironment,
                environmentPointer, workingDirectory, ref startup, out var information))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW");
            return information;
        }
        finally { Marshal.FreeHGlobal(environmentPointer); }
    }

    internal static IReadOnlyDictionary<string, string> BuildEnvironmentDictionary(NssmServiceConfiguration configuration)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (configuration.Environment.Length == 0)
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables()) values[(string)item.Key] = item.Value?.ToString() ?? "";
        Apply(values, configuration.Environment);
        Apply(values, configuration.EnvironmentExtra);
        return values;
    }

    private static string BuildEnvironment(IEnumerable<KeyValuePair<string, string?>> environment) =>
        string.Join('\0', environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => item.Key + "=" + (item.Value ?? string.Empty))) + "\0\0";

    private static string BuildEnvironment(IReadOnlyDictionary<string, string> environment) =>
        string.Join('\0', environment.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => item.Key + "=" + item.Value)) + "\0\0";

    private static void Apply(Dictionary<string, string> environment, IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Length == 0 || entry[0] == '=') continue;
            var separator = entry.IndexOf('=');
            if (separator > 0) environment[entry[..separator]] = Expand(entry[(separator + 1)..], environment);
        }
    }

    internal static string Expand(string value, IReadOnlyDictionary<string, string> environment)
    {
        var output = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            var opening = value.IndexOf('%', index);
            if (opening < 0) { output.Append(value, index, value.Length - index); break; }
            var closing = value.IndexOf('%', opening + 1);
            if (closing < 0) { output.Append(value, index, value.Length - index); break; }
            output.Append(value, index, opening - index);
            var name = value[(opening + 1)..closing];
            if (environment.TryGetValue(name, out var expanded)) output.Append(expanded);
            else output.Append(value, opening, closing - opening + 1);
            index = closing + 1;
        }
        return output.ToString();
    }

    internal static string ResolveWorkingDirectory(NssmServiceConfiguration configuration, string expandedApplication, IReadOnlyDictionary<string, string> environment)
    {
        var configured = Expand(configuration.AppDirectory, environment);
        if (configured.Length > 0) return configured;
        var fallback = NssmCore.strip_basename(expandedApplication);
        return fallback.Length > 0 ? fallback : Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    }

    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"") + '"';
    private static uint Priority(string value) => value.ToUpperInvariant() switch
    {
        "REALTIME_PRIORITY_CLASS" => RealtimePriorityClass,
        "HIGH_PRIORITY_CLASS" => HighPriorityClass,
        "ABOVE_NORMAL_PRIORITY_CLASS" => AboveNormalPriorityClass,
        "BELOW_NORMAL_PRIORITY_CLASS" => BelowNormalPriorityClass,
        "IDLE_PRIORITY_CLASS" => IdlePriorityClass,
        _ => NormalPriorityClass
    };
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo { public uint Size; public string? Reserved; public string? Desktop; public string? Title; public uint X; public uint Y; public uint XSize; public uint YSize; public uint XCountChars; public uint YCountChars; public uint FillAttribute; public uint Flags; public ushort ShowWindow; public ushort Reserved2; public IntPtr Reserved2Pointer; public IntPtr StandardInput; public IntPtr StandardOutput; public IntPtr StandardError; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessAffinityMask(IntPtr process, out nuint processAffinityMask, out nuint systemAffinityMask);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessAffinityMask(IntPtr process, nuint processAffinityMask);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
}
