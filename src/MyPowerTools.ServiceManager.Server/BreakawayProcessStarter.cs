using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Starts a child process detached from the current process's job object, so that the child
/// survives the parent (ServiceManager) being killed or restarted, while still redirecting
/// stdout/stderr for log capture. On Windows this uses <c>CreateProcess</c> with
/// <c>CREATE_BREAKAWAY_FROM_JOB</c> and inheritable anonymous pipes; on other platforms it
/// falls back to <see cref="Process.Start(ProcessStartInfo)"/> (Unix processes are not
/// job-coupled by default).
///
/// This is the concrete realization of the rule "the unit must outlive the ServiceManager".
/// Without breakaway, force-killing the ServiceManager cascades to its children on Windows.
/// </summary>
internal static class BreakawayProcessStarter
{
    /// <summary>
    /// Starts the process. Returns the <see cref="Process"/> plus <see cref="StreamReader"/>s
    /// for stdout/stderr (non-null only when <paramref name="psi"/> requested redirection).
    ///
    /// On Windows this first attempts <c>CREATE_BREAKAWAY_FROM_JOB</c> so the child survives the
    /// parent being killed. If the parent's job object forbids breakaway (common under some test
    /// hosts and CI), it falls back to the managed <see cref="Process.Start(ProcessStartInfo)"/>
    /// path so the unit still launches; re-adoption via persisted PID/token then keeps the unit
    /// alive across a graceful ServiceManager restart.
    /// </summary>
    public static BreakawayProcess Start(ProcessStartInfo psi)
    {
        if (!OperatingSystem.IsWindows())
        {
            var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            return new BreakawayProcess(p, psi.RedirectStandardOutput ? p.StandardOutput : null, psi.RedirectStandardError ? p.StandardError : null);
        }

        try
        {
            return StartBreakawayWindows(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5 /* ERROR_ACCESS_DENIED */)
        {
            // Job object does not allow breakaway (e.g. restricted test/CI host). Fall back to the
            // managed launcher so the unit still starts; graceful-restart re-adoption still applies.
            var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null.");
            return new BreakawayProcess(p, psi.RedirectStandardOutput ? p.StandardOutput : null, psi.RedirectStandardError ? p.StandardError : null);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static unsafe BreakawayProcess StartBreakawayWindows(ProcessStartInfo psi)
    {
        const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
        const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
        const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        const uint CREATE_NO_WINDOW = 0x08000000;
        const uint STARTF_USESTDHANDLES = 0x00000100;

        var commandLine = BuildCommandLine(psi.FileName, psi.ArgumentList);
        var workingDirectory = string.IsNullOrEmpty(psi.WorkingDirectory) ? null : psi.WorkingDirectory;

        StreamReader? stdoutReader = null;
        StreamReader? stderrReader = null;

        var startupInfo = new STARTUPINFOW
        {
            cb = (uint)Marshal.SizeOf<STARTUPINFOW>(),
            dwFlags = STARTF_USESTDHANDLES
        };

        IntPtr stdoutWrite = IntPtr.Zero;
        IntPtr stderrWrite = IntPtr.Zero;
        var securityAttributes = new SECURITY_ATTRIBUTES { nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };

        try
        {
            if (psi.RedirectStandardOutput)
            {
                if (!CreatePipe(out var outRead, out stdoutWrite, ref securityAttributes, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(stdout) failed");
                }

                SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
                startupInfo.hStdOutput = outRead;
                var outSafe = new Microsoft.Win32.SafeHandles.SafeFileHandle(outRead, ownsHandle: true);
                stdoutReader = new StreamReader(new FileStream(outSafe, FileAccess.Read), System.Text.Encoding.Default);
            }

            if (psi.RedirectStandardError)
            {
                if (!CreatePipe(out var errRead, out stderrWrite, ref securityAttributes, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(stderr) failed");
                }

                SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0);
                startupInfo.hStdError = errRead;
                var errSafe = new Microsoft.Win32.SafeHandles.SafeFileHandle(errRead, ownsHandle: true);
                stderrReader = new StreamReader(new FileStream(errSafe, FileAccess.Read), System.Text.Encoding.Default);
            }

            var creationFlags = CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW;

            byte[]? environmentBlock = null;
            if (psi.Environment.Count > 0)
            {
                environmentBlock = BuildEnvironmentBlock(psi.Environment);
                creationFlags |= CREATE_UNICODE_ENVIRONMENT;
            }

            // Marshal the (managed) STARTUPINFOW into unmanaged memory so CreateProcessW can read it.
            var siPtr = Marshal.AllocHGlobal(Marshal.SizeOf<STARTUPINFOW>());
            try
            {
                Marshal.StructureToPtr(startupInfo, siPtr, fDeleteOld: false);

                PROCESS_INFORMATION pi;
                string? wd = workingDirectory;

                fixed (byte* envPtr = environmentBlock)
                fixed (char* cmdPtr = commandLine)
                fixed (char* wdPtr = wd)
                {
                    var envArg = environmentBlock is null ? IntPtr.Zero : (IntPtr)envPtr;
                    var ok = CreateProcessW(
                        lpApplicationName: null,
                        lpCommandLine: cmdPtr,
                        lpProcessAttributes: IntPtr.Zero,
                        lpThreadAttributes: IntPtr.Zero,
                        bInheritHandles: stdoutWrite != IntPtr.Zero || stderrWrite != IntPtr.Zero,
                        dwCreationFlags: creationFlags,
                        lpEnvironment: envArg,
                        lpCurrentDirectory: wdPtr,
                        lpStartupInfo: siPtr,
                        lpProcessInformation: out pi);

                    if (!ok)
                    {
                        var err = Marshal.GetLastWin32Error();
                        throw new Win32Exception(err, $"CreateProcessW failed (error {err}) for '{psi.FileName}'.");
                    }

                    CloseHandle(pi.hThread);
                    Process process;
                    try
                    {
                        process = Process.GetProcessById((int)pi.dwProcessId);
                    }
                    finally
                    {
                        CloseHandle(pi.hProcess);
                    }

                    return new BreakawayProcess(process, stdoutReader, stderrReader);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(siPtr);
            }
        }
        catch
        {
            stdoutReader?.Dispose();
            stderrReader?.Dispose();
            if (stdoutWrite != IntPtr.Zero) CloseHandle(stdoutWrite);
            if (stderrWrite != IntPtr.Zero) CloseHandle(stderrWrite);
            throw;
        }
        finally
        {
            // The child has its own inheritable copies; close our side of the write ends so EOF reaches the readers.
            if (stdoutWrite != IntPtr.Zero) CloseHandle(stdoutWrite);
            if (stderrWrite != IntPtr.Zero) CloseHandle(stderrWrite);
        }
    }

    private static string BuildCommandLine(string fileName, System.Collections.ObjectModel.Collection<string> args)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(QuoteIfNeeded(fileName));
        foreach (var arg in args)
        {
            sb.Append(' ').Append(QuoteIfNeeded(arg));
        }

        return sb.ToString();
    }

    private static string QuoteIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (value.IndexOf(' ') < 0 && value.IndexOf('"') < 0)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static byte[] BuildEnvironmentBlock(System.Collections.Generic.IDictionary<string, string?> env)
    {
        // Unicode environment block: sequence of "NAME=VALUE\0" entries, terminated by an extra \0.
        using var ms = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(ms, System.Text.Encoding.Unicode);
        foreach (var (key, value) in env)
        {
            writer.Write(key.ToCharArray());
            writer.Write('=');
            writer.Write((value ?? string.Empty).ToCharArray());
            writer.Write('\0');
        }

        writer.Write('\0');
        return ms.ToArray();
    }

    private const uint HANDLE_FLAG_INHERIT = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe extern bool CreateProcessW(
        string? lpApplicationName,
        char* lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        char* lpCurrentDirectory,
        IntPtr lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>Result of a breakaway start: the process plus redirected output readers.</summary>
internal sealed class BreakawayProcess
{
    public BreakawayProcess(Process process, StreamReader? standardOutput, StreamReader? standardError)
    {
        Process = process;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public Process Process { get; }
    public StreamReader? StandardOutput { get; }
    public StreamReader? StandardError { get; }
}
