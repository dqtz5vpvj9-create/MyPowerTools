using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Input;
using AdbForwarder.MyPowerTools;
using AndroidTools.MyPowerTools;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using MyPowerTools.HostControl;
using MyPowerTools.Broker;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Linux;
using MyPowerTools.Platform.Mac;
using MyPowerTools.Platform.Windows;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;
using MyPowerTools.SampleModules.DotNet;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI;
using ScreenEase.MyPowerTools;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using HealthCheckSnapshot = MyPowerTools.Abstractions.HealthCheckSnapshot;
using HostProto = MyPowerTools.Protocol.HostControl.V1;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using ModuleStatusSnapshot = MyPowerTools.Abstractions.ModuleStatusSnapshot;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using MptOperationConstraints = MyPowerTools.Abstractions.MptOperationConstraints;
using MptRuntimeError = MyPowerTools.Abstractions.MptRuntimeError;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;
using SettingsSchemaDocument = MyPowerTools.Abstractions.SettingsSchemaDocument;
using SettingsSnapshotDocument = MyPowerTools.Abstractions.SettingsSnapshotDocument;
using SettingsValidationResult = MyPowerTools.Abstractions.SettingsValidationResult;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    [Fact]
    public void Broker_declared_command_returns_permission_required()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));

        var result = runtime.ExecuteCommand(new CommandRequest("broker-test", "adb-forwarder.portproxy.apply", new JsonObject()));

        Assert.False(result.Success);
        Assert.Equal("permission-required", result.State);
        Assert.Equal("MPT_PERMISSION_REQUIRED", result.Error!.Code);
    }

    [Fact]
    public void Broker_audit_redacts_sensitive_values()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var broker = new PrivilegedBroker(audit);

        broker.Evaluate("network.apply", "elevated", "token=abc123", "adb-forwarder", "listen=5556");

        var entry = audit.ReadAll().Single();
        Assert.Contains("token=****", entry.Reason);
    }

    [Fact]
    public void PrivilegedBroker_requires_broker_for_planned_privilege_levels()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-privileged-levels-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var broker = new PrivilegedBroker(audit);
        var brokered = new[] { "elevated", "service", "serviceUser", "serviceSystem", "sensitive", "broker" };

        foreach (var level in brokered)
        {
            var decision = broker.Evaluate($"test.{level}", level, $"reason for {level}", "test-module", $"scope:{level}");
            Assert.True(decision.RequiresBroker);
            Assert.Equal("MPT_PERMISSION_REQUIRED", decision.ErrorCode);
        }

        var userDecision = broker.Evaluate("test.user", "user", "ordinary user action", "test-module", "scope:user");
        var entries = audit.ReadAll();

        Assert.False(userDecision.RequiresBroker);
        Assert.Equal("", userDecision.ErrorCode);
        Assert.All(brokered, level => Assert.Contains(entries, entry => entry.PermissionLevel == level && entry.RequiresBroker));
        Assert.Contains(entries, entry => entry.PermissionLevel == "user" && !entry.RequiresBroker);
    }

    [Fact]
    public async Task ServiceBroker_restart_audits_service_user_level()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-service-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var services = new RecordingServiceManager();
        var broker = new ServiceBroker(services, audit);

        var result = await broker.RestartAsync(
            "smartbird-thermostat",
            "smartbird-user-service",
            "restart after token=abc123",
            CancellationToken.None);
        var entries = audit.ReadAll();
        var auditText = File.ReadAllText(path);

        Assert.True(result.Success);
        Assert.Equal(["stop:smartbird-user-service", "start:smartbird-user-service"], services.Operations);
        Assert.All(entries, entry => Assert.Equal("serviceUser", entry.PermissionLevel));
        Assert.Contains(entries, entry => entry.ActionId == "service.restart" && entry.Result == "requested" && entry.Rollback.Contains("start smartbird-user-service", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.ActionId == "service.restart" && entry.Result == "started");
        Assert.Contains("token=****", auditText);
        Assert.DoesNotContain("abc123", auditText);
    }

    [Fact]
    public async Task SecretBroker_stores_reads_deletes_and_redacts_audit()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-secret-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var broker = new SecretBroker(new InMemorySecretStore(), audit);

        var reference = await broker.SaveAsync("sample.dotnet", "api-token", "super-secret-value", "save token=abc123", CancellationToken.None);
        var value = await broker.ReadAsync("sample.dotnet", reference, "read password=hunter2", CancellationToken.None);
        await broker.DeleteAsync("sample.dotnet", reference, "delete secret=gone", CancellationToken.None);
        var missing = await broker.ReadAsync("sample.dotnet", reference, "read after delete", CancellationToken.None);

        Assert.Equal("secret://sample.dotnet/api-token", reference.Uri);
        Assert.Equal("super-secret-value", value);
        Assert.Null(missing);

        var auditText = File.ReadAllText(path);
        Assert.Contains("secret.save", auditText);
        Assert.Contains("secret.read", auditText);
        Assert.Contains("secret.delete", auditText);
        Assert.Contains("token=****", auditText);
        Assert.Contains("password=****", auditText);
        Assert.Contains("secret=****", auditText);
        Assert.DoesNotContain("super-secret-value", auditText);
        Assert.DoesNotContain("abc123", auditText);
        Assert.DoesNotContain("hunter2", auditText);
    }

    [Fact]
    public async Task InMemorySecretStore_rejects_unsafe_secret_references()
    {
        var store = new InMemorySecretStore();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync("sample.dotnet", "../api-token", "value", CancellationToken.None));

        var invalidRead = await store.ReadAsync(new SecretReference("secret://sample.dotnet/../api-token"), CancellationToken.None);
        Assert.Null(invalidRead);
    }

    [Fact]
    public async Task AutostartBroker_enable_disable_status_audits_user_actions()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-autostart-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var autostart = new RecordingAutostartService();
        var broker = new AutostartBroker(autostart, audit);

        var before = await broker.GetAsync("runner", "MyPowerTools.Runner", "test status", CancellationToken.None);
        var enable = await broker.EnableAsync("runner", "MyPowerTools.Runner", "\"runner.exe\"", "test enable", CancellationToken.None);
        var afterEnable = await broker.GetAsync("runner", "MyPowerTools.Runner", "test status", CancellationToken.None);
        var disable = await broker.DisableAsync("runner", "MyPowerTools.Runner", "test disable", CancellationToken.None);
        var afterDisable = await broker.GetAsync("runner", "MyPowerTools.Runner", "test status", CancellationToken.None);

        Assert.Equal("disabled", before.State);
        Assert.True(enable.Success);
        Assert.Equal("enabled", afterEnable.State);
        Assert.Equal("\"runner.exe\"", afterEnable.Detail);
        Assert.True(disable.Success);
        Assert.Equal("disabled", afterDisable.State);
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "autostart.enable" && entry.Result == "enabled");
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "autostart.disable" && entry.Result == "disabled");
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "autostart.status" && entry.Result == "enabled");
    }

    [Fact]
    public async Task NetworkBroker_change_set_executes_remove_then_apply_and_audits()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-network-broker-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var network = new RecordingNetworkBroker();
        var broker = new NetworkBroker(network, audit);
        var oldRule = new PortProxyRule("0.0.0.0", 5555, "127.0.0.1", 7555);
        var newRule = new PortProxyRule("0.0.0.0", 5555, "127.0.0.1", 5555);

        var result = await broker.ApplyChangeSetAsync(
            "adb-forwarder",
            new PortProxyChangeSet([newRule], [oldRule]),
            "test broker changeset",
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(["remove:0.0.0.0:5555", "apply:0.0.0.0:5555->127.0.0.1:5555"], network.Operations);
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "network.portproxy.changeset" && entry.Result == "success");
    }

    [Fact]
    public async Task NetworkBroker_change_set_rolls_back_after_partial_failure()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-network-broker-rollback-test", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var audit = new AuditLog(path);
        var network = new RecordingNetworkBroker
        {
            FailNextApply = true
        };
        var broker = new NetworkBroker(network, audit);
        var oldRule = new PortProxyRule("0.0.0.0", 5555, "127.0.0.1", 7555);
        var newRule = new PortProxyRule("0.0.0.0", 5555, "127.0.0.1", 5555);

        var result = await broker.ApplyChangeSetAsync(
            "adb-forwarder",
            new PortProxyChangeSet([newRule], [oldRule]),
            "test broker rollback",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("rolled-back", result.State);
        Assert.Equal(
            ["remove:0.0.0.0:5555", "apply:0.0.0.0:5555->127.0.0.1:5555", "apply:0.0.0.0:5555->127.0.0.1:7555"],
            network.Operations);
        Assert.Contains(audit.ReadAll(), entry => entry.ActionId == "network.portproxy.rollback" && entry.Result == "rolled-back");
    }

    [Fact]
    public void Package_store_installs_and_repairs_hashes()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "mpt-package-store", Guid.NewGuid().ToString("N"));
        var store = new PackageStore(storeRoot, Path.Combine(Root, "schemas"));

        var result = store.Install(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        var repairIssues = store.Repair("sample-dotnet");

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.True(File.Exists(Path.Combine(result.TargetPath, "shared", "package.signature.json")));
        Assert.Empty(repairIssues);
    }

    [Fact]
    public void Package_trust_strict_policy_requires_signature_hook()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mpt-package-trust", Guid.NewGuid().ToString("N"));
        var packageCopy = Path.Combine(tempRoot, "sample-dotnet");
        CopyDirectory(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"), packageCopy);
        var trust = new PackageTrustVerifier();

        var local = trust.Verify(packageCopy, PackageTrustPolicy.LocalDevelopment);
        var strictBefore = trust.Verify(packageCopy, PackageTrustPolicy.StrictSigned);
        var signaturePath = trust.WriteLocalSignatureHook(packageCopy);
        var strictAfter = trust.Verify(packageCopy, PackageTrustPolicy.StrictSigned);

        Assert.True(local.IsTrusted, string.Join(Environment.NewLine, local.Issues.Select(issue => issue.Message)));
        Assert.Equal("local-trust", local.State);
        Assert.False(strictBefore.IsTrusted);
        Assert.Contains(strictBefore.Issues, issue => issue.Severity == "error" && issue.Message.Contains("signature hook", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(signaturePath));
        Assert.True(strictAfter.IsTrusted, string.Join(Environment.NewLine, strictAfter.Issues.Select(issue => issue.Message)));
        Assert.Equal("signature-hook", strictAfter.State);
    }

    [Fact]
    public void Package_trust_rejects_hash_manifest_path_escape()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mpt-package-trust-escape", Guid.NewGuid().ToString("N"));
        var packageCopy = Path.Combine(tempRoot, "sample-dotnet");
        CopyDirectory(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"), packageCopy);
        File.WriteAllText(
            Path.Combine(packageCopy, "package.json"),
            """
            {
              "schemaVersion": "1.0",
              "id": "sample-dotnet",
              "displayName": "Sample .NET Module",
              "version": "0.2.0",
              "modules": [
                "module.json"
              ],
              "hashes": "../outside.hashes.json"
            }
            """);

        var report = new PackageTrustVerifier().Verify(packageCopy, PackageTrustPolicy.StrictSigned);

        Assert.False(report.IsTrusted);
        Assert.Equal("invalid-trust-manifest", report.State);
        Assert.Contains(report.Issues, issue => issue.Message.Contains("escapes package directory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Package_store_uninstall_and_rollback_restores_installed_package()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "mpt-package-store-rollback", Guid.NewGuid().ToString("N"));
        var store = new PackageStore(storeRoot, Path.Combine(Root, "schemas"));

        var install = store.Install(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        Assert.True(install.Success, string.Join(Environment.NewLine, install.Issues.Select(issue => issue.Message)));

        var markerPath = Path.Combine(install.TargetPath, "shared", "rollback-marker.txt");
        File.WriteAllText(markerPath, "restore me");
        var uninstall = store.Uninstall("sample-dotnet");
        var rollback = store.Rollback("sample-dotnet");

        Assert.True(uninstall.Success, string.Join(Environment.NewLine, uninstall.Issues.Select(issue => issue.Message)));
        Assert.True(rollback.Success, string.Join(Environment.NewLine, rollback.Issues.Select(issue => issue.Message)));
        Assert.True(Directory.Exists(rollback.TargetPath));
        Assert.True(File.Exists(markerPath));
        Assert.Empty(store.Repair("sample-dotnet"));
    }

    [Fact]
    public void Platform_capability_registry_degrades_missing_capabilities()
    {
        var registry = new CapabilityRegistry([
            new CapabilityDescriptor("ipc.local", "user", true, "test", "ok")
        ]);

        var resolution = registry.ResolveForModule("module", [
            new CapabilityRequest("module", "ipc.local", true, "ipc"),
            new CapabilityRequest("module", "network.portForwarding", false, "optional")
        ]);

        Assert.Equal("degraded", resolution.State);
    }

    [Fact]
    public void Platform_capability_registry_marks_missing_required_capability_unsupported()
    {
        var registry = new CapabilityRegistry([
            new CapabilityDescriptor("ipc.local", "user", true, "test", "ok")
        ]);

        var resolution = registry.ResolveForModule("module", [
            new CapabilityRequest("module", "ipc.local", true, "ipc"),
            new CapabilityRequest("module", "network.portForwarding", true, "required")
        ]);

        Assert.Equal("unsupported", resolution.State);
        Assert.False(resolution.IsUsable);
        Assert.Contains(resolution.Messages, message => message.Contains("network.portForwarding", StringComparison.Ordinal));
    }

    [Fact]
    public void Local_ipc_service_selects_platform_native_endpoint_shape()
    {
        var windows = new LocalIpcService(new PlatformId("windows", "x64"), Path.Combine(Path.GetTempPath(), "ignored"));
        var linux = new LocalIpcService(new PlatformId("linux", "x64"), Path.Combine(Path.GetTempPath(), "mpt-ipc-test"));
        var mac = new LocalIpcService(new PlatformId("macos", "arm64"), Path.Combine(Path.GetTempPath(), "mpt-ipc-test"));

        Assert.Equal(IpcTransport.NamedPipe, windows.RunnerEndpoint.Transport);
        Assert.Equal("mypowertools.runner.hostcontrol", windows.RunnerEndpoint.Address);
        Assert.Equal(IpcTransport.NamedPipe, windows.CreateEndpoint("sample.grpc").Transport);
        Assert.Equal("mypowertools.sample.grpc", windows.CreateEndpoint("sample.grpc").Address);

        Assert.Equal(IpcTransport.UnixDomainSocket, linux.RunnerEndpoint.Transport);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "mpt-ipc-test"), linux.RunnerEndpoint.Address, StringComparison.Ordinal);
        Assert.Equal(IpcTransport.UnixDomainSocket, linux.CreateEndpoint("sample.grpc").Transport);
        Assert.EndsWith("mypowertools.sample.grpc.sock", linux.CreateEndpoint("sample.grpc").Address, StringComparison.Ordinal);
        Assert.Equal(IpcTransport.UnixDomainSocket, mac.RunnerEndpoint.Transport);
    }

    [Fact]
    public async Task Mac_and_linux_platform_packs_expose_truthful_degraded_services()
    {
        var mac = new MacPlatformPack();
        var linux = new LinuxPlatformPack();

        Assert.False(mac.Capabilities.Resolve("network.portForwarding").Supported);
        Assert.True(mac.Capabilities.Resolve("ipc.local").Supported);
        Assert.True(linux.Capabilities.Resolve("process.inspect").Supported);
        Assert.Equal(IpcTransport.UnixDomainSocket, mac.LocalIpc.RunnerEndpoint.Transport);
        Assert.Equal(IpcTransport.UnixDomainSocket, linux.LocalIpc.RunnerEndpoint.Transport);

        var macService = await mac.Services.GetStatusAsync("sample", CancellationToken.None);
        var linuxAutostart = await linux.Autostart.GetAsync("sample", CancellationToken.None);
        var linuxNetwork = await linux.Network.ApplyPortProxyRuleAsync(
            new PortProxyRule("127.0.0.1", 12345, "127.0.0.1", 12346),
            CancellationToken.None);
        var processes = await linux.Processes.ListAsync(CancellationToken.None);

        Assert.Equal("unsupported", macService.State);
        Assert.Contains("launchd", macService.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("unsupported", linuxAutostart.State);
        Assert.False(linuxNetwork.Success);
        Assert.Equal("unsupported", linuxNetwork.State);
        Assert.NotEmpty(processes);
    }

    [Fact]
    public async Task Platform_packs_expose_hotkey_and_privilege_surfaces()
    {
        var mac = new MacPlatformPack();
        var linux = new LinuxPlatformPack();

        var registration = new HotkeyRegistration("command-palette", "Ctrl+Alt+Space", "runner", "Open the command palette.");
        var windowsRegistration = new HotkeyRegistration("command-palette-test", "Ctrl+Shift+F24", "runner", "Test Windows global hotkey registration.");
        var request = new PrivilegeRequest(
            "network.portproxy.apply",
            "elevated",
            "Apply a Windows portproxy rule through the broker.",
            "adb-forwarder",
            "127.0.0.1:45678",
            "Add a v4tov4 portproxy rule.",
            "Remove the v4tov4 portproxy rule.");

        var macHotkey = await mac.Hotkeys.RegisterAsync(registration, CancellationToken.None);
        var macPrivilege = await mac.Privileges.EvaluateAsync(request, CancellationToken.None);
        var linuxHotkey = await linux.Hotkeys.RegisterAsync(registration, CancellationToken.None);
        var linuxPrivilege = await linux.Privileges.EvaluateAsync(request, CancellationToken.None);

        if (OperatingSystem.IsWindows())
        {
            var windows = new WindowsPlatformPack();
            await using var windowsHotkeys = windows.Hotkeys;
            var windowsHotkey = await windowsHotkeys.RegisterAsync(windowsRegistration, CancellationToken.None);
            var windowsPrivilege = await windows.Privileges.EvaluateAsync(request, CancellationToken.None);

            Assert.True(windows.Capabilities.Resolve("hotkey.global").Supported);
            Assert.True(windows.Capabilities.Resolve("privilege.elevated").Supported);
            Assert.NotEqual("unsupported", windowsHotkey.State);
            if (windowsHotkey.Success)
            {
                Assert.Equal("registered", windowsHotkey.State);
                var unregister = await windowsHotkeys.UnregisterAsync(windowsRegistration.Id, CancellationToken.None);
                Assert.True(unregister.Success);
                Assert.Equal("unregistered", unregister.State);
            }
            else
            {
                Assert.Contains(windowsHotkey.State, ["conflict", "failed"]);
            }

            Assert.True(windowsPrivilege.RequiresBroker);
            Assert.Equal("permission-required", windowsPrivilege.State);
            Assert.Contains("Broker", windowsPrivilege.Message, StringComparison.OrdinalIgnoreCase);
        }

        Assert.False(mac.Capabilities.Resolve("hotkey.global").Supported);
        Assert.False(mac.Capabilities.Resolve("privilege.elevated").Supported);
        Assert.Equal("unsupported", macHotkey.State);
        Assert.Equal("unsupported", macPrivilege.State);
        Assert.True(macPrivilege.RequiresBroker);

        Assert.False(linux.Capabilities.Resolve("hotkey.global").Supported);
        Assert.False(linux.Capabilities.Resolve("privilege.elevated").Supported);
        Assert.Equal("unsupported", linuxHotkey.State);
        Assert.Equal("unsupported", linuxPrivilege.State);
        Assert.True(linuxPrivilege.RequiresBroker);
    }

    [Fact]
    public void Windows_hotkey_gesture_parser_maps_command_palette_gesture()
    {
        Assert.True(WindowsHotkeyGesture.TryParse("Alt + Ctrl + Space", out var parsed, out var error), error);
        Assert.NotNull(parsed);
        Assert.Equal("Ctrl+Alt+Space", parsed!.NormalizedGesture);
        Assert.Equal(0x0001u | 0x0002u, parsed.Modifiers);
        Assert.Equal(0x20u, parsed.VirtualKey);

        Assert.True(WindowsHotkeyGesture.TryParse("Ctrl+Shift+F24", out var functionKey, out error), error);
        Assert.Equal("Ctrl+Shift+F24", functionKey!.NormalizedGesture);

        Assert.False(WindowsHotkeyGesture.TryParse("Space", out _, out error));
        Assert.Contains("modifier", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Runner_wires_windows_global_hotkey_to_command_palette_startup()
    {
        var runnerPath = Path.Combine(Root, "src", "MyPowerTools.Runner", "Program.cs");
        var hotkeySynchronizerPath = Path.Combine(Root, "src", "MyPowerTools.Runner", "RunnerHotkeySynchronizer.cs");
        var appPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "App.cs");
        var mainWindowPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var startupPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "MainWindow.Startup.cs");
        var workspacePath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var startupOptionsPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ShellStartupOptions.cs");
        var runner = File.ReadAllText(runnerPath);
        var hotkeySynchronizer = File.ReadAllText(hotkeySynchronizerPath);
        var app = File.ReadAllText(appPath);
        var mainWindow = string.Concat(File.ReadAllText(mainWindowPath), File.ReadAllText(startupPath));
        var workspace = ReadShellWorkspaceControllerText();
        var startupOptions = File.ReadAllText(startupOptionsPath);

        Assert.Contains("StartHotkeysAsync", runner);
        Assert.Contains("new HotkeyRegistration(\"command-palette\", \"Ctrl+Alt+Space\"", runner);
        Assert.Contains("RunnerHotkeySynchronizer", runner);
        Assert.Contains("runtime.ListHotkeyBindings()", hotkeySynchronizer);
        Assert.Contains("SyncModuleHotkeysAsync", runner);
        Assert.Contains("WatchRuntimeHotkeyBindingsAsync", runner);
        Assert.Contains("hotkeys.UnregisterAsync", hotkeySynchronizer);
        Assert.Contains("RequiresHotkeySync(evt.Type)", runner);
        Assert.Contains("new HotkeyRegistration(binding.Id, normalizedGesture, binding.Scope, binding.Reason)", hotkeySynchronizer);
        Assert.Contains("runtime.ExecuteCommandAsync", runner);
        Assert.Contains("CreateCommandRequest", runner);
        Assert.Contains("new Sdk.CommandRequest", hotkeySynchronizer);
        Assert.Contains("--command-palette", runner);
        Assert.Contains("hotkeys.Pressed", runner);
        Assert.Contains("ShellStartupOptions.FromArgs", app);
        Assert.Contains("--command-palette", startupOptions);
        Assert.Contains("--data-root", startupOptions);
        Assert.Contains("--modules", startupOptions);
        Assert.Contains("--no-runner-bootstrap", startupOptions);
        var shellProgram = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Program.cs"));
        Assert.Contains("var opensHome = toolActivation is null && !startupOptions.FocusCommandPalette", shellProgram);
        Assert.Contains("? ShellHomeSnapshotCache.TryReadAsync", shellProgram);
        Assert.Contains("App.CachedHomeSnapshotTask = cachedHomeSnapshotTask", shellProgram);
        Assert.Contains("ShellRunnerBootstrapper.EnsureStartedAsync", shellProgram);
        Assert.Contains("loadHomeTools: opensHome", shellProgram);
        Assert.DoesNotContain("ShellRunnerBootstrapper.EnsureStartedAsync(startupOptions).GetAwaiter().GetResult()", shellProgram, StringComparison.Ordinal);
        Assert.Contains("ExecutableName(\"MyPowerTools.Runner\")", File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ShellRunnerBootstrapper.cs")));
        Assert.Contains("FocusCommandPaletteAsync", mainWindow);
        Assert.Contains("public async Task FocusCommandPaletteAsync()", workspace);
    }

    [Fact]
    public async Task Privileged_broker_implements_platform_privilege_contract()
    {
        var auditPath = Path.Combine(Path.GetTempPath(), "mpt-privilege-contract", Guid.NewGuid().ToString("N"), "audit.jsonl");
        var broker = new PrivilegedBroker(new AuditLog(auditPath));

        var decision = await broker.EvaluateAsync(
            new PrivilegeRequest("service.restart", "serviceUser", "Restart a user service.", "smartbird-thermostat", "SmartBirdService"),
            CancellationToken.None);

        Assert.True(decision.RequiresBroker);
        Assert.Equal("permission-required", decision.State);
        Assert.Equal(MptErrorCodes.PermissionRequired, decision.ErrorCode);
        Assert.Contains(broker.Audit, entry =>
            entry.ModuleId == "smartbird-thermostat" &&
            entry.ActionId == "service.restart" &&
            entry.RequiresBroker);
    }

    [Fact]
    public async Task Unsupported_tray_service_reports_actionable_degraded_state()
    {
        await using var tray = new UnsupportedTrayService("test tray", "native provider pending");
        var invoked = false;

        var result = await tray.StartAsync(
            new TrayOptions("test", "Test Tray", null, [new TrayMenuItem("open", "Open", IsDefault: true)]),
            (_, _) =>
            {
                invoked = true;
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unsupported", result.State);
        Assert.Equal("unsupported", tray.State);
        Assert.Contains("native provider pending", result.Message);
        Assert.False(invoked);
    }

    [Fact]
    public void Log_router_redacts_sensitive_values()
    {
        var router = new LogRouter(Path.Combine(Path.GetTempPath(), "mpt-log-test", Guid.NewGuid().ToString("N")));
        router.Append("pkg", "module", "info", "password=hunter2 token=abc", "invocation");

        var record = router.Tail("module").Single();
        Assert.Contains("password=****", record.Message);
        Assert.Contains("token=****", record.Message);
    }

    [Fact]
    public void Log_router_allows_parallel_append_to_same_module_file()
    {
        var router = new LogRouter(Path.Combine(Path.GetTempPath(), "mpt-log-parallel-test", Guid.NewGuid().ToString("N")));

        Parallel.For(0, 40, index => router.Append("pkg", "runner", "info", $"message={index}", $"invocation-{index}"));

        var records = router.Tail("runner", 100);
        Assert.Equal(40, records.Count);
        Assert.Contains(records, record => record.InvocationId == "invocation-0");
        Assert.Contains(records, record => record.InvocationId == "invocation-39");
    }
}
