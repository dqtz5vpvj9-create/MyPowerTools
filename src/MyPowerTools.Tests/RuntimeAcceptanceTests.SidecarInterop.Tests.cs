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
    public void Runtime_indexes_modules_without_starting_sidecars()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var snapshot = runtime.GetDashboardSnapshot();

        Assert.Contains(snapshot.Cards, card => card.ModuleId == "doubao-agent");
        Assert.Contains(snapshot.Cards, card => card.ModuleId == "android-tools.remote-commands");
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
    public async Task Runtime_supervisor_reports_repeated_http_facade_failures()
    {
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-http-supervisor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteHttpFacadeModuleManifest(packageRoot, ReserveUnusedLoopbackUrl());
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-http-supervisor", Guid.NewGuid().ToString("N"))));

        runtime.Load(packageRoot);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        var diagnostics = runtime.GetRuntimeDiagnostics();
        var module = Assert.Single(diagnostics.Modules);
        var alert = Assert.Single(runtime.GetDashboardSnapshot().Alerts);

        Assert.Equal("degraded", module.State);
        Assert.Equal("intervention-needed", module.SupervisorState);
        Assert.True(module.ObservationCount >= 4);
        Assert.True(module.ConsecutiveFailureCount >= 3);
        Assert.Contains("HTTP facade", module.SupervisorAction);
        Assert.Equal("module-supervisor-sample.http-runtime", alert.Id);
        Assert.Contains("HTTP facade", alert.Body);
    }

    [Fact]
    public async Task Runtime_supervisor_resets_after_http_facade_recovery()
    {
        await using var server = TestHttpFacadeServer.Start();
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-http-supervisor-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteHttpFacadeModuleManifest(packageRoot, server.BaseUrl);
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-http-supervisor-recovery", Guid.NewGuid().ToString("N"))));

        runtime.Load(packageRoot);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        await runtime.RefreshHealthAsync(CancellationToken.None);
        var module = Assert.Single(runtime.GetRuntimeDiagnostics().Modules);

        Assert.Equal("running", module.State);
        Assert.Equal("healthy", module.SupervisorState);
        Assert.Equal(0, module.ConsecutiveFailureCount);
        Assert.Equal("No action required.", module.SupervisorAction);
        Assert.Empty(runtime.GetDashboardSnapshot().Alerts);
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
    public async Task Runtime_drains_grpc_sidecar_stdio_into_process_diagnostics()
    {
        var sidecarCommand = FindSampleGrpcSidecarCommand();
        var pipeName = "mypowertools.sample.grpc.stdio." + Guid.NewGuid().ToString("N");
        var packageRoot = Path.Combine(Path.GetTempPath(), "mpt-grpc-stdio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(packageRoot);
        WriteGrpcSidecarModuleManifest(packageRoot, sidecarCommand, pipeName);

        await using var host = new GrpcIpcModuleRuntime();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-grpc-stdio", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(packageRoot);
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);

        RuntimeProcessDiagnostics process = null!;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            process = Assert.Single(runtime.GetRuntimeDiagnostics().Processes);
            if (process.StdoutLineCount > 0 && process.StderrLineCount > 0)
            {
                break;
            }

            await Task.Delay(100);
        }

        Assert.True(process.StdoutLineCount > 0, "Expected gRPC sidecar stdout to be drained.");
        Assert.True(process.StderrLineCount > 0, "Expected gRPC sidecar stderr to be drained.");
        Assert.False(string.IsNullOrWhiteSpace(process.LastStdout));
        Assert.Contains("mpt-sidecar stderr initialized", process.LastStderr);
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
    public async Task AndroidTools_powertoold_imports_powertool_commands_and_executes_text_tool()
    {
        await using var host = new GrpcIpcModuleRuntime();
        await using var runtime = new MptHostRuntime(
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
        var diagnostics = runtime.GetRuntimeDiagnostics();
        var process = Assert.Single(diagnostics.Processes.Where(process => process.TransportKind == "grpc-ipc"));

        Assert.True(dynamicCount > 0);
        Assert.Contains(runtime.ListCommands("Remove C++"), command => command.Id == "android-tools.remote-commands.run.remove_comments");
        Assert.True(catalog.Success);
        Assert.Contains("11 command(s)", catalog.Output);
        Assert.True(transform.Success);
        Assert.DoesNotContain("remove me", output);
        Assert.Contains("// keep me", output);
        Assert.Equal("package:android-tools-suite:runtime:powertoold", process.PoolKey);
        Assert.Contains("android-tools.notifications", process.ModuleIds);
        Assert.Contains("android-tools.process-monitor", process.ModuleIds);
        Assert.Contains("android-tools.remote-commands", process.ModuleIds);
    }

    [Fact]
    public async Task AndroidTools_powertoold_preserves_dynamic_command_metadata_over_grpc()
    {
        var commandsRoot = Path.Combine(Path.GetTempPath(), "mpt-android-grpc-metadata", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(commandsRoot);
        var commandsPath = Path.Combine(commandsRoot, "commands.yaml");
        await File.WriteAllTextAsync(commandsPath, """
commands:
  - id: shell_echo
    label: Shell Echo
    command: echo metadata
    description: Shell command with metadata.
    type: shell
""");
        var previous = Environment.GetEnvironmentVariable("MPT_ANDROIDTOOLS_COMMANDS");
        Environment.SetEnvironmentVariable("MPT_ANDROIDTOOLS_COMMANDS", commandsPath);
        try
        {
            await using var host = new GrpcIpcModuleRuntime();
            await using var runtime = new MptHostRuntime(
                new PackageReader(),
                PlatformId.Current(),
                RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-runtime-android-grpc-metadata", Guid.NewGuid().ToString("N"))),
                [host]);

            runtime.Load(Path.Combine(Root, "modules"));
            var dynamicCount = await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
            var command = Assert.Single(runtime.ListCommands("Shell Echo").Where(command => command.Id == "android-tools.remote-commands.run.shell_echo"));

            Assert.True(dynamicCount > 0);
            Assert.Equal("Android Tools", command.Category);
            Assert.Equal(120000, command.TimeoutMs);
            Assert.NotNull(command.Execution);
            Assert.Equal("module.execute", command.Execution!["type"]!.GetValue<string>());
            Assert.Equal("powertool.commands.yaml", command.Execution!["source"]!.GetValue<string>());
            Assert.Equal("shell_echo", command.Execution!["powertoolCommandId"]!.GetValue<string>());
            Assert.Equal("shell", command.Execution!["powertoolCommandType"]!.GetValue<string>());
            Assert.Contains(command.Parameters!, parameter => parameter.Id == "execute" && parameter.Type == "boolean" && parameter.DefaultValue == "false");
            Assert.Contains(command.Parameters!, parameter => parameter.Id == "timeoutMs" && parameter.Type == "number" && parameter.DefaultValue == "120000");
            Assert.Contains(MptOperationConstraints.RunsExternalProcesses, command.Constraints!);
            Assert.Contains(MptOperationConstraints.RequiresLongRunningLoop, command.Constraints!);
            Assert.True(command.SupportsProgress);
            Assert.True(command.SupportsCancellation);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MPT_ANDROIDTOOLS_COMMANDS", previous);
        }
    }

    [Fact]
    public async Task AndroidTools_powertoold_process_monitor_persists_shared_watch_list()
    {
        await using var host = new GrpcIpcModuleRuntime();
        await using var runtime = new MptHostRuntime(
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
        Assert.Contains(runtime.GetRuntimeDiagnostics().Processes, process =>
            process.PoolKey == "package:android-tools-suite:runtime:powertoold" &&
            process.ModuleIds.Contains("android-tools.process-monitor"));
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
}
