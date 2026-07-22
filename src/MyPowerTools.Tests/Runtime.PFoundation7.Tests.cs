using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;
using MyPowerTools.Runner;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using EventCursor = MyPowerTools.Abstractions.EventCursor;
using HostProto = MyPowerTools.Protocol.HostControl.V1;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using ModuleStatusSnapshot = MyPowerTools.Abstractions.ModuleStatusSnapshot;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using MptModuleEvent = MyPowerTools.Abstractions.MptModuleEvent;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;
using SettingsSchemaDocument = MyPowerTools.Abstractions.SettingsSchemaDocument;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    [Fact]
    [Trait("Foundation", "P7")]
    public async Task Runner_hotkey_sync_reregisters_gesture_updates_args_and_follows_enable_state()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-p7-hotkeys", Guid.NewGuid().ToString("N"));
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(dataRoot),
            []);

        runtime.Load(Path.Combine(Root, "modules"));
        await using var hotkeys = new RecordingHotkeyService();
        var synchronizer = new RunnerHotkeySynchronizer(hotkeys, runtime);
        var target = Assert.Single(runtime.ListHotkeyDiagnostics()
            .Where(binding => binding.Id == "screenease.toggle-enabled"));

        var initial = await synchronizer.SyncAsync(CancellationToken.None);

        Assert.Empty(initial);
        Assert.Empty(hotkeys.Registered);

        await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch(
                "screenease",
                runtime.GetSettings("screenease").Revision,
                new JsonObject
                {
                    ["$hotkeys"] = new JsonArray(new JsonObject
                    {
                        ["id"] = target.Id,
                        ["gesture"] = target.DefaultGesture,
                        ["reset"] = false,
                        ["disabled"] = false
                    })
                }),
            CancellationToken.None);
        var firstRegistration = await synchronizer.SyncAsync(CancellationToken.None);
        var original = Assert.Single(runtime.ListHotkeyBindings().Where(binding => binding.ModuleId == "screenease"));
        Assert.Contains(firstRegistration, item => item.Id == original.Id && item.Operation == "register");
        Assert.Equal(original.Gesture, Assert.Single(hotkeys.Registered.Values).Gesture);

        var overrideGesture = original.Gesture.Equals("Ctrl+Alt+F12", StringComparison.OrdinalIgnoreCase)
            ? "Ctrl+Alt+F11"
            : "Ctrl+Alt+F12";
        await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch(
                "screenease",
                runtime.GetSettings("screenease").Revision,
                new JsonObject
                {
                    ["$hotkeys"] = new JsonArray(new JsonObject
                    {
                        ["id"] = original.Id,
                        ["gesture"] = overrideGesture,
                        ["reset"] = false,
                        ["disabled"] = false,
                        ["commandArgs"] = new JsonObject { ["profileId"] = "night" }
                    })
                }),
            CancellationToken.None);

        var changed = await synchronizer.SyncAsync(CancellationToken.None);
        var request = synchronizer.CreateCommandRequest(new HotkeyInvocation(original.Id, overrideGesture, original.Scope, DateTimeOffset.UtcNow));

        Assert.Contains(changed, item => item.Id == original.Id && item.Operation == "reregister-unregister");
        Assert.Contains(changed, item => item.Id == original.Id && item.Operation == "reregister-register");
        Assert.Equal(overrideGesture, hotkeys.Registered[original.Id].Gesture);
        Assert.NotNull(request);
        Assert.Equal(original.CommandId, request.CommandId);
        Assert.Equal("night", request.Args["profileId"]!.GetValue<string>());

        await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch(
                "screenease",
                runtime.GetSettings("screenease").Revision,
                new JsonObject
                {
                    ["$hotkeys"] = new JsonArray(new JsonObject
                    {
                        ["id"] = original.Id,
                        ["gesture"] = original.DefaultGesture,
                        ["reset"] = true,
                        ["disabled"] = false
                    })
                }),
            CancellationToken.None);

        var reset = await synchronizer.SyncAsync(CancellationToken.None);
        Assert.Contains(reset, item => item.Id == original.Id && item.Operation == "unregister");
        Assert.DoesNotContain(original.Id, hotkeys.Registered.Keys);
        Assert.Equal("disabled", runtime.ListHotkeyDiagnostics().Single(binding => binding.Id == original.Id).State);

        await runtime.UpdateSettingsWithApplyAsync(
            new SettingsPatch(
                "screenease",
                runtime.GetSettings("screenease").Revision,
                new JsonObject
                {
                    ["$hotkeys"] = new JsonArray(new JsonObject
                    {
                        ["id"] = original.Id,
                        ["gesture"] = original.DefaultGesture,
                        ["reset"] = false,
                        ["disabled"] = false
                    })
                }),
            CancellationToken.None);
        var restored = await synchronizer.SyncAsync(CancellationToken.None);
        Assert.Contains(restored, item => item.Id == original.Id && item.Operation == "register");
        Assert.Equal(original.DefaultGesture, hotkeys.Registered[original.Id].Gesture);

        await runtime.SetModuleEnabledAsync("screenease", enabled: false, CancellationToken.None);
        var disabled = await synchronizer.SyncAsync(CancellationToken.None);

        Assert.Contains(disabled, item => item.Id == original.Id && item.Operation == "unregister");
        Assert.DoesNotContain(original.Id, hotkeys.Registered.Keys);

        await runtime.SetModuleEnabledAsync("screenease", enabled: true, CancellationToken.None);
        var enabled = await synchronizer.SyncAsync(CancellationToken.None);

        Assert.Contains(enabled, item => item.Id == original.Id && item.Operation == "register");
        Assert.Equal(original.DefaultGesture, hotkeys.Registered[original.Id].Gesture);
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public async Task Module_event_alert_creates_notification_event_and_shell_refresh_plan()
    {
        var transport = new AlertEventTransportRuntime();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-p7-alert-events", Guid.NewGuid().ToString("N"))),
            [transport]);

        runtime.Load(Path.Combine(Root, "modules", "screenease"));

        var collected = await runtime.CollectModuleEventsAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);
        var notification = Assert.Single(runtime.ListNotifications());
        var hostEvents = runtime.HostEventsSince(0);
        var created = Assert.Single(hostEvents.Where(evt => evt.Type == "notification.created"));
        var refresh = ShellPageRefreshRouter.Route("Notifications", new HostProto.HostEvent
        {
            Seq = created.Seq,
            Type = created.Type,
            SourceId = created.ModuleId
        });

        Assert.Equal(1, collected);
        Assert.Equal("watch.alert", Assert.Single(hostEvents.Where(evt => evt.Type == "watch.alert")).Type);
        Assert.Equal("Disk pressure", notification.Title);
        Assert.Equal(notification.Id, created.Payload["notificationId"]!.GetValue<string>());
        Assert.True(refresh.ReloadNotifications);
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public async Task Runtime_persists_module_event_cursor_and_history_across_reload()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-p7-event-store", Guid.NewGuid().ToString("N"));
        var transport = new AlertEventTransportRuntime();
        await using (var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(dataRoot),
            [transport]))
        {
            runtime.Load(Path.Combine(Root, "modules", "screenease"));
            var collected = await runtime.CollectModuleEventsAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);

            Assert.Equal(1, collected);
        }

        var store = new ModuleEventStore(Path.Combine(dataRoot, "state"));
        Assert.Equal(1UL, store.CursorFor("screenease"));
        Assert.Contains(store.ReadHistory("screenease"), evt => evt.Type == "watch.alert" && evt.Seq == 1UL);

        var reloadedTransport = new AlertEventTransportRuntime();
        await using var reloaded = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(dataRoot),
            [reloadedTransport]);

        reloaded.Load(Path.Combine(Root, "modules", "screenease"));
        var duplicate = await reloaded.CollectModuleEventsAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Equal(0, duplicate);
        Assert.Equal([1UL], reloadedTransport.RequestedCursors);
        Assert.DoesNotContain(reloaded.HostEventsSince(0), evt => evt.Type == "watch.alert");
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public async Task Invocation_execution_cache_deduplicates_completed_results_and_evicts_old_entries()
    {
        var now = DateTimeOffset.UtcNow;
        var factoryCalls = 0;
        var cache = new InvocationExecutionCache(2, TimeSpan.FromSeconds(5), () => now);

        var first = cache.GetOrAdd("same", () =>
        {
            factoryCalls++;
            return Task.FromResult(CacheResult("same", "first"));
        });
        var duplicate = cache.GetOrAdd("same", () =>
        {
            factoryCalls++;
            return Task.FromResult(CacheResult("same", "duplicate"));
        });

        Assert.Same(first, duplicate);
        Assert.Equal("first", (await duplicate).Output);
        Assert.Equal(1, factoryCalls);
        Assert.True(cache.IsCompleted("same"));

        await cache.GetOrAdd("second", () => Task.FromResult(CacheResult("second", "second")));
        await cache.GetOrAdd("third", () => Task.FromResult(CacheResult("third", "third")));

        Assert.True(cache.Count <= 2);

        now = now.AddSeconds(6);
        Assert.False(cache.IsCompleted("same"));
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public async Task Invocation_execution_cache_detaches_completed_factory_capture_and_preserves_idempotent_result()
    {
        var cache = new InvocationExecutionCache(4, TimeSpan.FromMinutes(5));
        var (expected, capture) = await CompleteCapturedCacheInvocationAsync(cache);

        await AssertCacheCaptureCollectedAsync(capture);
        var duplicateFactoryCalls = 0;
        var duplicate = await cache.GetOrAdd("detached", () =>
        {
            duplicateFactoryCalls++;
            return Task.FromResult(CacheResult("detached", "duplicate"));
        });

        Assert.Equal(expected, duplicate);
        Assert.Equal(0, duplicateFactoryCalls);
        Assert.True(cache.IsCompleted("detached"));
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public async Task Runtime_diagnostics_split_module_transport_and_tool_runtime_state()
    {
        await using var inproc = new InProcDotNetModuleHost();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-p7-diagnostics", Guid.NewGuid().ToString("N"))),
            [inproc]);

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        var running = runtime.GetRuntimeDiagnostics().Modules.Single(module => module.ModuleId == "screenease");
        Assert.Equal("enabled", running.ModuleEnabledState);
        Assert.Equal("loaded", running.TransportActiveState);
        Assert.Equal("partial", running.ToolRuntimeState);

        await runtime.SetModuleEnabledAsync("screenease", enabled: false, CancellationToken.None);
        var disabled = runtime.GetRuntimeDiagnostics().Modules.Single(module => module.ModuleId == "screenease");
        Assert.Equal("disabled", disabled.ModuleEnabledState);
        Assert.Equal("inactive", disabled.TransportActiveState);
        Assert.Equal("disabled", disabled.ToolRuntimeState);

        var productionModules = runtime.GetRuntimeDiagnostics().Modules
            .Where(module => module.ModuleId is "doubao-agent" or "smartbird-thermostat" or "android-tools.remote-commands")
            .ToArray();
        Assert.NotEmpty(productionModules);
        Assert.All(productionModules, module => Assert.False(string.IsNullOrWhiteSpace(module.ToolRuntimeState)));
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public async Task Runtime_streaming_sidecar_crash_emits_runtime_unavailable_terminal_failure()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = $"mpt-p7-stream-crash-{Guid.NewGuid():N}";
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-p7-grpc-stream-crash", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-p7-runtime-grpc-stream-crash", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        var events = new List<CommandProgressEvent>();
        await foreach (var evt in runtime.ExecuteCommandStreamAsync(
                           new CommandRequest("stream-crash", "sample.grpc.stream-crash", new JsonObject()),
                           CancellationToken.None))
        {
            events.Add(evt);
        }

        Assert.Contains(events, evt => evt.Message == "sample stream started" && !evt.Terminal);
        var terminal = Assert.Single(events.Where(evt => evt.Terminal));
        Assert.Equal("failed", terminal.State);
        Assert.NotNull(terminal.FinalResult);
        Assert.Equal(MptErrorCodes.RuntimeUnavailable, terminal.FinalResult.Error!.Code);
        Assert.Contains("sidecar became unavailable", terminal.FinalResult.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public async Task Grpc_cancel_without_active_host_does_not_start_sidecar()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = $"mpt-p7-cancel-missing-{Guid.NewGuid():N}";
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-p7-grpc-cancel-missing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        var host = new GrpcIpcModuleRuntime();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-p7-runtime-grpc-cancel-missing", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);

        var result = await host.CancelCommandAsync(
            module,
            CreateModuleContext("sample-grpc-sidecar", "sample.grpc", "Sample gRPC Sidecar", ["status", "commands"]),
            "missing-invocation",
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("module-cancel-not-running", result.State);
        Assert.Empty(host.GetProcessDiagnostics());
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public void Grpc_stream_cleanup_exceptions_are_suppressed_after_terminal_failure()
    {
        var source = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.ModuleHost.GrpcIpc", "GrpcIpcModuleHost.cs"));

        Assert.Contains("terminalFailureEmitted", source, StringComparison.Ordinal);
        Assert.Contains("catch when (terminalFailureEmitted)", source, StringComparison.Ordinal);
        Assert.Contains("await RemoveHostAsync(poolKey)", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public async Task Shell_cancel_result_keeps_module_acceptance_or_rejection_evidence()
    {
        var item = new CommandItemViewModel(
            "sample.long",
            "sample",
            "Long command",
            "Runs until cancelled",
            "normal",
            false,
            "Sample",
            "",
            "",
            false,
            WaitingCommandStream,
            invocationId => Task.FromResult(new CommandCancellationStatus(
                true,
                invocationId,
                "host-cancelling-module-rejected",
                "Host cancellation requested; module rejected cancellation.")));

        var execution = item.ExecuteAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!item.CanCancel && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.True(item.CanCancel);
        await item.CancelAsync();
        await execution.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(item.ProgressEvents, evt =>
            evt.StateLabel.Contains("cancel rejected", StringComparison.OrdinalIgnoreCase) &&
            evt.Message.Contains("module rejected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Foundation", "P7")]
    public void Ui_surface_gate_scans_component_csharp_layer_and_requires_tokens()
    {
        var gateSource = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.UI.Testing", "UiSurfaceGate.cs"));
        var controlsSource = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.UI.Primitives", "MptControls.cs"));
        var themeSource = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.UI", "MptTheme.cs"));
        var issues = new UiSurfaceGate()
            .CheckShellSource(Root)
            .Where(issue => issue.Severity == "error")
            .ToArray();

        Assert.Contains("Directory.EnumerateFiles(uiRoot, \"*.cs\"", gateSource);
        Assert.DoesNotContain("new Thickness(", controlsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new CornerRadius(", controlsSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Brush.Parse", themeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Brushes.", themeSource, StringComparison.Ordinal);
        Assert.Contains("MptThemeTokens", themeSource);
        Assert.Empty(issues);
    }

    private static CommandExecutionResult CacheResult(string invocationId, string output)
    {
        return new CommandExecutionResult(invocationId, "cache.test", "succeeded", true, output);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(CommandExecutionResult Result, WeakReference Capture)> CompleteCapturedCacheInvocationAsync(
        InvocationExecutionCache cache)
    {
        var capture = new InvocationFactoryCapture("detached-result");
        var weakReference = new WeakReference(capture, trackResurrection: false);
        var factory = CreateCapturedCacheFactory(capture);
        var execution = cache.GetOrAdd("detached", factory);
        var result = await execution;
        execution = null!;
        factory = null!;
        capture = null!;
        return (result, weakReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Func<Task<CommandExecutionResult>> CreateCapturedCacheFactory(InvocationFactoryCapture capture)
    {
        return () => CompleteCapturedCacheFactoryAsync(capture);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<CommandExecutionResult> CompleteCapturedCacheFactoryAsync(InvocationFactoryCapture capture)
    {
        await Task.Yield();
        return CacheResult("detached", capture.Output);
    }

    private static async Task AssertCacheCaptureCollectedAsync(WeakReference capture)
    {
        for (var attempt = 0; attempt < 20 && capture.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }

        Assert.False(capture.IsAlive);
    }

    private sealed record InvocationFactoryCapture(string Output);

    private static async IAsyncEnumerable<CommandExecutionStatus> WaitingCommandStream(
        string commandId,
        JsonObject args,
        string invocationId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new CommandExecutionStatus("running", "Long command running.", false, 1);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class RecordingHotkeyService : IHotkeyService
    {
        public Dictionary<string, HotkeyRegistration> Registered { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<HotkeyRegistration> RegisterCalls { get; } = [];
        public List<string> UnregisterCalls { get; } = [];

        public event EventHandler<HotkeyInvocation>? Pressed
        {
            add { }
            remove { }
        }

        public Task<HotkeyRegistrationResult> RegisterAsync(HotkeyRegistration registration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Registered[registration.Id] = registration;
            RegisterCalls.Add(registration);
            return Task.FromResult(new HotkeyRegistrationResult(true, "registered", "recorded"));
        }

        public Task<HotkeyRegistrationResult> UnregisterAsync(string id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnregisterCalls.Add(id);
            return Task.FromResult(Registered.Remove(id)
                ? new HotkeyRegistrationResult(true, "unregistered", "recorded")
                : new HotkeyRegistrationResult(false, "not-registered", "not registered"));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AlertEventTransportRuntime : IModuleTransportRuntime
    {
        public string Kind => "inproc-dotnet";
        public List<ulong> RequestedCursors { get; } = [];

        public ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ModuleStatusSnapshot?>(new ModuleStatusSnapshot(
                module.Module.Manifest.Id,
                "running",
                "alert event transport",
                DateTimeOffset.UtcNow,
                [],
                0));
        }

        public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new SettingsSchemaDocument(module.Module.Manifest.Id, "{}"));
        }

        public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>([]);
        }

        public ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, "alert transport"));
        }

        public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
            RuntimeModuleRecord module,
            ModuleContext context,
            EventCursor cursor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestedCursors.Add(cursor.LastEventSeq);
            await Task.Yield();
            yield return new MptModuleEvent(
                module.Module.Manifest.Id,
                1,
                "watch.alert",
                DateTimeOffset.UtcNow,
                new JsonObject
                {
                    ["title"] = "Disk pressure",
                    ["message"] = "Disk monitor crossed the alert threshold."
                });
        }
    }
}
