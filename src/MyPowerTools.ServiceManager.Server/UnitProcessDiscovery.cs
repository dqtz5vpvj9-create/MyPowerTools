using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using MyPowerTools.Abstractions;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Finds live processes launched from a Service Unit manifest. This closes the recovery gap left
/// by a single PID state file: after a manager crash or an older failed re-adoption, matching unit
/// processes can still be recovered and duplicate instances can be retired safely.
///
/// Everything this class returns is a candidate for <c>Kill(entireProcessTree: true)</c>, so a
/// match has to be specific enough that no bystander can ever be selected. A candidate must run
/// the manifest's exact rooted image with the manifest's exact arguments, in the caller's session,
/// as the caller's user, and — where the environment block is readable — out of the same
/// MyPowerTools data root.
/// </summary>
internal static class UnitProcessDiscovery
{
    /// <summary>
    /// The marker that ties a unit process to the installation that launched it. Manifests carry
    /// it (configure-user-services.ps1 stamps every unit's environment with the resolved data
    /// root) and <see cref="UnitSupervisor"/> copies the manifest environment into the child, so a
    /// process reporting a different value belongs to another MyPowerTools instance.
    /// </summary>
    private const string InstanceMarkerVariable = "MPT_DATA_ROOT";

    private static readonly Lazy<int> CurrentSessionId = new(ReadCurrentSessionId);
    private static readonly Lazy<string?> CurrentUserId = new(ReadCurrentUserId);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static IReadOnlyList<Process> FindMatching(ServiceUnitManifest manifest)
    {
        // A manifest that names its executable without a directory can only be matched by file
        // name, which selects every process on the machine that happens to share that name.
        // Discovery declines rather than nominating strangers for termination.
        if (!Path.IsPathRooted(manifest.Exec))
        {
            return Array.Empty<Process>();
        }

        var matches = new List<Process>();
        foreach (var process in Process.GetProcesses())
        {
            if (Matches(process, manifest))
            {
                matches.Add(process);
            }
            else
            {
                process.Dispose();
            }
        }

        return matches;
    }

    public static bool Matches(Process process, ServiceUnitManifest manifest)
    {
        if (!Path.IsPathRooted(manifest.Exec))
        {
            return false;
        }

        try
        {
            if (process.HasExited || !RunsInCurrentSession(process))
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or PlatformNotSupportedException)
        {
            return false;
        }

        return CommandMatches(process, manifest) &&
               RunsAsCurrentUser(process) &&
               InstanceMarkerMatches(process, manifest);
    }

    private static bool CommandMatches(Process process, ServiceUnitManifest manifest)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsCommandMatches(process, manifest);
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxCommandMatches(process, manifest);
        }

        // macOS exposes another process's argument vector only to a privileged caller, so the
        // resolved image path is as specific as this platform gets.
        return DirectImageMatches(process, manifest.Exec);
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsCommandMatches(Process process, ServiceUnitManifest manifest)
    {
        try
        {
            var commandLine = QueryProcessCommandLine(process);
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                return false;
            }

            var actual = ParseCommandLineArguments(commandLine);
            if (actual.Count == 0)
            {
                return false;
            }

            var extension = Path.GetExtension(manifest.Exec);
            if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                return BatchCommandMatches(process, commandLine, actual, manifest);
            }

            return DirectImageMatches(process, manifest.Exec) &&
                   actual.Skip(1).SequenceEqual(manifest.Arguments, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    [SupportedOSPlatform("linux")]
    private static bool LinuxCommandMatches(Process process, ServiceUnitManifest manifest)
    {
        var image = ReadProcLinkTarget(process.Id, "exe");
        if (string.IsNullOrEmpty(image) || !PathEquals(image, manifest.Exec))
        {
            return false;
        }

        var commandLine = ReadProcNulSeparated(process.Id, "cmdline");
        return commandLine.Count > 0 &&
               commandLine.Skip(1).SequenceEqual(manifest.Arguments, StringComparer.Ordinal);
    }

    [SupportedOSPlatform("windows")]
    private static bool BatchCommandMatches(
        Process process,
        string commandLine,
        IReadOnlyList<string> actual,
        ServiceUnitManifest manifest)
    {
        var imageName = Path.GetFileName(TryGetImagePath(process));
        if (!string.Equals(imageName, "cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // CreateProcessW may expose either "cmd.exe /c unit.cmd ..." or the original
        // "unit.cmd ..." command line while the process image is cmd.exe.
        for (var index = 0; index < actual.Count; index++)
        {
            if (!PathEquals(actual[index], manifest.Exec))
            {
                continue;
            }

            return actual.Skip(index + 1).SequenceEqual(manifest.Arguments, StringComparer.Ordinal);
        }

        // cmd.exe wraps quoted batch paths in a second quote pair. CommandLineToArgvW then treats
        // the complete `/c` payload as one argument, so parse the payload again after locating the
        // exact manifest path.
        var pathIndex = commandLine.IndexOf(manifest.Exec, StringComparison.OrdinalIgnoreCase);
        if (pathIndex < 0)
        {
            return false;
        }

        var prefix = commandLine[..pathIndex];
        var commandSwitchIndex = prefix.LastIndexOf("/c", StringComparison.OrdinalIgnoreCase);
        if (commandSwitchIndex < 0 ||
            prefix[(commandSwitchIndex + 2)..].Any(character => !char.IsWhiteSpace(character) && character != '"'))
        {
            return false;
        }

        var suffix = commandLine[(pathIndex + manifest.Exec.Length)..];
        if (suffix.StartsWith('"'))
        {
            suffix = suffix[1..];
        }
        suffix = suffix.TrimStart();
        if (suffix.EndsWith('"'))
        {
            suffix = suffix[..^1];
        }

        var payload = $"\"{manifest.Exec}\" {suffix}";
        var payloadArguments = ParseCommandLineArguments(payload);
        return payloadArguments.Count > 0 &&
               PathEquals(payloadArguments[0], manifest.Exec) &&
               payloadArguments.Skip(1).SequenceEqual(manifest.Arguments, StringComparer.Ordinal);
    }

    private static bool DirectImageMatches(Process process, string expectedExec)
    {
        var actualImage = TryGetImagePath(process);
        return !string.IsNullOrWhiteSpace(actualImage) && PathEquals(actualImage, expectedExec);
    }

    private static bool RunsInCurrentSession(Process process)
    {
        // Windows logon sessions isolate one interactive user's units from another's; on Unix the
        // equivalent guarantee comes from the uid check in RunsAsCurrentUser.
        return !OperatingSystem.IsWindows() || process.SessionId == CurrentSessionId.Value;
    }

    private static bool RunsAsCurrentUser(Process process)
    {
        var expected = CurrentUserId.Value;
        if (expected is null)
        {
            return true;
        }

        string? actual = null;
        if (OperatingSystem.IsWindows())
        {
            actual = TryGetWindowsUserSid(process.Id);
        }
        else if (OperatingSystem.IsLinux())
        {
            actual = TryGetProcUserId(process.Id);
        }

        // An owner we cannot read is not evidence of a different owner; the image, arguments and
        // session checks still apply.
        return actual is null || string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static bool InstanceMarkerMatches(Process process, ServiceUnitManifest manifest)
    {
        if (manifest.Environment is null ||
            !manifest.Environment.TryGetValue(InstanceMarkerVariable, out var expected) ||
            string.IsNullOrEmpty(expected))
        {
            return true;
        }

        var block = TryReadEnvironmentBlock(process);
        if (block is null)
        {
            return true;
        }

        // The block was readable, so a process that does not carry the manifest's data root — or
        // carries a different one — belongs to another MyPowerTools instance and must survive.
        return string.Equals(FindVariable(block, InstanceMarkerVariable), expected, PathComparison);
    }

    private static string? TryReadEnvironmentBlock(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return TryReadWindowsEnvironmentBlock(process.Id);
        }

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var entries = ReadProcNulSeparated(process.Id, "environ");
        return entries.Count == 0 ? null : string.Join('\0', entries);
    }

    private static string? FindVariable(string block, string name)
    {
        foreach (var entry in block.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf('=');
            if (separator > 0 && entry.AsSpan(0, separator).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return entry[(separator + 1)..];
            }
        }

        return null;
    }

    private static string TryGetImagePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool PathEquals(string left, string right)
    {
        var comparison = PathComparison;
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch
        {
            return string.Equals(left, right, comparison);
        }
    }

    private static int ReadCurrentSessionId()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    private static string? ReadCurrentUserId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.User?.Value;
            }

            return OperatingSystem.IsLinux() ? TryGetProcUserId(Environment.ProcessId) : null;
        }
        catch
        {
            return null;
        }
    }

    [SupportedOSPlatform("linux")]
    private static string? TryGetProcUserId(int processId)
    {
        try
        {
            foreach (var line in File.ReadLines($"/proc/{processId}/status"))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    continue;
                }

                // "Uid:\treal\teffective\tsaved\tfilesystem" — the real uid identifies the owner.
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return fields.Length >= 2 ? fields[1] : null;
            }
        }
        catch
        {
            // An exited or foreign process simply has no readable owner.
        }

        return null;
    }

    [SupportedOSPlatform("linux")]
    private static string ReadProcLinkTarget(int processId, string entry)
    {
        try
        {
            return new FileInfo($"/proc/{processId}/{entry}").LinkTarget ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    [SupportedOSPlatform("linux")]
    private static IReadOnlyList<string> ReadProcNulSeparated(int processId, string entry)
    {
        try
        {
            var text = Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{processId}/{entry}")).TrimEnd('\0');
            return text.Length == 0 ? Array.Empty<string>() : text.Split('\0');
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? TryGetWindowsUserSid(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                const int tokenUser = 1;
                GetTokenInformation(token, tokenUser, IntPtr.Zero, 0, out var length);
                if (length <= 0)
                {
                    return null;
                }

                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    return GetTokenInformation(token, tokenUser, buffer, length, out _)
                        ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value
                        : null;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// Reads a process's environment block out of its PEB. Returns null when the block cannot be
    /// reached — a protected or higher-integrity process, or a layout this build does not know.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string? TryReadWindowsEnvironmentBlock(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var basic = new ProcessBasicInformation();
            if (NtQueryInformationProcess(process, 0, ref basic, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0 ||
                basic.PebBaseAddress == IntPtr.Zero)
            {
                return null;
            }

            var wide = IntPtr.Size == 8;
            var parameters = ReadPointer(process, basic.PebBaseAddress + (wide ? 0x20 : 0x10));
            if (parameters == IntPtr.Zero)
            {
                return null;
            }

            var block = ReadPointer(process, parameters + (wide ? 0x80 : 0x48));
            return block == IntPtr.Zero ? null : ReadEnvironmentBlock(process, block);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr ReadPointer(IntPtr process, IntPtr address)
    {
        var buffer = new byte[IntPtr.Size];
        if (!ReadProcessMemory(process, address, buffer, buffer.Length, out _))
        {
            return IntPtr.Zero;
        }

        return IntPtr.Size == 8
            ? new IntPtr(BitConverter.ToInt64(buffer, 0))
            : new IntPtr(BitConverter.ToInt32(buffer, 0));
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadEnvironmentBlock(IntPtr process, IntPtr address)
    {
        const int chunkSize = 4096;
        const int maximumBytes = 128 * 1024;
        var text = new StringBuilder();
        var buffer = new byte[chunkSize];
        for (var offset = 0; offset < maximumBytes; offset += chunkSize)
        {
            if (!ReadProcessMemory(process, address + offset, buffer, chunkSize, out var read) || read == IntPtr.Zero)
            {
                break;
            }

            var chunk = Encoding.Unicode.GetString(buffer, 0, (int)read & ~1);
            var terminator = chunk.IndexOf("\0\0", StringComparison.Ordinal);
            if (terminator >= 0)
            {
                text.Append(chunk, 0, terminator);
                return text.ToString();
            }

            text.Append(chunk);
        }

        return text.Length == 0 ? null : text.ToString();
    }

    private static IReadOnlyList<string> ParseCommandLineArguments(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var argumentCount);
        if (argv == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var arguments = new string[argumentCount];
            for (var index = 0; index < argumentCount; index++)
            {
                var pointer = Marshal.ReadIntPtr(argv, index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }

            return arguments;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    private static string QueryProcessCommandLine(Process process)
    {
        const int processCommandLineInformation = 60;
        const int statusInfoLengthMismatch = unchecked((int)0xC0000004);

        var processHandle = process.SafeHandle.DangerousGetHandle();
        var status = NtQueryInformationProcess(
            processHandle,
            processCommandLineInformation,
            IntPtr.Zero,
            0,
            out var requiredLength);
        if (status != statusInfoLengthMismatch || requiredLength <= Marshal.SizeOf<UnicodeString>())
        {
            return string.Empty;
        }

        var buffer = Marshal.AllocHGlobal(requiredLength);
        try
        {
            status = NtQueryInformationProcess(
                processHandle,
                processCommandLineInformation,
                buffer,
                requiredLength,
                out _);
            if (status != 0)
            {
                return string.Empty;
            }

            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            return value.Buffer == IntPtr.Zero || value.Length == 0
                ? string.Empty
                : Marshal.PtrToStringUni(value.Buffer, value.Length / sizeof(char)) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;
    private const uint TokenQuery = 0x0008;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        int tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);
}
