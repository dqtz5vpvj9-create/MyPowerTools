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
    private static readonly string Root = FindRepositoryRoot(AppContext.BaseDirectory);
    private const string PortProxySample = """
Listen on ipv4:             Connect to ipv4:

Address         Port        Address         Port
--------------- ----------  --------------- ----------
0.0.0.0         5555        127.0.0.1       7555
""";





















































































    private static async IAsyncEnumerable<CommandExecutionStatus> SingleCommandStatus(
        string state,
        string message,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new CommandExecutionStatus(state, message);
    }

    private static async IAsyncEnumerable<CommandExecutionStatus> CommandProgressStatuses(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new CommandExecutionStatus("accepted", "accepted", false, 1);
        yield return new CommandExecutionStatus("running", "running", false, 2);
        yield return new CommandExecutionStatus("succeeded", "done", true, 3);
    }

    private static async IAsyncEnumerable<CommandExecutionStatus> DelayedCommandStatus(
        TaskCompletionSource started,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.SetResult();
        await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        yield return new CommandExecutionStatus("succeeded", "finished");
    }











































    private static void AssertLiveHostControlScreenshotManifest()
    {
        var output = Path.Combine(Path.GetTempPath(), "mpt-shell-live-screenshot", Guid.NewGuid().ToString("N"));
        var timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        var dashboard = new HostProto.DashboardSnapshot { EventSeq = 7 };
        var card = new HostProto.ModuleCard
        {
            ModuleId = "sample.live",
            PackageId = "sample-live-package",
            Title = "Sample Live",
            State = "running",
            Summary = "Loaded from HostControl fixture data."
        };
        card.Metrics.Add(new HostProto.Metric { Label = "Transport", Value = "fixture-hostcontrol" });
        card.Actions.Add(new HostProto.QuickAction { CommandId = "sample.live.run", Title = "Run", Style = "primary" });
        dashboard.Cards.Add(card);

        var modules = new HostProto.ListModulesResponse();
        var selected = new HostProto.ModuleSummary
        {
            ModuleId = "sample.live",
            PackageId = "sample-live-package",
            DisplayName = "Sample Live",
            State = "running",
            Summary = "Live HostControl module.",
            Enabled = true
        };
        modules.Modules.Add(selected);
        var commands = new HostProto.ListCommandsResponse();
        var command = new HostProto.CommandItem
        {
            CommandId = "sample.live.run",
            ModuleId = "sample.live",
            Title = "Run live command",
            Subtitle = "Uses live HostControl data.",
            DangerLevel = "normal"
        };
        command.Parameters.Add(new HostProto.CommandParameter
        {
            Id = "reason",
            Label = "Reason",
            Type = "string",
            Required = true,
            DefaultValue = "live snapshot"
        });
        commands.Commands.Add(command);

        var packages = new HostProto.ListPackagesResponse();
        packages.Packages.Add(new HostProto.PackageSummary
        {
            PackageId = "sample-live-package",
            DisplayName = "Sample Live Package",
            Version = "0.2.0",
            Publisher = "tests",
            Directory = output,
            TrustState = "trusted",
            TrustPolicy = "local"
        });
        packages.Packages[0].ModuleIds.Add("sample.live");
        var notifications = new HostProto.ListNotificationsResponse();
        notifications.Notifications.Add(new HostProto.NotificationItem
        {
            Id = "n-live",
            Time = timestamp,
            ModuleId = "sample.live",
            Level = "info",
            Title = "Live event",
            Body = "Notification came from HostControl fixture data."
        });
        var diagnostics = new HostProto.RuntimeDiagnostics
        {
            RunnerVersion = "0.2.0",
            HostControlProtocolVersion = "1.0",
            ModuleProtocolVersion = "1.0",
            PlatformRid = "test",
            DotnetVersion = Environment.Version.ToString(),
            OsDescription = "test",
            ProcessArchitecture = "x64",
            StartedAt = timestamp,
            CollectedAt = timestamp,
            CurrentEventSeq = 7,
            Paths = new HostProto.RuntimePathDiagnostics
            {
                Root = output,
                Settings = output,
                Logs = output,
                State = output,
                Packages = output,
                PackageRoot = output
            },
            Counts = new HostProto.RuntimeCountDiagnostics
            {
                PackageCount = 1,
                ModuleCount = 1,
                EnabledModuleCount = 1,
                RunningModuleCount = 1,
                CommandCount = 1,
                NotificationCount = 1
            }
        };
        diagnostics.Transports.Add(new HostProto.RuntimeTransportDiagnostics { Kind = "inproc-dotnet", RuntimeRegistered = true, ModuleCount = 1 });
        diagnostics.Modules.Add(new HostProto.RuntimeModuleDiagnostics
        {
            ModuleId = "sample.live",
            PackageId = "sample-live-package",
            DisplayName = "Sample Live",
            State = "running",
            Enabled = true,
            TransportKind = "inproc-dotnet",
            Summary = "Live HostControl module.",
            UpdatedAt = timestamp,
            LastObservedAt = timestamp
        });
        diagnostics.Hotkeys.Add(new HostProto.RuntimeHotkeyDiagnostics
        {
            Id = "sample.live.quick",
            ModuleId = "sample.live",
            CommandId = "sample.live.run",
            Gesture = "Ctrl+Alt+F10",
            Scope = "module",
            State = "ok",
            Message = "Gesture is available."
        });

        var data = new ShellHostControlSnapshotData(
            "fixture-hostcontrol",
            dashboard,
            commands,
            modules,
            selected,
            new HostProto.ModuleDetail
            {
                ModuleId = "sample.live",
                PackageId = "sample-live-package",
                DisplayName = "Sample Live",
                State = "running",
                Summary = "Live HostControl detail."
            },
            new HostProto.SettingsSchema
            {
                ModuleId = "sample.live",
                SchemaJson = """{"properties":{"profile":{"type":"string","title":"Profile"}}}"""
            },
            new HostProto.SettingsSnapshot
            {
                ModuleId = "sample.live",
                Revision = 3,
                Values = JsonStructMapper.ToStruct(new JsonObject { ["profile"] = "normal" }),
                UpdatedAt = timestamp
            },
            [
                new HostProto.LogEntry
                {
                    ModuleId = "sample.live",
                    Cursor = "1",
                    Time = timestamp,
                    Level = "info",
                    Message = "Live fixture log line."
                }
            ],
            notifications,
            packages,
            diagnostics,
            new HostProto.ListBrokerAuditResponse());

        var manifestPath = ShellRealScreenshotWriter.WriteSnapshotSetFromHostControlData(
            output,
            "light",
            "1366x768",
            "normal",
            data,
            "shell.dashboard");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var screenshots = manifest["screenshots"]!.AsArray();

        Assert.True(manifest["usesHostControlData"]!.GetValue<bool>());
        Assert.Equal("fixture-hostcontrol", manifest["dataSource"]!.GetValue<string>());
        Assert.Equal("shell.dashboard", manifest["surface"]!.GetValue<string>());
        Assert.Equal(1, manifest["screenshotCount"]!.GetValue<int>());
        Assert.Equal("dashboard", screenshots.Single()!["screenId"]!.GetValue<string>());
    }























































































































    private static async Task<WeakReference> LoadAndDisposeAdbForwarderAsync()
    {
        await using var host = new InProcDotNetModuleHost();
        var package = new PackageReader().ReadPackageDirectory(Path.Combine(Root, "modules", "adb-forwarder"));
        var module = package.Modules.Single();
        var loaded = await host.LoadAsync(module, CancellationToken.None);
        var loadContext = AssemblyLoadContext.GetLoadContext(loaded.GetType().Assembly);

        Assert.NotNull(loadContext);
        Assert.NotSame(AssemblyLoadContext.Default, loadContext);
        Assert.True(loadContext!.IsCollectible);

        var weakReference = new WeakReference(loadContext, trackResurrection: false);
        await loaded.DisposeAsync(CancellationToken.None);
        loaded = null!;
        loadContext = null;
        await host.DisposeAsync();
        return weakReference;
    }

    private static void WriteInProcDotNetModulePackage(string packageRoot, string moduleId, string packageId, string displayName, string typeName)
    {
        var assemblyPath = typeof(SampleDotNetModule).Assembly.Location;
        var assemblyName = Path.GetFileName(assemblyPath);
        File.Copy(assemblyPath, Path.Combine(packageRoot, assemblyName), overwrite: true);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = moduleId,
            ["packageId"] = packageId,
            ["displayName"] = displayName,
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = assemblyName,
                    ["type"] = typeName
                }
            },
            ["capabilities"] = new JsonArray("status", "commands")
        };

        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<(string LoadedAssemblyPath, CommandExecutionResult Before, CommandExecutionResult StillLoaded)> LoadAndReplaceShadowPluginAsync(
        InProcDotNetModuleHost host,
        MptModuleDefinition module,
        ModuleContext context,
        string packageRoot,
        string replacementRoot)
    {
        var loaded = await host.LoadAsync(module, context, CancellationToken.None);
        var loadedAssemblyPath = loaded.GetType().Assembly.Location;
        var before = await loaded.ExecuteCommandAsync(new CommandRequest("shadow-before", "shadow.update.dependency", new JsonObject()), CancellationToken.None);

        File.Copy(Path.Combine(replacementRoot, "ShadowUpdatePlugin.dll"), Path.Combine(packageRoot, "ShadowUpdatePlugin.dll"), overwrite: true);
        File.Copy(Path.Combine(replacementRoot, "PluginSharedDependency.dll"), Path.Combine(packageRoot, "PluginSharedDependency.dll"), overwrite: true);
        File.SetLastWriteTimeUtc(Path.Combine(packageRoot, "ShadowUpdatePlugin.dll"), DateTime.UtcNow.AddMinutes(1));
        var stillLoaded = await loaded.ExecuteCommandAsync(new CommandRequest("shadow-still-loaded", "shadow.update.dependency", new JsonObject()), CancellationToken.None);
        loaded = null!;
        return (loadedAssemblyPath, before, stillLoaded);
    }

    private static async Task BuildGeneratedInProcPluginPackageAsync(
        string packageRoot,
        string moduleId,
        string packageId,
        string assemblyName,
        string dependencyValue,
        string dependencyVersion)
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "mpt-generated-plugin-src", Guid.NewGuid().ToString("N"));
        var dependencyRoot = Path.Combine(sourceRoot, "PluginSharedDependency");
        var pluginRoot = Path.Combine(sourceRoot, assemblyName);
        Directory.CreateDirectory(dependencyRoot);
        Directory.CreateDirectory(pluginRoot);

        await File.WriteAllTextAsync(Path.Combine(dependencyRoot, "PluginSharedDependency.csproj"), """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>PluginSharedDependency</AssemblyName>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>
</Project>
""");
        await File.WriteAllTextAsync(Path.Combine(dependencyRoot, "SharedDependency.cs"), $$"""
using System.Reflection;

[assembly: AssemblyVersion("{{dependencyVersion}}")]

namespace PluginSharedDependency;

public static class SharedDependency
{
    public static string Report() => "{{dependencyValue}}";
}
""");

        var abstractionsProject = Path.Combine(Root, "src", "MyPowerTools.Abstractions", "MyPowerTools.Abstractions.csproj");
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, $"{assemblyName}.csproj"), $$"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>{{assemblyName}}</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{{abstractionsProject}}" />
    <ProjectReference Include="{{Path.Combine(dependencyRoot, "PluginSharedDependency.csproj")}}" />
  </ItemGroup>
</Project>
""");
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "GeneratedModule.cs"), $$""""
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using PluginSharedDependency;

namespace {{assemblyName}};

public sealed class GeneratedModule : IMptModule
{
    public string Id => "{{moduleId}}";
    public string PackageId => "{{packageId}}";
    public Version Version => new(0, 2, 0);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings"]));
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new ModuleStatusSnapshot(
            Id,
            "running",
            SharedDependency.Report(),
            DateTimeOffset.UtcNow,
            [new HealthCheckSnapshot("dependency", "Dependency", true, SharedDependency.Report())],
            1));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            new("{{moduleId}}.dependency", Id, "Read dependency", "Generated InProc dependency probe", "action")
        ];
        return ValueTask.FromResult(commands);
    }

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new CommandExecutionResult(
            request.InvocationId,
            request.CommandId,
            "succeeded",
            true,
            SharedDependency.Report()));
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, "{}"));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, new JsonObject(), DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsValidationResult(true, []));
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([]);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }
}
"""");

        var publish = await RunDotnetAsync(
            "publish",
            Path.Combine(pluginRoot, $"{assemblyName}.csproj"),
            "--nologo",
            "--disable-build-servers",
            "-c",
            "Release",
            "-o",
            packageRoot,
            "-nr:false",
            "-p:UseSharedCompilation=false");
        Assert.True(publish.ExitCode == 0, publish.Output);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = moduleId,
            ["packageId"] = packageId,
            ["displayName"] = moduleId,
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = $"{assemblyName}.dll",
                    ["type"] = $"{assemblyName}.GeneratedModule"
                }
            },
            ["capabilities"] = new JsonArray("status", "commands", "settings")
        };

        await File.WriteAllTextAsync(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ModuleContext CreateGeneratedPluginContext(string packageId, string moduleId)
    {
        return CreateModuleContext(
            packageId,
            moduleId,
            $"generated-{moduleId}",
            ["status", "commands", "settings"]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RefreshDynamicCommandsAndRelease(MptHostRuntime runtime)
    {
        var count = runtime.RefreshDynamicCommandsAsync(CancellationToken.None).GetAwaiter().GetResult();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return count;
    }

    private static JsonObject CreateRuntimePolicyManifest(JsonObject runtimePolicy)
    {
        return new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "runtime-policy-sample",
            ["packageId"] = "runtime-policy-sample",
            ["displayName"] = "Runtime Policy Sample",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "grpc-ipc",
                    ["priority"] = 90,
                    ["command"] = "sample-sidecar",
                    ["args"] = new JsonArray("sample")
                }
            },
            ["runtimePolicy"] = runtimePolicy,
            ["capabilities"] = new JsonArray("status", "commands")
        };
    }

    private static JsonObject RuntimePolicyInProcRules(int maxCallMs)
    {
        return new JsonObject
        {
            ["maxCallMs"] = maxCallMs,
            ["allowNativeDll"] = false,
            ["allowWindow"] = false,
            ["allowBackgroundThreads"] = false,
            ["loadContext"] = "collectible",
            ["shadowCopy"] = true
        };
    }

    private static JsonObject RuntimePolicySidecarRules(int readyTimeoutMs, int restartLimit, int restartWindowSeconds)
    {
        return new JsonObject
        {
            ["readyTimeoutMs"] = readyTimeoutMs,
            ["restartLimit"] = restartLimit,
            ["restartWindowSeconds"] = restartWindowSeconds,
            ["killProcessTree"] = true
        };
    }

    private static void WriteRuntimePolicySelectionModule(
        string packageRoot,
        JsonObject runtimePolicy,
        bool includeSidecar,
        bool includeInProc)
    {
        var entrypoints = new JsonArray();
        if (includeInProc)
        {
            var assemblyPath = typeof(SampleDotNetModule).Assembly.Location;
            var assemblyName = Path.GetFileName(assemblyPath);
            File.Copy(assemblyPath, Path.Combine(packageRoot, assemblyName), overwrite: true);
            entrypoints.Add(new JsonObject
            {
                ["kind"] = "inproc-dotnet",
                ["priority"] = 100,
                ["assembly"] = assemblyName,
                ["type"] = "MyPowerTools.SampleModules.DotNet.SampleDotNetModule"
            });
        }

        if (includeSidecar)
        {
            entrypoints.Add(new JsonObject
            {
                ["kind"] = "grpc-ipc",
                ["priority"] = 10,
                ["command"] = "tools/sidecar.exe",
                ["windows"] = new JsonObject
                {
                    ["transport"] = "named-pipe",
                    ["name"] = "mypowertools.runtime-policy-test"
                },
                ["linux"] = new JsonObject
                {
                    ["transport"] = "unix-domain-socket",
                    ["path"] = "/tmp/mypowertools-runtime-policy-test.sock"
                },
                ["macos"] = new JsonObject
                {
                    ["transport"] = "unix-domain-socket",
                    ["path"] = "/tmp/mypowertools-runtime-policy-test.sock"
                }
            });
        }

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "runtime-policy-selection",
            ["packageId"] = "runtime-policy-selection",
            ["displayName"] = "Runtime Policy Selection",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = entrypoints,
            ["runtimePolicy"] = runtimePolicy,
            ["capabilities"] = new JsonArray("status", "commands")
        };
        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteRuntimePolicyOperationModule(
        string packageRoot,
        string commandId,
        JsonArray constraints,
        bool brokerApprovalOnly = false)
    {
        var assemblyPath = typeof(SampleDotNetModule).Assembly.Location;
        var assemblyName = Path.GetFileName(assemblyPath);
        File.Copy(assemblyPath, Path.Combine(packageRoot, assemblyName), overwrite: true);
        var execution = new JsonObject { ["type"] = "module.execute" };
        if (brokerApprovalOnly)
        {
            execution["brokerApprovalOnly"] = true;
        }

        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "runtime-policy-selection",
            ["packageId"] = "runtime-policy-selection",
            ["displayName"] = "Runtime Policy Selection",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = assemblyName,
                    ["type"] = "MyPowerTools.SampleModules.DotNet.SampleDotNetModule"
                }
            },
            ["runtimePolicy"] = new JsonObject
            {
                ["preferred"] = "inproc",
                ["allowInProc"] = true,
                ["inProcRules"] = RuntimePolicyInProcRules(3000),
                ["operationRules"] = RuntimePolicyOperationRules()
            },
            ["capabilities"] = new JsonArray("status", "commands"),
            ["staticIndexes"] = new JsonObject
            {
                ["commands"] = "commands.index.json"
            }
        };
        var commands = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["commands"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = commandId,
                    ["title"] = "Policy operation command",
                    ["kind"] = "action",
                    ["constraints"] = constraints,
                    ["execution"] = execution
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

    private static void WriteRuntimePolicyMixedRouteModule(string packageRoot)
    {
        var assemblyPath = typeof(SampleDotNetModule).Assembly.Location;
        var assemblyName = Path.GetFileName(assemblyPath);
        File.Copy(assemblyPath, Path.Combine(packageRoot, assemblyName), overwrite: true);
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "runtime-policy-selection",
            ["packageId"] = "runtime-policy-selection",
            ["displayName"] = "Runtime Policy Selection",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = assemblyName,
                    ["type"] = "MyPowerTools.SampleModules.DotNet.SampleDotNetModule"
                },
                new JsonObject
                {
                    ["kind"] = "grpc-ipc",
                    ["priority"] = 60,
                    ["windows"] = new JsonObject
                    {
                        ["transport"] = "named-pipe",
                        ["name"] = "mypowertools.runtime-policy-command-route"
                    },
                    ["linux"] = new JsonObject
                    {
                        ["transport"] = "unix-domain-socket",
                        ["path"] = "/tmp/mypowertools-runtime-policy-command-route.sock"
                    },
                    ["macos"] = new JsonObject
                    {
                        ["transport"] = "unix-domain-socket",
                        ["path"] = "/tmp/mypowertools-runtime-policy-command-route.sock"
                    }
                }
            },
            ["runtimePolicy"] = new JsonObject
            {
                ["preferred"] = "inproc",
                ["allowInProc"] = true,
                ["inProcRules"] = RuntimePolicyInProcRules(3000),
                ["operationRules"] = RuntimePolicyOperationRules()
            },
            ["capabilities"] = new JsonArray("status", "commands"),
            ["staticIndexes"] = new JsonObject
            {
                ["commands"] = "commands.index.json"
            }
        };
        var commands = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["commands"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "runtime-policy-selection.status",
                    ["title"] = "Policy status command",
                    ["kind"] = "action",
                    ["execution"] = new JsonObject { ["type"] = "module.execute" }
                },
                new JsonObject
                {
                    ["id"] = "runtime-policy-selection.external",
                    ["title"] = "Policy external command",
                    ["kind"] = "action",
                    ["constraints"] = new JsonArray(MptOperationConstraints.RunsExternalProcesses),
                    ["execution"] = new JsonObject { ["type"] = "module.execute" }
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

    private static JsonObject RuntimePolicyOperationRules()
    {
        return new JsonObject
        {
            ["status"] = "inproc-or-sidecar",
            ["settings"] = "inproc-or-sidecar",
            ["commandProvider"] = "inproc-or-sidecar",
            ["longRunningCommand"] = "sidecar-required",
            ["systemMutation"] = "broker-required",
            ["nativeHardware"] = "sidecar-required",
            ["elevatedWrite"] = "broker-required",
            ["externalProcess"] = "sidecar-required"
        };
    }

    private static void WriteMissingAssemblyInProcModule(string packageRoot, bool allowDevelopmentFallback)
    {
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["id"] = "sample.dotnet",
            ["packageId"] = "sample-dotnet",
            ["displayName"] = "Sample .NET Module",
            ["version"] = "0.2.0",
            ["moduleSdk"] = "1.0",
            ["entrypoints"] = new JsonArray
            {
                new JsonObject
                {
                    ["kind"] = "inproc-dotnet",
                    ["priority"] = 100,
                    ["assembly"] = "MyPowerTools.SampleModules.DotNet.dll",
                    ["type"] = "MyPowerTools.SampleModules.DotNet.SampleDotNetModule"
                }
            },
            ["capabilities"] = new JsonArray("status", "commands")
        };

        if (allowDevelopmentFallback)
        {
            manifest["development"] = new JsonObject
            {
                ["allowAlreadyLoadedFallback"] = true
            };
        }

        File.WriteAllText(
            Path.Combine(packageRoot, "module.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
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

    private static string ReadShellViewModelsText()
    {
        var viewModelRoot = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "ViewModels");
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(viewModelRoot, "*.cs", SearchOption.AllDirectories)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }

    private static string ReadShellWorkspaceControllerText()
    {
        var servicesRoot = Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Services");
        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(servicesRoot, "ShellWorkspaceController*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllText));
    }

    private static Dictionary<string, string?> CaptureEnvironment(IReadOnlyList<string> names)
    {
        return names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.OrdinalIgnoreCase);
    }

    private static void RestoreEnvironment(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var pair in values)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    private static string CanonicalPathText(string value)
    {
        return value.Replace('\\', '/').TrimEnd('/');
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

    private static string ReserveUnusedLoopbackUrl()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return $"http://127.0.0.1:{port}";
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetAsync(params string[] arguments)
    {
        if (TryCreateBuiltProjectStartInfo(arguments, redirectOutput: true, out var projectPsi))
        {
            return await RunProcessAsync(projectPsi, TimeSpan.FromMinutes(3));
        }

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

        DisableBuildServerReuse(psi);
        return await RunProcessAsync(psi, TimeSpan.FromMinutes(5));
    }

    private static async Task<(int ExitCode, string Output)> RunPwshAsync(params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "pwsh.exe",
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

        return await RunProcessAsync(psi, TimeSpan.FromMinutes(5));
    }

    private static Process StartDotnetProcess(params string[] arguments)
    {
        if (TryCreateBuiltProjectStartInfo(arguments, redirectOutput: false, out var projectPsi))
        {
            return Process.Start(projectPsi) ?? throw new InvalidOperationException($"Could not start {projectPsi.FileName}.");
        }

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

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(ProcessStartInfo psi, TimeSpan timeout)
    {
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}.");
        var outputTask = psi.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
        var errorTask = psi.RedirectStandardError ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
        using var timeoutSource = new CancellationTokenSource(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            KillProcessTree(process);
            var partialOutput = await ReadProcessOutputWithTimeoutAsync(outputTask, errorTask, TimeSpan.FromSeconds(5));
            return (-1, $"Process timed out after {timeout}.\n{partialOutput}");
        }

        return (process.ExitCode, await ReadProcessOutputWithTimeoutAsync(outputTask, errorTask, TimeSpan.FromSeconds(5)));
    }

    private static async Task<string> ReadProcessOutputWithTimeoutAsync(Task<string> outputTask, Task<string> errorTask, TimeSpan timeout)
    {
        var combined = Task.WhenAll(outputTask, errorTask);
        if (await Task.WhenAny(combined, Task.Delay(timeout)) != combined)
        {
            return $"Process exited, but redirected output streams did not close within {timeout}.";
        }

        var output = outputTask.Result;
        var error = errorTask.Result;
        return output + error;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool TryCreateBuiltProjectStartInfo(
        IReadOnlyList<string> dotnetArguments,
        bool redirectOutput,
        out ProcessStartInfo psi)
    {
        psi = new ProcessStartInfo();
        if (dotnetArguments.Count < 4 ||
            !string.Equals(dotnetArguments[0], "run", StringComparison.Ordinal) ||
            !string.Equals(dotnetArguments[1], "--project", StringComparison.Ordinal))
        {
            return false;
        }

        var projectPath = dotnetArguments[2];
        var separatorIndex = -1;
        for (var index = 3; index < dotnetArguments.Count; index++)
        {
            if (string.Equals(dotnetArguments[index], "--", StringComparison.Ordinal))
            {
                separatorIndex = index;
                break;
            }
        }

        if (separatorIndex < 0)
        {
            return false;
        }

        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return false;
        }

        var projectName = Path.GetFileNameWithoutExtension(projectPath);
#if DEBUG
        const string buildConfiguration = "Debug";
#else
        const string buildConfiguration = "Release";
#endif
        var outputDirectory = Path.Combine(projectDirectory, "bin", buildConfiguration, "net10.0");
        var executablePath = Path.Combine(outputDirectory, OperatingSystem.IsWindows() ? $"{projectName}.exe" : projectName);
        var dllPath = Path.Combine(outputDirectory, $"{projectName}.dll");
        var useExecutable = File.Exists(executablePath);
        if (!useExecutable && !File.Exists(dllPath))
        {
            return false;
        }

        psi = new ProcessStartInfo
        {
            FileName = useExecutable ? executablePath : "dotnet",
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = true
        };
        DisableBuildServerReuse(psi);

        if (!useExecutable)
        {
            psi.ArgumentList.Add(dllPath);
        }

        for (var index = separatorIndex + 1; index < dotnetArguments.Count; index++)
        {
            psi.ArgumentList.Add(dotnetArguments[index]);
        }

        return true;
    }

    private static void DisableBuildServerReuse(ProcessStartInfo psi)
    {
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
    }

    private static async Task<HostControlClient> WaitForHostControlAsync(string endpointAddress)
    {
        var endpoint = new IpcEndpoint(
            OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket,
            endpointAddress);
        Exception? lastError = null;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            HostControlClient? client = null;
            try
            {
                client = HostControlClient.ForEndpoint(endpoint);
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

    private static JsonObject DoubaoArgs(string plannerBaseUrl, string toolBaseUrl, string mcpBaseUrl)
    {
        return new JsonObject
        {
            ["plannerBaseUrl"] = plannerBaseUrl,
            ["toolBaseUrl"] = toolBaseUrl,
            ["mcpBaseUrl"] = mcpBaseUrl,
            ["plannerHealthPath"] = "/health",
            ["toolHealthPath"] = "/health",
            ["mcpHealthPath"] = "/health"
        };
    }

    private static JsonObject SmartBirdArgs(string baseUrl)
    {
        return new JsonObject
        {
            ["baseUrl"] = baseUrl,
            ["energyServerBaseUrl"] = baseUrl,
            ["adbPath"] = "adb-missing-for-smartbird-test"
        };
    }

    private static ModuleContext CreateScreenEaseContext(string name, IDisplayService? display = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tests", name, Guid.NewGuid().ToString("N"));
        return new ModuleContext(
            "test-host",
            "1.0",
            "screenease",
            "screenease",
            Path.Combine(root, "data"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "logs"),
            PlatformId.Current().Rid,
            ["display.profile"],
            display is null
                ? null
                : new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["display.profile"] = display
                });
    }

    private static ModuleContext CreateModuleContext(string packageId, string moduleId, string name, IReadOnlyList<string> grantedCapabilities)
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tests", name, Guid.NewGuid().ToString("N"));
        return new ModuleContext(
            "test-host",
            "1.0",
            packageId,
            moduleId,
            Path.Combine(root, "state", "modules", moduleId, "data"),
            Path.Combine(root, "state", "modules", moduleId, "cache"),
            Path.Combine(root, "state", "modules", moduleId, "logs"),
            PlatformId.Current().Rid,
            grantedCapabilities);
    }

    private sealed class RecordingDisplayService : IDisplayService, IScreenEaseDisplayResetService
    {
        private readonly DisplayWriterStatus _writerStatus;
        private readonly BrokerOperationResult _applyResult;
        private readonly BrokerOperationResult _resetResult;

        public RecordingDisplayService(
            DisplayWriterStatus? writerStatus = null,
            BrokerOperationResult? applyResult = null,
            BrokerOperationResult? resetResult = null)
        {
            _writerStatus = writerStatus ?? new DisplayWriterStatus(true, "ready", "test writer ready");
            _applyResult = applyResult ?? new BrokerOperationResult(true, "success", "test profile applied");
            _resetResult = resetResult ?? new BrokerOperationResult(true, "reset", "test gamma ramp reset");
        }

        public List<DisplayProfileIntent> AppliedIntents { get; } = [];
        public int ResetCalls { get; private set; }

        public Task<IReadOnlyList<DisplaySnapshot>> ListDisplaysAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<DisplaySnapshot> displays =
            [
                new(@"\\.\DISPLAY1", "Test display", "connected", 1920, 1080, 60, "landscape", true, "test display")
            ];
            return Task.FromResult(displays);
        }

        public Task<DisplayWriterStatus> GetWriterStatusAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(_writerStatus);
        }

        public Task<BrokerOperationResult> ApplyProfileAsync(DisplayProfileIntent intent, CancellationToken cancellationToken)
        {
            AppliedIntents.Add(intent);
            return Task.FromResult(_applyResult with { Message = $"{_applyResult.Message}: {intent.ProfileId}" });
        }

        public Task<BrokerOperationResult> ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResetCalls++;
            return Task.FromResult(_resetResult);
        }
    }

    private sealed class RecordingOverlayService : IScreenEaseOverlayService
    {
        private ScreenEaseOverlayState _state = ScreenEaseOverlayState.Hidden();
        public int ApplyCalls { get; private set; }
        public int HideCalls { get; private set; }
        public bool DisposeCalled { get; private set; }

        public Task<ScreenEaseOverlayState> GetStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_state);
        }

        public Task<ScreenEaseOverlayState> ApplyAsync(ScreenEaseOverlaySettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            var normalized = ScreenEaseOverlaySettings.Normalize(settings);
            _state = new ScreenEaseOverlayState(true, normalized.OpacityPercent, normalized.ColorHex, 2, "applied", "test overlay applied");
            return Task.FromResult(_state);
        }

        public Task<ScreenEaseOverlayState> HideAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HideCalls++;
            _state = ScreenEaseOverlayState.Hidden(_state.ColorHex);
            return Task.FromResult(_state);
        }

        public void Dispose()
        {
            DisposeCalled = true;
        }
    }

    private sealed class RecordingModuleHotkeyConfigurationService : IModuleHotkeyConfigurationService
    {
        public IReadOnlyList<ModuleHotkeyConfiguration> LastApplied { get; private set; } = [];
        public int ApplyCount { get; private set; }

        public Task ApplyAsync(IReadOnlyList<ModuleHotkeyConfiguration> hotkeys, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCount++;
            LastApplied = hotkeys.ToArray();
            return Task.CompletedTask;
        }
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
                var line when line.Contains(" /api/events ", StringComparison.Ordinal) => ("200 OK", "{\"events\":[{\"id\":\"evt-1\",\"level\":\"info\",\"message\":\"temperature stable\"}]}"),
                var line when line.Contains(" /api/energy/status ", StringComparison.Ordinal) => ("200 OK", "{\"enabled\":true,\"online\":true,\"url\":\"http://127.0.0.1:18988\"}"),
                var line when line.Contains(" /api/ping ", StringComparison.Ordinal) => ("200 OK", "pong token=abc123"),
                var line when line.Contains(" /health ", StringComparison.Ordinal) => ("200 OK", "{\"status\":\"ok\",\"service\":\"test-health\"}"),
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

    private sealed class RecordingServiceManager : IServiceManager
    {
        public List<string> Operations { get; } = [];

        public Task<ServiceStatus> GetStatusAsync(string serviceName, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ServiceStatus(serviceName, "running", $"{serviceName} is running."));
        }

        public Task<BrokerOperationResult> StartAsync(string serviceName, CancellationToken cancellationToken)
        {
            Operations.Add($"start:{serviceName}");
            return Task.FromResult(new BrokerOperationResult(true, "started", $"Started {serviceName}."));
        }

        public Task<BrokerOperationResult> StopAsync(string serviceName, CancellationToken cancellationToken)
        {
            Operations.Add($"stop:{serviceName}");
            return Task.FromResult(new BrokerOperationResult(true, "stopped", $"Stopped {serviceName}."));
        }
    }

    private sealed class SequenceHostControlProbe : IHostControlConnectionProbe
    {
        private readonly Queue<object> _steps;

        public SequenceHostControlProbe(params object[] steps)
        {
            _steps = new Queue<object>(steps);
        }

        public Task<HostControlConnectionProbeResult> PingAsync(CancellationToken cancellationToken)
        {
            var step = _steps.Count == 0
                ? new HostControlConnectionProbeResult("0.2.0", "running")
                : _steps.Dequeue();

            if (step is Exception ex)
            {
                return Task.FromException<HostControlConnectionProbeResult>(ex);
            }

            return Task.FromResult((HostControlConnectionProbeResult)step);
        }
    }

    private sealed class SequenceHostEventSource : IHostControlEventSource
    {
        private readonly Queue<IReadOnlyList<object>> _subscriptions;

        public SequenceHostEventSource(params IReadOnlyList<object>[] subscriptions)
        {
            _subscriptions = new Queue<IReadOnlyList<object>>(subscriptions);
        }

        public List<ulong> RequestedSeqs { get; } = [];

        public async IAsyncEnumerable<HostProto.HostEvent> SubscribeAsync(
            ulong lastEventSeq,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestedSeqs.Add(lastEventSeq);
            if (_subscriptions.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            foreach (var step in _subscriptions.Dequeue())
            {
                await Task.Yield();
                if (step is Exception ex)
                {
                    throw ex;
                }

                yield return (HostProto.HostEvent)step;
            }
        }
    }

    private static HostProto.HostEvent HostEvent(ulong seq, string sourceId, string type)
    {
        return new HostProto.HostEvent
        {
            Seq = seq,
            SourceId = sourceId,
            Type = type,
            Time = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        };
    }

    private sealed class RecordingSettingsTransportRuntime : IModuleTransportRuntime
    {
        public RecordingSettingsTransportRuntime(string kind)
        {
            Kind = kind;
        }

        public string Kind { get; }
        public int ValidateCount { get; private set; }
        public int ApplyCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public SettingsPatch? ValidatedPatch { get; private set; }
        public SettingsSnapshotDocument? AppliedSnapshot { get; private set; }
        public bool FailApply { get; init; }
        public bool BlockCommandUntilCancelled { get; init; }
        public TaskCompletionSource CommandStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ModuleStatusSnapshot?>(new ModuleStatusSnapshot(
                module.Module.Manifest.Id,
                "running",
                "recording settings runtime",
                DateTimeOffset.UtcNow,
                [],
                0));
        }

        public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new SettingsSchemaDocument(module.Module.Manifest.Id, """{"type":"object","properties":{}}"""));
        }

        public ValueTask<SettingsValidationResult> ValidateSettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsPatch patch, CancellationToken cancellationToken)
        {
            ValidateCount++;
            ValidatedPatch = patch;
            return ValueTask.FromResult(new SettingsValidationResult(true, []));
        }

        public ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(RuntimeModuleRecord module, ModuleContext context, SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
        {
            ApplyCount++;
            AppliedSnapshot = snapshot;
            if (FailApply)
            {
                throw new InvalidOperationException("synthetic apply failure");
            }

            return ValueTask.FromResult(snapshot);
        }

        public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>([]);
        }

        public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(RuntimeModuleRecord module, ModuleContext context, CommandRequest request, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            if (BlockCommandUntilCancelled)
            {
                CommandStarted.SetResult();
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return new CommandExecutionResult(
                request.InvocationId,
                request.CommandId,
                "succeeded",
                true,
                "recorded");
        }
    }

    private sealed class DuplicateEventTransportRuntime : IModuleTransportRuntime
    {
        public string Kind => "inproc-dotnet";
        public List<ulong> RequestedCursors { get; } = [];

        public ValueTask<ModuleStatusSnapshot?> GetStatusAsync(RuntimeModuleRecord module, ModuleContext context, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<ModuleStatusSnapshot?>(new ModuleStatusSnapshot(
                module.Module.Manifest.Id,
                "running",
                "duplicate event transport",
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
            return ValueTask.FromResult(new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, "duplicate transport"));
        }

        public async IAsyncEnumerable<MyPowerTools.Abstractions.MptModuleEvent> SubscribeEventsAsync(
            RuntimeModuleRecord module,
            ModuleContext context,
            MyPowerTools.Abstractions.EventCursor cursor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            RequestedCursors.Add(cursor.LastEventSeq);
            await Task.Yield();
            for (var index = 0; index < 2; index++)
            {
                yield return new MyPowerTools.Abstractions.MptModuleEvent(
                    module.Module.Manifest.Id,
                    1,
                    "duplicate.test",
                    DateTimeOffset.UtcNow,
                    new JsonObject { ["message"] = "duplicate event" });
            }
        }
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata _requestHeaders = [];
        private readonly Metadata _responseTrailers = [];
        private Status _status = new(StatusCode.OK, "");
        private WriteOptions? _writeOptions;

        public TestServerCallContext(Metadata? requestHeaders = null)
        {
            if (requestHeaders is null)
            {
                return;
            }

            foreach (var header in requestHeaders)
            {
                _requestHeaders.Add(header);
            }
        }

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

    private sealed class RecordingServerStreamWriter<T> : IServerStreamWriter<T>
    {
        public List<T> Messages { get; } = [];
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(T message)
        {
            Messages.Add(message);
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
