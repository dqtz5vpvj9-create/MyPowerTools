using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LocalLagCleaner.MyPowerTools;

internal sealed record SystemFileHandlePathProbeResult(
    SystemFileHandleAttribution Attribution,
    IReadOnlyList<FileHandlePathGroupSnapshot> PathGroups);

internal static class SystemFileHandlePathProbe
{
    private const uint ProcessDuplicateHandle = 0x00000040;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint FileNameOpened = 0x00000008;
    private const uint VolumeNameNt = 0x00000002;
    private const int ErrorAccessDenied = 5;
    private const int MaximumPathChars = 32 * 1024;

    public static SystemFileHandlePathProbeResult Read(
        ulong totalFileHandles,
        IReadOnlyList<SystemFileHandleDescriptor> samples,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "System file handle path attribution supports Windows only.");
        }

        using var systemProcess = OpenProcess(
            ProcessDuplicateHandle,
            inheritHandle: false,
            processId: 4);
        if (systemProcess.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            var requiresAdministrator = error == ErrorAccessDenied;
            var message = requiresAdministrator
                ? "PID 4 rejected PROCESS_DUP_HANDLE for the current token; an administrator diagnostic run is required for bounded path sampling."
                : $"OpenProcess(PID 4, PROCESS_DUP_HANDLE) failed: {new Win32Exception(error).Message} ({error}).";
            return new SystemFileHandlePathProbeResult(
                new SystemFileHandleAttribution(
                    totalFileHandles,
                    samples.Count,
                    0,
                    0,
                    0,
                    requiresAdministrator,
                    message),
                []);
        }

        var observations = new List<PathObservation>();
        var errorCounts = new Dictionary<int, int>();
        var attempted = 0;
        var duplicated = 0;
        foreach (var sample in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted++;
            if (!DuplicateHandle(
                    systemProcess,
                    new IntPtr(unchecked((long)sample.HandleValue)),
                    GetCurrentProcess(),
                    out var duplicatedHandle,
                    0,
                    inheritHandle: false,
                    DuplicateSameAccess))
            {
                var error = Marshal.GetLastWin32Error();
                errorCounts[error] = errorCounts.GetValueOrDefault(error) + 1;
                continue;
            }

            using (duplicatedHandle)
            {
                duplicated++;
                var kind = FileKind(
                    GetFileType(duplicatedHandle.DangerousGetHandle()));
                var path = TryReadPath(duplicatedHandle);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                path = SanitizePath(path);

                observations.Add(new PathObservation(
                    PathGroup(path),
                    kind,
                    path));
            }
        }

        var pathGroups = observations
            .GroupBy(
                item => (item.Group, item.Kind),
                new PathGroupKeyComparer())
            .Select(group => new FileHandlePathGroupSnapshot(
                group.Key.Group,
                group.Key.Kind,
                group.Count(),
                observations.Count == 0
                    ? 0
                    : Math.Round(group.Count() * 100d / observations.Count, 2),
                group
                    .Select(item => item.Path)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToArray()))
            .OrderByDescending(item => item.SampleCount)
            .ThenBy(item => item.PathGroup, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var errors = errorCounts.Count == 0
            ? ""
            : " Duplicate failures: " +
              string.Join(
                  ", ",
                  errorCounts
                      .OrderByDescending(item => item.Value)
                      .Select(item => $"{item.Key}×{item.Value}")) +
              ".";
        return new SystemFileHandlePathProbeResult(
            new SystemFileHandleAttribution(
                totalFileHandles,
                samples.Count,
                attempted,
                duplicated,
                observations.Count,
                false,
                $"Uniformly sampled {samples.Count:n0} of {totalFileHandles:n0} PID 4 File handles; duplicated {duplicated:n0}, resolved {observations.Count:n0} opened paths into {pathGroups.Length:n0} groups.{errors}"),
            pathGroups);
    }

    private static string TryReadPath(SafeFileHandle handle)
    {
        var capacity = 1024;
        while (capacity <= MaximumPathChars)
        {
            var builder = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(
                handle,
                builder,
                checked((uint)capacity),
                FileNameOpened | VolumeNameNt);
            if (length == 0)
            {
                return "";
            }

            if (length < capacity)
            {
                return builder.ToString();
            }

            capacity = checked((int)Math.Min(
                MaximumPathChars + 1L,
                (long)length + 1));
        }

        return "";
    }

    private static string PathGroup(string path)
    {
        var normalized = path.Replace('/', '\\').Trim();
        var parts = normalized
            .Split(
                '\\',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return "(empty)";
        }

        var keep = 1;
        if (string.Equals(parts[0], "Device", StringComparison.OrdinalIgnoreCase))
        {
            keep = parts.Length >= 2 &&
                   (string.Equals(parts[1], "NamedPipe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(parts[1], "Mailslot", StringComparison.OrdinalIgnoreCase))
                ? Math.Min(2, parts.Length)
                : parts.Length >= 2 &&
                  string.Equals(parts[1], "Mup", StringComparison.OrdinalIgnoreCase)
                    ? Math.Min(4, parts.Length)
                    : Math.Min(3, parts.Length);
        }
        else if (parts[0].EndsWith(
                     ":",
                     StringComparison.OrdinalIgnoreCase))
        {
            keep = Math.Min(2, parts.Length);
        }
        else if (string.Equals(parts[0], "?", StringComparison.Ordinal))
        {
            keep = Math.Min(3, parts.Length);
        }
        else
        {
            keep = Math.Min(2, parts.Length);
        }

        return "\\" + string.Join("\\", parts.Take(keep));
    }

    private static string SanitizePath(string value)
    {
        var userName = Environment.UserName;
        return string.IsNullOrWhiteSpace(userName)
            ? value
            : value.Replace(
                userName,
                "%USERNAME%",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string FileKind(uint type)
    {
        return type switch
        {
            0x0001 => "Disk",
            0x0002 => "Character",
            0x0003 => "Pipe",
            0x8000 => "Remote",
            _ => "Unknown"
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        SafeProcessHandle sourceProcessHandle,
        IntPtr sourceHandle,
        IntPtr targetProcessHandle,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathChars,
        uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    private sealed record PathObservation(
        string Group,
        string Kind,
        string Path);

    private sealed class PathGroupKeyComparer :
        IEqualityComparer<(string Group, string Kind)>
    {
        public bool Equals(
            (string Group, string Kind) x,
            (string Group, string Kind) y)
        {
            return string.Equals(
                       x.Group,
                       y.Group,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       x.Kind,
                       y.Kind,
                       StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((string Group, string Kind) value)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Group),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Kind));
        }
    }
}
