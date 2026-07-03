using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformPack
{
    public ICapabilityRegistry Capabilities { get; } = new CapabilityRegistry(
    [
        new("tray", "user", true, "Windows tray", "Windows tray provider available."),
        new("hotkey.global", "user", true, "Win32 hotkey", "Global hotkey provider available."),
        new("notification.desktop", "user", true, "Windows notification", "Desktop notification provider available."),
        new("autostart.user", "user", true, "Startup folder", "User autostart provider available."),
        new("service.user", "user", true, "sc.exe / Task Scheduler", "User service diagnostics available."),
        new("service.system", "elevated", true, "Windows Service", "System service actions require Broker approval."),
        new("privilege.elevated", "elevated", true, "UAC broker", "Elevated actions require Broker approval."),
        new("display.profile", "user", true, "Windows display provider", "Display capability is routed to module native hosts."),
        new("network.portForwarding", "elevated", true, "netsh portproxy", "Port proxy writes require NetworkBroker approval."),
        new("ipc.local", "user", true, "Named Pipes", "Named Pipe IPC available."),
        new("secret.store", "sensitive", true, "Windows Credential Manager", "Per-user Credential Manager secret references available."),
        new("process.inspect", "user", true, "Process API", "Process inspection available."),
        new("adb.device", "user", true, "adb CLI", "ADB is resolved through diagnostics.")
    ]);

    public IServiceManager Services { get; } = new WindowsServiceManager();
    public INetworkBroker Network { get; } = new WindowsNetworkBroker();
    public ISecretStore Secrets { get; } = new WindowsCredentialSecretStore();
    public IProcessService Processes { get; } = new WindowsProcessService();
    public IDisplayService Display { get; } = new WindowsDisplayService();
    public IAutostartService Autostart { get; } = new WindowsAutostartService();
    public ITrayService Tray { get; } = new WindowsTrayService();
}

public sealed class WindowsServiceManager : IServiceManager
{
    public Task<ServiceStatus> GetStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("query");
        psi.ArgumentList.Add(serviceName);

        return RunAsync(psi, cancellationToken).ContinueWith(task =>
        {
            var output = task.Result.Output + task.Result.Error;
            var state = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)
                ? "running"
                : output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)
                    ? "stopped"
                    : task.Result.ExitCode == 0 ? "unknown" : "missing";
            return new ServiceStatus(serviceName, state, output.Trim());
        }, cancellationToken);
    }

    public Task<BrokerOperationResult> StartAsync(string serviceName, CancellationToken cancellationToken)
    {
        return Task.FromResult(new BrokerOperationResult(false, "permission-required", $"Start service '{serviceName}' must be executed through ServiceBroker."));
    }

    public Task<BrokerOperationResult> StopAsync(string serviceName, CancellationToken cancellationToken)
    {
        return Task.FromResult(new BrokerOperationResult(false, "permission-required", $"Stop service '{serviceName}' must be executed through ServiceBroker."));
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(ProcessStartInfo psi, CancellationToken cancellationToken)
    {
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask, await errorTask);
    }
}

public sealed class WindowsNetworkBroker : INetworkBroker
{
    private static readonly Regex RuleRegex = new(@"^\s*(?<listen>\S+)\s+(?<listenPort>\d+)\s+(?<connect>\S+)\s+(?<connectPort>\d+)\s*$", RegexOptions.Compiled);

    public async Task<IReadOnlyList<PortProxyRule>> ListPortProxyRulesAsync(CancellationToken cancellationToken)
    {
        var result = await RunNetshAsync(["interface", "portproxy", "show", "v4tov4"], cancellationToken);
        if (result.ExitCode != 0)
        {
            return [];
        }

        return result.Output.Split(Environment.NewLine)
            .Select(line => RuleRegex.Match(line))
            .Where(match => match.Success)
            .Select(match => new PortProxyRule(
                match.Groups["listen"].Value,
                int.Parse(match.Groups["listenPort"].Value),
                match.Groups["connect"].Value,
                int.Parse(match.Groups["connectPort"].Value)))
            .ToArray();
    }

    public async Task<BrokerOperationResult> ApplyPortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
    {
        var validation = Validate(rule);
        if (validation is not null)
        {
            return validation;
        }

        if (!HasAdministratorToken())
        {
            return PermissionRequired(rule, "Applying");
        }

        var result = await RunNetshAsync(
            [
                "interface",
                "portproxy",
                "add",
                "v4tov4",
                $"listenaddress={rule.ListenAddress}",
                $"listenport={rule.ListenPort}",
                $"connectaddress={rule.ConnectAddress}",
                $"connectport={rule.ConnectPort}"
            ],
            cancellationToken);

        return result.ExitCode == 0
            ? new BrokerOperationResult(true, "success", $"Applied portproxy rule {Scope(rule)}.")
            : new BrokerOperationResult(false, "failed", $"netsh add failed with code {result.ExitCode}: {Trim(result.Error + result.Output)}");
    }

    public async Task<BrokerOperationResult> RemovePortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
    {
        var validation = Validate(rule, requireConnect: false);
        if (validation is not null)
        {
            return validation;
        }

        if (!HasAdministratorToken())
        {
            return PermissionRequired(rule, "Removing");
        }

        var result = await RunNetshAsync(
            [
                "interface",
                "portproxy",
                "delete",
                "v4tov4",
                $"listenaddress={rule.ListenAddress}",
                $"listenport={rule.ListenPort}"
            ],
            cancellationToken);

        return result.ExitCode == 0
            ? new BrokerOperationResult(true, "success", $"Removed portproxy rule {rule.ListenAddress}:{rule.ListenPort}.")
            : new BrokerOperationResult(false, "failed", $"netsh delete failed with code {result.ExitCode}: {Trim(result.Error + result.Output)}");
    }

    private static BrokerOperationResult? Validate(PortProxyRule rule, bool requireConnect = true)
    {
        if (string.IsNullOrWhiteSpace(rule.ListenAddress))
        {
            return new BrokerOperationResult(false, "validation-failed", "listenAddress is required.");
        }

        if (requireConnect && string.IsNullOrWhiteSpace(rule.ConnectAddress))
        {
            return new BrokerOperationResult(false, "validation-failed", "connectAddress is required.");
        }

        if (!IsPort(rule.ListenPort))
        {
            return new BrokerOperationResult(false, "validation-failed", "listenPort must be between 1 and 65535.");
        }

        if (requireConnect && !IsPort(rule.ConnectPort))
        {
            return new BrokerOperationResult(false, "validation-failed", "connectPort must be between 1 and 65535.");
        }

        return null;
    }

    private static bool IsPort(int port) => port is >= 1 and <= 65535;

    private static BrokerOperationResult PermissionRequired(PortProxyRule rule, string verb)
    {
        return new BrokerOperationResult(
            false,
            "permission-required",
            $"{verb} {rule.ListenAddress}:{rule.ListenPort} requires an elevated PrivilegedBroker process with NetworkBroker audit.");
    }

    private static bool HasAdministratorToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunNetshAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var psi = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start netsh.exe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string Scope(PortProxyRule rule)
    {
        return $"{rule.ListenAddress}:{rule.ListenPort}->{rule.ConnectAddress}:{rule.ConnectPort}";
    }

    private static string Trim(string value)
    {
        value = value.Trim();
        return value.Length <= 500 ? value : value[..500] + "...";
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxCredentialBlobBytes = 5120;
    private const string TargetPrefix = "MyPowerTools/";

    public Task<SecretReference> SaveAsync(string moduleId, string name, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(secret);

        var reference = SecretReference.Create(moduleId, name);
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > MaxCredentialBlobBytes)
        {
            throw new InvalidOperationException($"Credential Manager generic credentials are limited to {MaxCredentialBlobBytes} bytes.");
        }

        GCHandle? pinned = null;
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = CreateTargetName(moduleId, name),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = bytes.Length == 0 ? IntPtr.Zero : Pin(bytes, out pinned),
                Persist = CredentialPersistLocalMachine,
                UserName = moduleId,
                Comment = "MyPowerTools module secret reference"
            };

            if (!CredWrite(ref credential, 0))
            {
                ThrowCredentialException("CredWrite");
            }
        }
        finally
        {
            if (pinned is { IsAllocated: true })
            {
                pinned.Value.Free();
            }
        }

        return Task.FromResult(reference);
    }

    public Task<string?> ReadAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!reference.TryGetParts(out var moduleId, out var name))
        {
            return Task.FromResult<string?>(null);
        }

        if (!CredRead(CreateTargetName(moduleId, name), CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<string?>(null);
            }

            throw new Win32Exception(error, $"CredRead failed for secret reference {reference.Uri}.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0)
            {
                return Task.FromResult<string?>("");
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!reference.TryGetParts(out var moduleId, out var name))
        {
            return Task.CompletedTask;
        }

        if (!CredDelete(CreateTargetName(moduleId, name), CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, $"CredDelete failed for secret reference {reference.Uri}.");
            }
        }

        return Task.CompletedTask;
    }

    private static string CreateTargetName(string moduleId, string name)
    {
        SecretReference.ValidatePart(moduleId, nameof(moduleId));
        SecretReference.ValidatePart(name, nameof(name));
        return $"{TargetPrefix}{moduleId}/{name}";
    }

    private static IntPtr Pin(byte[] bytes, out GCHandle? pinned)
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        pinned = handle;
        return handle.AddrOfPinnedObject();
    }

    private static void ThrowCredentialException(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        throw new Win32Exception(error, $"{operation} failed for MyPowerTools Credential Manager secret.");
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPointer);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }
}

public sealed class WindowsProcessService : IProcessService
{
    public Task<IReadOnlyList<ProcessSnapshot>> ListAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProcessSnapshot> result = Process.GetProcesses()
            .OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(process =>
            {
                try
                {
                    return new ProcessSnapshot(process.Id, process.ProcessName, process.HasExited ? "exited" : "running", process.MainWindowTitle);
                }
                catch
                {
                    return new ProcessSnapshot(process.Id, process.ProcessName, "unknown", "");
                }
            })
            .ToArray();

        return Task.FromResult(result);
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsAutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public Task<ServiceStatus> GetAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateId(id);
        if (validation is not null)
        {
            return Task.FromResult(new ServiceStatus(id, "invalid", validation.Message));
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = key?.GetValue(id) as string;
        return Task.FromResult(string.IsNullOrWhiteSpace(command)
            ? new ServiceStatus(id, "disabled", $"No HKCU Run entry exists for '{id}'.")
            : new ServiceStatus(id, "enabled", command));
    }

    public Task<BrokerOperationResult> EnableAsync(string id, string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateId(id) ?? ValidateCommand(command);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key.SetValue(id, command, RegistryValueKind.String);
        return Task.FromResult(new BrokerOperationResult(true, "enabled", $"Enabled HKCU Run autostart entry '{id}'."));
    }

    public Task<BrokerOperationResult> DisableAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateId(id);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(id) is null)
        {
            return Task.FromResult(new BrokerOperationResult(true, "disabled", $"HKCU Run autostart entry '{id}' was already disabled."));
        }

        key.DeleteValue(id, throwOnMissingValue: false);
        return Task.FromResult(new BrokerOperationResult(true, "disabled", $"Disabled HKCU Run autostart entry '{id}'."));
    }

    private static BrokerOperationResult? ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return new BrokerOperationResult(false, "validation-failed", "Autostart id is required.");
        }

        if (id.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            return new BrokerOperationResult(false, "validation-failed", "Autostart id contains characters that are invalid for a Windows Run value name.");
        }

        return null;
    }

    private static BrokerOperationResult? ValidateCommand(string command)
    {
        return string.IsNullOrWhiteSpace(command)
            ? new BrokerOperationResult(false, "validation-failed", "Autostart command is required.")
            : null;
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsDisplayService : IDisplayService
{
    private const int MonitorinfofPrimary = 0x00000001;

    public Task<IReadOnlyList<DisplaySnapshot>> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<DisplaySnapshot>>([]);
        }

        var displays = new List<DisplaySnapshot>();
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx();
            info.Size = Marshal.SizeOf<MonitorInfoEx>();
            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var width = Math.Abs(info.Monitor.Right - info.Monitor.Left);
            var height = Math.Abs(info.Monitor.Bottom - info.Monitor.Top);
            var primary = (info.Flags & MonitorinfofPrimary) != 0;
            displays.Add(new DisplaySnapshot(
                string.IsNullOrWhiteSpace(info.DeviceName) ? $"monitor-{displays.Count + 1}" : info.DeviceName,
                primary ? "Primary display" : $"Display {displays.Count + 1}",
                "connected",
                width,
                height,
                0,
                width >= height ? "landscape" : "portrait",
                primary,
                $"Bounds {info.Monitor.Left},{info.Monitor.Top} {width}x{height}"));
            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return Task.FromResult<IReadOnlyList<DisplaySnapshot>>(displays);
    }

    public Task<BrokerOperationResult> ApplyProfileAsync(DisplayProfileIntent intent, CancellationToken cancellationToken)
    {
        var message = "Display brightness and color temperature writes require the ScreenEase native host. The profile intent was validated by the module and can be applied once the native host is available.";
        return Task.FromResult(new BrokerOperationResult(false, "native-host-required", message));
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
