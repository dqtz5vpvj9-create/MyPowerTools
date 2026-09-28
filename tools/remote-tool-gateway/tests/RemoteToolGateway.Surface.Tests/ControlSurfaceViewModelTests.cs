using System.Text.Json.Nodes;
using MyPowerTools.AvaloniaSdk;
using RemoteToolGateway.Surface;

namespace RemoteToolGateway.Surface.Tests;

/// <summary>
/// Records the two execution paths the desktop page uses and returns canned module payloads.
/// </summary>
internal sealed class FakeSurfaceHost : IGatewaySurfaceHost
{
    public List<(string CommandId, JsonObject Args)> ModuleCommands { get; } = [];
    public List<(string InvocationId, string CommandId, JsonObject Args)> RuntimeCommands { get; } = [];
    public JsonObject InspectPayload { get; set; } = DefaultPayload();
    public SurfaceCommandOutcome RuntimeOutcome { get; set; } = new("succeeded", true, "已执行。", "", "", false);
    public string? FailModuleCommand { get; set; }
    public bool SupportsInvocationScopedExecution { get; set; } = true;
    public Func<string, string, JsonObject, Task<SurfaceCommandOutcome>>? RuntimeExecutor { get; set; }
    public Func<string, JsonObject, Task<JsonObject>>? ModuleExecutor { get; set; }

    public Task<JsonObject> ExecuteModuleCommandAsync(string commandId, JsonObject args, CancellationToken cancellationToken)
    {
        if (FailModuleCommand is not null && commandId.EndsWith(FailModuleCommand, StringComparison.Ordinal))
            throw new InvalidOperationException("模块拒绝了该操作。");
        ModuleCommands.Add((commandId, (JsonObject)args.DeepClone()));
        if (ModuleExecutor is not null) return ModuleExecutor(commandId, args);
        if (commandId.EndsWith(".inspect", StringComparison.Ordinal)) return Task.FromResult((JsonObject)InspectPayload.DeepClone());
        if (commandId.EndsWith(".confirmation.claim", StringComparison.Ordinal))
        {
            return Task.FromResult(new JsonObject
            {
                ["invocationId"] = args["invocationId"]!.GetValue<string>(),
                ["commandId"] = "svc.restart",
                ["commandTitle"] = "重启服务",
                ["deviceName"] = "我的手机",
                ["requiresElevation"] = true,
                ["kind"] = "elevated",
                ["args"] = new JsonObject { ["service"] = "Spooler" }
            });
        }

        if (commandId.EndsWith(".confirmation.resolve", StringComparison.Ordinal)) return Task.FromResult((JsonObject)args.DeepClone());
        return Task.FromResult(new JsonObject { ["ok"] = true, ["code"] = "mpt://control/AAAA" });
    }

    public async Task<SurfaceCommandOutcome> ExecuteRuntimeCommandAsync(
        string invocationId,
        string commandId,
        JsonObject args,
        CancellationToken cancellationToken)
    {
        if (!SupportsInvocationScopedExecution)
            throw new NotSupportedException("host has no invocation-scoped execution");
        RuntimeCommands.Add((invocationId, commandId, (JsonObject)args.DeepClone()));
        if (RuntimeExecutor is not null) return await RuntimeExecutor(invocationId, commandId, args);
        return RuntimeOutcome;
    }

    public static JsonObject DefaultPayload() => new()
    {
        ["listener"] = new JsonObject
        {
            ["enabled"] = true,
            ["running"] = true,
            ["address"] = "100.64.0.2",
            ["port"] = 49541,
            ["endpoint"] = "http://100.64.0.2:49541",
            ["message"] = "正在等待已授权的手机。",
            ["availableAddresses"] = new JsonArray("100.64.0.2")
        },
        ["defaultPort"] = 49541,
        ["grants"] = new JsonArray
        {
            new JsonObject
            {
                ["grantId"] = "g-0123456789abcdef",
                ["deviceName"] = "我的手机",
                ["allowElevated"] = true,
                ["commandIds"] = new JsonArray("svc.restart"),
                ["activeInvocations"] = 0,
                ["lastUsedAt"] = ""
            }
        },
        ["pending"] = new JsonArray
        {
            new JsonObject
            {
                ["invocationId"] = "inv-pending-0001",
                ["grantId"] = "g-0123456789abcdef",
                ["deviceName"] = "我的手机",
                ["commandId"] = "svc.restart",
                ["commandTitle"] = "重启服务",
                ["kind"] = "elevated",
                ["reason"] = "此操作需要管理员权限，必须在电脑上确认。",
                ["requiresElevation"] = true,
                ["argsSummary"] = "service=Spooler",
                ["claimed"] = false,
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O")
            }
        },
        ["catalog"] = new JsonObject
        {
            ["tools"] = new JsonArray(),
            ["commands"] = new JsonArray
            {
                new JsonObject
                {
                    ["commandId"] = "svc.restart",
                    ["moduleId"] = "nssm-manager",
                    ["title"] = "重启服务",
                    ["subtitle"] = "nssm-manager",
                    ["dangerLevel"] = "",
                    ["requiresElevation"] = true,
                    ["manageOnly"] = false
                },
                new JsonObject
                {
                    ["commandId"] = "input-monitor.status",
                    ["moduleId"] = "input-monitor",
                    ["title"] = "查看状态",
                    ["subtitle"] = "input-monitor",
                    ["dangerLevel"] = "",
                    ["requiresElevation"] = false,
                    ["manageOnly"] = false
                },
                new JsonObject
                {
                    ["commandId"] = "remote-tool-gateway.grant.revoke",
                    ["moduleId"] = "remote-tool-gateway",
                    ["title"] = "撤销授权",
                    ["subtitle"] = "remote-tool-gateway",
                    ["dangerLevel"] = "",
                    ["requiresElevation"] = false,
                    ["manageOnly"] = true
                }
            }
        },
        ["invocations"] = new JsonArray
        {
            new JsonObject
            {
                ["invocationId"] = "inv-pending-0001",
                ["commandId"] = "svc.restart",
                ["deviceName"] = "我的手机",
                ["state"] = "awaiting-confirmation",
                ["message"] = "等待电脑确认。",
                ["terminal"] = false
            }
        },
        ["audit"] = new JsonArray
        {
            new JsonObject
            {
                ["kind"] = "confirmation-requested",
                ["commandId"] = "svc.restart",
                ["message"] = "elevated: service=Spooler",
                ["state"] = "awaiting-confirmation"
            }
        }
    };
}

public sealed class ControlSurfaceViewModelTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LeavingOrHidingDuringCodeRequest_DoesNotRevealTheLateResult(bool create, bool detach)
    {
        var response = new TaskCompletionSource<JsonObject>();
        var host = new FakeSurfaceHost
        {
            ModuleExecutor = (command, _) => command.EndsWith(".inspect", StringComparison.Ordinal)
                ? Task.FromResult(FakeSurfaceHost.DefaultPayload())
                : response.Task
        };
        var model = new ControlSurfaceViewModel(host);
        var request = create ? model.CreateGrantAsync() : model.ShowCodeAsync("g-0123456789abcdef");
        Assert.Single(host.ModuleCommands);
        if (detach) model.Detach(); else model.ClearCreatedCode();
        response.SetResult(new JsonObject
        {
            ["code"] = "mpt://control/late-response",
            ["grantId"] = "g-0123456789abcdef",
            ["deviceName"] = "手机"
        });
        await request;
        Assert.False(model.HasCreatedCode);
        Assert.Empty(model.CreatedCodeContext);
    }

    [Fact]
    public async Task Load_ShowsPendingAndGrants_AndExecutesNothing()
    {
        var host = new FakeSurfaceHost();
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();

        Assert.True(viewModel.Loaded);
        Assert.True(viewModel.ListenerRunning);
        Assert.Equal("http://100.64.0.2:49541", viewModel.ListenerEndpoint);
        Assert.Single(viewModel.Grants);
        Assert.Equal(1, viewModel.Grants[0].CommandCount);
        Assert.Single(viewModel.Pending);
        Assert.Equal("service=Spooler", viewModel.Pending[0].ArgsSummary);
        Assert.True(viewModel.HasPending);
        Assert.Empty(host.RuntimeCommands);
        Assert.DoesNotContain(host.ModuleCommands, call => call.CommandId.EndsWith(".confirmation.resolve", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirm_RunsThroughTheExistingEntryPoint_AndReportsTheRealOutcome()
    {
        var host = new FakeSurfaceHost();
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();

        await viewModel.ConfirmAsync(viewModel.Pending[0]);

        var runtime = Assert.Single(host.RuntimeCommands);
        Assert.Equal("svc.restart", runtime.CommandId);
        Assert.Equal("Spooler", runtime.Args["service"]!.GetValue<string>());
        // The phone's invocation id is preserved, so a phone cancel can still reach this call.
        Assert.Equal("inv-pending-0001", runtime.InvocationId);

        var resolve = Assert.Single(host.ModuleCommands.Where(call => call.CommandId.EndsWith(".confirmation.resolve", StringComparison.Ordinal)));
        Assert.True(resolve.Args["accepted"]!.GetValue<bool>());
        Assert.Equal("inv-pending-0001", resolve.Args["invocationId"]!.GetValue<string>());
        Assert.Equal("succeeded", resolve.Args["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Confirm_PassesThroughAPermissionRequiredOutcome_WithoutTurningItIntoSuccess()
    {
        var host = new FakeSurfaceHost
        {
            RuntimeOutcome = new SurfaceCommandOutcome("permission-required", false, "", "command.failed", "Broker approval required.", false)
        };
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();

        await viewModel.ConfirmAsync(viewModel.Pending[0]);

        var resolve = Assert.Single(host.ModuleCommands.Where(call => call.CommandId.EndsWith(".confirmation.resolve", StringComparison.Ordinal)));
        Assert.Equal("permission-required", resolve.Args["state"]!.GetValue<string>());
        Assert.Equal("Broker approval required.", resolve.Args["errorMessage"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reject_ResolvesWithoutRunningAnything()
    {
        var host = new FakeSurfaceHost();
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();

        await viewModel.RejectAsync(viewModel.Pending[0]);

        Assert.Empty(host.RuntimeCommands);
        var resolve = Assert.Single(host.ModuleCommands.Where(call => call.CommandId.EndsWith(".confirmation.resolve", StringComparison.Ordinal)));
        Assert.False(resolve.Args["accepted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CommandPicker_NeverOffersTheGatewaysOwnManagementCommands()
    {
        var host = new FakeSurfaceHost();
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();

        var management = viewModel.Catalog.Single(row => row.CommandId == "remote-tool-gateway.grant.revoke");
        Assert.False(management.Selectable);

        viewModel.SelectCommands(["svc.restart", "input-monitor.status", "remote-tool-gateway.grant.revoke"]);
        var selection = viewModel.CurrentSelection();
        Assert.Contains("svc.restart", selection);
        Assert.Contains("input-monitor.status", selection);
        Assert.DoesNotContain("remote-tool-gateway.grant.revoke", selection);
    }

    [Fact]
    public async Task StartListener_SendsTheConfiguredAddressAndPort()
    {
        var host = new FakeSurfaceHost();
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();
        viewModel.Address = "100.64.0.9";
        viewModel.Port = "50555";

        await viewModel.StartListenerAsync();

        var start = Assert.Single(host.ModuleCommands.Where(call => call.CommandId.EndsWith(".listener.start", StringComparison.Ordinal)));
        Assert.Equal("100.64.0.9", start.Args["address"]!.GetValue<string>());
        Assert.Equal(50555, start.Args["port"]!.GetValue<int>());
    }

    [Fact]
    public async Task ModuleFailure_IsReportedOnThePage_WithoutThrowing()
    {
        var host = new FakeSurfaceHost { FailModuleCommand = ".listener.start" };
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();

        await viewModel.StartListenerAsync();

        Assert.Equal("模块拒绝了该操作。", viewModel.Status);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task Confirm_WhenTheHostCannotKeepTheInvocationId_RefusesWithoutExecuting()
    {
        var host = new FakeSurfaceHost { SupportsInvocationScopedExecution = false };
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();

        await viewModel.ConfirmAsync(viewModel.Pending[0]);

        Assert.Empty(host.RuntimeCommands);
        var resolve = Assert.Single(host.ModuleCommands.Where(call => call.CommandId.EndsWith(".confirmation.resolve", StringComparison.Ordinal)));
        Assert.False(resolve.Args["accepted"]!.GetValue<bool>());
        Assert.Contains("不支持保留手机调用 ID", resolve.Args["errorMessage"]!.GetValue<string>());
        // Never claim a request the page cannot execute under the phone's own id.
        Assert.DoesNotContain(host.ModuleCommands, call => call.CommandId.EndsWith(".confirmation.claim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModuleEvents_TriggerOneCoalescedReload_AndDetachStopsThem()
    {
        var host = new FakeSurfaceHost();
        var viewModel = new ControlSurfaceViewModel(host);
        List<Action<MptSurfaceEvent>> handlers = [];
        Action<MptSurfaceEvent>? captured = null;
        viewModel.Attach(callback =>
        {
            captured = callback;
            handlers.Add(callback);
            return new Disposable(() => handlers.Remove(callback));
        });
        await viewModel.LoadAsync();
        var afterLoad = host.ModuleCommands.Count;

        Assert.True(viewModel.IsAttached);
        Assert.NotNull(captured);
        for (var index = 0; index < 5; index++)
        {
            captured!(new MptSurfaceEvent((ulong)index + 1, "remote-tool-gateway", "remote-tool-gateway.confirmation.changed", DateTimeOffset.UtcNow, new JsonObject()));
        }

        await WaitAsync(() => host.ModuleCommands.Count > afterLoad);
        // A burst of five events collapses into one extra inspect call, not five.
        Assert.Equal(afterLoad + 1, host.ModuleCommands.Count);
        Assert.Equal(2, viewModel.RefreshCount);

        // Unrelated module traffic is ignored.
        captured!(new MptSurfaceEvent(90, "file-transfer", "file-transfer.transfer.changed", DateTimeOffset.UtcNow, new JsonObject()));
        await Task.Delay(100);
        Assert.Equal(afterLoad + 1, host.ModuleCommands.Count);

        viewModel.Detach();
        Assert.False(viewModel.IsAttached);
        Assert.Empty(handlers);

        // After detach nothing reloads, and no refresh loop keeps running.
        captured!(new MptSurfaceEvent(91, "remote-tool-gateway", "remote-tool-gateway.invocation.changed", DateTimeOffset.UtcNow, new JsonObject()));
        await Task.Delay(100);
        Assert.Equal(afterLoad + 1, host.ModuleCommands.Count);
        Assert.Equal(2, viewModel.RefreshCount);
    }

    [Fact]
    public async Task Reload_KeepsTheUsersCommandSelectionAndDeviceName()
    {
        var host = new FakeSurfaceHost();
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();

        viewModel.SelectCommands(["svc.restart"]);
        viewModel.NewDeviceName = "我的新手机";
        viewModel.NewAllowElevated = true;
        viewModel.CommandFilter = "svc";

        await viewModel.LoadAsync();

        var selection = viewModel.CurrentSelection();
        Assert.Contains("svc.restart", selection);
        Assert.DoesNotContain("input-monitor.status", selection);
        Assert.Equal("我的新手机", viewModel.NewDeviceName);
        Assert.True(viewModel.NewAllowElevated);
        Assert.Equal("svc", viewModel.CommandFilter);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("Condition was not met in time.");
    }

    private sealed class Disposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    [Fact]
    public async Task CreateGrant_UsesOnlySelectableCommands_AndShowsTheCode()
    {
        var host = new FakeSurfaceHost();
        var viewModel = new ControlSurfaceViewModel(host);
        await viewModel.LoadAsync();
        viewModel.NewDeviceName = "新手机";
        viewModel.NewAllowElevated = false;
        viewModel.SelectCommands(["input-monitor.status", "remote-tool-gateway.grant.revoke"]);

        await viewModel.CreateGrantAsync();

        var create = Assert.Single(host.ModuleCommands.Where(call => call.CommandId.EndsWith(".grant.create", StringComparison.Ordinal)));
        var commandIds = create.Args["commandIds"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(["input-monitor.status"], commandIds);
        Assert.Equal("新手机", create.Args["deviceName"]!.GetValue<string>());
        Assert.Equal("mpt://control/AAAA", viewModel.CreatedCode);
    }
}
