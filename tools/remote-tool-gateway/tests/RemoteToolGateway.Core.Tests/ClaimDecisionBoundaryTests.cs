using System.Net;
using System.Text.Json.Nodes;
using RemoteToolGateway.Core;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// The confirmation decision boundary: the grant as it exists after the slow catalog read is the one
/// that decides, and it is read inside the same boundary that publishes grant updates, revocations
/// and the claim itself. These tests park the catalog read on a barrier and change the authorization
/// while the claim is inside it — the exact window a stale snapshot would have exploited.
/// </summary>
public sealed class ClaimDecisionBoundaryTests
{
    private static JsonObject Submission(string invocationId, string commandId) => new()
    {
        ["invocationId"] = invocationId,
        ["commandId"] = commandId,
        ["args"] = new JsonObject()
    };

    private static async Task<(GatewayHarness Harness, GatewayGrant Grant, string Token)> PendingAsync(
        string invocationId,
        bool allowElevated = true)
    {
        var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (grant, token, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated);
        using var client = harness.Client(token);
        var response = await client.PostAsync(ControlWire.InvocationsPath, Submission(invocationId, "svc.restart").ToJsonString());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (harness, grant, token);
    }

    [Fact]
    public async Task Claim_WhileTheCatalogReadIsBlocked_ThenTheWhitelistIsNarrowed_CannotClaim()
    {
        var (harness, grant, _) = await PendingAsync("inv-boundary-0001");
        await using var _harness = harness;

        harness.Bridge.ResetSignals();
        harness.Bridge.CatalogGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claim = harness.Service.ClaimConfirmationAsync("inv-boundary-0001", CancellationToken.None);
        await harness.Bridge.CatalogRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The desktop narrows the authorization while the confirmation is waiting for the catalog.
        await harness.Service.UpdateGrantAsync(grant.GrantId, "手机", [], allowElevated: true, CancellationToken.None);
        harness.Bridge.CatalogGate.SetResult();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => claim);
        Assert.Contains("授权", error.Message);

        var record = harness.Service.Invocations.Find("inv-boundary-0001")!;
        var snapshot = record.Snapshot();
        Assert.True(snapshot.Terminal);
        Assert.Equal("failed", snapshot.State);
        Assert.Equal(ControlErrorCodes.CommandNotAuthorized, record.ToWire().Result.ErrorCode);
        Assert.False(snapshot.Claimed);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task Claim_WhileTheCatalogReadIsBlocked_ThenElevationIsCleared_CannotClaim()
    {
        var (harness, grant, _) = await PendingAsync("inv-boundary-0002");
        await using var _harness = harness;

        harness.Bridge.ResetSignals();
        harness.Bridge.CatalogGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claim = harness.Service.ClaimConfirmationAsync("inv-boundary-0002", CancellationToken.None);
        await harness.Bridge.CatalogRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.Service.UpdateGrantAsync(grant.GrantId, "手机", ["svc.restart"], allowElevated: false, CancellationToken.None);
        harness.Bridge.CatalogGate.SetResult();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => claim);
        Assert.Contains("提权", error.Message);

        var record = harness.Service.Invocations.Find("inv-boundary-0002")!;
        Assert.True(record.Snapshot().Terminal);
        Assert.Equal(ControlErrorCodes.ElevationNotAllowed, record.ToWire().Result.ErrorCode);
        Assert.False(record.Snapshot().Claimed);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task Claim_WhileTheCatalogReadIsBlocked_ThenRevoked_CannotClaim()
    {
        var (harness, grant, token) = await PendingAsync("inv-boundary-0003");
        await using var _harness = harness;

        harness.Bridge.ResetSignals();
        harness.Bridge.CatalogGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claim = harness.Service.ClaimConfirmationAsync("inv-boundary-0003", CancellationToken.None);
        await harness.Bridge.CatalogRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.Service.RevokeGrantAsync(grant.GrantId, CancellationToken.None);
        harness.Bridge.CatalogGate.SetResult();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => claim);
        Assert.True(error.Message.Contains("撤销") || error.Message.Contains("结束"), error.Message);

        var record = harness.Service.Invocations.Find("inv-boundary-0003")!;
        Assert.True(record.Snapshot().Terminal);
        Assert.False(record.Snapshot().Claimed);
        Assert.Equal(0, harness.Bridge.ExecutionCount);

        using var client = harness.Client(token);
        await HttpAssert.AssertErrorAsync(await client.GetAsync(ControlWire.CatalogPath),
            HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Claim_AfterTheCatalogRead_UsesTheCurrentWhitelist_NotTheSnapshot()
    {
        // The same narrowing, but applied after the catalog was read: the claim still has to decide
        // against the current grant because its decision sits inside the boundary.
        var (harness, grant, _) = await PendingAsync("inv-boundary-0004");
        await using var _harness = harness;

        await harness.Service.UpdateGrantAsync(grant.GrantId, "手机", [], allowElevated: true, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.ClaimConfirmationAsync("inv-boundary-0004", CancellationToken.None));
        Assert.Contains("授权", error.Message);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task GrantUpdate_WorksWhileTheListenerIsStopped_AndReportsNoEndpoint()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("svc.restart", moduleId: "nssm-manager", requiresElevation: true);
        var (grant, _, _) = await harness.GrantAsync("手机", ["svc.restart"], allowElevated: true);
        // Editing an authorization must keep working after the user turns the listener off.
        await harness.Service.StopListenerAsync();

        var updated = await harness.Service.UpdateGrantAsync(grant.GrantId, "手机", ["svc.restart"], allowElevated: false, CancellationToken.None);
        Assert.Equal("", updated["endpoint"]!.GetValue<string>());
        Assert.False(updated["allowElevated"]!.GetValue<bool>());
        Assert.False(updated.ContainsKey("code"));
    }

    [Fact]
    public async Task OtherDevices_CanStillSubmit_WhileARevocationWaitsForASlowRuntimeCancel()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.long");
        harness.Bridge.WithCommand("demo.status");
        harness.Bridge.Handler = (invocationId, commandId, _, token) => commandId == "demo.long"
            ? NeverEndingAsync(invocationId, commandId, token)
            : SucceedsAsync(invocationId, commandId);
        var (slowGrant, slowToken, _) = await harness.GrantAsync("手机 A", ["demo.long"]);
        var (_, fastToken, _) = await harness.GrantAsync("手机 B", ["demo.status"]);
        using var slowClient = harness.Client(slowToken);
        using var fastClient = harness.Client(fastToken);

        await slowClient.PostAsync(ControlWire.InvocationsPath, Submission("inv-boundary-0005", "demo.long").ToJsonString());

        harness.Bridge.CancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revocation = harness.Service.RevokeGrantAsync(slowGrant.GrantId, CancellationToken.None);
        await harness.Bridge.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(revocation.IsCompleted);

        // The admission boundary is free while device A's cancel is still blocked.
        var response = await fastClient.PostAsync(ControlWire.InvocationsPath, Submission("inv-boundary-0006", "demo.status").ToJsonString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await HttpAssert.JsonAsync(response);
        Assert.Equal("succeeded", body["state"]!.GetValue<string>());

        harness.Bridge.CancelGate.SetResult();
        await revocation;
        Assert.Null(harness.Service.Grants.Find(slowGrant.GrantId));
    }

    private static async IAsyncEnumerable<HostExecutionEvent> NeverEndingAsync(
        string invocationId,
        string commandId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new HostExecutionEvent(invocationId, commandId, "running", "正在执行。", false, null);
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(50, cancellationToken); }
            catch (OperationCanceledException) { yield break; }
        }
    }

    private static async IAsyncEnumerable<HostExecutionEvent> SucceedsAsync(string invocationId, string commandId)
    {
        await Task.Yield();
        yield return new HostExecutionEvent(invocationId, commandId, "succeeded", "完成。", true,
            new ControlInvocationResult(invocationId, "succeeded", "完成。", "", "", "", false, null));
    }
}
