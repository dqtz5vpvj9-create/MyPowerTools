using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdbForwarder.MyPowerTools;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using MyPowerTools.HostControl;
using MyPowerTools.Broker;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.SampleModules.DotNet;
using MyPowerTools.UI;

namespace MyPowerTools.Tests;

public sealed class RuntimeAcceptanceTests
{
    private static readonly string Root = FindRepositoryRoot(AppContext.BaseDirectory);
    private const string PortProxySample = """
Listen on ipv4:             Connect to ipv4:

Address         Port        Address         Port
--------------- ----------  --------------- ----------
0.0.0.0         5555        127.0.0.1       7555
""";

    [Fact]
    public void Examples_validate_against_schemas()
    {
        var validator = new SchemaPackageValidator(Path.Combine(Root, "schemas"));
        var reports = validator.ValidatePackageRoot(Path.Combine(Root, "modules"));
        Assert.All(reports, report => Assert.True(report.IsValid, string.Join(Environment.NewLine, report.Issues.Select(issue => issue.Message))));
    }

    [Fact]
    public async Task Cli_validate_modules_returns_successful_exit_code()
    {
        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "validate",
            "modules");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("adb-forwarder: valid", result.Output);
        Assert.DoesNotContain(": invalid", result.Output);
    }

    [Fact]
    public async Task Cli_module_enable_disable_persists_state()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-module-state", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj");

        var disable = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "module",
            "disable",
            "doubao-agent",
            "--data-root",
            dataRoot);

        var visibleOnly = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "module",
            "list",
            "--data-root",
            dataRoot);

        var includeDisabled = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "module",
            "list",
            "--include-disabled",
            "--data-root",
            dataRoot);

        var enable = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "module",
            "enable",
            "doubao-agent",
            "--data-root",
            dataRoot);

        Assert.Equal(0, disable.ExitCode);
        Assert.Contains("doubao-agent: disabled (disabled)", disable.Output);
        Assert.Equal(0, visibleOnly.ExitCode);
        Assert.DoesNotContain("doubao-agent ", visibleOnly.Output);
        Assert.Equal(0, includeDisabled.ExitCode);
        Assert.Contains("doubao-agent disabled disabled", includeDisabled.Output);
        Assert.Equal(0, enable.ExitCode);
        Assert.Contains("doubao-agent: enabled", enable.Output);
    }

    [Fact]
    public async Task Cli_diagnostics_reports_runtime_snapshot()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-diagnostics", Guid.NewGuid().ToString("N"));
        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "diagnostics",
            "--data-root",
            dataRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("runner: 0.2.0", result.Output);
        Assert.Contains("modules: 7 enabled=7 disabled=0", result.Output);
        Assert.Contains("transport: inproc-dotnet", result.Output);
        Assert.Contains("module: screenease", result.Output);
    }

    [Fact]
    public async Task Cli_diagnostics_reports_grpc_process_snapshot()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.cli." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-grpc-diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "diagnostics",
            "--modules",
            packageRoot,
            "--data-root",
            Path.Combine(Path.GetTempPath(), "mpt-cli-grpc-diagnostics-data", Guid.NewGuid().ToString("N")));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("process: grpc-ipc pool=module:sample.grpc state=running", result.Output);
        Assert.Contains("starts=1/4", result.Output);
        Assert.Contains(pipeName, result.Output);
        Assert.Contains("modules=sample.grpc", result.Output);
    }

    [Fact]
    public async Task Cli_validate_contracts_reports_all_module_contracts()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-contracts", Guid.NewGuid().ToString("N"));
        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "validate",
            "contracts",
            "--data-root",
            dataRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Module contract validation passed: 5 packages, 7 modules.", result.Output);
        Assert.Contains("contract: adb-forwarder", result.Output);
        Assert.Contains("contract: android-tools.remote-commands", result.Output);
        Assert.Contains("contract: screenease", result.Output);
        Assert.Contains("settings=runtime-schema", result.Output);
        Assert.Contains("logs=ok", result.Output);
    }

    [Fact]
    public void Ui_snapshot_writes_contract_manifest()
    {
        var output = Path.Combine(Path.GetTempPath(), "mpt-ui-snapshot", Guid.NewGuid().ToString("N"));
        var manifestPath = new UiSurfaceGate().WriteSnapshotSet(
            Path.Combine(Root, "modules"),
            output,
            new UiSnapshotRequest("dashboard-card", "light", "1366x768", "normal"));

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var snapshots = manifest["snapshots"]!.AsArray();

        Assert.True(File.Exists(manifestPath));
        Assert.True(manifest["snapshotCount"]!.GetValue<int>() >= 5);
        Assert.Equal(manifest["snapshotCount"]!.GetValue<int>(), manifest["pixelSnapshotCount"]!.GetValue<int>());
        Assert.Contains(snapshots, item => item!["surfaceId"]!.GetValue<string>() == "adb-forwarder.dashboard");
        Assert.All(Directory.GetFiles(output, "*.snapshot.json"), path => Assert.Contains("sourceSha256", File.ReadAllText(path)));
        Assert.Equal(snapshots.Count, Directory.GetFiles(output, "*.snapshot.png").Length);
        Assert.All(snapshots, item =>
        {
            var pixelName = item!["pixelSnapshot"]!.GetValue<string>();
            var pixelPath = Path.Combine(output, pixelName);
            Assert.True(File.Exists(pixelPath), $"Missing pixel snapshot {pixelPath}");
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(pixelPath).Take(8).ToArray());
            Assert.Equal(64, item["pixelSha256"]!.GetValue<string>().Length);
            Assert.Equal(1366, item["pixelWidth"]!.GetValue<int>());
            Assert.Equal(768, item["pixelHeight"]!.GetValue<int>());
            Assert.True(item["pixelUniqueColorCount"]!.GetValue<int>() > 3);
            Assert.True(item["pixelNonBackgroundPixels"]!.GetValue<int>() > 0);
        });
    }

    [Fact]
    public void Ui_shell_snapshot_writes_key_surface_matrix()
    {
        var output = Path.Combine(Path.GetTempPath(), "mpt-shell-ui-snapshot", Guid.NewGuid().ToString("N"));
        var manifestPath = new UiSurfaceGate().WriteShellSnapshotSet(
            output,
            new UiSnapshotRequest("*", "light", "1366x768", "normal"));

        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var snapshots = manifest["snapshots"]!.AsArray();
        var required = manifest["requiredSurfaces"]!.AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();

        Assert.True(File.Exists(manifestPath));
        Assert.Equal("1.0", manifest["schemaVersion"]!.GetValue<string>());
        Assert.Equal(8, manifest["requiredSurfaceCount"]!.GetValue<int>());
        Assert.True(manifest["snapshotCount"]!.GetValue<int>() >= required.Length);
        Assert.Equal(manifest["snapshotCount"]!.GetValue<int>(), manifest["pixelSnapshotCount"]!.GetValue<int>());
        foreach (var surfaceId in required)
        {
            Assert.Contains(snapshots, item => item!["surfaceId"]!.GetValue<string>() == surfaceId);
        }

        Assert.Contains(snapshots, item => item!["surfaceId"]!.GetValue<string>() == "shell.package-manager");
        Assert.Contains(snapshots, item => item!["surfaceId"]!.GetValue<string>() == "shell.runtime-diagnostics");
        Assert.Equal(snapshots.Count, Directory.GetFiles(output, "*.snapshot.png").Length);
        Assert.All(snapshots, item =>
        {
            var pixelName = item!["pixelSnapshot"]!.GetValue<string>();
            var pixelPath = Path.Combine(output, pixelName);
            Assert.True(File.Exists(pixelPath), $"Missing pixel snapshot {pixelPath}");
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(pixelPath).Take(8).ToArray());
            Assert.Equal(64, item["pixelSha256"]!.GetValue<string>().Length);
            Assert.Equal(1366, item["pixelWidth"]!.GetValue<int>());
            Assert.Equal(768, item["pixelHeight"]!.GetValue<int>());
            Assert.True(item["pixelUniqueColorCount"]!.GetValue<int>() > 3);
            Assert.True(item["pixelNonBackgroundPixels"]!.GetValue<int>() > 0);
        });
    }

    [Fact]
    public void Runtime_indexes_modules_without_starting_sidecars()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var snapshot = runtime.GetDashboardSnapshot();

        Assert.Contains(snapshot.Cards, card => card.ModuleId == "doubao-agent");
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "android-tools.remote-commands");
    }

    [Fact]
    public void Runtime_persists_module_enable_disable_state()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-module-state", Guid.NewGuid().ToString("N"));
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current(), RuntimePaths.Create(dataRoot));
        runtime.Load(Path.Combine(Root, "modules"));

        var disabled = runtime.SetModuleEnabled("doubao-agent", enabled: false);

        Assert.Equal("disabled", disabled.State);
        Assert.DoesNotContain(runtime.GetDashboardSnapshot().Cards, card => card.ModuleId == "doubao-agent");
        Assert.DoesNotContain(runtime.ListCommands("Doubao"), command => command.Id == "doubao-agent.open");
        Assert.DoesNotContain(runtime.ListModules(includeDisabled: false), module => module.Module.Manifest.Id == "doubao-agent");
        Assert.Contains(runtime.ListModules(includeDisabled: true), module =>
            module.Module.Manifest.Id == "doubao-agent" &&
            module.Status.State == "disabled");

        var reloaded = new MptHostRuntime(new PackageReader(), PlatformId.Current(), RuntimePaths.Create(dataRoot));
        reloaded.Load(Path.Combine(Root, "modules"));

        Assert.DoesNotContain(reloaded.GetDashboardSnapshot().Cards, card => card.ModuleId == "doubao-agent");
        Assert.Contains(reloaded.ListModules(includeDisabled: true), module =>
            module.Module.Manifest.Id == "doubao-agent" &&
            module.Status.State == "disabled");

        var enabled = reloaded.SetModuleEnabled("doubao-agent", enabled: true);

        Assert.NotEqual("disabled", enabled.State);
        Assert.Contains(reloaded.GetDashboardSnapshot().Cards, card => card.ModuleId == "doubao-agent");
    }

    [Fact]
    public void Command_execution_is_idempotent_by_invocation_id()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var request = new CommandRequest("same-invocation", "doubao-agent.open", new JsonObject());

        var first = runtime.ExecuteCommand(request);
        var second = runtime.ExecuteCommand(request);

        Assert.Equal(first.Output, second.Output);
        Assert.True(second.Success);
    }

    [Fact]
    public void Command_index_reads_declared_static_commands()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));

        var commands = runtime.ListCommands("Doubao");

        Assert.Contains(commands, command => command.Id == "doubao-agent.health.check");
    }

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
    public void Settings_command_reads_revision()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));

        var result = runtime.ExecuteCommand(new CommandRequest("settings-read", "screenease.settings.read", new JsonObject()));

        Assert.True(result.Success);
        Assert.Contains("Settings revision", result.Output);
    }

    [Fact]
    public void Settings_revision_conflicts_are_detected()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var current = runtime.GetSettings("doubao-agent");
        runtime.UpdateSettings(new SettingsPatch("doubao-agent", current.Revision, new JsonObject { ["enabled"] = true }));

        Assert.Throws<SettingsConflictException>(() =>
            runtime.UpdateSettings(new SettingsPatch("doubao-agent", current.Revision, new JsonObject { ["enabled"] = false })));
    }

    [Fact]
    public void Settings_persist_and_rollback()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-settings-test", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(path);
        var current = store.Get("sample.dotnet");
        var updated = store.Update(new SettingsPatch("sample.dotnet", current.Revision, new JsonObject { ["enabled"] = true }));
        store.Update(new SettingsPatch("sample.dotnet", updated.Revision, new JsonObject { ["enabled"] = false }));

        var rolledBack = store.Rollback("sample.dotnet");

        Assert.True(rolledBack.Values["enabled"]!.GetValue<bool>());
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
    public async Task Cli_runner_autostart_status_reports_missing_entry()
    {
        var result = await RunDotnetAsync(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
            "--",
            "runner",
            "autostart",
            "status",
            "--id",
            $"mpt-test-{Guid.NewGuid():N}");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("disabled", result.Output);
        Assert.Contains("No HKCU Run entry exists", result.Output);
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
    public async Task Cli_package_install_uninstall_rollback_uses_store_root()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-package-store", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj");
        var fixture = Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet");
        var target = Path.Combine(storeRoot, "sample-dotnet");
        var rollbackTarget = target + ".rollback";

        var install = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "install",
            fixture,
            "--store-root",
            storeRoot);

        var uninstall = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "uninstall",
            "sample-dotnet",
            "--store-root",
            storeRoot);

        Assert.Equal(0, install.ExitCode);
        Assert.Contains("sample-dotnet: success", install.Output);
        Assert.Equal(0, uninstall.ExitCode);
        Assert.Contains("sample-dotnet: success", uninstall.Output);
        Assert.False(Directory.Exists(target));
        Assert.True(Directory.Exists(rollbackTarget));

        var rollback = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "rollback",
            "sample-dotnet",
            "--store-root",
            storeRoot);

        var repair = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "repair",
            "sample-dotnet",
            "--store-root",
            storeRoot);

        Assert.Equal(0, rollback.ExitCode);
        Assert.Contains("sample-dotnet: success", rollback.Output);
        Assert.True(Directory.Exists(target));
        Assert.False(Directory.Exists(rollbackTarget));
        Assert.Equal(0, repair.ExitCode);
        Assert.Contains("repair check passed.", repair.Output);
    }

    [Fact]
    public async Task Cli_inspect_package_hash_and_doctor_cover_package_commands()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-package-commands", Guid.NewGuid().ToString("N"));
        var packageCopy = Path.Combine(tempRoot, "sample-dotnet");
        CopyDirectory(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"), packageCopy);
        var project = Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj");
        var hashPath = Path.Combine(packageCopy, "shared", "package.hashes.json");
        var signaturePath = Path.Combine(packageCopy, "shared", "package.signature.json");

        var inspect = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "inspect",
            "modules");

        var hash = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "package",
            "hash",
            packageCopy);

        var sign = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "package",
            "sign-local",
            packageCopy);

        var trust = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "package",
            "trust",
            packageCopy,
            "--strict");

        var doctor = await RunDotnetAsync(
            "run",
            "--project",
            project,
            "--",
            "doctor");

        Assert.Equal(0, inspect.ExitCode);
        Assert.Contains("android-tools-suite", inspect.Output);
        Assert.Contains("android-tools.remote-commands", inspect.Output);
        Assert.Contains("permission: apply-portproxy level=broker capability=network.portForwarding", inspect.Output);
        Assert.Contains("requires: network.portForwarding required", inspect.Output);
        Assert.Equal(0, hash.ExitCode);
        Assert.Contains("package.hashes.json", hash.Output);
        Assert.True(File.Exists(hashPath));
        Assert.Contains("module.json", File.ReadAllText(hashPath));
        Assert.Equal(0, sign.ExitCode);
        Assert.Contains("package.signature.json", sign.Output);
        Assert.True(File.Exists(signaturePath));
        Assert.Equal(0, trust.ExitCode);
        Assert.Contains("sample-dotnet: signature-hook", trust.Output);
        Assert.Equal(0, doctor.ExitCode);
        Assert.Contains("packages: 5 checked, errors: 0", doctor.Output);
        Assert.Contains("modules: 7", doctor.Output);
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
    public async Task Inproc_sample_module_executes_command()
    {
        _ = typeof(SampleDotNetModule).Assembly;
        var package = new PackageReader().ReadPackageDirectory(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        var module = package.Modules.Single();
        var host = new InProcDotNetModuleHost();

        var loaded = await host.LoadAsync(module, CancellationToken.None);
        var result = await loaded.ExecuteCommandAsync(new CommandRequest("test", "sample.dotnet.ping", new JsonObject()), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("pong", result.Output);
    }

    [Fact]
    public async Task Runtime_delegates_dynamic_inproc_commands_to_transport_host()
    {
        _ = typeof(SampleDotNetModule).Assembly;
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-dynamic", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-dynamic-inproc", "sample.dotnet.ping", new JsonObject()),
            CancellationToken.None);
        var snapshot = runtime.GetDashboardSnapshot();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("ping"), command => command.Id == "sample.dotnet.ping");
        Assert.True(result.Success);
        Assert.Contains("pong", result.Output);
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "sample.dotnet" && card.State == "running");
    }

    [Fact]
    public async Task Runtime_executes_http_facade_module_against_local_service()
    {
        await using var server = TestHttpFacadeServer.Start();
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-http-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteHttpFacadeModuleManifest(packageRoot, server.BaseUrl);
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-http", Guid.NewGuid().ToString("N"))));

        runtime.Load(packageRoot);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        var snapshot = runtime.GetDashboardSnapshot();
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-http-ping", "sample.http-runtime.ping", new JsonObject()),
            CancellationToken.None);
        var logs = runtime.TailLogs("sample.http-runtime");

        Assert.Contains(snapshot.Cards, card => card.ModuleId == "sample.http-runtime" && card.State == "running");
        Assert.True(result.Success, result.Error?.Message);
        Assert.Contains("HTTP 200", result.Output);
        Assert.Contains("pong", result.Output);
        Assert.Contains("token=****", result.Output);
        Assert.DoesNotContain("abc123", result.Output);
        Assert.Contains(logs, record => record.InvocationId == "runtime-http-ping" && record.EventSeq > 0);
    }

    [Fact]
    public async Task Runtime_runs_grpc_sidecar_module_over_native_ipc()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-sidecar", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("gRPC"), command => command.Id == "sample.grpc.ping");
        Assert.True(result.Success);
        Assert.Contains("pong", result.Output);
    }

    [Fact]
    public async Task Runtime_recovers_grpc_sidecar_after_process_crash()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-crash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-crash", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        var firstPing = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-first-ping", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var crash = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-crash", "sample.grpc.crash", new JsonObject()),
            CancellationToken.None);

        await Task.Delay(750);

        var recoveredPing = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-recovered-ping", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var logs = runtime.TailLogs("sample.grpc");

        Assert.True(firstPing.Success);
        Assert.True(crash.Success);
        Assert.True(recoveredPing.Success);
        Assert.Contains("pong", recoveredPing.Output);
        Assert.NotEqual(ExtractPid(firstPing.Output), ExtractPid(recoveredPing.Output));
        Assert.Contains(logs, record => record.InvocationId == "runtime-grpc-crash" && record.EventSeq > 0);
        Assert.Contains(logs, record => record.InvocationId == "runtime-grpc-recovered-ping" && record.EventSeq > 0);
    }

    [Fact]
    public async Task Runtime_restarts_grpc_process_pool_on_request()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.restart." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-restart", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var first = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-restart-first", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var before = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var restart = await runtime.RestartRuntimeProcessAsync("grpc-ipc", before.PoolKey, CancellationToken.None);
        var afterRestart = runtime.GetRuntimeDiagnostics();
        var second = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-restart-second", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var afterSecond = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        Assert.True(first.Success, first.Error?.Message);
        Assert.True(restart.Success, restart.Message);
        Assert.Equal("restarting", restart.State);
        Assert.Contains("sample.grpc", restart.ModuleIds);
        Assert.Empty(afterRestart.Processes);
        Assert.True(second.Success, second.Error?.Message);
        Assert.NotEqual(ExtractPid(first.Output), ExtractPid(second.Output));
        Assert.Equal(2, afterSecond.StartCount);
        Assert.Equal(before.PoolKey, afterSecond.PoolKey);
    }

    [Fact]
    public async Task Runtime_pauses_and_resumes_grpc_restart_policy()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.policy." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-policy", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var first = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-policy-first", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var process = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var paused = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", process.PoolKey, paused: true, "maintenance", CancellationToken.None);
        var pausedDiagnostics = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);
        var crash = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-policy-crash", "sample.grpc.crash", new JsonObject()),
            CancellationToken.None);
        await Task.Delay(500);
        var blocked = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-policy-blocked", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var blockedDiagnostics = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var resumed = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", process.PoolKey, paused: false, "", CancellationToken.None);
        var recovered = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-policy-recovered", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var recoveredDiagnostics = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        Assert.True(first.Success, first.Error?.Message);
        Assert.True(paused.Success, paused.Message);
        Assert.Equal("paused", paused.RestartPolicy);
        Assert.Equal("paused", pausedDiagnostics.RestartPolicy);
        Assert.Contains("maintenance", pausedDiagnostics.PolicyReason);
        Assert.True(crash.Success, crash.Error?.Message);
        Assert.False(blocked.Success);
        Assert.Equal("MPT_RUNTIME_UNAVAILABLE", blocked.Error!.Code);
        Assert.Contains("restart policy is paused", blocked.Error.Message);
        Assert.Equal("paused", blockedDiagnostics.State);
        Assert.Equal(0, blockedDiagnostics.ProcessId);
        Assert.Equal("paused", blockedDiagnostics.RestartPolicy);
        Assert.Contains("sample.grpc", blockedDiagnostics.ModuleIds);
        Assert.True(resumed.Success, resumed.Message);
        Assert.Equal("automatic", resumed.RestartPolicy);
        Assert.True(recovered.Success, recovered.Error?.Message);
        Assert.NotEqual(ExtractPid(first.Output), ExtractPid(recovered.Output));
        Assert.Equal("automatic", recoveredDiagnostics.RestartPolicy);
    }

    [Fact]
    public async Task Runtime_persists_grpc_restart_policy_across_runtime_reload()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.persisted.policy." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-persisted-policy", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-persisted-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using (var host = new GrpcIpcModuleRuntime())
        {
            var runtime = new MptHostRuntime(
                new PackageReader(),
                PlatformId.Current(),
                RuntimePaths.Create(dataRoot),
                [host]);

            runtime.Load(packageRoot);
            var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var paused = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", "module:sample.grpc", paused: true, "maintenance window", CancellationToken.None, "test");
            var diagnostics = runtime.GetRuntimeDiagnostics();

            Assert.True(dynamicCount > 0);
            Assert.True(paused.Success, paused.Message);
            var process = Assert.Single(diagnostics.Processes);
            Assert.Equal("paused", process.RestartPolicy);
            Assert.Contains("maintenance window", process.PolicyReason);
            Assert.Contains(diagnostics.ProcessPolicyHistory, entry => entry.RestartPolicy == "paused" && entry.Source == "test");

            await runtime.DisposeAsync();
        }

        await using (var host = new GrpcIpcModuleRuntime())
        {
            var runtime = new MptHostRuntime(
                new PackageReader(),
                PlatformId.Current(),
                RuntimePaths.Create(dataRoot),
                [host]);

            runtime.Load(packageRoot);
            var restoredDiagnostics = runtime.GetRuntimeDiagnostics();
            var restoredProcess = Assert.Single(restoredDiagnostics.Processes);
            var blockedDynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var resumed = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", "module:sample.grpc", paused: false, "maintenance complete", CancellationToken.None, "test");
            var recoveredDynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var recovered = await runtime.ExecuteCommandAsync(
                new CommandRequest("runtime-grpc-persisted-policy-recovered", "sample.grpc.ping", new JsonObject()),
                CancellationToken.None);
            var finalDiagnostics = runtime.GetRuntimeDiagnostics();

            Assert.Equal("paused", restoredProcess.State);
            Assert.Equal(0, restoredProcess.ProcessId);
            Assert.Equal("paused", restoredProcess.RestartPolicy);
            Assert.Contains("maintenance window", restoredProcess.PolicyReason);
            Assert.Contains("sample.grpc", restoredProcess.ModuleIds);
            Assert.Equal(0, blockedDynamicCount);
            Assert.True(resumed.Success, resumed.Message);
            Assert.Equal("automatic", resumed.RestartPolicy);
            Assert.True(recoveredDynamicCount > 0);
            Assert.True(recovered.Success, recovered.Error?.Message);
            Assert.Contains(finalDiagnostics.ProcessPolicyHistory, entry => entry.RestartPolicy == "automatic" && entry.Source == "test");
            Assert.Contains(finalDiagnostics.ProcessPolicyHistory, entry => entry.RestartPolicy == "paused" && entry.Source == "test");

            await runtime.DisposeAsync();
        }
    }

    [Fact]
    public async Task Runtime_expires_grpc_restart_policy_and_recovers_pool()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.expiring.policy." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-expiring-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-expiring-policy", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var expiresAt = DateTimeOffset.UtcNow.AddMilliseconds(300);
        var paused = await runtime.SetRuntimeProcessRestartPolicyAsync("grpc-ipc", "module:sample.grpc", paused: true, "short window", CancellationToken.None, "test", expiresAt);
        var pausedDiagnostics = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);
        await Task.Delay(900);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var recovered = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-expiring-policy-recovered", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);
        var diagnostics = runtime.GetRuntimeDiagnostics();
        var process = Assert.Single(diagnostics.Processes);

        Assert.True(paused.Success, paused.Message);
        Assert.Equal("paused", paused.RestartPolicy);
        Assert.NotNull(paused.ExpiresAt);
        Assert.Equal("paused", pausedDiagnostics.RestartPolicy);
        Assert.NotNull(pausedDiagnostics.PolicyExpiresAt);
        Assert.True(dynamicCount > 0);
        Assert.True(recovered.Success, recovered.Error?.Message);
        Assert.Equal("automatic", process.RestartPolicy);
        Assert.Contains(diagnostics.ProcessPolicyHistory, entry => entry.RestartPolicy == "automatic" && entry.Source == "runtime.expiry");
    }

    [Fact]
    public async Task Grpc_ipc_runtime_enforces_restart_limit_after_crash_loop()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.limit." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-crash-limit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-crash-limit", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            var crash = await runtime.ExecuteCommandAsync(
                new CommandRequest($"runtime-grpc-limit-crash-{i}", "sample.grpc.crash", new JsonObject()),
                CancellationToken.None);
            await Task.Delay(500);
            var recovered = await runtime.ExecuteCommandAsync(
                new CommandRequest($"runtime-grpc-limit-recover-{i}", "sample.grpc.ping", new JsonObject()),
                CancellationToken.None);

            Assert.True(crash.Success, crash.Error?.Message);
            Assert.True(recovered.Success, recovered.Error?.Message);
        }

        var finalCrash = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-limit-final-crash", "sample.grpc.crash", new JsonObject()),
            CancellationToken.None);
        await Task.Delay(500);
        var blocked = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-grpc-limit-blocked", "sample.grpc.ping", new JsonObject()),
            CancellationToken.None);

        Assert.True(finalCrash.Success, finalCrash.Error?.Message);
        Assert.False(blocked.Success);
        Assert.Equal("MPT_RUNTIME_UNAVAILABLE", blocked.Error!.Code);
        Assert.Contains("restart limit reached", blocked.Error.Message);
    }

    [Fact]
    public async Task Runtime_pools_package_runtime_sidecar_across_modules()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.shared." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-shared", Guid.NewGuid().ToString("N"));
        WriteSharedGrpcRuntimePackage(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-shared", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var first = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-shared-one", "sample.shared.one.ping", new JsonObject()),
            CancellationToken.None);
        var second = await runtime.ExecuteCommandAsync(
            new CommandRequest("runtime-shared-two", "sample.shared.two.ping", new JsonObject()),
            CancellationToken.None);

        Assert.True(dynamicCount >= 4);
        Assert.Contains(runtime.ListCommands("sample.shared.one"), command => command.Id == "sample.shared.one.ping");
        Assert.Contains(runtime.ListCommands("sample.shared.two"), command => command.Id == "sample.shared.two.ping");
        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(ExtractPid(first.Output), ExtractPid(second.Output));

        var diagnostics = runtime.GetRuntimeDiagnostics();
        var process = Assert.Single(diagnostics.Processes.Where(process => process.TransportKind == "grpc-ipc"));
        Assert.Equal("package:sample-shared-runtime:runtime:shared-grpc", process.PoolKey);
        Assert.Equal("running", process.State);
        Assert.Equal(int.Parse(ExtractPid(first.Output)), process.ProcessId);
        Assert.Contains(pipeName, process.Endpoint);
        Assert.True(process.StartCount > 0);
        Assert.Equal(4, process.RestartLimit);
        Assert.NotNull(process.LastStartedAt);
        Assert.Contains("sample.shared.one", process.ModuleIds);
        Assert.Contains("sample.shared.two", process.ModuleIds);
    }

    [Fact]
    public async Task AdbForwarder_inproc_module_collects_diagnostics()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-adb-forwarder", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var summary = await runtime.ExecuteCommandAsync(
            new CommandRequest("adb-forwarder-diagnostics", "adb-forwarder.diagnostics.summary", new JsonObject()),
            CancellationToken.None);
        var devices = await runtime.ExecuteCommandAsync(
            new CommandRequest("adb-forwarder-devices", "adb-forwarder.devices.scan", new JsonObject()),
            CancellationToken.None);
        var snapshot = runtime.GetDashboardSnapshot();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("ADB"), command => command.Id == "adb-forwarder.diagnostics.summary");
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "adb-forwarder" && card.State != "unsupported");
        Assert.True(summary.Success);
        Assert.Contains("\"moduleId\":\"adb-forwarder\"", summary.Output);
        Assert.True(devices.Success);
        Assert.Contains("\"tool\":\"adb\"", devices.Output);
    }

    [Fact]
    public async Task AndroidTools_imports_powertool_commands_and_executes_text_tool()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-android-tools", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var catalog = await runtime.ExecuteCommandAsync(
            new CommandRequest("android-tools-catalog", "android-tools.remote-commands.catalog.summary", new JsonObject()),
            CancellationToken.None);
        var transform = await runtime.ExecuteCommandAsync(
            new CommandRequest("android-tools-remove-comments", "android-tools.remote-commands.run.remove_comments", new JsonObject
            {
                ["input"] = "int value = 1; // remove me\nconst char* text = \"// keep me\";\n"
            }),
            CancellationToken.None);
        var output = JsonNode.Parse(transform.Output)!.AsObject()["output"]!.GetValue<string>();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("Remove C++"), command => command.Id == "android-tools.remote-commands.run.remove_comments");
        Assert.True(catalog.Success);
        Assert.Contains("11 command(s)", catalog.Output);
        Assert.True(transform.Success);
        Assert.DoesNotContain("remove me", output);
        Assert.Contains("// keep me", output);
    }

    [Fact]
    public async Task AndroidTools_process_monitor_persists_shared_watch_list()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-android-process", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var save = await runtime.ExecuteCommandAsync(
            new CommandRequest("android-tools-process-save", "android-tools.process-monitor.watch.save", new JsonObject
            {
                ["processes"] = new JsonArray("dotnet", "pwsh")
            }),
            CancellationToken.None);
        var summary = await runtime.ExecuteCommandAsync(
            new CommandRequest("android-tools-process-summary", "android-tools.process-monitor.status.summary", new JsonObject()),
            CancellationToken.None);
        var payload = JsonNode.Parse(summary.Output)!.AsObject();
        var configured = payload["configured"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

        Assert.True(save.Success);
        Assert.True(summary.Success);
        Assert.Contains("dotnet", configured);
        Assert.Contains("pwsh", configured);
    }

    [Fact]
    public async Task ScreenEase_inproc_module_manages_profiles_and_display_state()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-screenease", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var status = await runtime.ExecuteCommandAsync(
            new CommandRequest("screenease-status", "screenease.status.summary", new JsonObject()),
            CancellationToken.None);
        var apply = await runtime.ExecuteCommandAsync(
            new CommandRequest("screenease-apply", "screenease.profile.apply", new JsonObject { ["profileId"] = "night" }),
            CancellationToken.None);
        var list = await runtime.ExecuteCommandAsync(
            new CommandRequest("screenease-list", "screenease.profile.list", new JsonObject()),
            CancellationToken.None);
        var snapshot = runtime.GetDashboardSnapshot();

        var applied = JsonNode.Parse(apply.Output)!.AsObject();
        var profiles = JsonNode.Parse(list.Output)!.AsObject();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("ScreenEase status"), command => command.Id == "screenease.status.summary");
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "screenease" && card.State != "unsupported");
        Assert.True(status.Success);
        Assert.Contains("\"displayCount\"", status.Output);
        Assert.True(apply.Success);
        Assert.Equal("night", applied["activeProfileId"]!.GetValue<string>());
        Assert.Equal("native-host-required", applied["nativeHost"]!.AsObject()["state"]!.GetValue<string>());
        Assert.True(list.Success);
        Assert.Equal("night", profiles["activeProfileId"]!.GetValue<string>());
    }

    [Fact]
    public void AdbForwarder_parses_netsh_portproxy_rules()
    {
        var rules = PortProxyParser.Parse(PortProxySample);

        var rule = Assert.Single(rules);
        Assert.Equal("0.0.0.0", rule.ListenAddress);
        Assert.Equal(5555, rule.ListenPort);
        Assert.Equal("127.0.0.1", rule.ConnectAddress);
        Assert.Equal(7555, rule.ConnectPort);
    }

    [Fact]
    public void AdbForwarder_plans_apply_with_rollback()
    {
        var current = PortProxyParser.Parse(PortProxySample);
        var (mappings, messages) = AdbPortProxyModel.ParseMappings(CreateAdbPortProxyArgs(PortProxySample));

        var plan = AdbPortProxyPlanner.CreateApplyPlan(mappings, current);

        Assert.Empty(messages);
        Assert.Single(plan.ToApply);
        Assert.Single(plan.ToRemove);
        var rollback = Assert.Single(plan.Rollback);
        Assert.Equal("apply", rollback.Operation);
        Assert.Equal(7555, rollback.Rule.ConnectPort);
    }

    [Fact]
    public async Task AdbForwarder_apply_request_returns_broker_plan()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-adb-forwarder-apply", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("adb-forwarder-apply-request", "adb-forwarder.portproxy.apply", CreateAdbPortProxyArgs(PortProxySample)),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("permission-required", result.State);
        Assert.Equal("MPT_PERMISSION_REQUIRED", result.Error!.Code);
        Assert.Equal("network.portproxy.apply", result.Error.Details!["actionId"]!.GetValue<string>());
        Assert.Equal("NetworkBroker", result.Error.Details!["broker"]!.GetValue<string>());
        Assert.Single(result.Error.Details!["expectedChange"]!.AsObject()["apply"]!.AsArray());
        Assert.Single(result.Error.Details!["expectedChange"]!.AsObject()["remove"]!.AsArray());
        Assert.Single(result.Error.Details!["rollback"]!.AsArray());
    }

    [Fact]
    public async Task HostControl_execute_command_exposes_permission_details()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-permission", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-permission-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));
        var request = new MyPowerTools.Protocol.HostControl.V1.ExecuteCommandRequest
        {
            InvocationId = "hostcontrol-permission",
            CommandId = "adb-forwarder.portproxy.apply",
            Args = JsonStructMapper.ToStruct(CreateAdbPortProxyArgs(PortProxySample))
        };

        var response = await service.ExecuteCommand(request, new TestServerCallContext());

        Assert.Equal("permission-required", response.State);
        Assert.Equal("MPT_PERMISSION_REQUIRED", response.ErrorCode);
        Assert.Equal("network.portproxy.apply", response.ErrorDetails.Fields["actionId"].StringValue);
        Assert.Single(response.ErrorDetails.Fields["rollback"].ListValue.Values);
    }

    [Fact]
    public async Task HostControl_module_detail_exposes_declared_permissions()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime);

        var modules = await service.ListModules(new MyPowerTools.Protocol.HostControl.V1.ListModulesRequest { IncludeDisabled = true }, new TestServerCallContext());
        var summary = modules.Modules.Single(module => module.ModuleId == "adb-forwarder");
        var detail = await service.GetModuleDetail(new MyPowerTools.Protocol.HostControl.V1.GetModuleDetailRequest { ModuleId = "adb-forwarder" }, new TestServerCallContext());

        Assert.Contains(summary.Permissions, permission => permission.Id == "apply-portproxy" && permission.Level == "broker");
        Assert.Contains(summary.Requirements, requirement => requirement.Capability == "network.portForwarding" && requirement.Required);
        Assert.Contains(detail.Permissions, permission => permission.Capability == "network.portForwarding" && permission.Reason.Contains("端口转发", StringComparison.Ordinal));
        Assert.Contains(detail.Requirements, requirement => requirement.Capability == "adb.devices" && requirement.Required);
    }

    [Fact]
    public async Task HostControl_lists_broker_audit_entries()
    {
        var audit = new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-audit", Guid.NewGuid().ToString("N"), "audit.jsonl"));
        audit.Append(new BrokerAuditEntry(
            "audit-1",
            DateTimeOffset.UtcNow,
            "adb-forwarder",
            "network.portproxy.apply",
            "elevated",
            "0.0.0.0:5555",
            "test audit",
            true,
            "evaluated",
            "remove 0.0.0.0:5555"));
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, audit);

        var response = await service.ListBrokerAudit(
            new MyPowerTools.Protocol.HostControl.V1.ListBrokerAuditRequest { ModuleId = "adb-forwarder", Limit = 10 },
            new TestServerCallContext());

        var entry = Assert.Single(response.Entries);
        Assert.Equal("audit-1", entry.AuditId);
        Assert.Equal("network.portproxy.apply", entry.ActionId);
        Assert.Equal("evaluated", entry.Result);
    }

    [Fact]
    public async Task HostControl_lists_notification_center_entries()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var notification = runtime.ExecuteCommand(new CommandRequest("hostcontrol-notification", "doubao-agent.notification.test", new JsonObject()));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-notifications-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var response = await service.ListNotifications(
            new MyPowerTools.Protocol.HostControl.V1.ListNotificationsRequest { Limit = 10 },
            new TestServerCallContext());

        Assert.True(notification.Success);
        var entry = Assert.Single(response.Notifications);
        Assert.Equal("doubao-agent", entry.ModuleId);
        Assert.Equal("Send Doubao test notification", entry.Title);
        Assert.False(entry.IsRead);
    }

    [Fact]
    public async Task HostControl_lists_package_summaries()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-packages-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var response = await service.ListPackages(
            new MyPowerTools.Protocol.HostControl.V1.ListPackagesRequest { IncludeDisabled = true },
            new TestServerCallContext());

        Assert.Equal(5, response.Packages.Count);
        var androidTools = response.Packages.Single(package => package.PackageId == "android-tools-suite");
        Assert.Equal(3u, androidTools.ModuleCount);
        Assert.Contains("android-tools.remote-commands", androidTools.ModuleIds);
        Assert.Equal("shared/package.hashes.json", androidTools.Hashes);
        Assert.Equal("signature-hook", androidTools.TrustState);
        Assert.Equal("local", androidTools.TrustPolicy);
        Assert.Equal("shared/package.signature.json", androidTools.SignaturePath);
        Assert.Equal(0u, androidTools.TrustIssueCount);
    }

    [Fact]
    public async Task HostControl_set_module_enabled_updates_runtime_state()
    {
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-module-state", Guid.NewGuid().ToString("N"))));
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-module-state-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var detail = await service.SetModuleEnabled(
            new MyPowerTools.Protocol.HostControl.V1.SetModuleEnabledRequest { ModuleId = "doubao-agent", Enabled = false },
            new TestServerCallContext());
        var visibleOnly = await service.ListModules(
            new MyPowerTools.Protocol.HostControl.V1.ListModulesRequest(),
            new TestServerCallContext());
        var includeDisabled = await service.ListModules(
            new MyPowerTools.Protocol.HostControl.V1.ListModulesRequest { IncludeDisabled = true },
            new TestServerCallContext());

        Assert.Equal("disabled", detail.State);
        Assert.DoesNotContain(visibleOnly.Modules, module => module.ModuleId == "doubao-agent");
        var disabledModule = includeDisabled.Modules.Single(module => module.ModuleId == "doubao-agent");
        Assert.False(disabledModule.Enabled);
        Assert.Equal("disabled", disabledModule.State);
    }

    [Fact]
    public async Task HostControl_returns_runtime_diagnostics_snapshot()
    {
        await using var host = new InProcDotNetModuleHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-diagnostics", Guid.NewGuid().ToString("N"));
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(dataRoot),
            [host]);
        runtime.Load(Path.Combine(Root, "modules"));
        runtime.ExecuteCommand(new CommandRequest("diagnostics-history", "doubao-agent.open", new JsonObject()));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-diagnostics-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var diagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());

        Assert.Equal("0.2.0", diagnostics.RunnerVersion);
        Assert.Equal("1.0", diagnostics.HostControlProtocolVersion);
        Assert.Equal(5u, diagnostics.Counts.PackageCount);
        Assert.Equal(7u, diagnostics.Counts.ModuleCount);
        Assert.Equal((uint)runtime.CurrentEventSeq, diagnostics.CurrentEventSeq);
        Assert.Equal(dataRoot, diagnostics.Paths.Root);
        Assert.Contains(diagnostics.Transports, transport => transport.Kind == "inproc-dotnet" && transport.RuntimeRegistered);
        Assert.Contains(diagnostics.Modules, module => module.ModuleId == "doubao-agent" && module.Enabled);
        Assert.Contains(diagnostics.RecentCommands, command => command.InvocationId == "diagnostics-history");
    }

    [Fact]
    public async Task HostControl_returns_grpc_process_diagnostics_snapshot()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.hostcontrol." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-diagnostics-data", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-diagnostics-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var diagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());

        var process = Assert.Single(diagnostics.Processes.Where(process => process.TransportKind == "grpc-ipc"));
        Assert.True(dynamicCount > 0);
        Assert.Equal("module:sample.grpc", process.PoolKey);
        Assert.Equal("running", process.State);
        Assert.True(process.ProcessId > 0);
        Assert.Contains(pipeName, process.Endpoint);
        Assert.Equal(4u, process.RestartLimit);
        Assert.True(process.StartCount > 0);
        Assert.NotNull(process.LastStartedAt);
        Assert.Contains("sample.grpc", process.ModuleIds);
    }

    [Fact]
    public async Task HostControl_restarts_grpc_process_pool()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.hostcontrol.restart." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-restart-data", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-restart-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));
        var diagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());
        var process = Assert.Single(diagnostics.Processes);

        var restart = await service.RestartRuntimeProcess(
            new MyPowerTools.Protocol.HostControl.V1.RestartRuntimeProcessRequest
            {
                TransportKind = process.TransportKind,
                PoolKey = process.PoolKey
            },
            new TestServerCallContext());
        var afterRestart = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());

        Assert.True(restart.Success, restart.Message);
        Assert.Equal("grpc-ipc", restart.TransportKind);
        Assert.Equal("module:sample.grpc", restart.PoolKey);
        Assert.Equal("restarting", restart.State);
        Assert.Contains("sample.grpc", restart.ModuleIds);
        Assert.Empty(afterRestart.Processes);
    }

    [Fact]
    public async Task HostControl_sets_grpc_process_restart_policy()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.hostcontrol.policy." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-policy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-policy-data", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-grpc-policy-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));
        var diagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());
        var process = Assert.Single(diagnostics.Processes);
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

        var paused = await service.SetRuntimeProcessRestartPolicy(
            new MyPowerTools.Protocol.HostControl.V1.SetRuntimeProcessRestartPolicyRequest
            {
                TransportKind = process.TransportKind,
                PoolKey = process.PoolKey,
                Paused = true,
                Reason = "operator pause",
                Source = "hostcontrol-test",
                ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(expiresAt)
            },
            new TestServerCallContext());
        var pausedDiagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());
        var resumed = await service.SetRuntimeProcessRestartPolicy(
            new MyPowerTools.Protocol.HostControl.V1.SetRuntimeProcessRestartPolicyRequest
            {
                TransportKind = process.TransportKind,
                PoolKey = process.PoolKey,
                Paused = false
            },
            new TestServerCallContext());

        Assert.True(paused.Success, paused.Message);
        Assert.Equal("paused", paused.RestartPolicy);
        Assert.NotNull(paused.ExpiresAt);
        var pausedProcess = Assert.Single(pausedDiagnostics.Processes);
        Assert.Equal("paused", pausedProcess.RestartPolicy);
        Assert.Contains("operator pause", pausedProcess.PolicyReason);
        Assert.NotNull(pausedProcess.PolicyExpiresAt);
        var history = Assert.Single(pausedDiagnostics.ProcessPolicyHistory);
        Assert.Equal("paused", history.RestartPolicy);
        Assert.Equal("hostcontrol-test", history.Source);
        Assert.Contains("operator pause", history.Reason);
        Assert.NotNull(history.ExpiresAt);
        Assert.True(resumed.Success, resumed.Message);
        Assert.Equal("automatic", resumed.RestartPolicy);
    }

    [Fact]
    public async Task Cli_restarts_runner_grpc_process_pool_over_hostcontrol()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.cli.runner.restart." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-runner-grpc-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-cli-runner-grpc-restart-data", Guid.NewGuid().ToString("N"));
        var runner = StartDotnetProcess(
            "run",
            "--project",
            Path.Combine(Root, "src", "MyPowerTools.Runner", "MyPowerTools.Runner.csproj"),
            "--",
            "--modules",
            packageRoot,
            "--data-root",
            dataRoot,
            "--no-tray");

        try
        {
            using var client = await WaitForDefaultHostControlAsync();
            var diagnostics = await client.GetRuntimeDiagnosticsAsync();
            var process = Assert.Single(diagnostics.Processes);

            var restart = await RunDotnetAsync(
                "run",
                "--project",
                Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
                "--",
                "runner",
                "process",
                "restart",
                process.TransportKind,
                process.PoolKey);
            var afterRestart = await client.GetRuntimeDiagnosticsAsync();

            Assert.Equal(0, restart.ExitCode);
            Assert.Contains("grpc-ipc module:sample.grpc: restarting", restart.Output);
            Assert.Empty(afterRestart.Processes);

            var repopulated = await client.ExecuteCommandAsync("sample.grpc.ping");
            var repopulatedProcess = Assert.Single((await client.GetRuntimeDiagnosticsAsync()).Processes);
            var pause = await RunDotnetAsync(
                "run",
                "--project",
                Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
                "--",
                "runner",
                "process",
                "pause",
                repopulatedProcess.TransportKind,
                repopulatedProcess.PoolKey,
                "--reason",
                "cli maintenance",
                "--duration-minutes",
                "30");
            var policyDiagnostics = await client.GetRuntimeDiagnosticsAsync();
            var crash = await client.ExecuteCommandAsync("sample.grpc.crash");
            await Task.Delay(500);
            var blocked = await client.ExecuteCommandAsync("sample.grpc.ping");
            var pausedProcess = Assert.Single((await client.GetRuntimeDiagnosticsAsync()).Processes);
            var resume = await RunDotnetAsync(
                "run",
                "--project",
                Path.Combine(Root, "src", "MyPowerTools.Cli", "MyPowerTools.Cli.csproj"),
                "--",
                "runner",
                "process",
                "resume",
                repopulatedProcess.TransportKind,
                repopulatedProcess.PoolKey);
            var recovered = await client.ExecuteCommandAsync("sample.grpc.ping");

            Assert.Equal("succeeded", repopulated.State);
            Assert.Equal(0, pause.ExitCode);
            Assert.Contains("paused", pause.Output);
            Assert.Contains("expires:", pause.Output);
            Assert.Contains(policyDiagnostics.ProcessPolicyHistory, entry => entry.Source == "cli" && entry.RestartPolicy == "paused");
            Assert.Equal("succeeded", crash.State);
            Assert.Equal("failed", blocked.State);
            Assert.Equal("MPT_RUNTIME_UNAVAILABLE", blocked.ErrorCode);
            Assert.Contains("restart policy is paused", blocked.ErrorMessage);
            Assert.Equal("paused", pausedProcess.State);
            Assert.Equal("paused", pausedProcess.RestartPolicy);
            Assert.Contains("sample.grpc", pausedProcess.ModuleIds);
            Assert.Equal(0, resume.ExitCode);
            Assert.Contains("Automatic restart is enabled", resume.Output);
            Assert.Equal("succeeded", recovered.State);

            await client.QuitRunnerAsync();
            Assert.True(runner.WaitForExit(30000), "Runner did not exit after HostControl QuitRunner.");
            Assert.Equal(0, runner.ExitCode);
        }
        finally
        {
            if (!runner.HasExited)
            {
                runner.Kill(entireProcessTree: true);
                await runner.WaitForExitAsync();
            }

            runner.Dispose();
        }
    }

    [Fact]
    public async Task HostControl_quit_runner_requests_host_shutdown()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        var lifetime = new RecordingHostApplicationLifetime();
        var service = new HostControlGrpcService(
            runtime,
            new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-quit-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")),
            lifetime);

        await service.QuitRunner(
            new MyPowerTools.Protocol.HostControl.V1.QuitRunnerRequest(),
            new TestServerCallContext());

        Assert.True(lifetime.StopRequested);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    private static string FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static JsonObject CreateAdbPortProxyArgs(string currentPortProxyText)
    {
        return new JsonObject
        {
            ["reason"] = "test broker apply",
            ["currentPortProxyText"] = currentPortProxyText,
            ["mappings"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "device-5555",
                    ["name"] = "Test device 5555",
                    ["enabled"] = true,
                    ["listenAddress"] = "0.0.0.0",
                    ["listenPort"] = 5555,
                    ["connectAddress"] = "127.0.0.1",
                    ["connectPort"] = 5555
                }
            }
        };
    }

    private static string FindSampleGrpcSidecarCommand()
    {
        var fileName = OperatingSystem.IsWindows()
            ? "MyPowerTools.SampleSidecar.Grpc.exe"
            : "MyPowerTools.SampleSidecar.Grpc";
        var command = Path.Combine(Root, "src", "MyPowerTools.SampleSidecar.Grpc", "bin", "Debug", "net10.0", fileName);
        Assert.True(File.Exists(command), $"Expected sample gRPC sidecar command at {command}");
        return command;
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await outputTask + await errorTask);
    }

    private static Process StartDotnetProcess(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Root,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet.");
    }

    private static async Task<HostControlClient> WaitForDefaultHostControlAsync()
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            HostControlClient? client = null;
            try
            {
                client = HostControlClient.ForDefaultEndpoint();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.PingAsync(timeout.Token);
                return client;
            }
            catch (Exception ex)
            {
                lastError = ex;
                client?.Dispose();
                await Task.Delay(500);
            }
        }

        throw new InvalidOperationException($"Runner HostControl endpoint did not become available: {lastError?.Message}");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void WriteGrpcSidecarModuleManifest(string packageRoot, string sidecarCommand, string pipeName)
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"{pipeName}.sock");
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "sample.grpc",
            ["packageId"] = "sample-grpc-sidecar",
            ["displayName"] = "Sample gRPC Sidecar",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "grpc-ipc",
                    ["priority"] = 90,
                    ["command"] = sidecarCommand,
                    ["args"] = new JsonArray(pipeName),
                    ["windows"] = new JsonObject
                    {
                        ["transport"] = "named-pipe",
                        ["name"] = pipeName
                    },
                    ["linux"] = new JsonObject
                    {
                        ["transport"] = "unix-domain-socket",
                        ["path"] = socketPath
                    },
                    ["macos"] = new JsonObject
                    {
                        ["transport"] = "unix-domain-socket",
                        ["path"] = socketPath
                    }
                }
            },
            ["capabilities"] = new JsonArray("status", "commands", "settings", "dashboardCard")
        };

        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteHttpFacadeModuleManifest(string packageRoot, string baseUrl)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "sample.http-runtime",
            ["packageId"] = "sample-http-runtime",
            ["displayName"] = "Sample HTTP Runtime",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "http",
                    ["priority"] = 80,
                    ["baseUrl"] = baseUrl,
                    ["health"] = new JsonObject
                    {
                        ["path"] = "/api/status"
                    }
                }
            },
            ["capabilities"] = new JsonArray("status", "commands", "dashboardCard"),
            ["staticIndexes"] = new JsonObject
            {
                ["commands"] = "commands.index.json"
            }
        };

        var commands = new JsonObject
        {
            ["commands"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "sample.http-runtime.ping",
                    ["title"] = "Ping HTTP Runtime",
                    ["subtitle"] = "Calls the local HTTP facade.",
                    ["kind"] = "action",
                    ["category"] = "Tests",
                    ["timeoutMs"] = 5000,
                    ["execution"] = new JsonObject
                    {
                        ["type"] = "http.request",
                        ["method"] = "POST",
                        ["path"] = "/api/ping",
                        ["body"] = new JsonObject
                        {
                            ["message"] = "hello"
                        }
                    }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(
            Path.Combine(packageRoot, "commands.index.json"),
            commands.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSharedGrpcRuntimePackage(string packageRoot, string sidecarCommand, string pipeName)
    {
        var modulesRoot = Path.Combine(packageRoot, "modules");
        var oneRoot = Path.Combine(modulesRoot, "one");
        var twoRoot = Path.Combine(modulesRoot, "two");
        Directory.CreateDirectory(oneRoot);
        Directory.CreateDirectory(twoRoot);

        var socketPath = Path.Combine(Path.GetTempPath(), $"{pipeName}.sock");
        var package = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "sample-shared-runtime",
            ["displayName"] = "Sample Shared Runtime",
            ["version"] = "0.2.0",
            ["modules"] = new JsonArray("modules/one/module.json", "modules/two/module.json"),
            ["shared"] = new JsonObject
            {
                ["runtimes"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "shared-grpc",
                        ["entrypoints"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["kind"] = "grpc-ipc",
                                ["priority"] = 90,
                                ["command"] = sidecarCommand,
                                ["args"] = new JsonArray(pipeName),
                                ["windows"] = new JsonObject
                                {
                                    ["transport"] = "named-pipe",
                                    ["name"] = pipeName
                                },
                                ["linux"] = new JsonObject
                                {
                                    ["transport"] = "unix-domain-socket",
                                    ["path"] = socketPath
                                },
                                ["macos"] = new JsonObject
                                {
                                    ["transport"] = "unix-domain-socket",
                                    ["path"] = socketPath
                                }
                            }
                        }
                    }
                }
            }
        };

        File.WriteAllText(
            Path.Combine(packageRoot, "package.json"),
            package.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        WritePackageRuntimeModule(oneRoot, "sample.shared.one");
        WritePackageRuntimeModule(twoRoot, "sample.shared.two");
    }

    private static void WritePackageRuntimeModule(string moduleRoot, string moduleId)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = moduleId,
            ["packageId"] = "sample-shared-runtime",
            ["displayName"] = moduleId,
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "package-runtime",
                    ["priority"] = 90,
                    ["runtimeId"] = "shared-grpc"
                }
            },
            ["capabilities"] = new JsonArray("status", "commands")
        };

        File.WriteAllText(
            Path.Combine(moduleRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ExtractPid(string output)
    {
        var marker = "pid=";
        var start = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        Assert.True(start >= 0, $"Expected pid marker in output: {output}");
        start += marker.Length;
        var end = output.IndexOf(' ', start);
        return end < 0 ? output[start..] : output[start..end];
    }

    private sealed class TestHttpFacadeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        private TestHttpFacadeServer(TcpListener listener)
        {
            _listener = listener;
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}";
            _acceptLoop = AcceptLoopAsync();
        }

        public string BaseUrl { get; }

        public static TestHttpFacadeServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new TestHttpFacadeServer(listener);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
            }

            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleClientAsync(client, _cts.Token), _cts.Token);
                }
                catch (Exception) when (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
        {
            using var _ = client;
            var stream = client.GetStream();
            var buffer = new byte[8192];
            var length = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            var request = Encoding.ASCII.GetString(buffer, 0, length);
            var firstLine = request.Split("\r\n", StringSplitOptions.None).FirstOrDefault() ?? "";
            var (status, body) = firstLine switch
            {
                var line when line.Contains(" /api/status ", StringComparison.Ordinal) => ("200 OK", "{\"state\":\"running\",\"service\":\"local-http-facade\"}"),
                var line when line.Contains(" /api/ping ", StringComparison.Ordinal) => ("200 OK", "pong token=abc123"),
                _ => ("404 Not Found", "missing")
            };
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n");

            await stream.WriteAsync(headers.AsMemory(0, headers.Length), cancellationToken);
            await stream.WriteAsync(bodyBytes.AsMemory(0, bodyBytes.Length), cancellationToken);
        }
    }

    private sealed class RecordingNetworkBroker : INetworkBroker
    {
        public List<string> Operations { get; } = [];
        public bool FailNextApply { get; init; }
        private bool _applyFailed;

        public Task<IReadOnlyList<PortProxyRule>> ListPortProxyRulesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<PortProxyRule>>([]);
        }

        public Task<BrokerOperationResult> ApplyPortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            Operations.Add($"apply:{rule.ListenAddress}:{rule.ListenPort}->{rule.ConnectAddress}:{rule.ConnectPort}");
            if (FailNextApply && !_applyFailed)
            {
                _applyFailed = true;
                return Task.FromResult(new BrokerOperationResult(false, "failed", "synthetic apply failure"));
            }

            return Task.FromResult(new BrokerOperationResult(true, "success", "applied"));
        }

        public Task<BrokerOperationResult> RemovePortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            Operations.Add($"remove:{rule.ListenAddress}:{rule.ListenPort}");
            return Task.FromResult(new BrokerOperationResult(true, "success", "removed"));
        }
    }

    private sealed class RecordingAutostartService : IAutostartService
    {
        private readonly Dictionary<string, string> _commands = new(StringComparer.OrdinalIgnoreCase);

        public Task<ServiceStatus> GetAsync(string id, CancellationToken cancellationToken)
        {
            return Task.FromResult(_commands.TryGetValue(id, out var command)
                ? new ServiceStatus(id, "enabled", command)
                : new ServiceStatus(id, "disabled", $"No autostart entry exists for '{id}'."));
        }

        public Task<BrokerOperationResult> EnableAsync(string id, string command, CancellationToken cancellationToken)
        {
            _commands[id] = command;
            return Task.FromResult(new BrokerOperationResult(true, "enabled", $"Enabled {id}."));
        }

        public Task<BrokerOperationResult> DisableAsync(string id, CancellationToken cancellationToken)
        {
            _commands.Remove(id);
            return Task.FromResult(new BrokerOperationResult(true, "disabled", $"Disabled {id}."));
        }
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders = [];
        private readonly Metadata _responseTrailers = [];
        private Status _status = new(StatusCode.OK, "");
        private WriteOptions? _writeOptions;

        protected override string MethodCore => "test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "local";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(1);
        protected override Metadata RequestHeadersCore => _requestHeaders;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore
        {
            get => _status;
            set => _status = value;
        }

        protected override WriteOptions? WriteOptionsCore
        {
            get => _writeOptions;
            set => _writeOptions = value;
        }

        protected override AuthContext AuthContextCore => new("", []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
        {
            throw new NotSupportedException();
        }

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public bool StopRequested { get; private set; }
        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            StopRequested = true;
            _stopping.Cancel();
        }
    }
}
