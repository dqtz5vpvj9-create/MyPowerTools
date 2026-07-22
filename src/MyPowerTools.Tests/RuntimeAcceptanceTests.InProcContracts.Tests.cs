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
    public async Task Inproc_direct_host_fault_restart_reclaims_context()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-direct-fault", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.fault-injection",
            "sample-dotnet-fault-injection",
            "Direct fault fixture",
            typeof(FaultInjectionDotNetModule).FullName!);
        await SetInProcMaxCallAsync(packageRoot, 1000);
        await using var host = new InProcDotNetModuleHost();
        await using var runtime = CreateInProcFixtureRuntime(host, "direct-fault-runtime");
        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);
        var context = CreateModuleContext(
            "sample-dotnet-fault-injection",
            "sample.dotnet.fault-injection",
            "direct-fault-context",
            ["status", "commands"]);
        await host.ListCommandsAsync(module, context, CancellationToken.None);
        var commands = new[]
        {
            "sample.dotnet.fault.throw",
            "sample.dotnet.fault.timeout",
            "sample.dotnet.fault.throw"
        };
        for (var attempt = 0; attempt < commands.Length; attempt++)
        {
            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await host.ExecuteCommandAsync(
                    module,
                    context,
                    new CommandRequest($"direct-fault-{attempt}", commands[attempt], new JsonObject()),
                    CancellationToken.None));
        }

        var restartStopwatch = Stopwatch.StartNew();
        var restart = await host.RestartProcessAsync("module:sample.dotnet.fault-injection", CancellationToken.None);
        restartStopwatch.Stop();
        Assert.True(
            restart.Success,
            $"Success={restart.Success}; State={restart.State}; Elapsed={restartStopwatch.Elapsed}; Message={restart.Message}");
    }

    [Fact]
    public async Task Inproc_soft_isolation_quarantines_repeated_faults_without_stopping_other_modules()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-soft-isolation", Guid.NewGuid().ToString("N"));
        var faultRoot = Path.Combine(packageRoot, "fault");
        var healthyRoot = Path.Combine(packageRoot, "healthy");
        Directory.CreateDirectory(faultRoot);
        Directory.CreateDirectory(healthyRoot);
        WriteInProcDotNetModulePackage(
            faultRoot,
            "sample.dotnet.fault-injection",
            "sample-dotnet-fault-injection",
            "Fault injection module",
            typeof(FaultInjectionDotNetModule).FullName!);
        WriteInProcDotNetModulePackage(
            healthyRoot,
            "sample.dotnet",
            "sample-dotnet",
            "Healthy sample module",
            typeof(SampleDotNetModule).FullName!);

        var faultManifestPath = Path.Combine(faultRoot, "module.json");
        var faultManifest = JsonNode.Parse(await File.ReadAllTextAsync(faultManifestPath))!.AsObject();
        faultManifest["runtimePolicy"] = new JsonObject
        {
            ["preferred"] = "inproc",
            ["allowInProc"] = true,
            ["inProcRules"] = new JsonObject
            {
                ["maxCallMs"] = 1000,
                ["allowNativeDll"] = false,
                ["allowWindow"] = false,
                ["allowBackgroundThreads"] = false,
                ["loadContext"] = "collectible",
                ["shadowCopy"] = true,
            }
        };
        await File.WriteAllTextAsync(
            faultManifestPath,
            faultManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-soft-isolation", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var registeredCommands = runtime.ListCommands(null);
        Assert.Contains(registeredCommands, command => command.Id == "sample.dotnet.fault.throw");
        Assert.Contains(registeredCommands, command => command.Id == "sample.dotnet.fault.timeout");
        Assert.Contains(registeredCommands, command => command.Id == "sample.dotnet.fault.ok");

        var firstFault = await runtime.ExecuteCommandAsync(
            new CommandRequest("fault-1", "sample.dotnet.fault.throw", new JsonObject()),
            CancellationToken.None);
        var timeoutFault = await runtime.ExecuteCommandAsync(
            new CommandRequest("fault-2", "sample.dotnet.fault.timeout", new JsonObject()),
            CancellationToken.None);
        var thirdFault = await runtime.ExecuteCommandAsync(
            new CommandRequest("fault-3", "sample.dotnet.fault.throw", new JsonObject()),
            CancellationToken.None);
        var blocked = await runtime.ExecuteCommandAsync(
            new CommandRequest("fault-blocked", "sample.dotnet.fault.ok", new JsonObject()),
            CancellationToken.None);
        var healthy = await runtime.ExecuteCommandAsync(
            new CommandRequest("healthy", "sample.dotnet.ping", new JsonObject()),
            CancellationToken.None);
        var diagnostics = runtime.GetRuntimeDiagnostics();

        Assert.False(firstFault.Success);
        Assert.False(timeoutFault.Success);
        Assert.Contains("maxCallMs=1000", timeoutFault.Error?.Message ?? "");
        Assert.False(thirdFault.Success);
        Assert.False(blocked.Success);
        Assert.Contains("quarantined", blocked.Error?.Message ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.True(healthy.Success, healthy.Error?.Message);
        Assert.Contains("pong", healthy.Output);
        Assert.Contains(diagnostics.Processes, process =>
            process.TransportKind == "inproc-dotnet" &&
            process.PoolKey == "module:sample.dotnet.fault-injection" &&
            process.State == "circuit-open" &&
            process.PolicyReason.Contains("Soft isolation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(diagnostics.Processes, process =>
            process.TransportKind == "inproc-dotnet" &&
            process.PoolKey == "module:sample.dotnet" &&
            process.State == "loaded");

        var reset = await runtime.RestartRuntimeProcessAsync(
            "inproc-dotnet",
            "module:sample.dotnet.fault-injection",
            CancellationToken.None);
        Assert.True(reset.Success, reset.Message);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var recovered = await runtime.ExecuteCommandAsync(
            new CommandRequest("fault-recovered", "sample.dotnet.fault.ok", new JsonObject()),
            CancellationToken.None);

        Assert.True(recovered.Success, recovered.Error?.Message);
        Assert.Contains("recovered", recovered.Output);
    }

    [Fact]
    public async Task Inproc_timeout_that_ignores_cancellation_opens_circuit_immediately_without_disposing_live_code()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-orphan-isolation", Guid.NewGuid().ToString("N"));
        var faultRoot = Path.Combine(packageRoot, "fault");
        var healthyRoot = Path.Combine(packageRoot, "healthy");
        Directory.CreateDirectory(faultRoot);
        Directory.CreateDirectory(healthyRoot);
        WriteInProcDotNetModulePackage(
            faultRoot,
            "sample.dotnet.fault-injection",
            "sample-dotnet-fault-injection",
            "Fault injection module",
            typeof(FaultInjectionDotNetModule).FullName!);
        WriteInProcDotNetModulePackage(
            healthyRoot,
            "sample.dotnet",
            "sample-dotnet",
            "Healthy sample module",
            typeof(SampleDotNetModule).FullName!);

        var manifestPath = Path.Combine(faultRoot, "module.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["runtimePolicy"] = new JsonObject
        {
            ["preferred"] = "inproc",
            ["allowInProc"] = true,
            ["inProcRules"] = new JsonObject
            {
                ["maxCallMs"] = 1000,
                ["allowNativeDll"] = false,
                ["allowWindow"] = false,
                ["allowBackgroundThreads"] = false,
                ["loadContext"] = "collectible",
                ["shadowCopy"] = true
            }
        };
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-orphan-isolation", Guid.NewGuid().ToString("N"))),
            [host]);
        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        var timedOut = await runtime.ExecuteCommandAsync(
            new CommandRequest("orphan-timeout", "sample.dotnet.fault.ignore-timeout", new JsonObject()),
            CancellationToken.None);
        var blocked = await runtime.ExecuteCommandAsync(
            new CommandRequest("orphan-blocked", "sample.dotnet.fault.ok", new JsonObject()),
            CancellationToken.None);
        var healthy = await runtime.ExecuteCommandAsync(
            new CommandRequest("orphan-healthy", "sample.dotnet.ping", new JsonObject()),
            CancellationToken.None);
        var faultProcess = Assert.Single(
            runtime.GetRuntimeDiagnostics().Processes,
            process => process.PoolKey == "module:sample.dotnet.fault-injection");
        var restart = await runtime.RestartRuntimeProcessAsync(
            "inproc-dotnet",
            "module:sample.dotnet.fault-injection",
            CancellationToken.None);

        Assert.False(timedOut.Success);
        Assert.Contains("maxCallMs=1000", timedOut.Error?.Message ?? "");
        Assert.False(blocked.Success);
        Assert.True(healthy.Success, healthy.Error?.Message);
        Assert.Equal("runner-restart-required", faultProcess.State);
        Assert.Contains("still running", faultProcess.PolicyReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("manual-runner-restart", faultProcess.RestartPolicy);
        Assert.Contains("Restart Runner", faultProcess.PolicyReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(restart.Success);
        Assert.Equal("runner-restart-required", restart.State);
    }

    [Fact]
    public async Task Inproc_initialization_faults_dispose_every_provisional_instance_before_circuit_reset()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-init-failure", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.initialize-failure",
            "sample-dotnet-initialize-failure",
            "Initialization failure fixture",
            typeof(InitializeFailureDotNetModule).FullName!);
        await SetInProcMaxCallAsync(packageRoot, 5000);

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = CreateInProcFixtureRuntime(host, "init-failure-runtime");
        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);
        var context = CreateModuleContext(
            "sample-dotnet-initialize-failure",
            "sample.dotnet.initialize-failure",
            "init-failure-context",
            ["status", "commands"]);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await host.ListCommandsAsync(module, context, CancellationToken.None));
        }

        await WaitForFixtureCounterAsync(context.DataDirectory, "disposed-instances.txt", 3, TimeSpan.FromSeconds(3));
        await WaitForFixtureCounterAsync(context.DataDirectory, "contexts-unloading.txt", 3, TimeSpan.FromSeconds(3));
        await WaitForFixtureCounterAsync(context.DataDirectory, "finalized-instances.txt", 3, TimeSpan.FromSeconds(3));
        Assert.Equal(3, ReadFixtureCounter(context.DataDirectory, "initialize-attempts.txt"));
        Assert.Equal(3, ReadFixtureCounter(context.DataDirectory, "disposed-instances.txt"));
        Assert.Equal(3, ReadFixtureCounter(context.DataDirectory, "contexts-unloading.txt"));
        Assert.Equal(3, ReadFixtureCounter(context.DataDirectory, "finalized-instances.txt"));
        var circuit = Assert.Single(host.GetProcessDiagnostics());
        Assert.Equal("circuit-open", circuit.State);

        var restart = await host.RestartProcessAsync("module:sample.dotnet.initialize-failure", CancellationToken.None);

        Assert.True(restart.Success, restart.Message);
        Assert.Equal("circuit-reset", restart.State);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await host.ListCommandsAsync(module, context, CancellationToken.None));
        await WaitForFixtureCounterAsync(context.DataDirectory, "disposed-instances.txt", 4, TimeSpan.FromSeconds(3));
        await WaitForFixtureCounterAsync(context.DataDirectory, "contexts-unloading.txt", 4, TimeSpan.FromSeconds(3));
        await WaitForFixtureCounterAsync(context.DataDirectory, "finalized-instances.txt", 4, TimeSpan.FromSeconds(3));
        Assert.Equal(4, ReadFixtureCounter(context.DataDirectory, "initialize-attempts.txt"));
        Assert.Equal(4, ReadFixtureCounter(context.DataDirectory, "disposed-instances.txt"));
        Assert.Equal(4, ReadFixtureCounter(context.DataDirectory, "contexts-unloading.txt"));
        Assert.Equal(4, ReadFixtureCounter(context.DataDirectory, "finalized-instances.txt"));
    }

    [Fact]
    public async Task Inproc_slow_success_started_before_a_fault_cannot_erase_the_newer_fault()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-fault-order", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.fault-injection",
            "sample-dotnet-fault-injection",
            "Fault ordering fixture",
            typeof(FaultInjectionDotNetModule).FullName!);
        await SetInProcMaxCallAsync(packageRoot, 5000);

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = CreateInProcFixtureRuntime(host, "fault-order-runtime");
        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);
        var context = CreateModuleContext(
            "sample-dotnet-fault-injection",
            "sample.dotnet.fault-injection",
            "fault-order-context",
            ["status", "commands"]);
        await host.ListCommandsAsync(module, context, CancellationToken.None);

        var slowSuccess = host.ExecuteCommandAsync(
            module,
            context,
            new CommandRequest("slow-success", "sample.dotnet.fault.slow-success", new JsonObject()),
            CancellationToken.None).AsTask();
        await WaitForFixtureFileAsync(context.DataDirectory, "slow-success.started", TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await host.ExecuteCommandAsync(
                module,
                context,
                new CommandRequest("newer-fault", "sample.dotnet.fault.throw", new JsonObject()),
                CancellationToken.None));
        TouchFixtureFile(context.DataDirectory, "slow-success.release");
        var successfulResult = await slowSuccess.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(successfulResult.Success, successfulResult.Error?.Message);
        var degraded = Assert.Single(host.GetProcessDiagnostics());
        Assert.Equal("degraded", degraded.State);
        Assert.Contains("1/3", degraded.PolicyReason);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await host.ExecuteCommandAsync(
                    module,
                    context,
                    new CommandRequest($"follow-up-fault-{attempt}", "sample.dotnet.fault.throw", new JsonObject()),
                    CancellationToken.None));
        }

        var circuit = Assert.Single(host.GetProcessDiagnostics());
        Assert.Equal("circuit-open", circuit.State);
    }

    [Theory]
    [InlineData(0L, "event-open.started", "event-open.release", "events.subscribe.open")]
    [InlineData(1L, "event-current.started", "event-current.release", "events.subscribe.current")]
    public async Task Inproc_event_setup_and_current_that_ignore_cancellation_open_runner_restart_circuit(
        long cursorSequence,
        string startedFile,
        string releaseFile,
        string operation)
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-event-fault", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.event-fault",
            "sample-dotnet-event-fault",
            "Event fault fixture",
            typeof(EventFaultInjectionDotNetModule).FullName!);
        await SetInProcMaxCallAsync(packageRoot, 1000);

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = CreateInProcFixtureRuntime(host, $"event-fault-{cursorSequence}");
        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);
        var context = CreateModuleContext(
            "sample-dotnet-event-fault",
            "sample.dotnet.event-fault",
            $"event-fault-context-{cursorSequence}",
            ["status", "events"]);
        // The host event relay consumes the module stream with its own cursor; the
        // fixture selects its blocking phase via a control file.
        Directory.CreateDirectory(context.DataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(context.DataDirectory, "blocking-mode.txt"),
            operation.Contains("current", StringComparison.OrdinalIgnoreCase) ? "current" : "open");
        await host.GetStatusAsync(module, context, CancellationToken.None);
        var enumerator = host.SubscribeEventsAsync(
            module,
            context,
            new MyPowerTools.Abstractions.EventCursor((ulong)cursorSequence),
            CancellationToken.None).GetAsyncEnumerator();

        try
        {
            var moveNext = enumerator.MoveNextAsync().AsTask();
            await WaitForFixtureFileAsync(context.DataDirectory, startedFile, TimeSpan.FromSeconds(3));
            var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await moveNext.WaitAsync(TimeSpan.FromSeconds(6)));

            Assert.Contains("maxCallMs=1000", failure.Message);
            Assert.Contains(operation, failure.Message, StringComparison.OrdinalIgnoreCase);
            var diagnostic = Assert.Single(host.GetProcessDiagnostics());
            Assert.Equal("runner-restart-required", diagnostic.State);
            Assert.Equal("manual-runner-restart", diagnostic.RestartPolicy);
            Assert.Contains("Restart Runner", diagnostic.PolicyReason, StringComparison.OrdinalIgnoreCase);

            var restart = await host.RestartProcessAsync(
                "module:sample.dotnet.event-fault",
                CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(4));
            Assert.False(restart.Success);
            Assert.Equal("runner-restart-required", restart.State);
        }
        finally
        {
            TouchFixtureFile(context.DataDirectory, releaseFile);
            try
            {
                await enumerator.DisposeAsync();
            }
            catch
            {
                // The stream has already crossed its terminal fault boundary.
            }
        }
    }

    [Fact]
    public async Task Inproc_lingering_context_after_circuit_restart_blocks_a_fresh_instance()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-leaky-circuit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.leaky",
            "sample-dotnet-leaky",
            "Leaky circuit fixture",
            typeof(LeakyDotNetModule).FullName!);
        await SetInProcMaxCallAsync(packageRoot, 1000);

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = CreateInProcFixtureRuntime(host, "leaky-circuit-runtime");
        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);
        var context = CreateModuleContext(
            "sample-dotnet-leaky",
            "sample.dotnet.leaky",
            "leaky-circuit-context",
            ["status", "commands"]);
        await host.ListCommandsAsync(module, context, CancellationToken.None);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await host.ExecuteCommandAsync(
                    module,
                    context,
                    new CommandRequest($"leaky-fault-{attempt}", "sample.dotnet.leaky.throw", new JsonObject()),
                    CancellationToken.None));
        }

        Assert.Equal(1, ReadFixtureCounter(context.DataDirectory, "instances.txt"));
        var restartStopwatch = Stopwatch.StartNew();
        var restart = await host.RestartProcessAsync("module:sample.dotnet.leaky", CancellationToken.None);
        restartStopwatch.Stop();

        Assert.False(restart.Success);
        Assert.True(
            string.Equals("pending-runner-restart", restart.State, StringComparison.Ordinal),
            $"Expected pending-runner-restart, got '{restart.State}' after {restartStopwatch.Elapsed}. {restart.Message}");
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
            await host.ListCommandsAsync(module, context, CancellationToken.None));
        Assert.Equal(1, ReadFixtureCounter(context.DataDirectory, "instances.txt"));
        var pending = Assert.Single(host.GetProcessDiagnostics());
        Assert.Equal("pending-runner-restart", pending.State);
        Assert.Equal("manual-runner-restart", pending.RestartPolicy);
    }

    [Fact]
    public async Task Inproc_concurrent_dispose_is_idempotent_and_disposes_module_once()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-concurrent-dispose", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.dispose-tracking",
            "sample-dotnet-dispose-tracking",
            "Dispose tracking fixture",
            typeof(DisposeTrackingDotNetModule).FullName!);
        var module = new PackageReader().ReadPackageDirectory(packageRoot).Modules.Single();
        var context = CreateModuleContext(
            "sample-dotnet-dispose-tracking",
            "sample.dotnet.dispose-tracking",
            "concurrent-dispose-context",
            ["status"]);
        var host = new InProcDotNetModuleHost();
        await host.LoadAsync(module, context, CancellationToken.None);
        using var start = new ManualResetEventSlim(false);
        var disposals = Enumerable.Range(0, 24)
            .Select(_ => Task.Run(async () =>
            {
                start.Wait();
                await host.DisposeAsync();
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(6));

        Assert.Equal(1, ReadFixtureCounter(context.DataDirectory, "disposed-instances.txt"));
    }

    [Fact]
    public async Task Inproc_alc_finalizer_probe_is_bounded_for_the_restart_caller()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-finalizer-probe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.blocking-finalizer",
            "sample-dotnet-blocking-finalizer",
            "Blocking finalizer fixture",
            typeof(BlockingFinalizerDotNetModule).FullName!);
        var module = new PackageReader().ReadPackageDirectory(packageRoot).Modules.Single();
        var context = CreateModuleContext(
            "sample-dotnet-blocking-finalizer",
            "sample.dotnet.blocking-finalizer",
            "blocking-finalizer-context",
            ["status"]);
        await using var host = new InProcDotNetModuleHost();
        await host.LoadAsync(module, context, CancellationToken.None);

        var restartTask = host.RestartProcessAsync(
            "module:sample.dotnet.blocking-finalizer",
            CancellationToken.None).AsTask();
        await WaitForFixtureFileAsync(context.DataDirectory, "finalizer.started", TimeSpan.FromSeconds(3));
        Task completed;
        try
        {
            completed = await Task.WhenAny(restartTask, Task.Delay(TimeSpan.FromSeconds(3.5)));
        }
        finally
        {
            TouchFixtureFile(context.DataDirectory, "finalizer.release");
        }

        var restart = await restartTask.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Same(restartTask, completed);
        Assert.False(restart.Success);
        Assert.Equal("pending-runner-restart", restart.State);
    }

    [Fact]
    public async Task Inproc_event_consumer_paused_after_yield_blocks_disable_without_disposing_live_module()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-inproc-paused-event", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteInProcDotNetModulePackage(
            packageRoot,
            "sample.dotnet.event-fault",
            "sample-dotnet-event-fault",
            "Paused event fixture",
            typeof(EventFaultInjectionDotNetModule).FullName!);
        await SetInProcMaxCallAsync(packageRoot, 1000);

        await using var host = new InProcDotNetModuleHost();
        await using var runtime = CreateInProcFixtureRuntime(host, "paused-event-runtime");
        runtime.Load(packageRoot);
        var module = Assert.Single(runtime.Modules);
        var context = CreateModuleContext(
            "sample-dotnet-event-fault",
            "sample.dotnet.event-fault",
            "paused-event-context",
            ["status", "events"]);
        // Relay-friendly fixture mode: yield one event without blocking. Cursor 0:
        // relay consumers filter in host sequence space, which starts at 1.
        Directory.CreateDirectory(context.DataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(context.DataDirectory, "blocking-mode.txt"),
            "none");
        await host.GetStatusAsync(module, context, CancellationToken.None);
        var enumerator = host.SubscribeEventsAsync(
            module,
            context,
            new MyPowerTools.Abstractions.EventCursor(0),
            CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await host.DisableModuleAsync(module, context, new HashSet<string>(), CancellationToken.None)
                    .AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(4)));
            Assert.Contains("active", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
            Assert.Equal(0, ReadFixtureCounter(context.DataDirectory, "disposed-instances.txt"));
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

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
            "tools/adb-forwarder/current-integration/src/AdbForwarder.MyPowerTools/AdbForwarder.MyPowerTools.csproj",
            "tools/remote-notifications/current-integration/src/AndroidTools.MyPowerTools/AndroidTools.MyPowerTools.csproj",
            "tools/doubao-computer-use/current-integration/src/DoubaoAgent.MyPowerTools/DoubaoAgent.MyPowerTools.csproj",
            "tools/screenease/current-integration/src/ScreenEase.MyPowerTools/ScreenEase.MyPowerTools.csproj",
            "tools/smartbird-thermostat/current-integration/src/SmartBirdThermostat.MyPowerTools/SmartBirdThermostat.MyPowerTools.csproj"
        };

        foreach (var projectFile in projectFiles)
        {
            var content = File.ReadAllText(Path.Combine(Root, projectFile));
            // The abstractions contract may come from a project reference or from the
            // MyPowerTools.ToolSdk package (the current module packaging strategy).
            Assert.True(
                content.Contains("MyPowerTools.Abstractions.csproj", StringComparison.Ordinal) ||
                content.Contains("\"MyPowerTools.ToolSdk\"", StringComparison.Ordinal),
                $"{projectFile} must reference the abstractions contract (project reference or MyPowerTools.ToolSdk package).");
            Assert.DoesNotContain("MyPowerTools.Runtime.csproj", content);
            Assert.DoesNotContain("\"MyPowerTools.Runtime\"", content);
        }
    }

    [Fact]
    public void Production_modules_and_templates_import_abstractions_sdk_namespace()
    {
        var sourceRoots = new[]
        {
            "tools/adb-forwarder/current-integration/src/AdbForwarder.MyPowerTools",
            "tools/remote-notifications/current-integration/src/AndroidTools.MyPowerTools",
            "tools/doubao-computer-use/current-integration/src/DoubaoAgent.MyPowerTools",
            "tools/screenease/current-integration/src/ScreenEase.MyPowerTools",
            "tools/smartbird-thermostat/current-integration/src/SmartBirdThermostat.MyPowerTools",
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
        var project = File.ReadAllText(Path.Combine(Root, "tools/screenease/current-integration/src/ScreenEase.MyPowerTools/ScreenEase.MyPowerTools.csproj"));
        var source = File.ReadAllText(Path.Combine(Root, "tools/screenease/current-integration/src/ScreenEase.MyPowerTools/ScreenEaseModule.cs"));
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

    private static MptHostRuntime CreateInProcFixtureRuntime(InProcDotNetModuleHost host, string name)
    {
        return new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-inproc-regression", name, Guid.NewGuid().ToString("N"))),
            [host]);
    }

    private static async Task SetInProcMaxCallAsync(string packageRoot, int maxCallMs)
    {
        var manifestPath = Path.Combine(packageRoot, "module.json");
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
        manifest["runtimePolicy"] = new JsonObject
        {
            ["preferred"] = "inproc",
            ["allowInProc"] = true,
            ["inProcRules"] = new JsonObject
            {
                ["maxCallMs"] = maxCallMs,
                ["allowNativeDll"] = false,
                ["allowWindow"] = false,
                ["allowBackgroundThreads"] = false,
                ["loadContext"] = "collectible",
                ["shadowCopy"] = true
            }
        };
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int ReadFixtureCounter(string directory, string fileName)
    {
        var path = Path.Combine(directory, fileName);
        return File.Exists(path) && int.TryParse(File.ReadAllText(path), out var value) ? value : 0;
    }

    private static async Task WaitForFixtureCounterAsync(
        string directory,
        string fileName,
        int expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ReadFixtureCounter(directory, fileName) < expected && DateTime.UtcNow < deadline)
        {
            GC.Collect();
            await Task.Delay(20);
        }

        Assert.True(
            ReadFixtureCounter(directory, fileName) >= expected,
            $"Fixture counter '{fileName}' did not reach {expected} within {timeout}.");
    }

    private static async Task WaitForFixtureFileAsync(string directory, string fileName, TimeSpan timeout)
    {
        var path = Path.Combine(directory, fileName);
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(File.Exists(path), $"Fixture marker '{fileName}' was not created within {timeout}.");
    }

    private static void TouchFixtureFile(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), "release");
    }
}
