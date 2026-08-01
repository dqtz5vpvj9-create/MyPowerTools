using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace LocalLagCleaner.MyPowerTools;

[SupportedOSPlatform("windows")]
internal static class FileSystemFilterProbe
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const int ScStatusProcessInfo = 0;
    private const uint ServiceRunning = 4;

    public static IReadOnlyList<FileSystemFilterSnapshot> Read(
        IReadOnlyList<PoolTagSnapshot> poolTags)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "File system filter inventory supports Windows only.");
        }

        using var services = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services",
            writable: false);
        if (services is null)
        {
            throw new InvalidOperationException(
                "HKLM service registry could not be opened.");
        }

        var manager = OpenSCManager(
            machineName: null,
            databaseName: null,
            ScManagerConnect);
        try
        {
            var candidates = new List<FilterCandidate>();
            foreach (var serviceName in services
                         .GetSubKeyNames()
                         .Order(StringComparer.OrdinalIgnoreCase))
            {
                using var service = services.OpenSubKey(
                    serviceName,
                    writable: false);
                if (service is null)
                {
                    continue;
                }

                using var instances = service.OpenSubKey(
                    "Instances",
                    writable: false);
                var group = service.GetValue("Group") as string ?? "";
                var type = ReadInt32(service.GetValue("Type"));
                var altitudes = ReadAltitudes(instances);
                var explicitlyFileSystemFilter =
                    group.StartsWith(
                        "FSFilter",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        serviceName,
                        "fltmgr",
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(
                        serviceName,
                        "wcifs",
                        StringComparison.OrdinalIgnoreCase);
                if (!explicitlyFileSystemFilter &&
                    altitudes.Count == 0)
                {
                    continue;
                }

                if (type is not (1 or 2) &&
                    !explicitlyFileSystemFilter)
                {
                    continue;
                }

                var imagePath = ResolveDriverPath(
                    serviceName,
                    service.GetValue("ImagePath") as string);
                var metadata = ReadMetadata(imagePath);
                var displayName =
                    metadata.Description.Length > 0
                        ? metadata.Description
                        : service.GetValue("DisplayName") as string ??
                          serviceName;
                var running = manager != IntPtr.Zero &&
                              IsServiceRunning(manager, serviceName);
                var company = metadata.Company;
                var isMicrosoft = company.Contains(
                    "Microsoft",
                    StringComparison.OrdinalIgnoreCase);
                var assessment = Assess(
                    serviceName,
                    group,
                    running,
                    isMicrosoft,
                    poolTags);
                candidates.Add(new FilterCandidate(
                    new FileSystemFilterSnapshot(
                        serviceName,
                        displayName,
                        group,
                        string.Join(", ", altitudes),
                        imagePath,
                        company,
                        metadata.Version,
                        running,
                        isMicrosoft,
                        assessment.Likelihood,
                        assessment.Evidence),
                    assessment.Score));
            }

            return candidates
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Snapshot.Running)
                .ThenBy(
                    item => item.Snapshot.ServiceName,
                    StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Snapshot)
                .ToArray();
        }
        finally
        {
            if (manager != IntPtr.Zero)
            {
                _ = CloseServiceHandle(manager);
            }
        }
    }

    private static IReadOnlyList<string> ReadAltitudes(
        RegistryKey? instances)
    {
        if (instances is null)
        {
            return [];
        }

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (instances.GetValue("Altitude") is string rootAltitude &&
            rootAltitude.Length > 0)
        {
            values.Add(rootAltitude);
        }

        foreach (var instanceName in instances.GetSubKeyNames())
        {
            using var instance = instances.OpenSubKey(
                instanceName,
                writable: false);
            if (instance?.GetValue("Altitude") is string altitude &&
                altitude.Length > 0)
            {
                values.Add(altitude);
            }
        }

        return values
            .OrderByDescending(ParseAltitude)
            .ThenBy(item => item, StringComparer.Ordinal)
            .ToArray();
    }

    private static double ParseAltitude(string value)
    {
        return double.TryParse(
            value,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var altitude)
            ? altitude
            : 0;
    }

    private static string ResolveDriverPath(
        string serviceName,
        string? configuredPath)
    {
        var windowsRoot = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        var path = (configuredPath ?? "").Trim().Trim('"');
        if (path.Length == 0)
        {
            path = Path.Combine(
                windowsRoot,
                "System32",
                "drivers",
                serviceName + ".sys");
        }
        else
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (path.StartsWith(
                    @"\SystemRoot\",
                    StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(
                    windowsRoot,
                    path[@"\SystemRoot\".Length..]);
            }
            else if (path.StartsWith(
                         @"System32\",
                         StringComparison.OrdinalIgnoreCase))
            {
                path = Path.Combine(windowsRoot, path);
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(windowsRoot, path);
            }
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static DriverMetadata ReadMetadata(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new DriverMetadata("", "", "");
            }

            var version = FileVersionInfo.GetVersionInfo(path);
            return new DriverMetadata(
                version.CompanyName?.Trim() ?? "",
                version.FileVersion?.Trim() ?? "",
                version.FileDescription?.Trim() ?? "");
        }
        catch
        {
            return new DriverMetadata("", "", "");
        }
    }

    private static FilterAssessment Assess(
        string serviceName,
        string group,
        bool running,
        bool isMicrosoft,
        IReadOnlyList<PoolTagSnapshot> poolTags)
    {
        var wcBytes = poolTags
            .Where(item => item.Tag.StartsWith(
                "WC",
                StringComparison.OrdinalIgnoreCase))
            .Aggregate(0UL, (sum, item) => sum + item.TotalBytes);
        var fmBytes = poolTags
            .Where(item => item.Tag.StartsWith(
                "FM",
                StringComparison.OrdinalIgnoreCase))
            .Aggregate(0UL, (sum, item) => sum + item.TotalBytes);

        if (string.Equals(
                serviceName,
                "wcifs",
                StringComparison.OrdinalIgnoreCase) &&
            running &&
            wcBytes > 0)
        {
            return new FilterAssessment(
                100,
                "强相关候选",
                $"wcifs 正在运行；同次 WC* Pool Tag 合计 {LagDiagnosticsEngine.FormatBytes(wcBytes)}，与容器/隔离文件链相关。");
        }

        if (string.Equals(
                serviceName,
                "fltmgr",
                StringComparison.OrdinalIgnoreCase) &&
            running)
        {
            return new FilterAssessment(
                90,
                "Filter Manager 基础设施",
                $"fltmgr 承载过滤链；同次 FM* Pool Tag 合计 {LagDiagnosticsEngine.FormatBytes(fmBytes)}，该证据需要结合具体过滤器实例继续归因。");
        }

        var score = running ? 30 : 0;
        if (running && !isMicrosoft)
        {
            score += 25;
        }
        if (group.Contains(
                "Activity Monitor",
                StringComparison.OrdinalIgnoreCase) ||
            group.Contains(
                "Anti-Virus",
                StringComparison.OrdinalIgnoreCase) ||
            group.Contains(
                "Virtualization",
                StringComparison.OrdinalIgnoreCase) ||
            group.Contains(
                "HSM",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        var likelihood = !running
            ? "已注册，当前未运行"
            : !isMicrosoft
                ? "运行中的第三方候选"
                : "运行中的系统候选";
        var evidence =
            $"{(running ? "驱动服务正在运行" : "驱动服务当前未运行")}；加载组 {group}.";
        return new FilterAssessment(score, likelihood, evidence);
    }

    private static bool IsServiceRunning(
        IntPtr manager,
        string serviceName)
    {
        var service = OpenService(
            manager,
            serviceName,
            ServiceQueryStatus);
        if (service == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var size = Marshal.SizeOf<ServiceStatusProcess>();
            return QueryServiceStatusEx(
                       service,
                       ScStatusProcessInfo,
                       out var status,
                       size,
                       out _) &&
                   status.CurrentState == ServiceRunning;
        }
        finally
        {
            _ = CloseServiceHandle(service);
        }
    }

    private static int ReadInt32(object? value)
    {
        return value switch
        {
            int number => number,
            uint number => checked((int)number),
            long number => checked((int)number),
            _ => -1
        };
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(
        string? machineName,
        string? databaseName,
        uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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

    [DllImport("advapi32.dll")]
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

    private sealed record DriverMetadata(
        string Company,
        string Version,
        string Description);

    private sealed record FilterAssessment(
        int Score,
        string Likelihood,
        string Evidence);

    private sealed record FilterCandidate(
        FileSystemFilterSnapshot Snapshot,
        int Score);
}
