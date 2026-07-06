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
    public async Task Inproc_already_loaded_fallback_requires_development_flag()
    {
        _ = typeof(SampleDotNetModule).Assembly;
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-fallback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteMissingAssemblyInProcModule(packageRoot, allowDevelopmentFallback: false);
        var package = new PackageReader().ReadPackageDirectory(packageRoot);
        await using var host = new InProcDotNetModuleHost();

        var blocked = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await host.LoadAsync(package.Modules.Single(), CancellationToken.None).AsTask());

        WriteMissingAssemblyInProcModule(packageRoot, allowDevelopmentFallback: true);
        package = new PackageReader().ReadPackageDirectory(packageRoot);
        var loaded = await host.LoadAsync(package.Modules.Single(), CancellationToken.None);
        var result = await loaded.ExecuteCommandAsync(new CommandRequest("fallback", "sample.dotnet.ping", new JsonObject()), CancellationToken.None);

        Assert.Contains("development.allowAlreadyLoadedFallback", blocked.Message);
        Assert.True(result.Success, result.Error?.Message);
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
    public async Task Inproc_disk_module_uses_collectible_load_context_and_unloads()
    {
        var weakReference = await LoadAndDisposeAdbForwarderAsync();

        for (var attempt = 0; attempt < 10 && weakReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(100);
        }

        Assert.False(weakReference.IsAlive);
    }

    [Fact]
    public async Task Runtime_restarts_or_marks_inproc_module_for_runner_restart()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-clean-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        await BuildGeneratedInProcPluginPackageAsync(
            packageRoot,
            "clean.restart",
            "clean-restart",
            "CleanRestartPlugin",
            "clean-restart",
            "1.0.0.0");

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-inproc-clean-restart", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        var dynamicCount = RefreshDynamicCommandsAndRelease(runtime);
        var before = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var restart = await runtime.RestartRuntimeProcessAsync("inproc-dotnet", before.PoolKey, CancellationToken.None);
        var after = runtime.GetRuntimeDiagnostics();

        Assert.True(dynamicCount > 0);
        Assert.Equal("loaded", before.State);
        Assert.Equal(Environment.ProcessId, before.ProcessId);
        Assert.Contains("clean.restart", restart.ModuleIds);
        if (restart.Success)
        {
            Assert.Equal("unloaded", restart.State);
            Assert.Empty(after.Processes);
        }
        else
        {
            Assert.Equal("pending-runner-restart", restart.State);
            var pending = Assert.Single(after.Processes);
            Assert.Equal("pending-runner-restart", pending.State);
            Assert.Contains("clean.restart", pending.ModuleIds);
        }
    }

    [Fact]
    public async Task Runtime_marks_inproc_unload_failure_as_pending_runner_restart()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-leaky-restart", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.leaky",
            "sample-dotnet-leaky",
            "Leaky .NET Module",
            typeof(LeakyDotNetModule).FullName!);

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-inproc-leaky-restart", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var before = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        var restart = await runtime.RestartRuntimeProcessAsync("inproc-dotnet", before.PoolKey, CancellationToken.None);
        var pending = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);

        Assert.True(dynamicCount > 0);
        Assert.Equal("loaded", before.State);
        Assert.False(restart.Success);
        Assert.Equal("pending-runner-restart", restart.State);
        Assert.Contains("collectible AssemblyLoadContext", restart.Message);
        Assert.Equal("pending-runner-restart", pending.State);
        Assert.Equal("manual-runner-restart", pending.RestartPolicy);
        Assert.Contains("collectible AssemblyLoadContext", pending.PolicyReason);
        Assert.Contains("sample.dotnet.leaky", pending.ModuleIds);

        var package = new PackageReader().ReadPackageDirectory(packageRoot);
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await host.LoadAsync(package.Modules.Single(), CancellationToken.None));
        Assert.Contains("restart Runner", blocked.Message);
    }

    [Fact]
    public async Task Inproc_plugins_with_conflicting_dependency_versions_load_in_separate_contexts()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-inproc-conflict", Guid.NewGuid().ToString("N"));
        var packageOne = Path.Combine(root, "package-one");
        var packageTwo = Path.Combine(root, "package-two");
        Directory.CreateDirectory(packageOne);
        Directory.CreateDirectory(packageTwo);

        await BuildGeneratedInProcPluginPackageAsync(packageOne, "conflict.one", "conflict-one", "ConflictPluginOne", "dependency-v1", "1.0.0.0");
        await BuildGeneratedInProcPluginPackageAsync(packageTwo, "conflict.two", "conflict-two", "ConflictPluginTwo", "dependency-v2", "2.0.0.0");

        await using var host = new InProcDotNetModuleHost();
        var reader = new PackageReader();
        var moduleOne = reader.ReadPackageDirectory(packageOne).Modules.Single();
        var moduleTwo = reader.ReadPackageDirectory(packageTwo).Modules.Single();

        var loadedOne = await host.LoadAsync(moduleOne, CreateGeneratedPluginContext("conflict-one", "conflict.one"), CancellationToken.None);
        var loadedTwo = await host.LoadAsync(moduleTwo, CreateGeneratedPluginContext("conflict-two", "conflict.two"), CancellationToken.None);
        var resultOne = await loadedOne.ExecuteCommandAsync(new CommandRequest("conflict-one", "conflict.one.dependency", new JsonObject()), CancellationToken.None);
        var resultTwo = await loadedTwo.ExecuteCommandAsync(new CommandRequest("conflict-two", "conflict.two.dependency", new JsonObject()), CancellationToken.None);
        var contextOne = AssemblyLoadContext.GetLoadContext(loadedOne.GetType().Assembly);
        var contextTwo = AssemblyLoadContext.GetLoadContext(loadedTwo.GetType().Assembly);
        var dependencyOne = contextOne!.Assemblies.Single(assembly => assembly.GetName().Name == "PluginSharedDependency");
        var dependencyTwo = contextTwo!.Assemblies.Single(assembly => assembly.GetName().Name == "PluginSharedDependency");

        Assert.True(resultOne.Success, resultOne.Error?.Message);
        Assert.True(resultTwo.Success, resultTwo.Error?.Message);
        Assert.Contains("dependency-v1", resultOne.Output);
        Assert.Contains("dependency-v2", resultTwo.Output);
        Assert.NotSame(contextOne, contextTwo);
        Assert.Equal(new Version(1, 0, 0, 0), dependencyOne.GetName().Version);
        Assert.Equal(new Version(2, 0, 0, 0), dependencyTwo.GetName().Version);
    }

    [Fact]
    public async Task Inproc_module_update_uses_shadow_copy_instead_of_original_package_dll()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-inproc-shadow-update", Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "package");
        var replacementRoot = Path.Combine(root, "replacement");
        Directory.CreateDirectory(packageRoot);
        Directory.CreateDirectory(replacementRoot);

        await BuildGeneratedInProcPluginPackageAsync(packageRoot, "shadow.update", "shadow-update", "ShadowUpdatePlugin", "dependency-v1", "1.0.0.0");
        await BuildGeneratedInProcPluginPackageAsync(replacementRoot, "shadow.update", "shadow-update", "ShadowUpdatePlugin", "dependency-v2", "2.0.0.0");

        var reader = new PackageReader();
        var module = reader.ReadPackageDirectory(packageRoot).Modules.Single();
        var context = CreateGeneratedPluginContext("shadow-update", "shadow.update");
        await using var firstHost = new InProcDotNetModuleHost();

        var (loadedAssemblyPath, before, stillLoaded) = await LoadAndReplaceShadowPluginAsync(firstHost, module, context, packageRoot, replacementRoot);
        var restart = await firstHost.RestartProcessAsync("module:shadow.update", CancellationToken.None);
        await using var secondHost = new InProcDotNetModuleHost();
        var reloaded = await secondHost.LoadAsync(reader.ReadPackageDirectory(packageRoot).Modules.Single(), context, CancellationToken.None);
        var after = await reloaded.ExecuteCommandAsync(new CommandRequest("shadow-after", "shadow.update.dependency", new JsonObject()), CancellationToken.None);

        Assert.True(before.Success, before.Error?.Message);
        Assert.True(stillLoaded.Success, stillLoaded.Error?.Message);
        Assert.True(after.Success, after.Error?.Message);
        Assert.True(restart.Success, restart.Message);
        Assert.Contains("inproc-shadow", loadedAssemblyPath);
        Assert.StartsWith(context.CacheDirectory, loadedAssemblyPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(loadedAssemblyPath.StartsWith(packageRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("dependency-v1", before.Output);
        Assert.Contains("dependency-v1", stillLoaded.Output);
        Assert.Contains("dependency-v2", after.Output);
    }

    [Fact]
    public void Production_module_projects_reference_abstractions_not_runtime()
    {
        var projectFiles = new[]
        {
            "src/AdbForwarder.MyPowerTools/AdbForwarder.MyPowerTools.csproj",
            "src/AndroidTools.MyPowerTools/AndroidTools.MyPowerTools.csproj",
            "src/DoubaoAgent.MyPowerTools/DoubaoAgent.MyPowerTools.csproj",
            "src/ScreenEase.MyPowerTools/ScreenEase.MyPowerTools.csproj",
            "src/SmartBirdThermostat.MyPowerTools/SmartBirdThermostat.MyPowerTools.csproj"
        };

        foreach (var projectFile in projectFiles)
        {
            var content = File.ReadAllText(Path.Combine(Root, projectFile));
            Assert.Contains("MyPowerTools.Abstractions.csproj", content);
            Assert.DoesNotContain("MyPowerTools.Runtime.csproj", content);
        }
    }

    [Fact]
    public void Production_modules_and_templates_import_abstractions_sdk_namespace()
    {
        var sourceRoots = new[]
        {
            "src/AdbForwarder.MyPowerTools",
            "src/AndroidTools.MyPowerTools",
            "src/DoubaoAgent.MyPowerTools",
            "src/ScreenEase.MyPowerTools",
            "src/SmartBirdThermostat.MyPowerTools",
            "src/MyPowerTools.SampleModules.DotNet",
            "templates/dotnet-inproc-module"
        };

        foreach (var sourceRoot in sourceRoots)
        {
            var files = Directory.EnumerateFiles(Path.Combine(Root, sourceRoot), "*.cs", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var source = string.Join(Environment.NewLine, files.Select(File.ReadAllText));
            Assert.Contains("using MyPowerTools.Abstractions;", source);
            Assert.DoesNotContain("using MyPowerTools.Runtime;", source);
        }
    }

    [Fact]
    public void ScreenEase_module_uses_capability_provider_not_concrete_platform_packs()
    {
        var project = File.ReadAllText(Path.Combine(Root, "src/ScreenEase.MyPowerTools/ScreenEase.MyPowerTools.csproj"));
        var source = File.ReadAllText(Path.Combine(Root, "src/ScreenEase.MyPowerTools/ScreenEaseModule.cs"));
        var combined = project + Environment.NewLine + source;

        foreach (var forbidden in new[]
        {
            "MyPowerTools.Platform.Windows",
            "MyPowerTools.Platform.Mac",
            "MyPowerTools.Platform.Linux",
            "WindowsPlatformPack",
            "MacPlatformPack",
            "LinuxPlatformPack"
        })
        {
            Assert.DoesNotContain(forbidden, combined);
        }

        Assert.Contains("TryGetCapability<IDisplayService>", source);
        Assert.Contains("\"display.profile\"", source);
    }

    [Fact]
    public void Runtime_shell_and_host_do_not_reference_concrete_module_projects()
    {
        var hostProjectFiles = new[]
        {
            "src/MyPowerTools.Runtime/MyPowerTools.Runtime.csproj",
            "src/MyPowerTools.HostControl/MyPowerTools.HostControl.csproj",
            "src/MyPowerTools.Shell.Avalonia/MyPowerTools.Shell.Avalonia.csproj"
        };
        var concreteModuleTokens = new[]
        {
            "AdbForwarder.MyPowerTools",
            "AndroidTools.MyPowerTools",
            "DoubaoAgent.MyPowerTools",
            "ScreenEase.MyPowerTools",
            "SmartBirdThermostat.MyPowerTools"
        };

        foreach (var projectFile in hostProjectFiles)
        {
            var content = File.ReadAllText(Path.Combine(Root, projectFile));
            foreach (var token in concreteModuleTokens)
            {
                Assert.DoesNotContain(token, content);
            }
        }

        foreach (var sourceRoot in new[] { "src/MyPowerTools.Runtime", "src/MyPowerTools.HostControl", "src/MyPowerTools.Shell.Avalonia" })
        {
            foreach (var sourceFile in Directory.EnumerateFiles(Path.Combine(Root, sourceRoot), "*.cs", SearchOption.AllDirectories))
            {
                var content = File.ReadAllText(sourceFile);
                foreach (var token in concreteModuleTokens)
                {
                    Assert.DoesNotContain(token, content);
                }
            }
        }
    }

    [Fact]
    public void Abstractions_project_exposes_named_plugin_contracts()
    {
        var contracts = File.ReadAllText(Path.Combine(Root, "src/MyPowerTools.Abstractions/NamedPluginContracts.cs"));
        var pluginContracts = File.ReadAllText(Path.Combine(Root, "src/MyPowerTools.Abstractions/PluginContracts.cs"));
        var compatibility = File.ReadAllText(Path.Combine(Root, "src/MyPowerTools.Abstractions/RuntimeCompatibility.cs"));
        var project = File.ReadAllText(Path.Combine(Root, "src/MyPowerTools.Abstractions/MyPowerTools.Abstractions.csproj"));

        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project);
        Assert.Contains("namespace MyPowerTools.Abstractions;", pluginContracts);
        Assert.Contains("interface IMptModule", pluginContracts);
        Assert.Contains("record UiSurfaceDescriptor", pluginContracts);
        Assert.Contains("namespace MyPowerTools.Runtime;", compatibility);
        Assert.Contains("[Obsolete(\"Use MyPowerTools.Abstractions.IMptModule.\")]", compatibility);
        foreach (var token in new[]
        {
            "interface IMptModuleFactory",
            "interface IModuleContext",
            "interface ICommandContext",
            "record ModuleStatus",
            "record ModuleCommand",
            "record CommandResult",
            "record SettingsSchema",
            "record ModuleEvent"
        })
        {
            Assert.Contains(token, contracts);
        }
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
}
