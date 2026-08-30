using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MyPowerTools.Abstractions;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Finds live processes launched from a Service Unit manifest. This closes the recovery gap left
/// by a single PID state file: after a manager crash or an older failed re-adoption, matching unit
/// processes can still be recovered and duplicate instances can be retired safely.
/// </summary>
internal static class UnitProcessDiscovery
{
    public static IReadOnlyList<Process> FindMatching(ServiceUnitManifest manifest)
    {
        if (!OperatingSystem.IsWindows())
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
        if (!OperatingSystem.IsWindows())
        {
            return DirectImageMatches(process, manifest.Exec);
        }

        try
        {
            if (process.HasExited)
            {
                return false;
            }

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
        if (string.IsNullOrWhiteSpace(actualImage))
        {
            return false;
        }

        if (Path.IsPathRooted(expectedExec))
        {
            return PathEquals(actualImage, expectedExec);
        }

        var expectedName = Path.GetFileName(expectedExec);
        var actualName = Path.GetFileName(actualImage);
        return string.Equals(actualName, expectedName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   Path.GetFileNameWithoutExtension(actualName),
                   Path.GetFileNameWithoutExtension(expectedName),
                   StringComparison.OrdinalIgnoreCase);
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
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        public readonly ushort Length;
        public readonly ushort MaximumLength;
        public readonly IntPtr Buffer;
    }

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        IntPtr processInformation,
        int processInformationLength,
        out int returnLength);
}
