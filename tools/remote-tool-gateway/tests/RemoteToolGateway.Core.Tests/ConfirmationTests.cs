using System.Net;
using System.Text.Json.Nodes;
using RemoteToolGateway.Core;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// Desktop confirmation behaviour: nothing elevated or dangerous runs without an explicit desktop
/// click, and the gateway never approves, escalates or fakes a pending state on its own.
/// </summary>
public sealed class ConfirmationTests
{
    private static JsonObject Submission(string invocationId, string commandId, JsonObject? args = null) => new()
    {
        ["invocationId"] = invocationId,
        ["commandId"] = commandId,
        ["args"] = args ?? new JsonObject()
    };

    [Fact]
    public async Task ElevatedCommand_WithoutAllowElevated_IsRejectedBeforeTheRuntime()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: false);
        using var client = harness.Client(token);

        var response = await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-elev-000001", "svc.restart").ToJsonString());
        await HttpAssert.AssertErrorAsync(response, HttpStatusCode.Forbidden, ControlErrorCodes.ElevationNotAllowed);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
        Assert.Empty(harness.Service.Invocations.PendingConfirmations());
    }

    [Fact]
    public async Task ElevatedConstraint_WithoutAllowElevated_IsAlsoRejectedBeforeTheRuntime()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.stop", moduleId: "nssm-manager", constraints: ["requiresElevatedWrites"]);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.stop"], allowElevated: false);
        using var client = harness.Client(token);

        var response = await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-elev-000002", "svc.stop").ToJsonString());
        await HttpAssert.AssertErrorAsync(response, HttpStatusCode.Forbidden, ControlErrorCodes.ElevationNotAllowed);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task ExplicitElevatedApproval_IsGatedByAllowElevated_AndConfirmedWhenAllowed()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.elevated-approval", execution: new JsonObject { ["approval"] = "elevated" });
        var (_, deniedToken, _) = await harness.GrantAsync("手机 A", ["demo.elevated-approval"], allowElevated: false);
        using var deniedClient = harness.Client(deniedToken);
        var denied = await deniedClient.PostAsync(ControlWire.InvocationsPath,
            Submission("inv-approval-001", "demo.elevated-approval").ToJsonString());
        await HttpAssert.AssertErrorAsync(denied, HttpStatusCode.Forbidden, ControlErrorCodes.ElevationNotAllowed);

        var (_, allowedToken, _) = await harness.GrantAsync("手机 B", ["demo.elevated-approval"], allowElevated: true);
        using var allowedClient = harness.Client(allowedToken);
        var pending = await HttpAssert.JsonAsync(await allowedClient.PostAsync(ControlWire.InvocationsPath,
            Submission("inv-approval-002", "demo.elevated-approval").ToJsonString()));
        Assert.Equal("awaiting-confirmation", pending["state"]!.GetValue<string>());
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task ExplicitDesktopApproval_RequiresTheDesktopConfirmation()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.desktop-approval", execution: new JsonObject { ["approval"] = "desktop" });
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.desktop-approval"]);
        using var client = harness.Client(token);

        var pending = await HttpAssert.JsonAsync(await client.PostAsync(ControlWire.InvocationsPath,
            Submission("inv-approval-003", "demo.desktop-approval").ToJsonString()));
        Assert.Equal("awaiting-confirmation", pending["state"]!.GetValue<string>());
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task ElevatedCommand_WithAllowElevated_WaitsForTheDesktopUser()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", title: "重启服务", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);

        var submit = await client.PostAsync(ControlWire.InvocationsPath,
            Submission("inv-confirm-0001", "svc.restart", new JsonObject { ["service"] = "Spooler" }).ToJsonString());
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        var pending = await HttpAssert.JsonAsync(submit);
        Assert.Equal("awaiting-confirmation", pending["state"]!.GetValue<string>());
        Assert.False(pending["terminal"]!.GetValue<bool>());
        Assert.Equal(0, harness.Bridge.ExecutionCount);

        var describe = await harness.Service.DescribeAsync(listenerEnabled: true, includeCatalog: false, CancellationToken.None);
        var listed = describe["pending"]!.AsArray();
        Assert.Single(listed);
        Assert.Equal("手机", listed[0]!["deviceName"]!.GetValue<string>());
        Assert.Equal("svc.restart", listed[0]!["commandId"]!.GetValue<string>());
        Assert.Equal("service=Spooler", listed[0]!["argsSummary"]!.GetValue<string>());
        Assert.True(listed[0]!["requiresElevation"]!.GetValue<bool>());

        // The desktop page claims the arguments, runs them through the ordinary local entry point
        // and reports only that real outcome back.
        var claim = await harness.Service.ClaimConfirmationAsync("inv-confirm-0001", CancellationToken.None);
        Assert.Equal("svc.restart", claim["commandId"]!.GetValue<string>());
        Assert.Equal("Spooler", claim["args"]!["service"]!.GetValue<string>());

        var outcome = await RunAsDesktopAsync(harness.Bridge, "inv-confirm-0001", "svc.restart", claim["args"]!.AsObject());
        Assert.Equal("succeeded", outcome.State);
        harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-confirm-0001",
            ["accepted"] = true,
            ["state"] = outcome.State,
            ["summary"] = outcome.Summary
        });

        var read = await client.GetAsync($"{ControlWire.InvocationsPath}/inv-confirm-0001");
        var final = await HttpAssert.JsonAsync(read);
        Assert.True(final["terminal"]!.GetValue<bool>());
        Assert.Equal("succeeded", final["state"]!.GetValue<string>());
        Assert.Equal("succeeded", final["result"]!["state"]!.GetValue<string>());
        Assert.Empty(harness.Service.Invocations.PendingConfirmations());
        Assert.Equal(1, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task RejectedConfirmation_EndsTheInvocation_WithoutAnyExecution()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);

        await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-reject-00001", "svc.restart").ToJsonString());
        harness.Service.ResolveConfirmation(new JsonObject { ["invocationId"] = "inv-reject-00001", ["accepted"] = false });

        var final = await HttpAssert.JsonAsync(await client.GetAsync($"{ControlWire.InvocationsPath}/inv-reject-00001"));
        Assert.True(final["terminal"]!.GetValue<bool>());
        Assert.Equal("rejected", final["state"]!.GetValue<string>());
        Assert.Equal(0, harness.Bridge.ExecutionCount);
        Assert.Empty(harness.Service.Invocations.PendingConfirmations());
    }

    [Fact]
    public async Task ResolveIsIdempotent_AndASecondClickCannotExecuteTwice()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);

        await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-once-000001", "svc.restart").ToJsonString());
        await harness.Service.ClaimConfirmationAsync("inv-once-000001", CancellationToken.None);
        var resolution = new JsonObject
        {
            ["invocationId"] = "inv-once-000001",
            ["accepted"] = true,
            ["state"] = "succeeded",
            ["summary"] = "done"
        };
        harness.Service.ResolveConfirmation(resolution);
        harness.Service.ResolveConfirmation(resolution);

        var final = await HttpAssert.JsonAsync(await client.GetAsync($"{ControlWire.InvocationsPath}/inv-once-000001"));
        Assert.Equal("succeeded", final["state"]!.GetValue<string>());
        Assert.Equal(0, harness.Bridge.ExecutionCount);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BrokerApprovalRequired_AfterTheDesktopClick_IsAnExplicitFailure_NotASuccess()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.broker", moduleId: "nssm-manager", execution: new JsonObject { ["type"] = "broker.request" });
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.broker"], allowElevated: true);
        using var client = harness.Client(token);

        await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-broker-00001", "svc.broker").ToJsonString());
        await harness.Service.ClaimConfirmationAsync("inv-broker-00001", CancellationToken.None);
        harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-broker-00001",
            ["accepted"] = true,
            ["state"] = "permission-required",
            ["summary"] = "",
            ["errorMessage"] = "Broker approval required."
        });

        var final = await HttpAssert.JsonAsync(await client.GetAsync($"{ControlWire.InvocationsPath}/inv-broker-00001"));
        Assert.True(final["terminal"]!.GetValue<bool>());
        Assert.Equal("permission-required", final["state"]!.GetValue<string>());
        Assert.Equal("permission-required", final["result"]!["errorCode"]!.GetValue<string>());
        Assert.True(final["result"]!["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnsupportedConfirmationKind_FailsExplicitly_AndCreatesNoPendingEntry()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.uac", execution: new JsonObject { ["approval"] = "interactive-uac" });
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.uac"], allowElevated: true);
        using var client = harness.Client(token);

        var response = await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-unsup-00001", "demo.uac").ToJsonString());
        await HttpAssert.AssertErrorAsync(response, HttpStatusCode.Conflict, ControlErrorCodes.UnsupportedConfirmation);
        Assert.Empty(harness.Service.Invocations.PendingConfirmations());
        Assert.Equal(0, harness.Bridge.ExecutionCount);

        var describe = await harness.Service.DescribeAsync(listenerEnabled: true, includeCatalog: false, CancellationToken.None);
        Assert.Empty(describe["pending"]!.AsArray());
    }

    [Fact]
    public async Task PhoneCancel_RemovesAPendingConfirmation_AndNeverExecutesIt()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);

        await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-pcancel-0001", "svc.restart").ToJsonString());
        var cancel = await client.PostAsync($"{ControlWire.InvocationsPath}/inv-pcancel-0001/cancel", "");
        var cancellation = await HttpAssert.JsonAsync(cancel);
        Assert.True(cancellation["accepted"]!.GetValue<bool>());
        Assert.Equal("cancelled", cancellation["state"]!.GetValue<string>());

        var final = await HttpAssert.JsonAsync(await client.GetAsync($"{ControlWire.InvocationsPath}/inv-pcancel-0001"));
        Assert.True(final["terminal"]!.GetValue<bool>());
        Assert.Equal("cancelled", final["state"]!.GetValue<string>());
        Assert.Empty(harness.Service.Invocations.PendingConfirmations());
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task NonElevatedSafeCommand_RunsDirectly_WithoutADesktopConfirmation()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("input-monitor.status", moduleId: "input-monitor", title: "查看状态");
        var (_, token, _) = await harness.GrantAsync("手机", ["input-monitor.status"]);
        using var client = harness.Client(token);

        var response = await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-direct-00001", "input-monitor.status").ToJsonString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await HttpAssert.JsonAsync(response);
        Assert.Equal("succeeded", body["state"]!.GetValue<string>());
        Assert.Equal(1, harness.Bridge.ExecutionCount);
    }

    private static async Task<(string State, string Summary)> RunAsDesktopAsync(
        FakeHostControlBridge bridge,
        string invocationId,
        string commandId,
        JsonObject args)
    {
        var state = "failed";
        var summary = "";
        await foreach (var evt in bridge.ExecuteStreamAsync(invocationId, commandId, args, CancellationToken.None))
        {
            if (evt.FinalResponse is { } final)
            {
                state = final.State;
                summary = final.Summary;
            }
            else if (evt.Terminal)
            {
                state = evt.State;
                summary = evt.Message;
            }
        }

        return (state, summary);
    }
}
