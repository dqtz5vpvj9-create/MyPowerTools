using System.Net;
using System.Text.Json.Nodes;
using RemoteToolGateway.Core;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// Revocation and authorization are ordered, not merely sequential: a request that is already
/// inside the gateway when a grant is revoked must not slip through, and a slow or failing runtime
/// cancel must not keep the grant alive.
/// </summary>
public sealed class RevocationRaceTests
{
    private static JsonObject Submission(string invocationId, string commandId) => new()
    {
        ["invocationId"] = invocationId,
        ["commandId"] = commandId,
        ["args"] = new JsonObject()
    };

    [Fact]
    public async Task Revoke_WhileTheCatalogReadIsBlocked_RejectsTheInFlightSubmission()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.status");
        var (grant, token, _) = await harness.GrantAsync("手机", ["demo.status"]);
        using var client = harness.Client(token);

        harness.Bridge.CatalogGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submission = client.PostAsync(ControlWire.InvocationsPath, Submission("inv-race-000001", "demo.status").ToJsonString());
        await harness.Bridge.CatalogRequested!.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The submission is parked inside the catalog read while the desktop revokes the grant.
        await harness.Service.RevokeGrantAsync(grant.GrantId, CancellationToken.None);
        harness.Bridge.CatalogGate.SetResult();

        var response = await submission;
        await HttpAssert.AssertErrorAsync(response, HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
        Assert.Null(harness.Service.Invocations.Find("inv-race-000001"));
    }

    [Fact]
    public async Task Revoke_WhileTheCatalogReadIsBlocked_RejectsANarrowedWhitelist()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.status");
        var (grant, token, _) = await harness.GrantAsync("手机", ["demo.status"]);
        using var client = harness.Client(token);

        harness.Bridge.CatalogGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submission = client.PostAsync(ControlWire.InvocationsPath, Submission("inv-race-000002", "demo.status").ToJsonString());
        await harness.Bridge.CatalogRequested!.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await harness.Service.UpdateGrantAsync(grant.GrantId, "手机", [], allowElevated: false, CancellationToken.None);
        harness.Bridge.CatalogGate.SetResult();

        var response = await submission;
        await HttpAssert.AssertErrorAsync(response, HttpStatusCode.Forbidden, ControlErrorCodes.CommandNotAuthorized);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task Revoke_WithASlowRuntimeCancel_RejectsNewRequestsBeforeTheCancelReturns()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.long");
        harness.Bridge.Handler = (invocationId, commandId, _, token) => NeverEndingAsync(invocationId, commandId, token);
        var (grant, token, _) = await harness.GrantAsync("手机", ["demo.long"]);
        using var client = harness.Client(token);

        await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-race-000003", "demo.long").ToJsonString());

        harness.Bridge.CancelGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var revocation = harness.Service.RevokeGrantAsync(grant.GrantId, CancellationToken.None);
        await harness.Bridge.CancelRequested!.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The revoke call is still blocked inside the runtime cancel, yet the token is already dead.
        Assert.False(revocation.IsCompleted);
        using var secondClient = harness.Client(token);
        var rejected = await secondClient.GetAsync(ControlWire.CatalogPath);
        await HttpAssert.AssertErrorAsync(rejected, HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);

        harness.Bridge.CancelGate.SetResult();
        var result = await revocation;
        Assert.Equal(grant.GrantId, result["revoked"]!.GetValue<string>());
    }

    [Fact]
    public async Task Revoke_WhenTheRuntimeCancelThrows_StillRevokesAndKeepsTheRealState()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.long");
        harness.Bridge.Handler = (invocationId, commandId, _, token) => NeverEndingAsync(invocationId, commandId, token);
        harness.Bridge.CancelHandler = _ => throw new IOException("runtime gone");
        var (grant, token, _) = await harness.GrantAsync("手机", ["demo.long"]);
        using var client = harness.Client(token);

        await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-race-000004", "demo.long").ToJsonString());
        var revoked = await harness.Service.RevokeGrantAsync(grant.GrantId, CancellationToken.None);

        Assert.Equal(grant.GrantId, revoked["revoked"]!.GetValue<string>());
        Assert.Equal(0, revoked["cancelledInvocations"]!.GetValue<int>());
        var record = harness.Service.Invocations.Find("inv-race-000004")!;
        Assert.False(record.Snapshot().Terminal);
        Assert.Null(harness.Service.Grants.Find(grant.GrantId));
    }

    [Fact]
    public async Task Cancel_WhenTheRuntimeRejects_ThenTheStreamEnds_ReportsUnknownNotCancelled()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.ends");
        harness.Bridge.Handler = (invocationId, commandId, _, _) => EndsWithoutTerminalAsync(invocationId, commandId);
        harness.Bridge.CancelHandler = invocationId => new HostCancellation(false, invocationId, "not-found", "运行时没有这个调用。");
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.ends"]);
        using var client = harness.Client(token);

        await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-race-000005", "demo.ends").ToJsonString());
        var cancel = await HttpAssert.JsonAsync(await client.PostAsync($"{ControlWire.InvocationsPath}/inv-race-000005/cancel", ""));
        Assert.False(cancel["accepted"]!.GetValue<bool>());

        var final = await WaitForTerminalAsync(client, "inv-race-000005");
        Assert.Equal("failed", final["state"]!.GetValue<string>());
        Assert.NotEqual("cancelled", final["state"]!.GetValue<string>());
        Assert.Equal("host-stream-ended", final["result"]!["errorCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Cancel_WhenTheRuntimeAccepts_ButNoTerminalArrives_ReportsUnknownNotCancelled()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.ends");
        harness.Bridge.Handler = (invocationId, commandId, _, token) => NeverEndingAsync(invocationId, commandId, token);
        harness.Bridge.CancelHandler = invocationId => new HostCancellation(true, invocationId, "cancelling", "运行时已接受取消。");
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.ends"]);
        using var client = harness.Client(token);

        await client.PostAsync(ControlWire.InvocationsPath, Submission("inv-race-000006", "demo.ends").ToJsonString());
        var cancel = await HttpAssert.JsonAsync(await client.PostAsync($"{ControlWire.InvocationsPath}/inv-race-000006/cancel", ""));
        Assert.True(cancel["accepted"]!.GetValue<bool>());
        Assert.Equal("cancelling", cancel["state"]!.GetValue<string>());
        Assert.False(cancel["terminal"]!.GetValue<bool>());

        // The cancel grace releases the local stream without the runtime ever answering.
        var final = await WaitForTerminalAsync(client, "inv-race-000006", TimeSpan.FromSeconds(10));
        Assert.Equal("failed", final["state"]!.GetValue<string>());
        Assert.Equal("cancel-unconfirmed", final["result"]!["errorCode"]!.GetValue<string>());
        Assert.True(final["result"]!["retryable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Cancel_WhenTheRuntimeRejects_ThenTheRealSuccess_IsStillReported()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.finishes");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Bridge.Handler = (invocationId, commandId, _, _) => FinishesLaterAsync(invocationId, commandId, release.Task);
        harness.Bridge.CancelHandler = invocationId => new HostCancellation(false, invocationId, "running", "取消未被接受。");
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.finishes"]);
        using var client = harness.Client(token);

        var submit = client.PostAsync(ControlWire.InvocationsPath, Submission("inv-race-000007", "demo.finishes").ToJsonString());
        await Task.Delay(100);
        var cancel = await HttpAssert.JsonAsync(await client.PostAsync($"{ControlWire.InvocationsPath}/inv-race-000007/cancel", ""));
        Assert.False(cancel["accepted"]!.GetValue<bool>());

        release.SetResult();
        await submit;
        var final = await WaitForTerminalAsync(client, "inv-race-000007");
        Assert.Equal("succeeded", final["state"]!.GetValue<string>());
    }

    private static async Task<JsonObject> WaitForTerminalAsync(GatewayClient client, string invocationId, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(8));
        JsonObject? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"{ControlWire.InvocationsPath}/{invocationId}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            last = await HttpAssert.JsonAsync(response);
            if (last["terminal"]!.GetValue<bool>()) return last;
            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"Invocation {invocationId} never became terminal. Last: {last?.ToJsonString()}");
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

    private static async IAsyncEnumerable<HostExecutionEvent> EndsWithoutTerminalAsync(string invocationId, string commandId)
    {
        await Task.Yield();
        yield return new HostExecutionEvent(invocationId, commandId, "running", "正在执行。", false, null);
    }

    private static async IAsyncEnumerable<HostExecutionEvent> FinishesLaterAsync(
        string invocationId,
        string commandId,
        Task release)
    {
        await Task.Yield();
        yield return new HostExecutionEvent(invocationId, commandId, "running", "正在执行。", false, null);
        await release;
        yield return new HostExecutionEvent(invocationId, commandId, "succeeded", "完成。", true,
            new ControlInvocationResult(invocationId, "succeeded", "完成。", "", "", "", false, null));
    }
}
