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
public sealed class WindowsPlatformPack : IPlatformPack
{
    private static readonly PlatformId CurrentPlatform = new("windows", PlatformId.Current().Architecture);
    private readonly Lazy<IHotkeyService> _hotkeys = new(CreateHotkeyService);

    public PlatformId Platform => CurrentPlatform;
    public PlatformTrayHost TrayHost => PlatformTrayHost.Runner;

    public ICapabilityRegistry Capabilities { get; } = new CapabilityRegistry(
    [
        new("tray", "user", true, "Windows tray", "Windows tray provider available."),
        new("hotkey.global", "user", true, "Win32 RegisterHotKey", "Global hotkey provider registers Win32 user hotkeys."),
        new("notification.desktop", "user", true, "Windows notification", "Desktop notification provider available."),
        new("clipboard.image", "sensitive", true, "Win32 Clipboard", "Native Win32 clipboard image and text provider available."),
        new("keyboard.shortcut", "user", true, "Windows SendInput", "Sends configurable shortcuts to the foreground application."),
        new("network.ssh", "user", true, "Windows OpenSSH", "The system OpenSSH client is used for SSH transfers."),
        new("web.surface", "user", true, "WebView2", "Process-isolated WebView2 surface provider available."),
        new("autostart.user", "user", true, "Startup folder", "User autostart provider available."),
        new("service.user", "user", true, "sc.exe / Task Scheduler", "User service diagnostics available."),
        new("service.system", "elevated", true, "Windows Service", "System service actions require Broker approval."),
        new("privilege.elevated", "elevated", true, "UAC broker", "Elevated actions require Broker approval."),
        new("display.profile", "user", true, "Windows display provider", "Display capability is routed to module native hosts."),
        new("network.portForwarding", "elevated", true, "netsh portproxy", "Port proxy writes require NetworkBroker approval."),
        new("ipc.local", "user", true, "Named Pipes", "Named Pipe IPC available."),
        new("secret.store", "sensitive", true, "Windows Credential Manager", "Per-user Credential Manager secret references available."),
        new("process.inspect", "user", true, "Process API", "Process inspection available."),
        new("adb.devices", "user", true, "adb CLI", "ADB is resolved through diagnostics.")
    ]);

    public IServiceManager Services { get; } = new WindowsServiceManager();
    public INetworkBroker Network { get; } = new WindowsNetworkBroker();
    public ISecretStore Secrets { get; } = new WindowsCredentialSecretStore();
    public IProcessService Processes { get; } = new WindowsProcessService();
    public IDisplayService Display { get; } = new WindowsDisplayService();
    public IAutostartService Autostart { get; } = new WindowsAutostartService();
    public ITrayService Tray { get; } = new WindowsTrayService();
    public INotificationService Notifications => (INotificationService)Tray;
    public IClipboardImageService ClipboardImages { get; } = new WindowsClipboardImageService();
    public IKeyboardShortcutService KeyboardShortcuts { get; } = new WindowsKeyboardShortcutService();
    public IHotkeyService Hotkeys => _hotkeys.Value;
    public IPrivilegeBroker Privileges { get; } = new BrokerRequiredPrivilegeBroker("UAC broker", "Elevated actions require MyPowerTools Broker approval and audit.");
    public ILocalIpc LocalIpc { get; } = new LocalIpcService(CurrentPlatform);

    private static IHotkeyService CreateHotkeyService()
    {
        return OperatingSystem.IsWindows()
            ? new WindowsGlobalHotkeyService()
            : new UnsupportedHotkeyService("Win32 RegisterHotKey", "Windows global hotkeys require user32.dll.");
    }
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
    private static readonly string NetshPath = ResolveTrustedSystemExecutable("netsh.exe");

    public async Task<IReadOnlyList<PortProxyRule>> ListPortProxyRulesAsync(CancellationToken cancellationToken)
    {
        var result = await RunNetshAsync(["interface", "portproxy", "show", "v4tov4"], cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"netsh portproxy list failed with code {result.ExitCode}: {Trim(result.Error + result.Output)}");
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
            FileName = NetshPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {NetshPath}.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            throw;
        }
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string ResolveTrustedSystemExecutable(string fileName)
    {
        var systemDirectory = Path.GetFullPath(Environment.SystemDirectory);
        var path = Path.GetFullPath(Path.Combine(systemDirectory, fileName));
        var expectedPrefix = systemDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"Trusted Windows executable is unavailable: {path}");
        }
        return path;
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
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
public sealed class WindowsDisplayService : INativeDisplayInventoryService
{
    private const int MonitorinfofPrimary = 0x00000001;
    private const uint McCapsBrightness = 0x00000002;
    private const uint McCapsColorTemperature = 0x00000008;

    public Task<IReadOnlyList<DisplaySnapshot>> ListDisplaysAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<IReadOnlyList<DisplaySnapshot>>([]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DisplaySnapshot>>(EnumerateDisplayTargets().Select(target => target.Snapshot).ToArray());
    }

    public Task<DisplayWriterStatus> GetWriterStatusAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new DisplayWriterStatus(false, "unsupported", "Windows display writer is available only on Windows."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var targets = EnumerateDisplayTargets();
            if (targets.Count == 0)
            {
                return Task.FromResult(new DisplayWriterStatus(false, "unsupported", "No Windows display targets were detected."));
            }

            var writable = 0;
            var probed = 0;
            var failures = new List<string>();
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var physicalMonitors = PhysicalMonitorSet.TryCreate(target.Handle, out var createError);
                if (physicalMonitors is null)
                {
                    failures.Add($"{target.Snapshot.Id}: {createError}");
                    continue;
                }

                foreach (var physical in physicalMonitors.Monitors)
                {
                    probed++;
                    if (TryGetCapabilities(physical.Handle, out var capabilities, out _, out var capabilityError) &&
                        (HasCapability(capabilities, McCapsBrightness) || HasCapability(capabilities, McCapsColorTemperature)))
                    {
                        writable++;
                    }
                    else if (capabilityError is not null)
                    {
                        failures.Add($"{DescribePhysical(target, physical)}: {capabilityError}");
                    }
                }
            }

            if (writable > 0)
            {
                return Task.FromResult(new DisplayWriterStatus(true, "ready", $"Windows DDC/CI display writer found {writable} writable physical monitor(s) from {probed} probed monitor(s)."));
            }

            var detail = failures.Count == 0
                ? "No physical monitor exposed brightness or color-temperature capability through DDC/CI."
                : string.Join("; ", failures.Take(4));
            return Task.FromResult(new DisplayWriterStatus(false, "unsupported", detail));
        }
        catch (DllNotFoundException ex)
        {
            return Task.FromResult(new DisplayWriterStatus(false, "native-host-required", $"Dxva2.dll is unavailable: {ex.Message}"));
        }
        catch (EntryPointNotFoundException ex)
        {
            return Task.FromResult(new DisplayWriterStatus(false, "native-host-required", $"Windows monitor configuration entry point is unavailable: {ex.Message}"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return Task.FromResult(new DisplayWriterStatus(false, "degraded", ex.Message));
        }
    }

    public Task<BrokerOperationResult> ApplyProfileAsync(DisplayProfileIntent intent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateIntent(intent);
        if (validation is not null)
        {
            return Task.FromResult(validation);
        }

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new BrokerOperationResult(false, "unsupported", "Windows display writer is available only on Windows."));
        }

        try
        {
            var targets = SelectTargets(intent);
            if (targets.Count == 0)
            {
                return Task.FromResult(new BrokerOperationResult(false, "unsupported", $"Display target '{intent.DisplayId}' was not found."));
            }

            var results = new List<DisplayWriteResult>();
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var physicalMonitors = PhysicalMonitorSet.TryCreate(target.Handle, out var createError);
                if (physicalMonitors is null)
                {
                    results.Add(DisplayWriteResult.Failed(target.Snapshot.Id, createError));
                    continue;
                }

                foreach (var physical in physicalMonitors.Monitors)
                {
                    results.Add(ApplyToPhysicalMonitor(target, physical, intent));
                }
            }

            return Task.FromResult(SummarizeWriteResults(results));
        }
        catch (DllNotFoundException ex)
        {
            return Task.FromResult(new BrokerOperationResult(false, "native-host-required", $"Dxva2.dll is unavailable: {ex.Message}"));
        }
        catch (EntryPointNotFoundException ex)
        {
            return Task.FromResult(new BrokerOperationResult(false, "native-host-required", $"Windows monitor configuration entry point is unavailable: {ex.Message}"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return Task.FromResult(new BrokerOperationResult(false, "degraded", ex.Message));
        }
    }

    private static BrokerOperationResult? ValidateIntent(DisplayProfileIntent intent)
    {
        if (string.IsNullOrWhiteSpace(intent.ProfileId))
        {
            return new BrokerOperationResult(false, "validation-failed", "profileId is required.");
        }

        if (intent.Brightness is null && intent.ColorTemperature is null)
        {
            return new BrokerOperationResult(false, "validation-failed", "At least one display setting is required.");
        }

        if (intent.Brightness is < 0 or > 100)
        {
            return new BrokerOperationResult(false, "validation-failed", "brightness must be between 0 and 100.");
        }

        if (intent.ColorTemperature is < 1000 or > 11500)
        {
            return new BrokerOperationResult(false, "validation-failed", "colorTemperature must be between 1000 and 11500.");
        }

        return null;
    }

    private static IReadOnlyList<LogicalDisplayTarget> SelectTargets(DisplayProfileIntent intent)
    {
        var targets = EnumerateDisplayTargets();
        if (string.IsNullOrWhiteSpace(intent.DisplayId) || string.Equals(intent.DisplayId, "all", StringComparison.OrdinalIgnoreCase))
        {
            return targets;
        }

        return targets
            .Where(target => string.Equals(target.Snapshot.Id, intent.DisplayId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<LogicalDisplayTarget> EnumerateDisplayTargets()
    {
        var targets = new List<LogicalDisplayTarget>();
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
            var snapshot = new DisplaySnapshot(
                string.IsNullOrWhiteSpace(info.DeviceName) ? $"monitor-{targets.Count + 1}" : info.DeviceName,
                primary ? "Primary display" : $"Display {targets.Count + 1}",
                "connected",
                width,
                height,
                0,
                width >= height ? "landscape" : "portrait",
                primary,
                $"Bounds {info.Monitor.Left},{info.Monitor.Top} {width}x{height}");
            targets.Add(new LogicalDisplayTarget(monitor, snapshot));
            return true;
        };

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return targets;
    }

    private static DisplayWriteResult ApplyToPhysicalMonitor(LogicalDisplayTarget target, PhysicalMonitor physical, DisplayProfileIntent intent)
    {
        var operations = new List<string>();
        var failures = new List<string>();
        if (!TryGetCapabilities(physical.Handle, out var capabilities, out var colorTemperatures, out var capabilityError))
        {
            return DisplayWriteResult.Failed(DescribePhysical(target, physical), capabilityError ?? "The monitor did not report DDC/CI capabilities.");
        }

        if (intent.Brightness is { } brightness)
        {
            if (!HasCapability(capabilities, McCapsBrightness))
            {
                failures.Add("brightness unsupported");
            }
            else if (TrySetBrightness(physical.Handle, brightness, out var brightnessMessage))
            {
                operations.Add(brightnessMessage);
            }
            else
            {
                failures.Add(brightnessMessage);
            }
        }

        if (intent.ColorTemperature is { } colorTemperature)
        {
            if (!HasCapability(capabilities, McCapsColorTemperature))
            {
                failures.Add("color temperature unsupported");
            }
            else if (!TryMapColorTemperature(colorTemperature, colorTemperatures, out var mappedTemperature, out var colorTemperatureFlag, out var mapMessage))
            {
                failures.Add(mapMessage);
            }
            else if (TrySetColorTemperature(physical.Handle, mappedTemperature, colorTemperatureFlag, out var temperatureMessage))
            {
                operations.Add(mapMessage == temperatureMessage ? temperatureMessage : $"{mapMessage}; {temperatureMessage}");
            }
            else
            {
                failures.Add(temperatureMessage);
            }
        }

        if (failures.Count > 0)
        {
            return operations.Count > 0
                ? DisplayWriteResult.Partial(DescribePhysical(target, physical), $"{string.Join(", ", operations)}; {string.Join(", ", failures)}")
                : DisplayWriteResult.Failed(DescribePhysical(target, physical), string.Join(", ", failures));
        }

        return DisplayWriteResult.Succeeded(DescribePhysical(target, physical), string.Join(", ", operations));
    }

    private static BrokerOperationResult SummarizeWriteResults(IReadOnlyList<DisplayWriteResult> results)
    {
        if (results.Count == 0)
        {
            return new BrokerOperationResult(false, "unsupported", "No physical monitor handles were available through Windows DDC/CI.");
        }

        var successes = results.Count(result => result.State == "success");
        var partials = results.Count(result => result.State == "partial");
        var failures = results.Count(result => result.State == "failed");
        var detail = string.Join("; ", results.Select(result => $"{result.Target}: {result.Message}").Take(6));
        if (successes == results.Count)
        {
            return new BrokerOperationResult(true, "success", $"Applied display profile through Windows DDC/CI on {successes} physical monitor(s). {detail}");
        }

        if (successes + partials > 0)
        {
            return new BrokerOperationResult(false, "partial", $"Applied display profile on {successes + partials} physical monitor(s); {failures} monitor(s) reported full failure and {partials} monitor(s) reported partial limitations. {detail}");
        }

        return new BrokerOperationResult(false, "unsupported", $"Windows DDC/CI writer could not apply this profile to any physical monitor. {detail}");
    }

    private static bool TryGetCapabilities(IntPtr physicalMonitor, out uint capabilities, out uint colorTemperatures, out string? error)
    {
        capabilities = 0;
        colorTemperatures = 0;
        if (GetMonitorCapabilities(physicalMonitor, out capabilities, out colorTemperatures))
        {
            error = null;
            return true;
        }

        error = $"GetMonitorCapabilities failed: {FormatLastError()}";
        return false;
    }

    private static bool TrySetBrightness(IntPtr physicalMonitor, int percent, out string message)
    {
        if (!GetMonitorBrightness(physicalMonitor, out var minimum, out _, out var maximum))
        {
            message = $"GetMonitorBrightness failed: {FormatLastError()}";
            return false;
        }

        if (maximum < minimum)
        {
            message = $"brightness range is invalid ({minimum}-{maximum})";
            return false;
        }

        var target = minimum + (uint)Math.Round((maximum - minimum) * (Math.Clamp(percent, 0, 100) / 100d));
        if (!SetMonitorBrightness(physicalMonitor, target))
        {
            message = $"SetMonitorBrightness({target}) failed: {FormatLastError()}";
            return false;
        }

        message = $"brightness {percent}% ({target}/{minimum}-{maximum})";
        return true;
    }

    private static bool TrySetColorTemperature(IntPtr physicalMonitor, McColorTemperature temperature, uint flag, out string message)
    {
        if (!SetMonitorColorTemperature(physicalMonitor, temperature))
        {
            message = $"SetMonitorColorTemperature({ColorTemperatureKelvin(flag)}K) failed: {FormatLastError()}";
            return false;
        }

        message = $"color temperature {ColorTemperatureKelvin(flag)}K";
        return true;
    }

    private static bool TryMapColorTemperature(
        int requestedKelvin,
        uint supportedFlags,
        out McColorTemperature temperature,
        out uint flag,
        out string message)
    {
        var supported = ColorTemperatureOptions
            .Where(option => HasCapability(supportedFlags, option.Flag))
            .OrderBy(option => Math.Abs(option.Kelvin - requestedKelvin))
            .ToArray();
        if (supported.Length == 0)
        {
            temperature = McColorTemperature.Unknown;
            flag = 0;
            message = "color temperature unsupported";
            return false;
        }

        var selected = supported[0];
        temperature = selected.Temperature;
        flag = selected.Flag;
        message = selected.Kelvin == requestedKelvin
            ? $"color temperature {selected.Kelvin}K"
            : $"color temperature mapped from {requestedKelvin}K to supported {selected.Kelvin}K";
        return true;
    }

    private static bool HasCapability(uint value, uint flag) => (value & flag) == flag;

    private static int ColorTemperatureKelvin(uint flag)
    {
        return ColorTemperatureOptions.First(option => option.Flag == flag).Kelvin;
    }

    private static string DescribePhysical(LogicalDisplayTarget target, PhysicalMonitor physical)
    {
        return string.IsNullOrWhiteSpace(physical.Description)
            ? target.Snapshot.Id
            : $"{target.Snapshot.Id}/{physical.Description}";
    }

    private static string FormatLastError()
    {
        var error = Marshal.GetLastWin32Error();
        return error == 0 ? "unknown Win32 error" : new Win32Exception(error).Message;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    private sealed record LogicalDisplayTarget(IntPtr Handle, DisplaySnapshot Snapshot);

    private sealed record ColorTemperatureOption(int Kelvin, uint Flag, McColorTemperature Temperature);

    private sealed record DisplayWriteResult(string Target, string State, string Message)
    {
        public static DisplayWriteResult Succeeded(string target, string message) => new(target, "success", message);
        public static DisplayWriteResult Partial(string target, string message) => new(target, "partial", message);
        public static DisplayWriteResult Failed(string target, string message) => new(target, "failed", message);
    }

    private sealed class PhysicalMonitorSet : IDisposable
    {
        private PhysicalMonitorSet(PhysicalMonitor[] monitors)
        {
            Monitors = monitors;
        }

        public PhysicalMonitor[] Monitors { get; }

        public static PhysicalMonitorSet? TryCreate(IntPtr logicalMonitor, out string error)
        {
            error = "";
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(logicalMonitor, out var count) || count == 0)
            {
                error = $"GetNumberOfPhysicalMonitorsFromHMONITOR failed: {FormatLastError()}";
                return null;
            }

            var monitors = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(logicalMonitor, count, monitors))
            {
                error = $"GetPhysicalMonitorsFromHMONITOR failed: {FormatLastError()}";
                return null;
            }

            return new PhysicalMonitorSet(monitors);
        }

        public void Dispose()
        {
            if (Monitors.Length > 0)
            {
                DestroyPhysicalMonitors((uint)Monitors.Length, Monitors);
            }
        }
    }

    private static readonly IReadOnlyList<ColorTemperatureOption> ColorTemperatureOptions =
    [
        new(4000, 0x00000001, McColorTemperature.K4000),
        new(5000, 0x00000002, McColorTemperature.K5000),
        new(6500, 0x00000004, McColorTemperature.K6500),
        new(7500, 0x00000008, McColorTemperature.K7500),
        new(8200, 0x00000010, McColorTemperature.K8200),
        new(9300, 0x00000020, McColorTemperature.K9300),
        new(10000, 0x00000040, McColorTemperature.K10000),
        new(11500, 0x00000080, McColorTemperature.K11500)
    ];

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("Dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint numberOfPhysicalMonitors);

    [DllImport("Dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint physicalMonitorArraySize, [Out] PhysicalMonitor[] physicalMonitorArray);

    [DllImport("Dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint physicalMonitorArraySize, [In] PhysicalMonitor[] physicalMonitorArray);

    [DllImport("Dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorCapabilities(IntPtr monitor, out uint monitorCapabilities, out uint supportedColorTemperatures);

    [DllImport("Dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimumBrightness, out uint currentBrightness, out uint maximumBrightness);

    [DllImport("Dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint newBrightness);

    [DllImport("Dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorColorTemperature(IntPtr monitor, McColorTemperature colorTemperature);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    private enum McColorTemperature
    {
        Unknown = 0,
        K4000 = 1,
        K5000 = 2,
        K6500 = 3,
        K7500 = 4,
        K8200 = 5,
        K9300 = 6,
        K10000 = 7,
        K11500 = 8
    }
}
