using System.Net;
using System.Text.Json.Nodes;
using RemoteToolGateway.Core;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// The claim/resolve/cancel/revoke state machine. These tests target the defects that only appear
/// when a desktop page, a phone cancel and a revocation interleave, so almost every case drives the
/// service concurrently instead of through a single sequential call.
/// </summary>
public sealed class ConfirmationLifecycleTests
{
    private static JsonObject Submission(string invocationId, string commandId, JsonObject? args = null) => new()
    {
        ["invocationId"] = invocationId,
        ["commandId"] = commandId,
        ["args"] = args ?? new JsonObject()
    };

    private static async Task<(HttpStatusCode Status, JsonObject Body)> SubmitAsync(
        GatewayClient client,
        string invocationId,
        string commandId,
        JsonObject? args = null)
    {
        var response = await client.PostAsync(ControlWire.InvocationsPath, Submission(invocationId, commandId, args).ToJsonString());
        return (response.StatusCode, await HttpAssert.JsonAsync(response));
    }

    private static async Task<JsonObject> ReadAsync(GatewayClient client, string invocationId) =>
        await HttpAssert.JsonAsync(await client.GetAsync($"{ControlWire.InvocationsPath}/{invocationId}"));

    [Fact]
    public async Task SecondClaim_IsRefused_AndNeverReturnsTheArgumentsAgain()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000001", "svc.restart", new JsonObject { ["service"] = "Spooler" });

        var first = await harness.Service.ClaimConfirmationAsync("inv-claim-000001", CancellationToken.None);
        Assert.Equal("Spooler", first["args"]!["service"]!.GetValue<string>());

        var second = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.ClaimConfirmationAsync("inv-claim-000001", CancellationToken.None));
        Assert.Contains("已被电脑端受理", second.Message);

        var state = await ReadAsync(client, "inv-claim-000001");
        Assert.Equal("claimed", state["state"]!.GetValue<string>());
        Assert.False(state["terminal"]!.GetValue<bool>());

        // The desktop page keeps showing the claimed request while the runtime executes it.
        var describe = await harness.Service.DescribeAsync(listenerEnabled: true, includeCatalog: false, CancellationToken.None);
        var listed = describe["pending"]!.AsArray().OfType<JsonObject>().Single();
        Assert.Equal("inv-claim-000001", listed["invocationId"]!.GetValue<string>());
        Assert.True(listed["claimed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Claim_AfterTheGrantWasRevoked_Fails_AndEndsThePendingInvocation()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (grant, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000002", "svc.restart");

        await harness.Service.RevokeGrantAsync(grant.GrantId, CancellationToken.None);

        // Revocation already ended the unclaimed pending request, so the claim can only report that.
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.ClaimConfirmationAsync("inv-claim-000002", CancellationToken.None));
        Assert.Contains("结束", error.Message);

        var record = harness.Service.Invocations.Find("inv-claim-000002");
        Assert.NotNull(record);
        var snapshot = record!.Snapshot();
        Assert.True(snapshot.Terminal);
        Assert.Equal("cancelled", snapshot.State);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task Claim_WhenTheGrantDisappearsDuringTheCatalogRead_FailsWithTheRevocationMessage()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (grant, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000011", "svc.restart");

        // The grant is removed, but the grant's active invocations are not touched yet: this is the
        // window a desktop claim can still be sitting in.
        harness.Bridge.ResetSignals();
        harness.Bridge.CatalogGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claim = harness.Service.ClaimConfirmationAsync("inv-claim-000011", CancellationToken.None);
        await harness.Bridge.CatalogRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Service.Grants.MarkRevoked(grant.GrantId);
        harness.Bridge.CatalogGate.SetResult();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => claim);
        Assert.True(error.Message.Contains("撤销") || error.Message.Contains("结束"), error.Message);

        var record = harness.Service.Invocations.Find("inv-claim-000011")!;
        Assert.True(record.Snapshot().Terminal);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task Claim_AfterTheCommandLeftTheWhitelist_Fails_AndEndsThePendingInvocation()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (grant, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000003", "svc.restart");

        await harness.Service.UpdateGrantAsync(grant.GrantId, "手机", [], allowElevated: true, CancellationToken.None);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.ClaimConfirmationAsync("inv-claim-000003", CancellationToken.None));
        Assert.Contains("授权", error.Message);

        var record = harness.Service.Invocations.Find("inv-claim-000003")!;
        Assert.True(record.Snapshot().Terminal);
        Assert.Equal("command-not-authorized", record.ToWire().Result.ErrorCode);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task ResolveAccept_WithoutAClaim_IsRefused()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000004", "svc.restart");

        var error = Assert.Throws<ArgumentException>(() => harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-claim-000004",
            ["accepted"] = true,
            ["state"] = "succeeded"
        }));
        Assert.Contains("claim", error.Message);

        Assert.Equal(0, harness.Bridge.ExecutionCount);
        var state = await ReadAsync(client, "inv-claim-000004");
        Assert.Equal("awaiting-confirmation", state["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reject_AfterAClaim_IsRefused_SoTheRealOutcomeWins()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000005", "svc.restart");
        await harness.Service.ClaimConfirmationAsync("inv-claim-000005", CancellationToken.None);

        var error = Assert.Throws<ArgumentException>(() => harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-claim-000005",
            ["accepted"] = false
        }));
        Assert.Contains("受理", error.Message);

        harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-claim-000005",
            ["accepted"] = true,
            ["state"] = "succeeded",
            ["summary"] = "done"
        });
        var state = await ReadAsync(client, "inv-claim-000005");
        Assert.True(state["terminal"]!.GetValue<bool>());
        Assert.Equal("succeeded", state["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task PhoneCancel_OnAClaimedInvocation_ReportsTheRuntimeAnswer_AndKeepsTheRealFinalResult()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        harness.Bridge.CancelHandler = invocationId => new HostCancellation(true, invocationId, "cancelling", "运行时已接受取消。");
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000006", "svc.restart");
        await harness.Service.ClaimConfirmationAsync("inv-claim-000006", CancellationToken.None);

        var cancel = await HttpAssert.JsonAsync(
            await client.PostAsync($"{ControlWire.InvocationsPath}/inv-claim-000006/cancel", ""));
        Assert.True(cancel["accepted"]!.GetValue<bool>());
        Assert.False(cancel["terminal"]!.GetValue<bool>());

        // The desktop chain finishes successfully anyway: the real result wins, nothing was cancelled.
        harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-claim-000006",
            ["accepted"] = true,
            ["state"] = "succeeded",
            ["summary"] = "服务已重启"
        });

        var state = await ReadAsync(client, "inv-claim-000006");
        Assert.True(state["terminal"]!.GetValue<bool>());
        Assert.Equal("succeeded", state["state"]!.GetValue<string>());
        Assert.Contains("inv-claim-000006", harness.Bridge.Cancellations);
    }

    [Fact]
    public async Task PhoneCancel_OnAClaimedInvocation_WhenTheRuntimeRejectsIt_KeepsTheClaimedState()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        harness.Bridge.CancelHandler = invocationId => new HostCancellation(false, invocationId, "not-found", "运行时还没有这个调用。");
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000007", "svc.restart");
        await harness.Service.ClaimConfirmationAsync("inv-claim-000007", CancellationToken.None);

        var cancel = await HttpAssert.JsonAsync(
            await client.PostAsync($"{ControlWire.InvocationsPath}/inv-claim-000007/cancel", ""));
        Assert.False(cancel["accepted"]!.GetValue<bool>());
        // The invocation document keeps its own state; the runtime's "not-found" reply is not
        // allowed to masquerade as the invocation state.
        Assert.Equal("claimed", cancel["state"]!.GetValue<string>());
        Assert.Equal("not-found", cancel["cancelState"]!.GetValue<string>());
        Assert.False(cancel["terminal"]!.GetValue<bool>());
    }

    [Fact]
    public async Task PhoneCancel_OnAPendingUnclaimedInvocation_StillEndsItLocally()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000008", "svc.restart");

        var cancel = await HttpAssert.JsonAsync(
            await client.PostAsync($"{ControlWire.InvocationsPath}/inv-claim-000008/cancel", ""));
        Assert.True(cancel["accepted"]!.GetValue<bool>());
        Assert.True(cancel["terminal"]!.GetValue<bool>());
        Assert.Equal("cancelled", cancel["state"]!.GetValue<string>());
        Assert.Empty(harness.Bridge.Cancellations);
    }

    [Fact]
    public async Task Revoke_WhileAClaimedInvocationIsExecuting_RequestsCancel_AndKeepsTheRealOutcome()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        harness.Bridge.CancelHandler = invocationId => new HostCancellation(true, invocationId, "cancelling", "运行时已接受取消。");
        var (grant, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000009", "svc.restart");
        await harness.Service.ClaimConfirmationAsync("inv-claim-000009", CancellationToken.None);

        var revoked = await harness.Service.RevokeGrantAsync(grant.GrantId, CancellationToken.None);
        Assert.Equal(1, revoked["cancelledInvocations"]!.GetValue<int>());
        Assert.Contains("inv-claim-000009", harness.Bridge.Cancellations);

        var record = harness.Service.Invocations.Find("inv-claim-000009")!;
        Assert.False(record.Snapshot().Terminal);

        harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-claim-000009",
            ["accepted"] = true,
            ["state"] = "failed",
            ["errorMessage"] = "服务重启失败"
        });
        Assert.Equal("failed", harness.Service.Invocations.Find("inv-claim-000009")!.Snapshot().State);

        // The revoked token cannot read or cancel anything any more.
        await HttpAssert.AssertErrorAsync(
            await client.GetAsync($"{ControlWire.InvocationsPath}/inv-claim-000009"),
            HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task TerminalResult_CannotBeFlippedByALateResolve()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-terminal-0001", "svc.restart");
        await harness.Service.ClaimConfirmationAsync("inv-terminal-0001", CancellationToken.None);

        harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-terminal-0001",
            ["accepted"] = true,
            ["state"] = "succeeded",
            ["summary"] = "服务已重启"
        });

        // A late, contradictory report must not rewrite a finished invocation.
        var late = harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-terminal-0001",
            ["accepted"] = true,
            ["state"] = "failed",
            ["errorMessage"] = "迟到的失败"
        });
        Assert.Equal("succeeded", late["state"]!.GetValue<string>());

        var state = await ReadAsync(client, "inv-terminal-0001");
        Assert.Equal("succeeded", state["state"]!.GetValue<string>());
        Assert.Equal("服务已重启", state["result"]!["summary"]!.GetValue<string>());
        Assert.Equal("", state["result"]!["errorCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task ConcurrentResolves_KeepExactlyOneTerminalResult()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-terminal-0002", "svc.restart");
        await harness.Service.ClaimConfirmationAsync("inv-terminal-0002", CancellationToken.None);

        var succeeded = Task.Run(() => harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-terminal-0002",
            ["accepted"] = true,
            ["state"] = "succeeded",
            ["summary"] = "成功"
        }));
        var failed = Task.Run(() => harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-terminal-0002",
            ["accepted"] = true,
            ["state"] = "failed",
            ["errorMessage"] = "失败"
        }));
        await Task.WhenAll(succeeded, failed);

        var first = await ReadAsync(client, "inv-terminal-0002");
        var winner = first["state"]!.GetValue<string>();
        Assert.Contains(winner, new[] { "succeeded", "failed" });
        Assert.True(first["terminal"]!.GetValue<bool>());

        // Whatever won is final: a later report with the other outcome cannot flip it.
        harness.Service.ResolveConfirmation(new JsonObject
        {
            ["invocationId"] = "inv-terminal-0002",
            ["accepted"] = true,
            ["state"] = winner == "succeeded" ? "failed" : "succeeded"
        });
        var after = await ReadAsync(client, "inv-terminal-0002");
        Assert.Equal(winner, after["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task TwoConcurrentClaims_ProduceExactlyOneWinner()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (_, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        using var client = harness.Client(token);
        await SubmitAsync(client, "inv-claim-000010", "svc.restart");

        var results = await Task.WhenAll(
            AttemptAsync(harness, "inv-claim-000010"),
            AttemptAsync(harness, "inv-claim-000010"));

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Empty(harness.Bridge.Cancellations);

        static async Task<bool> AttemptAsync(GatewayHarness harness, string invocationId)
        {
            try
            {
                await harness.Service.ClaimConfirmationAsync(invocationId, CancellationToken.None);
                return true;
            }
            catch (ArgumentException) { return false; }
        }
    }
}
