using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Avalonia.Controls;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.ModuleHost.StdioCompat;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;

using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using EventCursor = MyPowerTools.Abstractions.EventCursor;
using HealthCheckSnapshot = MyPowerTools.Abstractions.HealthCheckSnapshot;
using IMptModule = MyPowerTools.Abstractions.IMptModule;
using InitializeResult = MyPowerTools.Abstractions.InitializeResult;
using UiSurfaceDescriptor = MyPowerTools.Abstractions.UiSurfaceDescriptor;
using IMptModuleLifecycle = MyPowerTools.Abstractions.IMptModuleLifecycle;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using ModuleStatusSnapshot = MyPowerTools.Abstractions.ModuleStatusSnapshot;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using MptModuleEvent = MyPowerTools.Abstractions.MptModuleEvent;
using MptRuntimeError = MyPowerTools.Abstractions.MptRuntimeError;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;
using SettingsSchemaDocument = MyPowerTools.Abstractions.SettingsSchemaDocument;
using SettingsSnapshotDocument = MyPowerTools.Abstractions.SettingsSnapshotDocument;
using SettingsValidationResult = MyPowerTools.Abstractions.SettingsValidationResult;

namespace MyPowerTools.StandaloneHost.Tests;

public sealed class StandaloneHostTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(),
        "mpt-standalone-tests",
        Guid.NewGuid().ToString("N"));

    public StandaloneHostTests()
    {
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
            {
                Directory.Delete(_sandbox, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task Standalone_inproc_tool_executes_commands_and_streams_events_without_runner()
    {
        var toolDir = Path.Combine(_sandbox, "MptFixtureTool");
        StageSelfContainedFixtureTool(toolDir, toolId: "fixture-tool", portable: true);

        var options = StandaloneCliOptions.Parse(["--tool-root", toolDir, "--standalone"]);
        var resolved = StandaloneToolLocator.Resolve(options);

        Assert.Equal("fixture-tool", resolved.Descriptor.ToolId);
        Assert.Equal("Fixture Standalone Tool", resolved.Descriptor.Title);

        await using var backend = await StandaloneExecutionBackend.CreateAsync(
            resolved,
            options,
            CancellationToken.None);

        Assert.Equal("standalone-inproc", backend.Mode);
        Assert.False(backend.IsHubAttached);
        Assert.True(backend.IsPortable);
        Assert.Equal(Path.Combine(toolDir, "data"), backend.DataRoot);

        await backend.InitializeAsync(CancellationToken.None);
        await backend.StartAsync(CancellationToken.None);

        var status = await backend.GetStatusAsync(CancellationToken.None);
        Assert.Equal("running", status.State);

        var commands = await backend.ListCommandsAsync(CancellationToken.None);
        Assert.Contains(commands, c => c.Id == "fixture-tool.echo");

        var eventReceived = new TaskCompletionSource<MptSurfaceEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = backend.SubscribeEvents(evt =>
        {
            if (evt.Type == "fixture.echoed")
            {
                eventReceived.TrySetResult(evt);
            }
        });

        var result = await backend.ExecuteCommandAsync(
            "fixture-tool.echo",
            new JsonObject { ["message"] = "hello-standalone" },
            CancellationToken.None);

        Assert.True(result.Success);
        using var outputDoc = JsonDocument.Parse(result.Output);
        Assert.Equal("hello-standalone", outputDoc.RootElement.GetProperty("echo").GetString());
        Assert.True(outputDoc.RootElement.GetProperty("lifecycleStarted").GetBoolean());

        var received = await eventReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("fixture-tool", received.SourceId);
        Assert.Equal("hello-standalone", received.Payload["echo"]?.GetValue<string>());

        var units = await backend.ServiceUnitsFactory.ForTool("fixture-tool").ListAsync();
        Assert.Empty(units);
    }

    [Fact]
    public void SharedRuntimeAssemblyResolver_probes_hundred_kb_tool_against_sibling_shared_ui()
    {
        var suiteRoot = Path.Combine(_sandbox, "composable-suite");
        var sharedUi = Path.Combine(suiteRoot, "shared-ui");
        var lightTool = Path.Combine(suiteRoot, "MptLightTool");
        var siblingTool = Path.Combine(suiteRoot, "MptSiblingTool");
        var fakeLocalAppData = Path.Combine(_sandbox, "local-appdata");

        Directory.CreateDirectory(sharedUi);
        Directory.CreateDirectory(Path.Combine(sharedUi, "runtimes", "win-x64", "native"));
        Directory.CreateDirectory(Path.Combine(lightTool, "app"));
        Directory.CreateDirectory(siblingTool);

        StageSelfContainedFixtureTool(lightTool, toolId: "light-tool", portable: false);
        File.WriteAllText(
            Path.Combine(siblingTool, "tool.json"),
            """
            {
              "schemaVersion": "1.0",
              "toolId": "sibling-tool",
              "title": "Sibling Portable Tool",
              "primaryRouteId": "main",
              "routes": [
                {
                  "routeId": "main",
                  "surfaceId": "sibling.main",
                  "title": "Sibling",
                  "surface": {
                    "kind": "dotnet",
                    "assembly": "app/Sibling.dll",
                    "type": "Sibling.Factory"
                  }
                }
              ]
            }
            """);

        var sharedManagedDll = Path.Combine(sharedUi, "SyntheticSharedDependency.dll");
        File.WriteAllBytes(sharedManagedDll, [0x4D, 0x5A]);
        var sharedSkiaDll = Path.Combine(sharedUi, "runtimes", "win-x64", "native", "libSkiaSharp.dll");
        File.WriteAllBytes(sharedSkiaDll, [0x4D, 0x5A]);
        var sharedHarfBuzzDll = Path.Combine(sharedUi, "runtimes", "win-x64", "native", "libHarfBuzzSharp.dll");
        File.WriteAllBytes(sharedHarfBuzzDll, [0x4D, 0x5A]);

        var probeDirs = SharedRuntimeAssemblyResolver.BuildOrderedProbeDirectories(
            lightTool,
            appBaseDirectory: Path.Combine(lightTool, "app"),
            localAppDataDirectory: fakeLocalAppData);

        var appIdx = IndexOfPath(probeDirs, Path.Combine(lightTool, "app"));
        var sharedIdx = IndexOfPath(probeDirs, sharedUi);
        var localSharedIdx = IndexOfPath(probeDirs, Path.Combine(fakeLocalAppData, "MyPowerTools", "shared-ui"));
        var shellIdx = IndexOfPath(probeDirs, Path.Combine(fakeLocalAppData, "Programs", "MyPowerTools", "Shell"));

        Assert.True(appIdx >= 0);
        Assert.True(sharedIdx > appIdx);
        Assert.True(localSharedIdx > sharedIdx);
        Assert.True(shellIdx > localSharedIdx);

        var resolvedManaged = SharedRuntimeAssemblyResolver.TryResolveManagedAssemblyPath(
            new AssemblyName("SyntheticSharedDependency"),
            probeDirs);
        Assert.Equal(Path.GetFullPath(sharedManagedDll), resolvedManaged);

        var resolvedSkia = SharedRuntimeAssemblyResolver.TryResolveNativeLibraryPath(
            "SkiaSharp",
            probeDirs,
            ridOverride: "win-x64");
        Assert.Equal(Path.GetFullPath(sharedSkiaDll), resolvedSkia);

        var resolvedHarfBuzz = SharedRuntimeAssemblyResolver.TryResolveNativeLibraryPath(
            "libHarfBuzzSharp",
            probeDirs,
            ridOverride: "win-x64");
        Assert.Equal(Path.GetFullPath(sharedHarfBuzzDll), resolvedHarfBuzz);

        var options = StandaloneCliOptions.Parse(["--tool-root", lightTool, "--isolated"]);
        var resolvedTool = StandaloneToolLocator.Resolve(options);
        var neighborhood = StandaloneNeighborhoodScanner.Scan(resolvedTool, options);
        Assert.Single(neighborhood.SiblingTools);
        Assert.Equal("sibling-tool", neighborhood.SiblingTools[0].ToolId);
        Assert.Equal("Sibling Portable Tool", neighborhood.SiblingTools[0].Title);
    }

    [Fact]
    public async Task Hub_autonomously_discovers_standalone_tool_folder_dropped_into_custom_tools_and_sibling_mpt_dir()
    {
        var portableRoot = Path.Combine(_sandbox, "hub-aggregation");
        var installRoot = Path.Combine(portableRoot, "MyPowerTools");
        var modulesRoot = Path.Combine(installRoot, "modules");
        var dataRoot = Path.Combine(_sandbox, "hub-data");
        var customToolsDir = ToolDiscoveryConfiguration.CustomToolsDirectory(dataRoot);

        Directory.CreateDirectory(modulesRoot);
        Directory.CreateDirectory(customToolsDir);

        // 1) Drop a flattened standalone tool folder (module.json + ui/tool.json + app/*.dll) into custom-tools/
        var droppedCustomDir = Path.Combine(customToolsDir, "MptDroppedCustom");
        StageSelfContainedFixtureTool(droppedCustomDir, toolId: "dropped-custom", portable: false);

        // 2) Drop a sibling portable Mpt* folder right next to installRoot
        var siblingPortableDir = Path.Combine(portableRoot, "MptSiblingDropped");
        StageSelfContainedFixtureTool(siblingPortableDir, toolId: "sibling-dropped", portable: false);

        var discoveryRoots = ToolDiscoveryConfiguration.Resolve(installRoot, dataRoot);
        Assert.Contains(
            discoveryRoots,
            p => string.Equals(p, Path.GetFullPath(siblingPortableDir), StringComparison.OrdinalIgnoreCase));

        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(dataRoot),
            [new InProcDotNetModuleHost(), new StdioCompatModuleHost()]);

        runtime.Load(modulesRoot, discoveryRoots);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        var tools = runtime.ListTools(includeDisabled: true);
        var customTool = Assert.Single(tools, t => t.Descriptor.ToolId == "dropped-custom");
        var siblingTool = Assert.Single(tools, t => t.Descriptor.ToolId == "sibling-dropped");

        Assert.Equal("running", customTool.State);
        Assert.True(File.Exists(customTool.Descriptor.Routes[0].Assembly));
        Assert.Equal("running", siblingTool.State);
        Assert.True(File.Exists(siblingTool.Descriptor.Routes[0].Assembly));

        var execResult = await runtime.ExecuteCommandAsync(
            new CommandRequest(
                Guid.NewGuid().ToString("N"),
                "dropped-custom.echo",
                new JsonObject { ["message"] = "aggregated-into-hub" }),
            CancellationToken.None);

        Assert.True(execResult.Success);
        using var doc = JsonDocument.Parse(execResult.Output);
        Assert.Equal("aggregated-into-hub", doc.RootElement.GetProperty("echo").GetString());
    }

    [Fact]
    public async Task Headless_smoke_cli_verifies_real_avalonia_surface_creation_without_runner()
    {
        var toolDir = Path.Combine(_sandbox, "MptSmokeFixture");
        StageSelfContainedFixtureTool(toolDir, toolId: "smoke-fixture", portable: true);

        var hostDll = typeof(Program).Assembly.Location;
        var muxer = StandalonePackageNormalizer.ResolveDotnetMuxerPath()
            ?? throw new InvalidOperationException("dotnet muxer was not found.");

        var psi = new ProcessStartInfo
        {
            FileName = muxer,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(hostDll);
        psi.ArgumentList.Add("--tool-root");
        psi.ArgumentList.Add(toolDir);
        psi.ArgumentList.Add("--standalone");
        psi.ArgumentList.Add("--isolated");
        psi.ArgumentList.Add("--headless-smoke");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start MptStandaloneHost smoke process.");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        Assert.True(proc.ExitCode == 0, $"Smoke exited with {proc.ExitCode}. stdout={stdout} stderr={stderr}");
        using var json = JsonDocument.Parse(stdout.Trim());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("smoke-fixture", json.RootElement.GetProperty("toolId").GetString());
        Assert.Equal("standalone-inproc", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("commandsCount").GetInt32());
        Assert.Contains("Border", json.RootElement.GetProperty("surfaceControlType").GetString());
    }

    private static int IndexOfPath(IReadOnlyList<string> paths, string target)
    {
        var full = Path.GetFullPath(target);
        for (var i = 0; i < paths.Count; i++)
        {
            if (string.Equals(paths[i], full, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static void StageSelfContainedFixtureTool(string targetDir, string toolId, bool portable)
    {
        var appDir = Path.Combine(targetDir, "app");
        var uiDir = Path.Combine(targetDir, "ui");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(uiDir);

        if (portable)
        {
            File.WriteAllText(Path.Combine(targetDir, "portable.flag"), "portable=1\n");
        }

        var thisTestDll = typeof(StandaloneHostTests).Assembly.Location;
        var stagedDllName = "FixtureStandalonePayload.dll";
        File.Copy(thisTestDll, Path.Combine(appDir, stagedDllName), overwrite: true);

        var depsPath = Path.ChangeExtension(thisTestDll, ".deps.json");
        if (File.Exists(depsPath))
        {
            File.Copy(depsPath, Path.Combine(appDir, "FixtureStandalonePayload.deps.json"), overwrite: true);
        }

        // Deliberately write manifests using unflattened paths ("FixtureStandalonePayload.dll" and
        // "surface/FixtureStandalonePayload.dll") while the actual binary lives inside app/ — proving
        // zero-rewrite smart path resolution in both StandaloneHost and MptHostRuntime!
        var moduleJson = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = toolId,
            ["packageId"] = toolId,
            ["displayName"] = "Fixture Standalone Tool",
            ["version"] = "0.1.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = stagedDllName,
                    ["type"] = typeof(FixtureStandaloneModule).FullName
                }
            },
            ["capabilities"] = new JsonArray("lifecycle", "status", "commands", "events"),
            ["tools"] = new JsonArray("ui/tool.json")
        };
        File.WriteAllText(Path.Combine(targetDir, "module.json"), moduleJson.ToJsonString());

        var toolJson = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["toolId"] = toolId,
            ["ownerModuleId"] = toolId,
            ["title"] = "Fixture Standalone Tool",
            ["description"] = "Self-contained fixture tool for standalone and Hub aggregation tests.",
            ["icon"] = "tool.fixture",
            ["category"] = "Testing",
            ["type"] = "dotnet-surface",
            ["availability"] = "available",
            ["primaryRouteId"] = "main",
            ["routes"] = new JsonArray
            {
                new JsonObject
                {
                    ["routeId"] = "main",
                    ["surfaceId"] = $"{toolId}.main",
                    ["title"] = "Main",
                    ["surface"] = new JsonObject
                    {
                        ["kind"] = "dotnet",
                        ["assembly"] = $"surface/{stagedDllName}",
                        ["type"] = typeof(FixtureStandaloneSurfaceFactory).FullName
                    }
                }
            },
            ["homeCard"] = new JsonObject
            {
                ["summary"] = "Fixture tool summary",
                ["primaryActionLabel"] = "Open",
                ["order"] = 50
            }
        };
        File.WriteAllText(Path.Combine(uiDir, "tool.json"), toolJson.ToJsonString());
    }
}

public sealed class FixtureStandaloneModule : IMptModule, IMptModuleLifecycle
{
    private string _moduleId;
    private readonly Channel<MptModuleEvent> _events = Channel.CreateUnbounded<MptModuleEvent>();
    private bool _started;
    private ulong _seq;

    public FixtureStandaloneModule() : this("fixture-tool") { }

    public FixtureStandaloneModule(string moduleId)
    {
        _moduleId = moduleId;
    }

    public string Id => _moduleId;
    public string PackageId => $"mpt.fixture.{_moduleId}";
    public Version Version => new(0, 1, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(context.ModuleId))
        {
            _moduleId = context.ModuleId;
        }
        return ValueTask.FromResult(new InitializeResult(true, "1.0", []));
    }

    public ValueTask EnableAsync(ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask DisableAsync(ModuleContext context, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _started = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _started = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new ModuleStatusSnapshot(
            _moduleId,
            "running",
            "Fixture module healthy",
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("fixture", "Fixture check", true, "OK")],
            0));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> list =
        [
            new MptCommandDescriptor(
                $"{_moduleId}.echo",
                _moduleId,
                "Echo message",
                "Echoes input payload and publishes an event",
                "action")
        ];
        return ValueTask.FromResult(list);
    }

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var msg = request.Args["message"]?.GetValue<string>() ?? "";
        var nextSeq = ++_seq;
        var payload = new JsonObject
        {
            ["echo"] = msg,
            ["lifecycleStarted"] = _started
        };
        _events.Writer.TryWrite(new MptModuleEvent(_moduleId, nextSeq, "fixture.echoed", DateTimeOffset.UtcNow, payload));

        return ValueTask.FromResult(new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "succeeded",
            true,
            payload.ToJsonString()));
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(_moduleId, """{"type":"object","properties":{}}"""));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(_moduleId, 1, new JsonObject(), DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsValidationResult(true, []));
    }

    public ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(snapshot);
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([]);
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor after,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (evt.Seq > after.LastEventSeq)
            {
                yield return evt;
            }
        }
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}


public sealed class FixtureStandaloneSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Avalonia.Controls.Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        return new Avalonia.Controls.Border { Child = new Avalonia.Controls.TextBlock { Text = $"Fixture surface: {context.ToolId}" } };
    }
}
