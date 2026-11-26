using System.Net;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using RemoteToolGateway.Core;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// A fully isolated gateway: fake executor, in-memory secret store, loopback test transport on an
/// ephemeral port. Nothing here touches the real runtime, the shared preferences or the tailnet.
/// </summary>
internal sealed class GatewayHarness : IAsyncDisposable
{
    private GatewayHarness(string directory, FakeHostControlBridge bridge)
    {
        Directory = directory;
        Bridge = bridge;
        Secrets = new InMemorySecretStore();
        Service = new RemoteToolGatewayService(directory, Secrets, bridge, new RemoteToolGatewayOptions
        {
            ModuleId = "remote-tool-gateway",
            DeviceName = "测试电脑",
            Platform = "windows",
            DefaultPort = 49541,
            AllowLoopbackTransport = true
        });
    }

    public string Directory { get; }
    public FakeHostControlBridge Bridge { get; }
    public InMemorySecretStore Secrets { get; }
    public RemoteToolGatewayService Service { get; }
    public int Port => Service.ListenerPort;

    public static async Task<GatewayHarness> StartAsync(FakeHostControlBridge? bridge = null, bool startListener = true)
    {
        var harness = new GatewayHarness(TestPaths.NewDirectory("gateway"), bridge ?? new FakeHostControlBridge());
        await harness.Service.InitializeAsync(CancellationToken.None);
        if (startListener) await harness.Service.StartListenerAsync("127.0.0.1", 0, CancellationToken.None);
        return harness;
    }

    public async Task<(GatewayGrant Grant, string Token, string Code)> GrantAsync(
        string deviceName,
        IEnumerable<string> commandIds,
        bool allowElevated = false)
    {
        var payload = await Service.CreateGrantAsync(deviceName, commandIds.ToArray(), allowElevated, CancellationToken.None);
        var code = payload["code"]!.GetValue<string>();
        Assert.StartsWith(ControlConnectionCode.Scheme, code);
        var grantId = payload["grantId"]!.GetValue<string>();
        var grant = Service.Grants.Find(grantId);
        Assert.NotNull(grant);
        // The loopback test transport is not a Tailnet endpoint, so the token is read from the
        // secret store instead of from the code; the codec's Tailnet rules have their own tests.
        var token = await Secrets.ReadAsync(SecretReference.Create("remote-tool-gateway", "grant-" + grantId), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(token));
        return (grant!, token!, code);
    }

    public GatewayClient Client(string token) => new(Port, token);

    public string GrantsFile => Path.Combine(Directory, "grants.json");
    public string AuditFile => Path.Combine(Directory, "audit.jsonl");

    public async ValueTask DisposeAsync() => await Service.DisposeAsync();
}

public sealed class GatewayHttpTests
{
    private static JsonObject Submission(string invocationId, string commandId, JsonObject? args = null) => new()
    {
        ["invocationId"] = invocationId,
        ["commandId"] = commandId,
        ["args"] = args ?? new JsonObject()
    };

    private static async Task<(HttpStatusCode Status, JsonObject Body)> InvokeAsync(
        GatewayClient client,
        string invocationId,
        string commandId,
        JsonObject? args = null)
    {
        var response = await client.PostAsync(ControlWire.InvocationsPath, Submission(invocationId, commandId, args).ToJsonString());
        return (response.StatusCode, await HttpAssert.JsonAsync(response));
    }

    [Fact]
    public async Task Listener_IsOffByDefault_AndStopsAcceptingAfterStop()
    {
        await using var harness = await GatewayHarness.StartAsync(startListener: false);
        Assert.False(harness.Service.ListenerRunning);
        Assert.Contains("监听未开启", harness.Service.ListenerMessage);

        var describe = await harness.Service.DescribeAsync(listenerEnabled: false, includeCatalog: false, CancellationToken.None);
        Assert.False(describe["listener"]!["running"]!.GetValue<bool>());

        await harness.Service.StartListenerAsync("127.0.0.1", 0, CancellationToken.None);
        Assert.True(harness.Service.ListenerRunning);
        var port = harness.Service.ListenerPort;
        Assert.True(port > 0);

        await harness.Service.StopListenerAsync();
        Assert.False(harness.Service.ListenerRunning);
        using var client = new GatewayClient(port, "x");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(ControlWire.CatalogPath));
    }

    [Fact]
    public async Task Listener_RefusesLanWildcardAndFileTransferPort()
    {
        await using var harness = await GatewayHarness.StartAsync(startListener: false);
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.StartListenerAsync("192.168.1.10", 49541, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.StartListenerAsync("0.0.0.0", 49541, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.StartListenerAsync("127.0.0.1", RemoteToolGatewayService.FileTransferPort, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => harness.Service.StartListenerAsync("127.0.0.1", 80, CancellationToken.None));
        Assert.False(harness.Service.ListenerRunning);
    }

    [Fact]
    public async Task PeersOutsideTheTailnet_AreRefused_BeforeAnyHandlerRuns()
    {
        var handled = 0;
        await using var listener = new ControlHttpListener(IPAddress.Loopback, 0, allowLoopbackPeers: false,
            (_, _) =>
            {
                Interlocked.Increment(ref handled);
                return Task.FromResult(ControlResponse.Json(200, "{}"));
            });
        listener.Start();
        var port = listener.LocalEndpoint!.Port;
        using var client = new GatewayClient(port, "x");
        var response = await client.GetAsync(ControlWire.CatalogPath);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ControlErrorCodes.ForbiddenPeer, await HttpAssert.ErrorCodeAsync(response));
        Assert.Equal(0, handled);
    }

    [Fact]
    public async Task MissingWrongForeignAndRevokedTokens_AreRejected_WithJsonErrors()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithTool("demo.status").WithCommand("demo.status");
        var (grant, token, _) = await harness.GrantAsync("手机 A", ["demo.status"]);

        using (var anonymous = new GatewayClient(harness.Port, ""))
        {
            var response = await anonymous.GetAsync(ControlWire.CatalogPath);
            await HttpAssert.AssertErrorAsync(response, HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);
        }

        using (var wrong = new GatewayClient(harness.Port, new string('a', 48)))
        {
            var response = await wrong.GetAsync(ControlWire.CatalogPath);
            await HttpAssert.AssertErrorAsync(response, HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);
        }

        // A token minted by another gateway instance is not interchangeable.
        await using var other = await GatewayHarness.StartAsync();
        var (_, otherToken, _) = await other.GrantAsync("手机 B", ["demo.status"]);
        using (var foreign = new GatewayClient(harness.Port, otherToken))
        {
            var response = await foreign.GetAsync(ControlWire.CatalogPath);
            await HttpAssert.AssertErrorAsync(response, HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);
        }

        await harness.Service.RevokeGrantAsync(grant.GrantId, CancellationToken.None);
        using var revoked = new GatewayClient(harness.Port, token);
        var revokedResponse = await revoked.GetAsync(ControlWire.CatalogPath);
        await HttpAssert.AssertErrorAsync(revokedResponse, HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task FileTransferPairingToken_IsNeverAccepted()
    {
        await using var harness = await GatewayHarness.StartAsync();
        var pairingToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        using var client = new GatewayClient(harness.Port, pairingToken);
        var response = await client.GetAsync(ControlWire.CatalogPath);
        await HttpAssert.AssertErrorAsync(response, HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task Catalog_IsGrantScoped_AndManagementCommandsAreNeverAllowed()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge
            .WithTool("input-monitor", "input-monitor", "休息提醒")
            .WithTool("remote-tool-gateway", "remote-tool-gateway", "远程工具访问")
            .WithCommand("input-monitor.status", "input-monitor", "查看状态")
            .WithCommand("input-monitor.pause", "input-monitor", "暂停提醒")
            .WithCommand("remote-tool-gateway.grant.revoke", "remote-tool-gateway", "撤销授权");
        var (_, token, _) = await harness.GrantAsync("手机", ["input-monitor.status", "remote-tool-gateway.grant.revoke"]);

        using var client = harness.Client(token);
        var response = await client.GetAsync(ControlWire.CatalogPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalog = await HttpAssert.JsonAsync(response);
        Assert.Equal("测试电脑", catalog["device"]!["name"]!.GetValue<string>());
        Assert.Equal("windows", catalog["device"]!["platform"]!.GetValue<string>());
        var tools = catalog["tools"]!.AsArray();
        Assert.Contains(tools, tool => tool!["toolId"]!.GetValue<string>() == "input-monitor");

        var commands = catalog["commands"]!.AsArray().Select(item => item!.AsObject()).ToDictionary(item => item["commandId"]!.GetValue<string>());
        Assert.True(commands["input-monitor.status"]["allowed"]!.GetValue<bool>());
        Assert.False(commands["input-monitor.pause"]["allowed"]!.GetValue<bool>());
        Assert.Contains("未加入", commands["input-monitor.pause"]["notAllowedReason"]!.GetValue<string>());
        Assert.False(commands["remote-tool-gateway.grant.revoke"]["allowed"]!.GetValue<bool>());
        Assert.Contains("网关自身", commands["remote-tool-gateway.grant.revoke"]["notAllowedReason"]!.GetValue<string>());
        Assert.Equal("查看状态", commands["input-monitor.status"]["title"]!.GetValue<string>());
        Assert.Equal("input-monitor", commands["input-monitor.status"]["moduleId"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnauthorizedUnknownAndManagementCommands_AreRejected_WithoutExecuting()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge
            .WithCommand("demo.status")
            .WithCommand("demo.restart")
            .WithCommand("remote-tool-gateway.grant.revoke", "remote-tool-gateway");
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.status", "remote-tool-gateway.grant.revoke"]);
        using var client = harness.Client(token);

        var (unknownStatus, unknownBody) = await InvokeAsync(client, "inv-unknown-0001", "demo.missing");
        Assert.Equal(HttpStatusCode.NotFound, unknownStatus);
        Assert.Equal(ControlErrorCodes.CommandNotFound, unknownBody["error"]!["code"]!.GetValue<string>());

        var (deniedStatus, deniedBody) = await InvokeAsync(client, "inv-denied-0001", "demo.restart");
        Assert.Equal(HttpStatusCode.Forbidden, deniedStatus);
        Assert.Equal(ControlErrorCodes.CommandNotAuthorized, deniedBody["error"]!["code"]!.GetValue<string>());

        var (managementStatus, managementBody) = await InvokeAsync(client, "inv-manage-0001", "remote-tool-gateway.grant.revoke");
        Assert.Equal(HttpStatusCode.Forbidden, managementStatus);
        Assert.Equal(ControlErrorCodes.Forbidden, managementBody["error"]!["code"]!.GetValue<string>());

        Assert.Equal(0, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task AuthorizedCommand_ReturnsRealTerminalResult_AndBoundedPayload()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.status");
        harness.Bridge.Handler = (invocationId, commandId, _, _) => Huge(invocationId, commandId);
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.status"]);
        using var client = harness.Client(token);

        var (status, body) = await InvokeAsync(client, "inv-huge-000001", "demo.status");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body["terminal"]!.GetValue<bool>());
        Assert.Equal("succeeded", body["state"]!.GetValue<string>());
        var result = body["result"]!.AsObject();
        Assert.True(result["summary"]!.GetValue<string>().Length <= ControlWire.MaxSummaryLength);
        Assert.True(body["message"]!.GetValue<string>().Length <= ControlWire.MaxMessageLength);
        Assert.Equal("inv-huge-000001", result["invocationId"]!.GetValue<string>());
        Assert.Equal(1, harness.Bridge.ExecutionCount);

        static async IAsyncEnumerable<HostExecutionEvent> Huge(string invocationId, string commandId)
        {
            await Task.Yield();
            yield return new HostExecutionEvent(invocationId, commandId, "succeeded", new string('好', ControlWire.MaxSummaryLength * 3), true,
                new ControlInvocationResult(invocationId, "succeeded", new string('好', ControlWire.MaxSummaryLength * 3), new string('c', 900),
                    "", "", false, null));
        }
    }

    [Fact]
    public async Task FailedCommand_KeepsTheRealFailure_InsteadOfReportingSuccess()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.status");
        harness.Bridge.Handler = (invocationId, commandId, _, _) => Failing(invocationId, commandId);
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.status"]);
        using var client = harness.Client(token);

        var (status, body) = await InvokeAsync(client, "inv-fail-000001", "demo.status");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body["terminal"]!.GetValue<bool>());
        Assert.Equal("failed", body["state"]!.GetValue<string>());
        Assert.Equal("demo.failed", body["result"]!["errorCode"]!.GetValue<string>());
        Assert.Equal("真实失败原因", body["result"]!["errorMessage"]!.GetValue<string>());
        Assert.True(body["result"]!["retryable"]!.GetValue<bool>());

        static async IAsyncEnumerable<HostExecutionEvent> Failing(string invocationId, string commandId)
        {
            await Task.Yield();
            yield return new HostExecutionEvent(invocationId, commandId, "failed", "真实失败原因", true,
                new ControlInvocationResult(invocationId, "failed", "", "", "demo.failed", "真实失败原因", true, null));
        }
    }

    [Fact]
    public async Task DuplicateInvocationId_ExecutesOnce_AndConflictsAcrossGrants()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.status");
        var (_, tokenA, _) = await harness.GrantAsync("手机 A", ["demo.status"]);
        var (_, tokenB, _) = await harness.GrantAsync("手机 B", ["demo.status"]);
        using var clientA = harness.Client(tokenA);
        using var clientB = harness.Client(tokenB);

        var (firstStatus, first) = await InvokeAsync(clientA, "inv-dup-0000001", "demo.status");
        var (secondStatus, second) = await InvokeAsync(clientA, "inv-dup-0000001", "demo.status");
        Assert.Equal(HttpStatusCode.OK, firstStatus);
        Assert.Equal(HttpStatusCode.OK, secondStatus);
        Assert.Equal(first["state"]!.GetValue<string>(), second["state"]!.GetValue<string>());
        Assert.Equal(1, harness.Bridge.ExecutionCount);

        var conflict = await clientB.PostAsync(ControlWire.InvocationsPath,
            Submission("inv-dup-0000001", "demo.status").ToJsonString());
        await HttpAssert.AssertErrorAsync(conflict, HttpStatusCode.Conflict, ControlErrorCodes.InvocationExists);
        Assert.Equal(1, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task CrossGrant_ReadAndCancel_ReturnNotFound_WithoutLeakingState()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.long");
        harness.Bridge.Handler = (invocationId, commandId, _, token) => LongRunning(invocationId, commandId, token);
        var (_, tokenA, _) = await harness.GrantAsync("手机 A", ["demo.long"]);
        var (_, tokenB, _) = await harness.GrantAsync("手机 B", ["demo.long"]);
        using var clientA = harness.Client(tokenA);
        using var clientB = harness.Client(tokenB);

        var (submitStatus, submitted) = await InvokeAsync(clientA, "inv-cross-000001", "demo.long");
        Assert.Equal(HttpStatusCode.Accepted, submitStatus);
        Assert.False(submitted["terminal"]!.GetValue<bool>());

        var foreignRead = await clientB.GetAsync($"{ControlWire.InvocationsPath}/inv-cross-000001");
        await HttpAssert.AssertErrorAsync(foreignRead, HttpStatusCode.NotFound, ControlErrorCodes.NotFound);
        var foreignCancel = await clientB.PostAsync($"{ControlWire.InvocationsPath}/inv-cross-000001/cancel", "");
        await HttpAssert.AssertErrorAsync(foreignCancel, HttpStatusCode.NotFound, ControlErrorCodes.NotFound);

        var ownRead = await clientA.GetAsync($"{ControlWire.InvocationsPath}/inv-cross-000001");
        Assert.Equal(HttpStatusCode.OK, ownRead.StatusCode);
        var own = await HttpAssert.JsonAsync(ownRead);
        Assert.Equal("inv-cross-000001", own["invocationId"]!.GetValue<string>());
    }

    [Fact]
    public async Task CancelRunningInvocation_UsesTheRuntimeCancellation_AndReportsItsAnswer()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.long");
        harness.Bridge.Handler = (invocationId, commandId, _, token) => LongRunning(invocationId, commandId, token);
        harness.Bridge.CancelHandler = invocationId => new HostCancellation(true, invocationId, "cancelling", "运行时已接受取消。");
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.long"]);
        using var client = harness.Client(token);

        var (submitStatus, _) = await InvokeAsync(client, "inv-cancel-00001", "demo.long");
        Assert.Equal(HttpStatusCode.Accepted, submitStatus);

        var cancelResponse = await client.PostAsync($"{ControlWire.InvocationsPath}/inv-cancel-00001/cancel", "");
        Assert.Equal(HttpStatusCode.OK, cancelResponse.StatusCode);
        var cancellation = await HttpAssert.JsonAsync(cancelResponse);
        Assert.True(cancellation["accepted"]!.GetValue<bool>());
        Assert.True(cancellation["cancelAccepted"]!.GetValue<bool>());
        Assert.Equal("cancelling", cancellation["state"]!.GetValue<string>());
        // The cancel answer is also a full invocation document, so both halves of the contract read
        // the same body: state/terminal/result plus the real accepted flags.
        Assert.Equal("inv-cancel-00001", cancellation["invocationId"]!.GetValue<string>());
        Assert.Equal("demo.long", cancellation["commandId"]!.GetValue<string>());
        Assert.False(cancellation["terminal"]!.GetValue<bool>());
        Assert.NotNull(cancellation["result"]);
        Assert.Contains("inv-cancel-00001", harness.Bridge.Cancellations);

        var final = await WaitForTerminalAsync(harness, client, "inv-cancel-00001", TimeSpan.FromSeconds(8));
        Assert.Equal("cancelled", final["state"]!.GetValue<string>());

        // A second cancel on a finished invocation reports the real state, never "accepted".
        var again = await client.PostAsync($"{ControlWire.InvocationsPath}/inv-cancel-00001/cancel", "");
        var againBody = await HttpAssert.JsonAsync(again);
        Assert.False(againBody["accepted"]!.GetValue<bool>());
        Assert.False(againBody["cancelAccepted"]!.GetValue<bool>());
        Assert.Equal("cancelled", againBody["state"]!.GetValue<string>());
        Assert.True(againBody["terminal"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Revocation_ImmediatelyRejects_AndCancelsTheGrantsActiveInvocations()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.long");
        harness.Bridge.Handler = (invocationId, commandId, _, token) => LongRunning(invocationId, commandId, token);
        var (grant, token, _) = await harness.GrantAsync("手机", ["demo.long"]);
        using var client = harness.Client(token);

        var (submitStatus, _) = await InvokeAsync(client, "inv-revoke-00001", "demo.long");
        Assert.Equal(HttpStatusCode.Accepted, submitStatus);

        var revoked = await harness.Service.RevokeGrantAsync(grant.GrantId, CancellationToken.None);
        Assert.Equal(1, revoked["cancelledInvocations"]!.GetValue<int>());
        Assert.Contains("inv-revoke-00001", harness.Bridge.Cancellations);

        var rejected = await client.GetAsync(ControlWire.CatalogPath);
        await HttpAssert.AssertErrorAsync(rejected, HttpStatusCode.Unauthorized, ControlErrorCodes.Unauthorized);

        var final = await WaitForTerminalAsync(harness, client, "inv-revoke-00001", TimeSpan.FromSeconds(8));
        Assert.Equal("cancelled", final["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task RequestBounds_AndMalformedBodies_AreRejected()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.status");
        var (_, token, _) = await harness.GrantAsync("手机", ["demo.status"]);
        using var client = harness.Client(token);

        var oversized = new string('x', ControlWire.MaxRequestBytes + 1024);
        var largeResponse = await client.PostAsync(ControlWire.InvocationsPath, oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, largeResponse.StatusCode);
        Assert.Equal(ControlErrorCodes.PayloadTooLarge, await HttpAssert.ErrorCodeAsync(largeResponse));

        var malformed = await client.PostAsync(ControlWire.InvocationsPath, "not json");
        await HttpAssert.AssertErrorAsync(malformed, HttpStatusCode.BadRequest, ControlErrorCodes.BadRequest);

        var badId = await client.PostAsync(ControlWire.InvocationsPath, Submission("bad id/with%chars", "demo.status").ToJsonString());
        await HttpAssert.AssertErrorAsync(badId, HttpStatusCode.BadRequest, ControlErrorCodes.BadRequest);

        var tooLong = await client.PostAsync(ControlWire.InvocationsPath, Submission(new string('a', 65), "demo.status").ToJsonString());
        await HttpAssert.AssertErrorAsync(tooLong, HttpStatusCode.BadRequest, ControlErrorCodes.BadRequest);

        // A minimal client-generated id is accepted: the other half of the contract decides its shape.
        var minimalId = await client.PostAsync(ControlWire.InvocationsPath, Submission("1", "demo.status").ToJsonString());
        Assert.Equal(HttpStatusCode.OK, minimalId.StatusCode);

        var wrongMethod = await client.PostAsync(ControlWire.CatalogPath, "");
        await HttpAssert.AssertErrorAsync(wrongMethod, HttpStatusCode.MethodNotAllowed, ControlErrorCodes.MethodNotAllowed);

        var unknownPath = await client.GetAsync(ControlWire.Prefix + "/nope");
        await HttpAssert.AssertErrorAsync(unknownPath, HttpStatusCode.NotFound, ControlErrorCodes.NotFound);

        // Every rejected request above stopped before HostControl; only the accepted minimal-id
        // invocation reached the executor.
        Assert.Equal(1, harness.Bridge.ExecutionCount);
    }

    [Fact]
    public async Task TokensAndSecretArguments_NeverReachPreferencesAuditOrInspect()
    {
        await using var harness = await GatewayHarness.StartAsync();
        harness.Bridge.WithCommand("demo.rotate", dangerLevel: "danger");
        var (grant, token, _) = await harness.GrantAsync("手机", ["demo.rotate"]);
        using var client = harness.Client(token);

        var (submitStatus, body) = await InvokeAsync(client, "inv-secret-0001", "demo.rotate",
            new JsonObject { ["adminPassword"] = "hunter2-secret", ["note"] = "hello" });
        Assert.Equal(HttpStatusCode.Accepted, submitStatus);
        Assert.Equal("awaiting-confirmation", body["state"]!.GetValue<string>());

        var describe = await harness.Service.DescribeAsync(listenerEnabled: true, includeCatalog: false, CancellationToken.None);
        var describeText = describe.ToJsonString();
        Assert.DoesNotContain(token, describeText);
        Assert.DoesNotContain("hunter2-secret", describeText);
        Assert.Contains("****", describe["pending"]![0]!["argsSummary"]!.GetValue<string>());

        await harness.Service.Audit.FlushAsync();
        var grantsText = await File.ReadAllTextAsync(harness.GrantsFile);
        Assert.DoesNotContain(token, grantsText);
        Assert.Contains(grant.GrantId, grantsText);
        Assert.DoesNotContain("hunter2-secret", grantsText);

        var auditText = File.Exists(harness.AuditFile) ? await File.ReadAllTextAsync(harness.AuditFile) : "";
        Assert.DoesNotContain(token, auditText);
        Assert.DoesNotContain("hunter2-secret", auditText);

        // The claim path is the only one that returns the real arguments, and it is local-only.
        var claim = await harness.Service.ClaimConfirmationAsync("inv-secret-0001", CancellationToken.None);
        Assert.Equal("hunter2-secret", claim["args"]!["adminPassword"]!.GetValue<string>());
    }

    [Fact]
    public async Task IdleGateway_DoesNoBackgroundWork()
    {
        await using var harness = await GatewayHarness.StartAsync();
        var auditBefore = harness.Service.Audit.Recent(200).Count;
        var invocationsBefore = harness.Service.Invocations.Recent(50).Count;
        await Task.Delay(400);
        Assert.Equal(auditBefore, harness.Service.Audit.Recent(200).Count);
        Assert.Equal(invocationsBefore, harness.Service.Invocations.Recent(50).Count);
        Assert.Equal(0, harness.Bridge.ExecutionCount);
        Assert.True(harness.Service.ListenerRunning);
    }

    private static async IAsyncEnumerable<HostExecutionEvent> LongRunning(
        string invocationId,
        string commandId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new HostExecutionEvent(invocationId, commandId, "running", "正在执行。", false, null);
        try { await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken); }
        catch (OperationCanceledException) { }
        yield return new HostExecutionEvent(invocationId, commandId, "cancelled", "调用已取消。", true,
            new ControlInvocationResult(invocationId, "cancelled", "", "", "cancelled", "调用已取消。", true, null));
    }

    private static async Task<JsonObject> WaitForTerminalAsync(
        GatewayHarness harness,
        GatewayClient client,
        string invocationId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        JsonObject? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync($"{ControlWire.InvocationsPath}/{invocationId}");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                last = await HttpAssert.JsonAsync(response);
                if (last["terminal"]!.GetValue<bool>()) return last;
            }
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The grant was revoked and the token is already dead; the local record is the
                // only honest source left, and it must show a terminal cancellation.
                var record = harness.Service.Invocations.Find(invocationId);
                if (record is { Terminal: true }) return JsonNode.Parse(ControlWire.Serialize(record.ToWire()))!.AsObject();
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException($"Invocation {invocationId} did not reach a terminal state. Last: {last?.ToJsonString()}");
    }
}
