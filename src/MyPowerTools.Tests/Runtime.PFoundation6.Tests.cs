using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.ModuleHost.GrpcIpc;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.ViewModels;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using HostProto = MyPowerTools.Protocol.HostControl.V1;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    [Fact]
    public async Task Runtime_lifecycle_disable_unloads_inproc_and_enable_restores_commands()
    {
        await using var inproc = new InProcDotNetModuleHost();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-p6-lifecycle", Guid.NewGuid().ToString("N"))),
            [inproc]);

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        Assert.Contains(runtime.GetRuntimeDiagnostics().Processes, process =>
            process.TransportKind == "inproc-dotnet" &&
            process.PoolKey == "module:screenease" &&
            process.State == "loaded");
        Assert.Contains(runtime.ListCommands(""), command => command.ModuleId == "screenease");

        var disabled = await runtime.SetModuleEnabledAsync("screenease", enabled: false, CancellationToken.None);

        Assert.Equal("disabled", disabled.State);
        Assert.DoesNotContain(runtime.GetRuntimeDiagnostics().Processes, process =>
            process.TransportKind == "inproc-dotnet" &&
            process.PoolKey == "module:screenease" &&
            process.State == "loaded");
        Assert.DoesNotContain(runtime.ListCommands(""), command => command.ModuleId == "screenease");

        var enabled = await runtime.SetModuleEnabledAsync("screenease", enabled: true, CancellationToken.None);

        Assert.NotEqual("disabled", enabled.State);
        Assert.Contains(runtime.ListCommands(""), command => command.ModuleId == "screenease");
    }

    [Fact]
    public async Task Runtime_module_event_pump_collects_events_without_manual_collection()
    {
        var transport = new DuplicateEventTransportRuntime();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-p6-event-pump", Guid.NewGuid().ToString("N"))),
            [transport]);

        runtime.Load(Path.Combine(Root, "modules", "screenease"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        runtime.StartModuleEventPump();
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline &&
                   !runtime.HostEventsSince(0).Any(evt => evt.Type == "duplicate.test"))
            {
                await Task.Delay(100);
            }
        }
        finally
        {
            await runtime.StopModuleEventPumpAsync();
        }

        var events = runtime.HostEventsSince(0);
        Assert.True(
            events.Any(item => item.Type == "duplicate.test"),
            $"Expected duplicate.test event. events={string.Join(", ", events.Select(item => $"{item.Seq}:{item.ModuleId}:{item.Type}:{item.Payload.ToJsonString()}"))}; cursors={string.Join(",", transport.RequestedCursors)}");
        var evt = Assert.Single(events.Where(item => item.Type == "duplicate.test"));
        Assert.Equal(1UL, evt.Payload["moduleEventSeq"]!.GetValue<ulong>());
        Assert.Contains(0UL, transport.RequestedCursors);
    }

    [Fact]
    public async Task Runtime_hotkey_overrides_persist_reset_and_follow_module_enable_state()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-p6-hotkeys", Guid.NewGuid().ToString("N"));
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(dataRoot),
            []);

        runtime.Load(Path.Combine(Root, "modules"));
        var original = Assert.Single(runtime.ListHotkeyBindings().Where(binding => binding.ModuleId == "screenease"));
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
                        ["gesture"] = "Ctrl+Alt+Space",
                        ["reset"] = false,
                        ["disabled"] = false
                    })
                }),
            CancellationToken.None);

        Assert.Equal("conflict", runtime.ListHotkeyDiagnostics().Single(binding => binding.Id == original.Id).State);

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
                        ["commandArgs"] = new JsonObject { ["profileId"] = "day" }
                    })
                }),
            CancellationToken.None);

        var overridden = Assert.Single(runtime.ListHotkeyBindings().Where(binding => binding.ModuleId == "screenease"));
        Assert.Equal(overrideGesture, overridden.Gesture);
        Assert.False(runtime.ListHotkeyDiagnostics().Single(binding => binding.Id == original.Id).IsDefault);

        await using (var reloaded = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(dataRoot),
            []))
        {
            reloaded.Load(Path.Combine(Root, "modules"));
            Assert.Equal(overrideGesture, Assert.Single(reloaded.ListHotkeyBindings().Where(binding => binding.ModuleId == "screenease")).Gesture);
        }

        await runtime.SetModuleEnabledAsync("screenease", enabled: false, CancellationToken.None);
        Assert.DoesNotContain(runtime.ListHotkeyBindings(), binding => binding.ModuleId == "screenease");

        await runtime.SetModuleEnabledAsync("screenease", enabled: true, CancellationToken.None);
        Assert.Equal(overrideGesture, Assert.Single(runtime.ListHotkeyBindings().Where(binding => binding.ModuleId == "screenease")).Gesture);

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

        var reset = Assert.Single(runtime.ListHotkeyBindings().Where(binding => binding.ModuleId == "screenease"));
        Assert.Equal(original.DefaultGesture, reset.Gesture);
        Assert.True(runtime.ListHotkeyDiagnostics().Single(binding => binding.Id == original.Id).IsDefault);
    }

    [Fact]
    public async Task Runtime_grpc_readiness_waits_for_delayed_sidecar_and_preserves_typed_args_and_launch_context()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.p6.delay." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-p6-grpc-delay", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifestWithRuntimePolicy(
            packageRoot,
            sidecarCommand,
            pipeName,
            [pipeName, "--startup-delay-ms=650"],
            readyTimeoutMs: 5000);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-p6-runtime-grpc-delay", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        var stopwatch = Stopwatch.StartNew();
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        stopwatch.Stop();
        var result = await runtime.ExecuteCommandAsync(
            new CommandRequest(
                "runtime-p6-typed-args",
                "sample.grpc.echo",
                new JsonObject
                {
                    ["enabled"] = true,
                    ["retryCount"] = 3,
                    ["portMappings"] = new JsonArray(
                        new JsonObject
                        {
                            ["listen"] = new JsonObject { ["address"] = "0.0.0.0", ["port"] = 5555 },
                            ["connect"] = new JsonObject { ["address"] = "127.0.0.1", ["port"] = 7555 }
                        }),
                    ["processWatch"] = new JsonArray(
                        new JsonObject
                        {
                            ["name"] = "adb",
                            ["match"] = new JsonArray("adb.exe", "adb"),
                            ["required"] = false
                        })
                }),
            CancellationToken.None);

        Assert.True(dynamicCount > 0);
        Assert.True(stopwatch.ElapsedMilliseconds >= 300, $"Expected readiness retry to wait for delayed sidecar, elapsed={stopwatch.ElapsedMilliseconds}ms.");
        Assert.True(result.Success, result.Error?.Message);

        var payload = Assert.IsType<JsonObject>(JsonNode.Parse(result.Output));
        var env = Assert.IsType<JsonObject>(payload["env"]);
        var typedArgs = Assert.IsType<JsonObject>(payload["typedArgs"]);
        var portMappings = Assert.IsType<JsonArray>(typedArgs["portMappings"]);
        var firstMapping = Assert.IsType<JsonObject>(portMappings[0]);
        var listen = Assert.IsType<JsonObject>(firstMapping["listen"]);
        var processWatch = Assert.IsType<JsonArray>(typedArgs["processWatch"]);
        var firstWatch = Assert.IsType<JsonObject>(processWatch[0]);
        var match = Assert.IsType<JsonArray>(firstWatch["match"]);

        Assert.Equal(Path.GetFullPath(packageRoot), Path.GetFullPath(payload["cwd"]!.GetValue<string>()));
        Assert.Equal(Path.GetFullPath(packageRoot), Path.GetFullPath(env["MPT_PACKAGE_DIR"]!.GetValue<string>()));
        Assert.Equal("sample.grpc", env["MPT_MODULE_ID"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(env["MPT_ENDPOINT_TRANSPORT"]!.GetValue<string>()));
        Assert.Contains(pipeName, env["MPT_ENDPOINT_ADDRESS"]!.GetValue<string>());
        Assert.True(typedArgs["enabled"]!.GetValue<bool>());
        Assert.Equal(3, typedArgs["retryCount"]!.GetValue<int>());
        Assert.Equal(5555, listen["port"]!.GetValue<int>());
        Assert.Equal("adb", firstWatch["name"]!.GetValue<string>());
        Assert.Equal("adb.exe", match[0]!.GetValue<string>());
        Assert.False(firstWatch["required"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Runtime_grpc_readiness_reports_early_sidecar_exit()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.p6.exit." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-p6-grpc-exit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifestWithRuntimePolicy(
            packageRoot,
            sidecarCommand,
            pipeName,
            [pipeName, "--exit-before-ready"],
            readyTimeoutMs: 2000);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-p6-runtime-grpc-exit", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var module = Assert.Single(runtime.GetRuntimeDiagnostics().Modules);

        Assert.Equal(0, dynamicCount);
        Assert.Equal("degraded", module.State);
        Assert.Contains("process exited before readiness", module.Summary);
    }

    [Fact]
    public async Task Shell_settings_patch_writes_hotkey_override_and_reset()
    {
        var modules = new HostProto.ListModulesResponse();
        modules.Modules.Add(new HostProto.ModuleSummary
        {
            ModuleId = "screenease",
            DisplayName = "ScreenEase",
            State = "running"
        });
        var selected = modules.Modules.Single();
        var hotkeys = new[]
        {
            new HostProto.RuntimeHotkeyDiagnostics
            {
                Id = "screenease.profile.quick-apply",
                ModuleId = "screenease",
                CommandId = "screenease.profile.apply",
                Gesture = "Ctrl+Alt+F9",
                State = "registered",
                Message = "Registered.",
                IsDefault = true,
                DefaultGesture = "Ctrl+Alt+F9"
            }
        };
        var viewModel = ShellPageViewModelFactory.FromSettings(
            modules,
            selected,
            "{}",
            new JsonObject(),
            "{}",
            10,
            DateTimeOffset.UtcNow,
            hotkeys: hotkeys);
        var hotkey = Assert.Single(viewModel.Hotkeys);

        hotkey.Gesture = "Ctrl+Alt+F12";
        var editPatch = ShellPageViewModelFactory.BuildSettingsPatch(viewModel);
        var edit = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(editPatch["$hotkeys"])));

        Assert.True(viewModel.CanSave);
        Assert.Equal("Ctrl+Alt+F12", edit["gesture"]!.GetValue<string>());
        Assert.False(edit["reset"]!.GetValue<bool>());

        hotkey.ResetCommand.Execute(null);
        await Task.Yield();
        var resetPatch = ShellPageViewModelFactory.BuildSettingsPatch(viewModel);
        var reset = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(resetPatch["$hotkeys"])));

        Assert.Equal("Ctrl+Alt+F9", hotkey.Gesture);
        Assert.True(reset["reset"]!.GetValue<bool>());
        Assert.Equal("Ctrl+Alt+F9", reset["gesture"]!.GetValue<string>());
    }

    private static void WriteGrpcSidecarModuleManifestWithRuntimePolicy(
        string packageRoot,
        string sidecarCommand,
        string pipeName,
        IReadOnlyList<string> args,
        int readyTimeoutMs)
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"{pipeName}.sock");
        var argArray = new JsonArray();
        foreach (var arg in args)
        {
            argArray.Add(arg);
        }

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
                    ["args"] = argArray,
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
            ["capabilities"] = new JsonArray("status", "commands", "settings", "dashboardCard"),
            ["runtimePolicy"] = new JsonObject
            {
                ["preferred"] = "sidecar",
                ["allowInProc"] = false,
                ["sidecarRules"] = new JsonObject
                {
                    ["readyTimeoutMs"] = readyTimeoutMs,
                    ["restartLimit"] = 4,
                    ["restartWindowSeconds"] = 30,
                    ["killProcessTree"] = true
                }
            }
        };

        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
