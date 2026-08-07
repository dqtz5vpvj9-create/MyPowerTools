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
using SmartBirdThermostat.MyPowerTools;
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
    public async Task AndroidTools_remote_shell_command_streams_stdout_and_final_result()
    {
        var commandsRoot = Path.Combine(Path.GetTempPath(), "mpt-android-stream-commands", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(commandsRoot);
        var commandsPath = Path.Combine(commandsRoot, "commands.yaml");
        var shellCommand = OperatingSystem.IsWindows()
            ? "Write-Output mpt-stream-alpha"
            : "printf 'mpt-stream-alpha\\n'";
        await File.WriteAllTextAsync(commandsPath, $$"""
commands:
  - id: stream_echo
    label: Stream Echo
    command: {{shellCommand}}
    description: Command imported from Host settings path.
    type: shell
""");

        var remote = new AndroidToolsRemoteCommandsModule();
        await remote.InitializeAsync(CreateModuleContext("android-tools-suite", "android-tools.remote-commands", "android-remote-stream", ["remote.commands"]), CancellationToken.None);
        await remote.ApplySettingsAsync(
            new SettingsSnapshotDocument("android-tools.remote-commands", 2, new JsonObject { ["commandsYamlPath"] = commandsPath }, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var events = new List<MyPowerTools.Abstractions.CommandExecutionEvent>();
        await foreach (var evt in remote.ExecuteCommandStreamAsync(
            new CommandRequest("android-stream", "android-tools.remote-commands.run.stream_echo", new JsonObject { ["execute"] = true }),
            CancellationToken.None))
        {
            events.Add(evt);
        }

        var final = Assert.Single(events.Where(evt => evt.Terminal));
        Assert.Contains(events, evt => evt.State == "stdout" && evt.Message.Contains("mpt-stream-alpha", StringComparison.Ordinal));
        Assert.NotNull(final.FinalResult);
        Assert.True(final.FinalResult!.Success, final.FinalResult.Error?.Message);
        Assert.Contains("mpt-stream-alpha", final.FinalResult.Output);
    }

    [Fact]
    public async Task AndroidTools_remote_shell_command_stream_truncates_unbounded_output()
    {
        var commandsRoot = Path.Combine(Path.GetTempPath(), "mpt-android-stream-truncate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(commandsRoot);
        var commandsPath = Path.Combine(commandsRoot, "commands.yaml");
        var shellCommand = OperatingSystem.IsWindows()
            ? "1..1205 | ForEach-Object { 'mpt-stream-line-' + $_ }"
            : "i=1; while [ $i -le 1205 ]; do printf 'mpt-stream-line-%s\\n' \"$i\"; i=$((i+1)); done";
        await File.WriteAllTextAsync(commandsPath, $$"""
commands:
  - id: stream_many
    label: Stream Many
    command: {{shellCommand}}
    description: Command imported from Host settings path.
    type: shell
""");

        var remote = new AndroidToolsRemoteCommandsModule();
        await remote.InitializeAsync(CreateModuleContext("android-tools-suite", "android-tools.remote-commands", "android-remote-stream-truncate", ["remote.commands"]), CancellationToken.None);
        await remote.ApplySettingsAsync(
            new SettingsSnapshotDocument("android-tools.remote-commands", 2, new JsonObject { ["commandsYamlPath"] = commandsPath }, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var events = new List<MyPowerTools.Abstractions.CommandExecutionEvent>();
        await foreach (var evt in remote.ExecuteCommandStreamAsync(
                           new CommandRequest("android-stream-truncate", "android-tools.remote-commands.run.stream_many", new JsonObject { ["execute"] = true }),
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        var final = Assert.Single(events.Where(evt => evt.Terminal));
        var payload = JsonNode.Parse(final.FinalResult!.Output)!.AsObject();

        Assert.True(final.FinalResult.Success, final.FinalResult.Error?.Message);
        Assert.Contains(events, evt => evt.State == "output.truncated");
        Assert.True(events.Count(evt => evt.State == "stdout") <= 1000);
        Assert.True(payload["truncated"]!.GetValue<bool>());
        Assert.True(payload["stdoutLines"]!.GetValue<int>() >= 1205);
        Assert.Equal(1000, payload["maxStreamLineEvents"]!.GetValue<int>());
    }

    [Fact]
    public async Task AndroidTools_remote_shell_command_async_truncates_large_output()
    {
        var commandsRoot = Path.Combine(Path.GetTempPath(), "mpt-android-async-truncate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(commandsRoot);
        var commandsPath = Path.Combine(commandsRoot, "commands.yaml");
        var shellCommand = OperatingSystem.IsWindows()
            ? "1..2000 | ForEach-Object { 'mpt-async-line-' + $_ + '-' + ('x' * 220) }"
            : "i=1; while [ $i -le 2000 ]; do printf 'mpt-async-line-%s-%0220d\\n' \"$i\" 0; i=$((i+1)); done";
        await File.WriteAllTextAsync(commandsPath, $$"""
commands:
  - id: async_many
    label: Async Many
    command: {{shellCommand}}
    description: Command imported from Host settings path.
    type: shell
""");

        var remote = new AndroidToolsRemoteCommandsModule();
        await remote.InitializeAsync(CreateModuleContext("android-tools-suite", "android-tools.remote-commands", "android-remote-async-truncate", ["remote.commands"]), CancellationToken.None);
        await remote.ApplySettingsAsync(
            new SettingsSnapshotDocument("android-tools.remote-commands", 2, new JsonObject { ["commandsYamlPath"] = commandsPath }, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var result = await remote.ExecuteCommandAsync(
            new CommandRequest("android-async-truncate", "android-tools.remote-commands.run.async_many", new JsonObject { ["execute"] = true }),
            CancellationToken.None);
        var payload = JsonNode.Parse(result.Output)!.AsObject();

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(payload["truncated"]!.GetValue<bool>());
        Assert.True(payload["stdoutBytes"]!.GetValue<int>() > payload["maxOutputBytesPerStream"]!.GetValue<int>());
        Assert.True(payload["stdoutLines"]!.GetValue<int>() >= 2000);
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
    public async Task AndroidTools_notifications_reports_actionable_degraded_state_when_endpoint_config_is_invalid()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-android-notifications-invalid", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settingsPath = Path.Combine(root, "settings.json");
        var keyPath = Path.Combine(root, "test-key");
        await File.WriteAllTextAsync(keyPath, "test key");
        await File.WriteAllTextAsync(settingsPath, $$"""
{
  "protocol": "https",
  "host": "",
  "port": 8888,
  "channel": "test",
  "pollIntervalSeconds": 5,
  "privateKeyPath": {{JsonSerializer.Serialize(keyPath)}},
  "keepWindowsBanners": false
}
""");

        var previous = Environment.GetEnvironmentVariable("MPT_TOOL_DATA_ROOT");
        Environment.SetEnvironmentVariable("MPT_TOOL_DATA_ROOT", root);
        try
        {
            var module = new AndroidToolsNotificationsModule();
            await module.InitializeAsync(
                CreateModuleContext("android-tools-suite", "android-tools.notifications", "android-notifications-invalid", ["notifications.remote"]),
                CancellationToken.None);

            var status = await module.GetStatusAsync(CancellationToken.None);
            var serverCheck = await module.ExecuteCommandAsync(
                new CommandRequest("android-notifications-server-check", "android-tools.notifications.server.check", new JsonObject()),
                CancellationToken.None);
            var inbox = await module.ExecuteCommandAsync(
                new CommandRequest("android-notifications-inbox", "android-tools.notifications.inbox.summary", new JsonObject()),
                CancellationToken.None);

            var serverPayload = JsonNode.Parse(serverCheck.Output)!.AsObject();
            var inboxPayload = JsonNode.Parse(inbox.Output)!.AsObject();
            var endpoint = inboxPayload["endpoint"]!.AsObject();

            Assert.Equal("degraded", status.State);
            Assert.Contains(status.Checks, check => check.Id == "notification.config" && !check.Ok && check.Message.Contains("host", StringComparison.OrdinalIgnoreCase));
            Assert.True(serverCheck.Success);
            Assert.False(serverPayload["found"]!.GetValue<bool>());
            Assert.Contains("host", serverPayload["message"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
            Assert.True(inbox.Success);
            Assert.False(endpoint["found"]!.GetValue<bool>());
            Assert.Contains("legacyHistory", inboxPayload.Select(item => item.Key));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MPT_TOOL_DATA_ROOT", previous);
        }
    }

    [Fact]
    public async Task AndroidTools_process_monitor_reports_actionable_degraded_state_when_watch_list_is_empty()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-android-process-empty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var processesPath = Path.Combine(root, "processes.json");
        await File.WriteAllTextAsync(processesPath, "[]");

        var previous = Environment.GetEnvironmentVariable("MPT_ANDROIDTOOLS_PROCESSES");
        Environment.SetEnvironmentVariable("MPT_ANDROIDTOOLS_PROCESSES", processesPath);
        try
        {
            var module = new AndroidToolsProcessMonitorModule();
            await module.InitializeAsync(
                CreateModuleContext("android-tools-suite", "android-tools.process-monitor", "android-process-empty", ["process.monitor"]),
                CancellationToken.None);

            var status = await module.GetStatusAsync(CancellationToken.None);
            var watchList = await module.ExecuteCommandAsync(
                new CommandRequest("android-process-watch-list", "android-tools.process-monitor.watch.list", new JsonObject()),
                CancellationToken.None);
            var summary = await module.ExecuteCommandAsync(
                new CommandRequest("android-process-summary", "android-tools.process-monitor.status.summary", new JsonObject()),
                CancellationToken.None);
            var invalidSave = await module.ExecuteCommandAsync(
                new CommandRequest("android-process-empty-save", "android-tools.process-monitor.watch.save", new JsonObject { ["processes"] = new JsonArray() }),
                CancellationToken.None);

            var watchPayload = JsonNode.Parse(watchList.Output)!.AsObject();
            var summaryPayload = JsonNode.Parse(summary.Output)!.AsObject();

            Assert.Equal("degraded", status.State);
            Assert.Contains(status.Checks, check => check.Id == "process.config" && !check.Ok && check.Message.Contains("empty", StringComparison.OrdinalIgnoreCase));
            Assert.True(watchList.Success);
            Assert.Equal("env:MPT_ANDROIDTOOLS_PROCESSES", watchPayload["source"]!.GetValue<string>());
            Assert.Empty(watchPayload["processes"]!.AsArray());
            Assert.True(summary.Success);
            Assert.Empty(summaryPayload["configured"]!.AsArray());
            Assert.Empty(summaryPayload["states"]!.AsArray());
            Assert.False(invalidSave.Success);
            Assert.Equal(MptErrorCodes.ValidationFailed, invalidSave.Error!.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MPT_ANDROIDTOOLS_PROCESSES", previous);
        }
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

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("ScreenEase status"), command => command.Id == "screenease.status.summary");
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "screenease" && card.State != "unsupported");
        Assert.True(status.Success);
        Assert.Contains("\"displayCount\"", status.Output);
        var statusPayload = JsonNode.Parse(status.Output)!.AsObject();
        var displayCount = statusPayload["displayCount"]?.GetValue<int>() ?? 0;
        if (displayCount == 0)
        {
            // CI runners have no displays; the module degrades without hardware.
            Assert.Equal("degraded", statusPayload["state"]!.GetValue<string>());
            Assert.False(apply.Success);
            return;
        }
        if (!apply.Success)
        {
            // CI runners may expose a virtual display without controllable hardware.
            Assert.Equal("degraded", statusPayload["state"]!.GetValue<string>());
            return;
        }

        var applied = JsonNode.Parse(apply.Output)!.AsObject();
        var profiles = JsonNode.Parse(list.Output)!.AsObject();

        Assert.True(apply.Success);
        Assert.Equal("low-blue-evening", applied["activeProfileId"]!.GetValue<string>());
        Assert.Equal("logical-only", applied["nativeHost"]!.AsObject()["state"]!.GetValue<string>());
        Assert.True(applied["effect"]!["enabled"]!.GetValue<bool>());
        Assert.True(list.Success);
        Assert.Equal("low-blue-evening", profiles["activeProfileId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScreenEase_inproc_module_receives_the_host_display_capability_across_the_plugin_boundary()
    {
        await using var host = new InProcDotNetModuleHost();
        var display = new RecordingDisplayService();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-screenease-capability", Guid.NewGuid().ToString("N"))),
            [host],
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["display.profile"] = display
            });

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest("screenease-capability-status", "screenease.status.summary", new JsonObject()),
            CancellationToken.None);
        var payload = JsonNode.Parse(result.Output)!.AsObject();
        var firstDisplay = Assert.Single(payload["displays"]!.AsArray())!.AsObject();

        Assert.True(result.Success);
        Assert.Equal(@"\\.\DISPLAY1", firstDisplay["id"]!.GetValue<string>());
        Assert.Equal("connected", firstDisplay["state"]!.GetValue<string>());
        Assert.True(payload["nativeHost"]!["available"]!.GetValue<bool>());
        Assert.DoesNotContain("No display capability provider was injected", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScreenEase_profile_apply_uses_an_available_writer_by_default()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-default-write"), CancellationToken.None);

        var result = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-apply-default", "screenease.profile.apply", new JsonObject { ["profileId"] = "night" }),
            CancellationToken.None);
        var payload = JsonNode.Parse(result.Output)!.AsObject();
        var nativeHost = payload["nativeHost"]!.AsObject();

        Assert.True(result.Success);
        var intent = Assert.Single(display.AppliedIntents);
        Assert.Equal("low-blue-evening", intent.ProfileId);
        Assert.Equal("low-blue-evening", payload["activeProfileId"]!.GetValue<string>());
        Assert.Equal("success", nativeHost["state"]!.GetValue<string>());
        Assert.True(payload["effect"]!["enabled"]!.GetValue<bool>());
        Assert.True(nativeHost["hardwareWriteRequested"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ScreenEase_migrates_the_legacy_three_profile_state_to_the_complete_mode_set()
    {
        var context = CreateScreenEaseContext("screenease-legacy-profile-migration");
        Directory.CreateDirectory(context.DataDirectory);
        File.WriteAllText(
            Path.Combine(context.DataDirectory, "screenease-state.json"),
            """
            {
              "ActiveProfileId": "day",
              "Profiles": [
                { "Id": "day", "Name": "Day", "Brightness": 85, "ColorTemperature": 6500 },
                { "Id": "focus", "Name": "Focus", "Brightness": 70, "ColorTemperature": 5200 },
                { "Id": "night", "Name": "Night", "Brightness": 45, "ColorTemperature": 4200 }
              ],
              "Rules": [],
              "NativeHost": { "Enabled": false, "Available": false, "Message": "legacy" },
              "UpdatedAt": "2026-01-01T00:00:00Z"
            }
            """);
        var module = new ScreenEaseModule(new RecordingDisplayService());

        await module.InitializeAsync(context, CancellationToken.None);
        var result = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-list-migrated", "screenease.profile.list", new JsonObject()),
            CancellationToken.None);
        var payload = JsonNode.Parse(result.Output)!.AsObject();
        var profiles = payload["profiles"]!.AsArray();

        Assert.True(result.Success);
        Assert.Equal(7, profiles.Count);
        Assert.Equal("low-blue-evening", payload["activeProfileId"]!.GetValue<string>());
        Assert.Contains(profiles, profile => profile!["id"]!.GetValue<string>() == "long-read" && profile["name"]!.GetValue<string>() == "长读柔光");
        Assert.Contains(profiles, profile => profile!["id"]!.GetValue<string>() == "day-office" && profile["name"]!.GetValue<string>() == "日间办公" && profile["brightness"]!.GetValue<int>() == 100);
    }

    [Fact]
    public async Task ScreenEase_profile_apply_calls_display_writer_when_requested()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-explicit-write"), CancellationToken.None);

        var result = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-apply-native", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "night",
                ["displayId"] = @"\\.\DISPLAY1",
                ["hardwareWrite"] = true
            }),
            CancellationToken.None);
        var payload = JsonNode.Parse(result.Output)!.AsObject();
        var nativeHost = payload["nativeHost"]!.AsObject();
        var intent = Assert.Single(display.AppliedIntents);

        Assert.True(result.Success);
        Assert.Equal("low-blue-evening", intent.ProfileId);
        Assert.Equal(@"\\.\DISPLAY1", intent.DisplayId);
        Assert.Equal(75, intent.Brightness);
        Assert.Equal(3700, intent.ColorTemperature);
        Assert.Equal("success", nativeHost["state"]!.GetValue<string>());
        Assert.True(nativeHost["success"]!.GetValue<bool>());
        Assert.True(nativeHost["hardwareWriteRequested"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ScreenEase_profile_plan_honors_the_selected_display()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-targeted-plan"), CancellationToken.None);

        var selected = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-plan-selected", "screenease.profile.plan", new JsonObject
            {
                ["profileId"] = "night",
                ["displayId"] = @"\\.\DISPLAY1"
            }),
            CancellationToken.None);
        var missing = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-plan-missing", "screenease.profile.plan", new JsonObject
            {
                ["profileId"] = "night",
                ["displayId"] = @"\\.\DISPLAY9"
            }),
            CancellationToken.None);
        var selectedPayload = JsonNode.Parse(selected.Output)!.AsObject();
        var missingPayload = JsonNode.Parse(missing.Output)!.AsObject();

        Assert.True(selected.Success);
        Assert.Equal(@"\\.\DISPLAY1", selectedPayload["targetDisplayId"]!.GetValue<string>());
        Assert.Single(selectedPayload["expectedChange"]!["actions"]!.AsArray());
        Assert.Empty(missingPayload["expectedChange"]!["actions"]!.AsArray());
    }

    [Fact]
    public async Task ScreenEase_resolves_display_provider_from_module_context()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule();
        await module.InitializeAsync(CreateScreenEaseContext("screenease-context-display", display), CancellationToken.None);

        var result = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-context-native", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "night",
                ["displayId"] = @"\\.\DISPLAY1",
                ["hardwareWrite"] = true
            }),
            CancellationToken.None);
        var payload = JsonNode.Parse(result.Output)!.AsObject();
        var nativeHost = payload["nativeHost"]!.AsObject();
        var intent = Assert.Single(display.AppliedIntents);

        Assert.True(result.Success);
        Assert.Equal("low-blue-evening", intent.ProfileId);
        Assert.Equal(@"\\.\DISPLAY1", intent.DisplayId);
        Assert.Equal("success", nativeHost["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScreenEase_has_no_second_hidden_native_writer_toggle()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-configured-write"), CancellationToken.None);

        var commands = await module.ListCommandsAsync(CancellationToken.None);
        var apply = await module.ExecuteCommandAsync(
            new CommandRequest("screenease-apply-configured", "screenease.profile.apply", new JsonObject { ["profileId"] = "focus" }),
            CancellationToken.None);
        var applied = JsonNode.Parse(apply.Output)!.AsObject();

        Assert.DoesNotContain(commands, command => command.Id == "screenease.native-writer.configure");
        Assert.True(apply.Success);
        Assert.Single(display.AppliedIntents);
        Assert.Equal("success", applied["nativeHost"]!.AsObject()["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task DoubaoAgent_inproc_module_reports_planner_tool_and_mcp_services()
    {
        await using var planner = TestHttpFacadeServer.Start();
        await using var tool = TestHttpFacadeServer.Start();
        await using var mcp = TestHttpFacadeServer.Start();
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-doubao-agent", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var summary = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-status", "doubao-agent.status.summary", DoubaoArgs(planner.BaseUrl, tool.BaseUrl, mcp.BaseUrl)),
            CancellationToken.None);
        var plannerHealth = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-planner", "doubao-agent.planner.health", DoubaoArgs(planner.BaseUrl, tool.BaseUrl, mcp.BaseUrl)),
            CancellationToken.None);
        var selfTest = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-self-test", "doubao-agent.self-test", DoubaoArgs(planner.BaseUrl, tool.BaseUrl, mcp.BaseUrl)),
            CancellationToken.None);
        var logs = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-logs", "doubao-agent.logs.summary", new JsonObject()),
            CancellationToken.None);

        var statusPayload = JsonNode.Parse(summary.Output)!.AsObject();
        var services = statusPayload["services"]!.AsArray().Select(item => item!.AsObject()["id"]!.GetValue<string>()).ToArray();
        var selfTestPayload = JsonNode.Parse(selfTest.Output)!.AsObject();
        var logsPayload = JsonNode.Parse(logs.Output)!.AsObject();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("Doubao"), command => command.Id == "doubao-agent.planner.health");
        Assert.Contains(runtime.ListCommands("Doubao"), command => command.Id == "doubao-agent.tool.health");
        Assert.Contains(runtime.ListCommands("Doubao"), command => command.Id == "doubao-agent.mcp.health");
        Assert.True(summary.Success);
        Assert.Contains(statusPayload["state"]!.GetValue<string>(), new[] { "running", "degraded" });
        Assert.InRange(statusPayload["runningServices"]!.GetValue<int>(), 0, 3);
        Assert.Contains("planner", services);
        Assert.Contains("tool", services);
        Assert.Contains("mcp", services);
        var plannerService = statusPayload["services"]!.AsArray()
            .Select(item => item!.AsObject())
            .Single(item => item["id"]!.GetValue<string>() == "planner");
        Assert.Equal(plannerService["ok"]!.GetValue<bool>(), plannerHealth.Success);
        Assert.True(selfTest.Success);
        Assert.DoesNotContain("abc123", selfTest.Output);
        Assert.DoesNotContain("hunter2", selfTest.Output);
        Assert.Contains("token=****", selfTestPayload["redaction"]!.GetValue<string>());
        Assert.True(logs.Success);
        Assert.True(logsPayload["fileCount"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task DoubaoAgent_inproc_module_ignores_endpoint_overrides_and_uses_the_canonical_chain()
    {
        await using var planner = TestHttpFacadeServer.Start();
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-doubao-agent-partial", Guid.NewGuid().ToString("N"))),
            [host]);
        var unavailable = "http://127.0.0.1:1";

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var args = DoubaoArgs(planner.BaseUrl, unavailable, unavailable);
        var summary = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-partial-status", "doubao-agent.status.summary", args),
            CancellationToken.None);
        var toolHealth = await runtime.ExecuteCommandAsync(
            new CommandRequest("doubao-tool-degraded", "doubao-agent.tool.health", args),
            CancellationToken.None);

        var statusPayload = JsonNode.Parse(summary.Output)!.AsObject();
        var services = statusPayload["services"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        var ports = statusPayload["ports"]!.AsObject();
        var toolDetails = toolHealth.Success
            ? JsonNode.Parse(toolHealth.Output)!.AsObject()
            : toolHealth.Error!.Details!;

        Assert.True(summary.Success);
        Assert.Equal(3, statusPayload["totalServices"]!.GetValue<int>());
        Assert.Equal(38189, ports["planner"]!.GetValue<int>());
        Assert.Equal(38102, ports["tool"]!.GetValue<int>());
        Assert.Equal(38080, ports["mcp"]!.GetValue<int>());
        Assert.Contains(services, service => service["id"]!.GetValue<string>() == "planner");
        Assert.Contains(services, service => service["id"]!.GetValue<string>() == "tool");
        Assert.Contains(services, service => service["id"]!.GetValue<string>() == "mcp");
        Assert.Equal("tool", toolDetails["id"]!.GetValue<string>());
        Assert.Equal(toolHealth.Success, toolDetails["ok"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(toolDetails["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task SmartBird_inproc_module_reports_facade_config_and_hardware_degradation()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-smartbird", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var status = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-status", "smartbird-thermostat.status.summary", SmartBirdArgs(SmartBirdThermostatModule.CanonicalBaseUrl)),
            CancellationToken.None);
        var events = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-events", "smartbird-thermostat.events.list", SmartBirdArgs(SmartBirdThermostatModule.CanonicalBaseUrl)),
            CancellationToken.None);
        var configSave = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-config-save", "smartbird-thermostat.config.save", new JsonObject
            {
                ["targetTemperatureC"] = 52,
                ["pollIntervalSeconds"] = 45
            }),
            CancellationToken.None);
        var config = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-config-get", "smartbird-thermostat.config.get", SmartBirdArgs(SmartBirdThermostatModule.CanonicalBaseUrl)),
            CancellationToken.None);
        var diagnostics = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-hardware", "smartbird-thermostat.hardware.diagnostics", SmartBirdArgs(SmartBirdThermostatModule.CanonicalBaseUrl)),
            CancellationToken.None);
        var restart = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-restart", "smartbird-thermostat.service.restart", SmartBirdArgs(SmartBirdThermostatModule.CanonicalBaseUrl)),
            CancellationToken.None);
        var selfTest = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-self-test", "smartbird-thermostat.self-test", SmartBirdArgs(SmartBirdThermostatModule.CanonicalBaseUrl)),
            CancellationToken.None);
        var logs = await runtime.ExecuteCommandAsync(
            new CommandRequest("smartbird-logs", "smartbird-thermostat.logs.summary", SmartBirdArgs(SmartBirdThermostatModule.CanonicalBaseUrl)),
            CancellationToken.None);

        var statusPayload = JsonNode.Parse(status.Output)!.AsObject();
        var statusChecks = statusPayload["checks"]!.AsArray().Select(item => item!.AsObject()).ToArray();
        var eventPayload = JsonNode.Parse(events.Output)!.AsObject();
        var configPayload = JsonNode.Parse(config.Output)!.AsObject();
        var diagnosticsPayload = JsonNode.Parse(diagnostics.Output)!.AsObject();
        var logsPayload = JsonNode.Parse(logs.Output)!.AsObject();

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("SmartBird"), command => command.Id == "smartbird-thermostat.events.list");
        Assert.Contains(runtime.ListCommands("SmartBird"), command => command.Id == "smartbird-thermostat.hardware.diagnostics");
        Assert.True(status.Success);
        Assert.Equal("degraded", statusPayload["state"]!.GetValue<string>());
        Assert.Contains(statusChecks, check => check["id"]!.GetValue<string>() == "smartbird.status");
        Assert.Contains(statusChecks, check => check["id"]!.GetValue<string>() == "energy-server.status");
        Assert.True(events.Success);
        Assert.True(eventPayload["events"]!.AsArray().Count >= 0);
        Assert.True(configSave.Success);
        Assert.True(config.Success);
        Assert.Equal(52, configPayload["localConfig"]!.AsObject()["targetTemperatureC"]!.GetValue<double>());
        Assert.Equal(SmartBirdThermostatModule.CanonicalBaseUrl, configPayload["localConfig"]!.AsObject()["baseUrl"]!.GetValue<string>());
        Assert.Equal(SmartBirdThermostatModule.CanonicalScheduledTaskName, configPayload["localConfig"]!.AsObject()["scheduledTaskName"]!.GetValue<string>());
        Assert.True(diagnostics.Success);
        Assert.Equal("degraded", diagnosticsPayload["state"]!.GetValue<string>());
        Assert.False(restart.Success);
        Assert.Equal("failed", restart.State);
        Assert.Equal(MptErrorCodes.RuntimeUnavailable, restart.Error!.Code);
        Assert.True(selfTest.Success);
        Assert.DoesNotContain("abc123", selfTest.Output);
        Assert.Contains("token=****", selfTest.Output);
        Assert.True(logs.Success);
        Assert.True(logsPayload["fileCount"]!.GetValue<int>() >= 0);
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
}
