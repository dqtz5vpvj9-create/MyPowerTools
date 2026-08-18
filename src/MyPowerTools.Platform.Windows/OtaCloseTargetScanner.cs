using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace MyPowerTools.Platform.Windows;

public sealed record OtaCloseTarget(string Id, string DisplayName);

public static class OtaCloseTargetScanner
{
    public const string ReopenPlanFileName = "reopen-plan.json";

    public static string DefaultInstallRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "MyPowerTools");
    }

    public static string DefaultStateRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "ota-state");
    }

    public static IReadOnlyList<OtaCloseTarget> Scan(string? installRoot = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return ScanWindows(installRoot);
    }

    public static void WriteReopenPlan(string stateRoot, IReadOnlyList<OtaCloseTarget> targets)
    {
        Directory.CreateDirectory(stateRoot);
        var payload = new OtaReopenPlanDocument(
            1,
            targets.Select(target => new OtaReopenPlanTarget(target.Id, target.DisplayName)).ToArray());
        var path = Path.Combine(stateRoot, ReopenPlanFileName);
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                payload,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<OtaCloseTarget> ScanWindows(string? installRoot)
    {
        var root = Path.GetFullPath(installRoot ?? DefaultInstallRoot())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = root + Path.DirectorySeparatorChar;
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentId = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (process.Id == currentId &&
                    name.Equals("MyPowerTools.Cli", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var exe = TryGetImagePath(process);
                var commandLine = TryGetCommandLine(process.Id) ?? "";
                if (!TryClassify(name, exe, commandLine, root, prefix, out var id, out var displayName))
                {
                    continue;
                }

                found.TryAdd(id, displayName);
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        AddRunningScheduledTasks(found);

        return found
            .Select(pair => new OtaCloseTarget(pair.Key, pair.Value))
            .OrderBy(target => target.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    internal static bool TryClassify(
        string processName,
        string executablePath,
        string commandLine,
        string installRoot,
        string installPrefix,
        out string id,
        out string displayName)
    {
        id = "";
        displayName = "";
        var exe = executablePath ?? "";
        var cmd = commandLine ?? "";
        var name = processName ?? "";

        if (name.Equals("adb", StringComparison.OrdinalIgnoreCase) ||
            exe.EndsWith("\\adb.exe", StringComparison.OrdinalIgnoreCase))
        {
            id = "adb";
            displayName = "Android Debug Bridge (adb)";
            return true;
        }

        var insideInstall = exe.StartsWith(installPrefix, StringComparison.OrdinalIgnoreCase) ||
            exe.Equals(installRoot, StringComparison.OrdinalIgnoreCase);
        var commandUsesInstall = cmd.IndexOf(installPrefix, StringComparison.OrdinalIgnoreCase) >= 0;

        if (name.Equals("MyPowerTools.Shell.Avalonia", StringComparison.OrdinalIgnoreCase) ||
            (insideInstall && exe.EndsWith("\\MyPowerTools.exe", StringComparison.OrdinalIgnoreCase)))
        {
            id = "shell";
            displayName = "MyPowerTools";
            return true;
        }

        if (name.Equals("MyPowerTools.Runner", StringComparison.OrdinalIgnoreCase))
        {
            id = "runner";
            displayName = "MyPowerTools Runner";
            return true;
        }

        if (name.Equals("MyPowerTools.ServiceManager", StringComparison.OrdinalIgnoreCase))
        {
            id = "service-manager";
            displayName = "MyPowerTools Service Manager";
            return true;
        }

        if (name.Equals("MyPowerTools.Cli", StringComparison.OrdinalIgnoreCase) && insideInstall)
        {
            id = "cli";
            displayName = "MyPowerTools CLI";
            return true;
        }

        if (name.Equals("MyPowerTools.ElevatedBroker", StringComparison.OrdinalIgnoreCase))
        {
            id = "broker";
            displayName = "MyPowerTools Elevated Broker";
            return true;
        }

        if (commandUsesInstall || insideInstall)
        {
            if (ContainsIgnoreCase(cmd, "ddns.ps1") || ContainsIgnoreCase(cmd, "\\ddns.service\\"))
            {
                id = "ddns";
                displayName = "MyPowerTools DDNS";
                return true;
            }

            if (ContainsIgnoreCase(cmd, "adb-forwarder") || ContainsIgnoreCase(exe, "\\adb-forwarder"))
            {
                id = "adb-forwarder";
                displayName = "ADB Forwarder";
                return true;
            }

            if (ContainsIgnoreCase(cmd, "smartbird") || ContainsIgnoreCase(cmd, "thermostat"))
            {
                id = "smartbird";
                displayName = "SmartBird 恒温器";
                return true;
            }

            if (ContainsIgnoreCase(cmd, "energy-server") || ContainsIgnoreCase(cmd, "EnergyServer"))
            {
                id = "energy";
                displayName = "电量服务";
                return true;
            }

            if (ContainsIgnoreCase(cmd, "doubao"))
            {
                id = "doubao";
                displayName = "豆包计算机使用";
                return true;
            }

            if (ContainsIgnoreCase(cmd, "screenease"))
            {
                id = "screenease";
                displayName = "ScreenEase";
                return true;
            }

            if (ContainsIgnoreCase(cmd, "remote-notifications") ||
                ContainsIgnoreCase(cmd, "\\notifyapp") ||
                ContainsIgnoreCase(exe, "\\remote-notifications"))
            {
                id = "notifications";
                displayName = "远程通知";
                return true;
            }
        }

        if (insideInstall)
        {
            id = "other:" + name;
            displayName = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(exe) : name;
            return !string.IsNullOrWhiteSpace(displayName);
        }

        if (commandUsesInstall)
        {
            id = "host:" + name;
            displayName = string.IsNullOrWhiteSpace(name) ? "占用安装目录的程序" : name;
            return true;
        }

        return false;
    }

    private static bool ContainsIgnoreCase(string value, string fragment)
    {
        return value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [SupportedOSPlatform("windows")]
    private static void AddRunningScheduledTasks(IDictionary<string, string> found)
    {
        TryAddRunningScheduledTask(found, "SmartBirdThermostat", "smartbird", "SmartBird 恒温器");
        TryAddRunningScheduledTask(found, "EnergyServer", "energy", "电量服务");
    }

    [SupportedOSPlatform("windows")]
    private static void TryAddRunningScheduledTask(
        IDictionary<string, string> found,
        string taskName,
        string id,
        string displayName)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null)
            {
                return;
            }

            var service = Activator.CreateInstance(type);
            if (service is null)
            {
                return;
            }

            InvokeCom(service, "Connect");
            var folder = InvokeCom(service, "GetFolder", "\\");
            if (folder is null)
            {
                return;
            }

            var task = InvokeCom(folder, "GetTask", taskName);
            if (task is null)
            {
                return;
            }

            var state = task.GetType().InvokeMember(
                "State",
                BindingFlags.GetProperty,
                null,
                task,
                null);
            // TASK_STATE_RUNNING = 4
            if (state is not null && Convert.ToInt32(state) == 4)
            {
                found.TryAdd(id, displayName);
            }
        }
        catch
        {
        }
    }

    private static object? InvokeCom(object target, string name, params object[] arguments)
    {
        return target.GetType().InvokeMember(
            name,
            BindingFlags.InvokeMethod,
            null,
            target,
            arguments.Length == 0 ? null : arguments);
    }

    [SupportedOSPlatform("windows")]
    private static string TryGetImagePath(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return path;
            }
        }
        catch
        {
        }

        var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            var size = 1024;
            var buffer = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : "";
        }
        catch
        {
            return "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? TryGetCommandLine(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0)
            {
                return null;
            }

            var pointerSize = IntPtr.Size;
            var processParametersOffset = pointerSize == 8 ? 0x20 : 0x10;
            var addrBuf = new byte[pointerSize];
            if (!ReadProcessMemory(handle, pbi.PebBaseAddress + processParametersOffset, addrBuf, pointerSize, out _))
            {
                return null;
            }

            var processParameters = pointerSize == 8
                ? new IntPtr(BitConverter.ToInt64(addrBuf, 0))
                : new IntPtr(BitConverter.ToInt32(addrBuf, 0));
            if (processParameters == IntPtr.Zero)
            {
                return null;
            }

            var commandLineOffset = pointerSize == 8 ? 0x70 : 0x40;
            var unicode = new byte[pointerSize == 8 ? 16 : 8];
            if (!ReadProcessMemory(handle, processParameters + commandLineOffset, unicode, unicode.Length, out _))
            {
                return null;
            }

            var length = BitConverter.ToUInt16(unicode, 0);
            var buffer = pointerSize == 8
                ? new IntPtr(BitConverter.ToInt64(unicode, 8))
                : new IntPtr(BitConverter.ToInt32(unicode, 4));
            if (length == 0 || buffer == IntPtr.Zero)
            {
                return "";
            }

            var chars = new byte[length];
            if (!ReadProcessMemory(handle, buffer, chars, length, out _))
            {
                return null;
            }

            return Encoding.Unicode.GetString(chars);
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessVmRead = 0x0010;

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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        int flags,
        StringBuilder exeName,
        ref int size);

    private sealed record OtaReopenPlanDocument(int SchemaVersion, OtaReopenPlanTarget[] Targets);

    private sealed record OtaReopenPlanTarget(string Id, string DisplayName);
}
