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
    public void RuntimePaths_create_normalizes_relative_root_to_absolute_paths()
    {
        var current = Directory.GetCurrentDirectory();
        var baseRoot = Path.Combine(Path.GetTempPath(), "mpt-runtimepaths-relative", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseRoot);
        try
        {
            Directory.SetCurrentDirectory(baseRoot);
            var paths = RuntimePaths.Create(Path.Combine("relative", "data-root"));

            Assert.True(Path.IsPathFullyQualified(paths.Root));
            Assert.True(Path.IsPathFullyQualified(paths.Settings));
            Assert.True(Path.IsPathFullyQualified(paths.Logs));
            Assert.True(Path.IsPathFullyQualified(paths.State));
            Assert.True(Path.IsPathFullyQualified(paths.Packages));
            Assert.StartsWith(baseRoot, paths.Root, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.SetCurrentDirectory(current);
        }
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
    public void Module_state_allowlist_profile_is_seeded_once_and_remains_user_editable()
    {
        var stateRoot = Path.Combine(
            Path.GetTempPath(),
            "mpt-runtime-module-allowlist",
            Guid.NewGuid().ToString("N"));
        var initial = new ModuleStateStore(stateRoot, ["android-tools.notifications"]);

        Assert.True(initial.IsEnabled("android-tools.notifications"));
        Assert.False(initial.IsEnabled("android-tools.remote-commands"));
        Assert.True(File.Exists(Path.Combine(stateRoot, "modules.enabled.json")));

        initial.SetModuleEnabled("android-tools.remote-commands", enabled: true);
        var reloaded = new ModuleStateStore(stateRoot);

        Assert.True(reloaded.IsEnabled("android-tools.notifications"));
        Assert.True(reloaded.IsEnabled("android-tools.remote-commands"));
        Assert.False(reloaded.IsEnabled("process-monitor"));
    }

    [Fact]
    public void Mac_platform_pack_exposes_native_notification_web_keychain_and_launchd_capabilities()
    {
        IPlatformPack platform = new MacPlatformPack();

        Assert.True(platform.Capabilities.Resolve("notification.desktop").Supported);
        Assert.Equal("UserNotifications", platform.Capabilities.Resolve("notification.desktop").Provider);
        Assert.True(platform.Capabilities.Resolve("web.surface").Supported);
        Assert.Equal("WKWebView", platform.Capabilities.Resolve("web.surface").Provider);
        Assert.True(platform.Capabilities.Resolve("secret.store").Supported);
        Assert.True(platform.Capabilities.Resolve("autostart.user").Supported);
        Assert.True(platform.Capabilities.Resolve("service.user").Supported);
    }

    [Fact]
    public void Shell_navigation_command_is_not_runtime_fake_success()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var request = new CommandRequest("same-invocation", "doubao-agent.open", new JsonObject());

        var first = runtime.ExecuteCommand(request);
        var second = runtime.ExecuteCommand(request);

        Assert.Equal(first.State, second.State);
        Assert.Equal(first.Error?.Code, second.Error?.Code);
        Assert.False(second.Success);
        Assert.Equal(MptErrorCodes.UnsupportedTransport, second.Error!.Code);
        Assert.Contains("Shell navigation action", second.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Open request recorded", second.Error.Message, StringComparison.Ordinal);
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
    public async Task Runtime_command_stream_emits_progress_and_final_result()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var events = new List<CommandProgressEvent>();

        await foreach (var evt in runtime.ExecuteCommandStreamAsync(
            new CommandRequest("settings-stream", "screenease.settings.read", new JsonObject()),
            CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Collection(
            events,
            evt =>
            {
                Assert.Equal("accepted", evt.State);
                Assert.False(evt.Terminal);
            },
            evt =>
            {
                Assert.Equal("running", evt.State);
                Assert.False(evt.Terminal);
            },
            evt =>
            {
                Assert.Equal("succeeded", evt.State);
                Assert.True(evt.Terminal);
                Assert.NotNull(evt.FinalResult);
                Assert.Contains("Settings revision", evt.FinalResult!.Output);
            });
    }

    [Fact]
    public async Task Runtime_cancel_command_stops_running_invocation()
    {
        var transport = new RecordingSettingsTransportRuntime("inproc-dotnet")
        {
            BlockCommandUntilCancelled = true
        };
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-command-cancel", Guid.NewGuid().ToString("N"))),
            [transport]);
        runtime.Load(Path.Combine(Root, "modules"));
        var request = new CommandRequest("cancel-test", "screenease.native-writer.status", new JsonObject());

        var execution = runtime.ExecuteCommandAsync(request, CancellationToken.None);
        await transport.CommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancellation = runtime.CancelCommand(request.InvocationId);
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancellation.Accepted);
        Assert.Equal("host-cancelling-module-rejected", cancellation.State);
        Assert.Contains("module response", cancellation.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("cancelled", result.State);
        Assert.False(result.Success);
        Assert.Equal(MptErrorCodes.CommandCancelled, result.Error!.Code);
        Assert.Contains(runtime.ListCommandHistory(), entry =>
            entry.InvocationId == request.InvocationId &&
            entry.State == "cancelled" &&
            entry.Summary.Contains("cancelled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Production_commands_expose_parameter_metadata()
    {
        var screen = new ScreenEaseModule(new RecordingDisplayService());
        await screen.InitializeAsync(CreateScreenEaseContext("screenease-parameters"), CancellationToken.None);
        var screenCommands = await screen.ListCommandsAsync(CancellationToken.None);
        var screenApply = screenCommands.Single(command => command.Id == "screenease.profile.apply");

        var commandsRoot = Path.Combine(Path.GetTempPath(), "mpt-android-parameters-commands", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(commandsRoot);
        var commandsPath = Path.Combine(commandsRoot, "commands.yaml");
        await File.WriteAllTextAsync(commandsPath, """
commands:
  - id: shell_echo
    label: Shell Echo
    command: echo shell
    description: Shell command with explicit execute gate.
    type: shell
""");
        var remote = new AndroidToolsRemoteCommandsModule();
        await remote.InitializeAsync(CreateModuleContext("android-tools-suite", "android-tools.remote-commands", "android-parameters", ["remote.commands"]), CancellationToken.None);
        await remote.ApplySettingsAsync(
            new SettingsSnapshotDocument("android-tools.remote-commands", 2, new JsonObject { ["commandsYamlPath"] = commandsPath }, DateTimeOffset.UtcNow),
            CancellationToken.None);
        var androidShell = (await remote.ListCommandsAsync(CancellationToken.None)).Single(command => command.Id == "android-tools.remote-commands.run.shell_echo");

        var adb = new AdbForwarderModule();
        await adb.InitializeAsync(CreateModuleContext("adb-forwarder", "adb-forwarder", "adb-parameters", ["commands"]), CancellationToken.None);
        var apply = (await adb.ListCommandsAsync(CancellationToken.None)).Single(command => command.Id == "adb-forwarder.portproxy.apply");

        var doubaoHealth = StaticCommand("modules/doubao-agent/commands.index.json", "doubao-agent.status.summary");
        var restart = StaticCommand("modules/smartbird-thermostat/commands.index.json", "smartbird-thermostat.service.restart");

        Assert.Contains(screenApply.Parameters!, parameter => parameter.Id == "profileId");
        Assert.Contains(screenApply.Parameters!, parameter => parameter.Id == "hardwareWrite" && parameter.Type == "boolean");
        Assert.Contains(androidShell.Parameters!, parameter => parameter.Id == "execute" && parameter.Type == "boolean");
        Assert.Contains(androidShell.Parameters!, parameter => parameter.Id == "timeoutMs" && parameter.Type == "number");
        Assert.False(doubaoHealth.ContainsKey("parameters"));
        Assert.Contains(restart["parameters"]!.AsArray(), parameter => parameter!["id"]!.GetValue<string>() == "reason" && parameter["type"]!.GetValue<string>() == "multiline");
        Assert.Contains(apply.Parameters!, parameter => parameter.Id == "reason" && parameter.Type == "multiline");

        static JsonObject StaticCommand(string relativePath, string commandId)
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, relativePath)))!.AsObject();
            return root["commands"]!.AsArray()
                .Select(item => item!.AsObject())
                .Single(command => command["id"]!.GetValue<string>() == commandId);
        }
    }

    [Fact]
    public async Task Runtime_collects_module_event_stream_into_host_event_bus()
    {
        _ = typeof(SampleDotNetModule).Assembly;
        await using var host = new InProcDotNetModuleHost();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-module-events", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        var first = await runtime.CollectModuleEventsAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        var second = await runtime.CollectModuleEventsAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        var evt = Assert.Single(runtime.HostEventsSince(0).Where(item => item.Type == "sample.heartbeat"));

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal("sample.dotnet", evt.ModuleId);
        Assert.Equal(1UL, evt.Payload["moduleEventSeq"]!.GetValue<ulong>());
        Assert.Equal("sample module event stream is active", evt.Payload["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Runtime_module_event_cursor_prevents_duplicate_publication()
    {
        _ = typeof(SampleDotNetModule).Assembly;
        var transport = new DuplicateEventTransportRuntime();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-module-events-duplicates", Guid.NewGuid().ToString("N"))),
            [transport]);

        runtime.Load(Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet"));
        var first = await runtime.CollectModuleEventsAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        var second = await runtime.CollectModuleEventsAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None);
        var events = runtime.HostEventsSince(0).Where(item => item.Type == "duplicate.test").ToArray();

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal([0UL, 1UL], transport.RequestedCursors);
        var evt = Assert.Single(events);
        Assert.Equal(1UL, evt.Payload["moduleEventSeq"]!.GetValue<ulong>());
    }

    [Fact]
    public async Task Runtime_collects_production_module_events_and_notifications()
    {
        await using var inproc = new InProcDotNetModuleHost();
        await using var grpc = new GrpcIpcModuleRuntime();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-production-events", Guid.NewGuid().ToString("N"))),
            [inproc, grpc]);

        runtime.Load(Path.Combine(Root, "modules"));
        var count = await runtime.CollectModuleEventsAsync(TimeSpan.FromMilliseconds(1500), CancellationToken.None);
        var events = runtime.HostEventsSince(0);
        var productionModuleIds = events
            .Where(evt => evt.ModuleId is
                "adb-forwarder" or
                "screenease" or
                "doubao-agent" or
                "smartbird-thermostat" or
                "android-tools.notifications" or
                "android-tools.process-monitor" or
                "android-tools.remote-commands")
            .Select(evt => evt.ModuleId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var notifications = runtime.ListNotifications();

        Assert.True(count >= 4, $"Expected at least 4 production module events, got {count}.");
        Assert.True(productionModuleIds.Length >= 4, string.Join(", ", events.Select(evt => $"{evt.ModuleId}:{evt.Type}")));
        Assert.Contains(events, evt => evt.Type == "portproxy.changed");
        Assert.Contains(events, evt => evt.Type == "profile.applied");
        Assert.Contains(events, evt => evt.Type is "server.disconnected" or "message.received");
        Assert.NotEmpty(notifications);
    }
}
