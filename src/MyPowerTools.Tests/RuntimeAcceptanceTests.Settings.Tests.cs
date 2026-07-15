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
using SmartBirdThermostat.MyPowerTools;
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
    public async Task Settings_update_validates_stores_and_applies_runtime()
    {
        var transport = new RecordingSettingsTransportRuntime("inproc-dotnet");
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-settings-apply", Guid.NewGuid().ToString("N"))),
            [transport]);
        runtime.Load(Path.Combine(Root, "modules"));
        var current = runtime.GetSettings("screenease");

        var result = await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch("screenease", current.Revision, new JsonObject { ["enabled"] = true }),
            CancellationToken.None);

        Assert.Equal("applied", result.ApplyState);
        Assert.Contains("Settings applied", result.ApplyMessage);
        Assert.Equal(1, transport.ValidateCount);
        Assert.Equal(1, transport.ApplyCount);
        Assert.Equal("screenease", transport.ValidatedPatch!.ModuleId);
        Assert.Equal(result.Snapshot.Revision, transport.AppliedSnapshot!.Revision);
        Assert.True(result.Snapshot.Values["enabled"]!.GetValue<bool>());
        Assert.Contains(runtime.HostEventsSince(0), evt =>
            evt.Type == "settings.updated" &&
            evt.Payload["applyState"]!.GetValue<string>() == "applied");
    }

    [Fact]
    public async Task Settings_apply_failure_rolls_back_persisted_update()
    {
        var transport = new RecordingSettingsTransportRuntime("inproc-dotnet")
        {
            FailApply = true
        };
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-settings-rollback", Guid.NewGuid().ToString("N"))),
            [transport]);
        runtime.Load(Path.Combine(Root, "modules"));
        var current = runtime.GetSettings("screenease");

        var result = await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch("screenease", current.Revision, new JsonObject { ["enabled"] = true }),
            CancellationToken.None);

        var after = runtime.GetSettings("screenease");
        Assert.Equal("apply-failed-rolled-back", result.ApplyState);
        Assert.Contains("rolled back", result.ApplyMessage);
        Assert.Equal(current.Revision, result.Snapshot.Revision);
        Assert.Equal(current.Revision, after.Revision);
        Assert.Null(after.Values["enabled"]);
        Assert.Equal(1, transport.ApplyCount);
        Assert.Contains(runtime.HostEventsSince(0), evt =>
            evt.Type == "settings.updated" &&
            evt.Payload["applyState"]!.GetValue<string>() == "apply-failed-rolled-back");
    }

    [Fact]
    public void Settings_validate_apply_chain_is_wired_through_hostcontrol_and_shell()
    {
        var hostProtoPath = Path.Combine(Root, "proto", "mpt_host_control_v1.proto");
        var moduleProtoPath = Path.Combine(Root, "proto", "mpt_module_v1.proto");
        var runtimeContractsPath = Path.Combine(Root, "src", "MyPowerTools.Runtime", "ModuleContracts.cs");
        var abstractionsPath = Path.Combine(Root, "src", "MyPowerTools.Abstractions", "PluginContracts.cs");
        var runtimePath = Path.Combine(Root, "src", "MyPowerTools.Runtime", "MptHostRuntime.cs");
        var hostServicePath = Path.Combine(Root, "src", "MyPowerTools.HostControl", "HostControlGrpcService.cs");
        var shellSettingsPath = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellSettingsService.cs");
        var grpcHostPath = Path.Combine(Root, "src", "MyPowerTools.ModuleHost.GrpcIpc", "GrpcIpcModuleHost.cs");
        var hostProto = File.ReadAllText(hostProtoPath);
        var moduleProto = File.ReadAllText(moduleProtoPath);
        var runtimeContracts = File.ReadAllText(runtimeContractsPath);
        var abstractions = File.ReadAllText(abstractionsPath);
        var runtime = File.ReadAllText(runtimePath);
        var hostService = File.ReadAllText(hostServicePath);
        var shellSettings = File.ReadAllText(shellSettingsPath);
        var grpcHost = File.ReadAllText(grpcHostPath);

        Assert.Contains("rpc ValidateSettings", moduleProto);
        Assert.Contains("rpc ApplySettings", moduleProto);
        Assert.Contains("string apply_state = 5", hostProto);
        Assert.Contains("ValidateSettingsAsync(RuntimeModuleRecord module", runtimeContracts);
        Assert.Contains("ApplySettingsAsync(RuntimeModuleRecord module", runtimeContracts);
        Assert.Contains("ApplySettingsAsync(SettingsSnapshotDocument snapshot", abstractions);
        Assert.Contains("UpdateSettingsWithApplyAsync", runtime);
        Assert.Contains("runtime.ValidateSettingsAsync", runtime);
        Assert.Contains("runtime.ApplySettingsAsync", runtime);
        Assert.Contains("apply-failed-rolled-back", runtime);
        Assert.Contains("_settingsStore.Rollback", runtime);
        Assert.Contains("SettingsValidationException", hostService);
        Assert.Contains("ApplyState = applyState", hostService);
        Assert.Contains("apply-failed-rolled-back", shellSettings);
        Assert.Contains("ApplyTitle", shellSettings);
        Assert.Contains("ShellSettingsSaveResult.Failed", shellSettings);
        Assert.Contains("ValidateSettingsAsync(new ValidateSettingsRequest", grpcHost);
        Assert.Contains("ApplySettingsAsync(new ApplySettingsRequest", grpcHost);
        Assert.Contains("saved and applied", shellSettings);
        Assert.Contains("apply failed", shellSettings);
    }

    [Fact]
    public async Task Settings_apply_through_hostcontrol_rejects_doubao_endpoint_overrides()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-settings-doubao-real", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-settings-doubao-real-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var current = await service.GetSettings(new HostProto.GetSettingsRequest { ModuleId = "doubao-agent" }, new TestServerCallContext());
        var rejected = await Assert.ThrowsAsync<RpcException>(() => service.UpdateSettings(
                new HostProto.UpdateSettingsRequest
                {
                    ModuleId = "doubao-agent",
                    ExpectedRevision = current.Revision,
                    Patch = JsonStructMapper.ToStruct(new JsonObject
                    {
                        ["plannerBaseUrl"] = "http://127.0.0.1:45678",
                        ["toolBaseUrl"] = "http://127.0.0.1:45679",
                        ["mcpBaseUrl"] = "http://127.0.0.1:45680",
                        ["toolHealthPath"] = "/ready"
                    })
                },
                new TestServerCallContext()));
        var command = await service.ExecuteCommand(
            new HostProto.ExecuteCommandRequest
            {
                InvocationId = "doubao-settings-hostcontrol",
                CommandId = "doubao-agent.self-test",
                Args = JsonStructMapper.ToStruct(new JsonObject())
            },
            new TestServerCallContext());

        var payload = JsonNode.Parse(command.Summary)!.AsObject();
        var services = payload["services"]!.AsArray().Select(item => item!.AsObject()).ToArray();

        Assert.Equal(StatusCode.InvalidArgument, rejected.StatusCode);
        Assert.Contains("fixed", rejected.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("succeeded", command.State);
        Assert.Contains(services, service => service["id"]!.GetValue<string>() == "planner" && service["baseUrl"]!.GetValue<string>() == "http://127.0.0.1:38189");
        Assert.Contains(services, service => service["id"]!.GetValue<string>() == "tool" && service["baseUrl"]!.GetValue<string>() == "http://127.0.0.1:38102");
        Assert.Contains(services, service => service["id"]!.GetValue<string>() == "mcp" && service["healthPath"]!.GetValue<string>() == "/sse");
    }

    [Fact]
    public async Task Production_inproc_settings_apply_changes_module_command_behavior()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-settings-production-real", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        var adbCurrent = runtime.GetSettings("adb-forwarder");
        var adb = await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch("adb-forwarder", adbCurrent.Revision, new JsonObject { ["adbPath"] = "mpt-missing-adb-from-settings" }),
            CancellationToken.None);
        var devices = await runtime.ExecuteCommandAsync(
            new CommandRequest("adb-settings-devices", "adb-forwarder.devices.scan", new JsonObject()),
            CancellationToken.None);
        var devicesPayload = JsonNode.Parse(devices.Output)!.AsObject();

        var screenCurrent = runtime.GetSettings("screenease");
        var screen = await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch("screenease", screenCurrent.Revision, new JsonObject { ["activeProfileId"] = "focus" }),
            CancellationToken.None);
        var screenPlan = await runtime.ExecuteCommandAsync(
            new CommandRequest("screenease-settings-plan", "screenease.profile.plan", new JsonObject()),
            CancellationToken.None);
        var screenPayload = JsonNode.Parse(screenPlan.Output)!.AsObject();

        await using var server = TestHttpFacadeServer.Start();
        var smartCurrent = runtime.GetSettings("smartbird-thermostat");
        var smart = await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch("smartbird-thermostat", smartCurrent.Revision, new JsonObject
            {
                ["energyServerBaseUrl"] = server.BaseUrl,
                ["targetTemperatureC"] = 61
            }),
            CancellationToken.None);
        var config = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-settings-config", "smartbird-thermostat.config.get", new JsonObject()),
            CancellationToken.None);
        var configPayload = JsonNode.Parse(config.Output)!.AsObject()["localConfig"]!.AsObject();

        Assert.Equal("applied", adb.ApplyState);
        Assert.True(devices.Success);
        Assert.Equal("mpt-missing-adb-from-settings", devicesPayload["tool"]!.GetValue<string>());
        Assert.Equal("applied", screen.ApplyState);
        Assert.True(screenPlan.Success);
        Assert.Equal("bright-focus", screenPayload["profile"]!.AsObject()["id"]!.GetValue<string>());
        Assert.Equal("applied", smart.ApplyState);
        Assert.True(config.Success);
        Assert.Equal(SmartBirdThermostatModule.CanonicalBaseUrl, configPayload["baseUrl"]!.GetValue<string>());
        Assert.Equal(61, configPayload["targetTemperatureC"]!.GetValue<double>());
    }

    [Fact]
    public async Task AndroidTools_settings_apply_changes_module_command_behavior()
    {
        var commandsRoot = Path.Combine(Path.GetTempPath(), "mpt-android-settings-commands", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(commandsRoot);
        var commandsPath = Path.Combine(commandsRoot, "commands.yaml");
        await File.WriteAllTextAsync(commandsPath, """
commands:
  - id: settings_echo
    label: Settings Echo
    command: remove_cpp_comments
    description: Command imported from Host settings path.
    type: py
""");

        var remote = new AndroidToolsRemoteCommandsModule();
        await remote.InitializeAsync(CreateModuleContext("android-tools-suite", "android-tools.remote-commands", "android-remote-settings", ["remote.commands"]), CancellationToken.None);
        await remote.ApplySettingsAsync(
            new SettingsSnapshotDocument("android-tools.remote-commands", 2, new JsonObject { ["commandsYamlPath"] = commandsPath }, DateTimeOffset.UtcNow),
            CancellationToken.None);
        var remoteCommands = await remote.ListCommandsAsync(CancellationToken.None);
        var catalog = await remote.ExecuteCommandAsync(
            new CommandRequest("android-remote-settings-catalog", "android-tools.remote-commands.catalog.summary", new JsonObject()),
            CancellationToken.None);

        var notifications = new AndroidToolsNotificationsModule();
        await notifications.InitializeAsync(CreateModuleContext("android-tools-suite", "android-tools.notifications", "android-notifications-settings", ["notifications.remote"]), CancellationToken.None);
        await notifications.ApplySettingsAsync(
            new SettingsSnapshotDocument("android-tools.notifications", 2, new JsonObject
            {
                ["serverProtocol"] = "http",
                ["serverHost"] = "127.0.0.1",
                ["serverPort"] = 34567
            }, DateTimeOffset.UtcNow),
            CancellationToken.None);
        var inbox = await notifications.ExecuteCommandAsync(
            new CommandRequest("android-notifications-settings-inbox", "android-tools.notifications.inbox.summary", new JsonObject()),
            CancellationToken.None);
        var endpoint = JsonNode.Parse(inbox.Output)!.AsObject()["endpoint"]!.AsObject();

        await using var grpcHost = new GrpcIpcModuleRuntime();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-android-settings-grpc", Guid.NewGuid().ToString("N"))),
            [grpcHost]);
        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var current = runtime.GetSettings("android-tools.process-monitor");
        var process = await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch("android-tools.process-monitor", current.Revision, new JsonObject { ["processes"] = new JsonArray("dotnet", "pwsh") }),
            CancellationToken.None);
        var summary = await runtime.ExecuteCommandAsync(
            new CommandRequest("android-process-settings-summary", "android-tools.process-monitor.status.summary", new JsonObject()),
            CancellationToken.None);
        var configured = JsonNode.Parse(summary.Output)!.AsObject()["configured"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();

        Assert.Contains(remoteCommands, command => command.Id == "android-tools.remote-commands.run.settings_echo");
        Assert.Contains("host-settings", catalog.Output);
        Assert.True(inbox.Success);
        Assert.True(endpoint["found"]!.GetValue<bool>());
        Assert.Equal("127.0.0.1", endpoint["host"]!.GetValue<string>());
        Assert.Equal(34567, endpoint["port"]!.GetValue<int>());
        Assert.Equal("applied", process.ApplyState);
        Assert.True(summary.Success);
        Assert.Contains("dotnet", configured);
        Assert.Contains("pwsh", configured);
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
    public async Task HostControl_get_settings_schema_exposes_runtime_schema()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            transportRuntimes: [host]);
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-settings-schema-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var schema = await service.GetSettingsSchema(
            new HostProto.GetSettingsSchemaRequest { ModuleId = "doubao-agent" },
            new TestServerCallContext());

        Assert.Equal("doubao-agent", schema.ModuleId);
        Assert.Contains("plannerBaseUrl", schema.SchemaJson, StringComparison.Ordinal);
        Assert.Contains("\"const\": \"http://127.0.0.1:38189\"", schema.SchemaJson, StringComparison.Ordinal);
        Assert.Contains("\"readOnly\": true", schema.SchemaJson, StringComparison.Ordinal);
        Assert.Contains("redactSensitiveOutput", schema.SchemaJson, StringComparison.Ordinal);
    }
}
