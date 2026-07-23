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
using Grpc.Core.Interceptors;
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
    public async Task HostControl_publishes_canonical_tool_availability()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(
            runtime,
            new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-tool-availability", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var response = await service.ListTools(
            new HostProto.ListToolsRequest { IncludeDisabled = true },
            new TestServerCallContext());

        Assert.Equal("paused", response.Tools.Single(tool => tool.ToolId == "process-monitor").Availability);
        Assert.Equal("paused", response.Tools.Single(tool => tool.ToolId == "remote-commands").Availability);
        Assert.Equal("available", response.Tools.Single(tool => tool.ToolId == "screenease").Availability);
    }

    [Fact]
    public async Task HostControl_auth_interceptor_requires_local_token()
    {
        const string token = "expected-local-token-for-hostcontrol-auth";
        var interceptor = new HostControlAuthServerInterceptor(new HostControlAuthOptions(token));

        var rejected = await Assert.ThrowsAsync<RpcException>(() =>
            interceptor.UnaryServerHandler(
                new HostProto.PingRequest(),
                new TestServerCallContext(),
                (_, _) => Task.FromResult(new HostProto.PingResponse())));

        var accepted = await interceptor.UnaryServerHandler(
            new HostProto.PingRequest(),
            new TestServerCallContext(new Metadata { { HostControlAuthTokenStore.HeaderName, token } }),
            (_, _) => Task.FromResult(new HostProto.PingResponse { State = "running" }));

        Assert.Equal(StatusCode.Unauthenticated, rejected.StatusCode);
        Assert.Equal("running", accepted.State);
    }

    [Fact]
    public async Task HostControl_execute_command_exposes_permission_details()
    {
        await using var host = new InProcDotNetModuleHost();
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-permission", Guid.NewGuid().ToString("N"))),
            [host]);

        runtime.Load(Path.Combine(Root, "modules"));
        await runtime.RefreshDynamicCommandsAsync(CancellationToken.None);
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-permission-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));
        var request = new MyPowerTools.Protocol.HostControl.V1.ExecuteCommandRequest
        {
            InvocationId = "hostcontrol-permission",
            CommandId = "adb-forwarder.portproxy.apply",
            Args = JsonStructMapper.ToStruct(CreateAdbPortProxyArgs(PortProxySample))
        };

        var response = await service.ExecuteCommand(request, new TestServerCallContext());

        Assert.Equal("permission-required", response.State);
        Assert.Equal("MPT_PERMISSION_REQUIRED", response.ErrorCode);
        Assert.Equal("network.portproxy.apply", response.ErrorDetails.Fields["actionId"].StringValue);
        Assert.Single(response.ErrorDetails.Fields["rollback"].ListValue.Values);
    }

    [Fact]
    public async Task HostControl_execute_command_stream_exposes_progress_events()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime);
        var request = new HostProto.ExecuteCommandRequest
        {
            InvocationId = "hostcontrol-stream",
            CommandId = "screenease.settings.read",
            Args = JsonStructMapper.ToStruct(new JsonObject())
        };
        var writer = new RecordingServerStreamWriter<HostProto.CommandExecutionEvent>();

        await service.ExecuteCommandStream(request, writer, new TestServerCallContext());

        Assert.Collection(
            writer.Messages,
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
                Assert.Equal("succeeded", evt.FinalResponse.State);
                Assert.Contains("Settings revision", evt.FinalResponse.Summary);
            });
    }

    [Fact]
    public async Task HostControl_module_detail_exposes_declared_permissions()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime);

        var modules = await service.ListModules(new MyPowerTools.Protocol.HostControl.V1.ListModulesRequest { IncludeDisabled = true }, new TestServerCallContext());
        var summary = modules.Modules.Single(module => module.ModuleId == "adb-forwarder");
        var detail = await service.GetModuleDetail(new MyPowerTools.Protocol.HostControl.V1.GetModuleDetailRequest { ModuleId = "adb-forwarder" }, new TestServerCallContext());

        Assert.Contains(summary.Permissions, permission => permission.Id == "apply-portproxy" && permission.Level == "broker");
        Assert.Contains(summary.Requirements, requirement => requirement.Capability == "network.portForwarding" && requirement.Required);
        Assert.Contains(detail.Permissions, permission => permission.Capability == "network.portForwarding" && permission.Reason.Contains("端口转发", StringComparison.Ordinal));
        Assert.Contains(detail.Requirements, requirement => requirement.Capability == "adb.devices" && requirement.Required);
    }

    [Fact]
    public async Task HostControl_lists_broker_audit_entries()
    {
        var audit = new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-audit", Guid.NewGuid().ToString("N"), "audit.jsonl"));
        audit.Append(new BrokerAuditEntry(
            "audit-1",
            DateTimeOffset.UtcNow,
            "adb-forwarder",
            "network.portproxy.apply",
            "elevated",
            "0.0.0.0:5555",
            "test audit",
            true,
            "evaluated",
            "remove 0.0.0.0:5555"));
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, audit);

        var response = await service.ListBrokerAudit(
            new MyPowerTools.Protocol.HostControl.V1.ListBrokerAuditRequest { ModuleId = "adb-forwarder", Limit = 10 },
            new TestServerCallContext());

        var entry = Assert.Single(response.Entries);
        Assert.Equal("audit-1", entry.AuditId);
        Assert.Equal("network.portproxy.apply", entry.ActionId);
        Assert.Equal("evaluated", entry.Result);
    }

    [Fact]
    public async Task HostControl_lists_notification_center_entries()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var notification = runtime.ExecuteCommand(new CommandRequest("hostcontrol-notification", "doubao-agent.notification.test", new JsonObject()));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-notifications-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var response = await service.ListNotifications(
            new MyPowerTools.Protocol.HostControl.V1.ListNotificationsRequest { Limit = 10 },
            new TestServerCallContext());

        Assert.True(notification.Success);
        var entry = Assert.Single(response.Notifications);
        Assert.Equal("doubao-agent", entry.ModuleId);
        Assert.Equal("Send Doubao test notification", entry.Title);
        Assert.False(entry.IsRead);
    }

    [Fact]
    public async Task HostControl_lists_package_summaries()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-packages-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var response = await service.ListPackages(
            new MyPowerTools.Protocol.HostControl.V1.ListPackagesRequest { IncludeDisabled = true },
            new TestServerCallContext());

        Assert.Equal(5, response.Packages.Count);
        var androidTools = response.Packages.Single(package => package.PackageId == "android-tools-suite");
        Assert.Equal(3u, androidTools.ModuleCount);
        Assert.Contains("android-tools.remote-commands", androidTools.ModuleIds);
        Assert.Equal("shared/package.hashes.json", androidTools.Hashes);
        Assert.Equal("signature-hook", androidTools.TrustState);
        Assert.Equal("local", androidTools.TrustPolicy);
        Assert.Equal("shared/package.signature.json", androidTools.SignaturePath);
        Assert.True(androidTools.TrustIssueCount >= 0);
    }

    [Fact]
    public async Task HostControl_package_operations_reload_runtime_store()
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-package-store", Guid.NewGuid().ToString("N"));
        var store = new PackageStore(storeRoot, Path.Combine(Root, "schemas"));
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-package-store-data", Guid.NewGuid().ToString("N"))));
        runtime.Load(storeRoot);
        var service = new HostControlGrpcService(
            runtime,
            new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-package-store-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")),
            packageStore: store);

        var install = await service.InstallPackage(
            new MyPowerTools.Protocol.HostControl.V1.InstallPackageRequest
            {
                SourceDirectory = Path.Combine(Root, "tests", "fixtures", "modules", "sample-dotnet")
            },
            new TestServerCallContext());
        var repair = await service.RepairPackage(
            new MyPowerTools.Protocol.HostControl.V1.PackageOperationRequest { PackageId = "sample-dotnet" },
            new TestServerCallContext());
        var uninstall = await service.UninstallPackage(
            new MyPowerTools.Protocol.HostControl.V1.PackageOperationRequest { PackageId = "sample-dotnet" },
            new TestServerCallContext());
        var afterUninstall = await service.ListPackages(
            new MyPowerTools.Protocol.HostControl.V1.ListPackagesRequest { IncludeDisabled = true },
            new TestServerCallContext());
        var rollback = await service.RollbackPackage(
            new MyPowerTools.Protocol.HostControl.V1.PackageOperationRequest { PackageId = "sample-dotnet" },
            new TestServerCallContext());
        var afterRollback = await service.ListPackages(
            new MyPowerTools.Protocol.HostControl.V1.ListPackagesRequest { IncludeDisabled = true },
            new TestServerCallContext());

        Assert.True(install.Success, install.Message);
        Assert.Equal("install", install.Operation);
        Assert.Equal("sample-dotnet", install.PackageId);
        Assert.Equal(1u, install.PackageCount);
        Assert.Equal(1u, install.ModuleCount);
        Assert.True(repair.Success, repair.Message);
        Assert.Equal("repair", repair.Operation);
        Assert.Empty(repair.Issues);
        Assert.True(uninstall.Success, uninstall.Message);
        Assert.Empty(afterUninstall.Packages);
        Assert.True(rollback.Success, rollback.Message);
        var package = Assert.Single(afterRollback.Packages);
        Assert.Equal("sample-dotnet", package.PackageId);
        Assert.Equal("signature-hook", package.TrustState);
    }

    [Fact]
    public async Task HostControl_set_module_enabled_updates_runtime_state()
    {
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-module-state", Guid.NewGuid().ToString("N"))));
        runtime.Load(Path.Combine(Root, "modules"));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-module-state-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var detail = await service.SetModuleEnabled(
            new MyPowerTools.Protocol.HostControl.V1.SetModuleEnabledRequest { ModuleId = "doubao-agent", Enabled = false },
            new TestServerCallContext());
        var visibleOnly = await service.ListModules(
            new MyPowerTools.Protocol.HostControl.V1.ListModulesRequest(),
            new TestServerCallContext());
        var includeDisabled = await service.ListModules(
            new MyPowerTools.Protocol.HostControl.V1.ListModulesRequest { IncludeDisabled = true },
            new TestServerCallContext());

        Assert.Equal("disabled", detail.State);
        Assert.DoesNotContain(visibleOnly.Modules, module => module.ModuleId == "doubao-agent");
        var disabledModule = includeDisabled.Modules.Single(module => module.ModuleId == "doubao-agent");
        Assert.False(disabledModule.Enabled);
        Assert.Equal("disabled", disabledModule.State);
    }

    [Fact]
    public async Task HostControl_returns_runtime_diagnostics_snapshot()
    {
        await using var host = new InProcDotNetModuleHost();
        var dataRoot = Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-diagnostics", Guid.NewGuid().ToString("N"));
        var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(dataRoot),
            [host]);
        runtime.Load(Path.Combine(Root, "modules"));
        runtime.ExecuteCommand(new CommandRequest("diagnostics-history", "doubao-agent.open", new JsonObject()));
        var service = new HostControlGrpcService(runtime, new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-diagnostics-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")));

        var diagnostics = await service.GetRuntimeDiagnostics(
            new MyPowerTools.Protocol.HostControl.V1.RuntimeDiagnosticsRequest(),
            new TestServerCallContext());

        Assert.Equal("0.2.0", diagnostics.RunnerVersion);
        Assert.Equal("1.0", diagnostics.HostControlProtocolVersion);
        Assert.Equal(6u, diagnostics.Counts.PackageCount);
        Assert.Equal(8u, diagnostics.Counts.ModuleCount);
        Assert.Equal((uint)runtime.CurrentEventSeq, diagnostics.CurrentEventSeq);
        Assert.Equal(dataRoot, diagnostics.Paths.Root);
        Assert.Contains(diagnostics.Transports, transport => transport.Kind == "inproc-dotnet" && transport.RuntimeRegistered);
        Assert.Contains(diagnostics.Modules, module => module.ModuleId == "doubao-agent" && module.Enabled);
        Assert.Contains(diagnostics.Modules, module => module.ModuleId == "doubao-agent" && module.SupervisorState == "healthy" && module.ObservationCount > 0);
        Assert.Contains(diagnostics.RecentCommands, command => command.InvocationId == "diagnostics-history");
    }

    [Fact]
    public async Task HostControl_quit_runner_requests_host_shutdown()
    {
        var runtime = new MptHostRuntime(new PackageReader(), PlatformId.Current());
        var lifetime = new RecordingHostApplicationLifetime();
        var service = new HostControlGrpcService(
            runtime,
            new AuditLog(Path.Combine(Path.GetTempPath(), "mpt-hostcontrol-quit-audit", Guid.NewGuid().ToString("N"), "audit.jsonl")),
            lifetime);

        await service.QuitRunner(
            new MyPowerTools.Protocol.HostControl.V1.QuitRunnerRequest(),
            new TestServerCallContext());

        Assert.True(lifetime.StopRequested);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
    }
}
