using System.Text.Json.Nodes;
using MobileToolControl.Android;

namespace MobileToolControl.Android.Tests;

/// <summary>
/// Invocation rules from the contract: only authorized commands are submitted, a repeated invocation
/// id never executes twice, cancel returns the computer's real answer, and another grant's invocation
/// is neither readable nor probeable.
/// </summary>
public sealed class InvocationFlowTests
{
    private static JsonObject CatalogWith(params (string CommandId, bool Allowed, bool Elevation)[] commands)
    {
        var array = new JsonArray();
        foreach (var (commandId, allowed, elevation) in commands)
        {
            array.Add(new JsonObject
            {
                ["commandId"] = commandId,
                ["moduleId"] = "input-monitor",
                ["title"] = commandId,
                ["subtitle"] = "",
                ["dangerLevel"] = "",
                ["requiresElevation"] = elevation,
                ["supportsProgress"] = true,
                ["supportsCancellation"] = true,
                ["allowed"] = allowed,
                ["notAllowedReason"] = allowed ? "" : "此电脑未允许需要管理员权限的命令。",
                ["parameters"] = new JsonArray()
            });
        }

        return new JsonObject
        {
            ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
            ["tools"] = new JsonArray(),
            ["commands"] = array
        };
    }

    private static async Task<ModuleHarness> HarnessWithAsync(GatewayDouble gateway, params (string, bool, bool)[] commands)
    {
        gateway.Catalog = CatalogWith(commands);
        var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        return harness;
    }

    private static JsonObject InvokeArgs(string invocationId, string commandId, JsonObject? args = null) => new()
    {
        [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
        [MobileToolControlOptions.ArgumentCommandId] = commandId,
        [MobileToolControlOptions.ArgumentInvocationId] = invocationId,
        [MobileToolControlOptions.ArgumentArgs] = args ?? new JsonObject { ["category"] = "all" }
    };

    [Fact]
    public async Task InvokePostsTheContractBodyAndReturnsTheWireState()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("input-monitor.stats", true, false));
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvoke,
            InvokeArgs("inv-1", "input-monitor.stats"));

        Assert.True(result.Success);
        var post = Assert.Single(gateway.Requests, request => request.Method == "POST");
        Assert.Equal("/mpt-control/v1/invocations", post.Path);
        var body = JsonNode.Parse(post.Body)!.AsObject();
        Assert.Equal("inv-1", body["invocationId"]!.GetValue<string>());
        Assert.Equal("input-monitor.stats", body["commandId"]!.GetValue<string>());
        Assert.Equal("all", body["args"]!["category"]!.GetValue<string>());

        var payload = ModuleHarness.Payload(result);
        Assert.Equal("running", payload["state"]!.GetValue<string>());
        Assert.False(payload["terminal"]!.GetValue<bool>());
        Assert.Equal("inv-1", payload["invocationId"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnUnauthorizedCommandIsRefusedAndNeverSubmitted()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(
            gateway,
            ("input-monitor.stats", true, false),
            ("nssm-manager.restart", false, true));

        // Warm the catalog, then prove the refusal sends nothing at all.
        await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvoke,
            InvokeArgs("inv-elevated", "nssm-manager.restart"));

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.CommandNotAllowed, result.Error?.Code);
        Assert.Equal("此电脑未允许需要管理员权限的命令。", result.Error?.Message);
        Assert.Equal(0, gateway.RequestCount);
    }

    [Fact]
    public async Task AnUnknownCommandIsRefusedAndNeverSubmitted()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("input-monitor.stats", true, false));
        await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvoke,
            InvokeArgs("inv-x", "input-monitor.invented"));

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.UnknownCommand, result.Error?.Code);
        Assert.Equal(0, gateway.RequestCount);
    }

    [Fact]
    public async Task ACommandWithoutAnAllowedFlagIsTreatedAsNotAuthorized()
    {
        await using var gateway = GatewayDouble.Start();
        gateway.Catalog = new JsonObject
        {
            ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
            ["tools"] = new JsonArray(),
            ["commands"] = new JsonArray(new JsonObject
            {
                ["commandId"] = "input-monitor.stats",
                ["title"] = "输入监测",
                ["parameters"] = new JsonArray()
            })
        };
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvoke,
            InvokeArgs("inv-y", "input-monitor.stats"));

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.CommandNotAllowed, result.Error?.Code);
        Assert.Equal(0, gateway.RequestCount);
    }

    [Fact]
    public async Task RepeatingAnInvocationIdReadsTheStateInsteadOfSubmittingTwice()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("input-monitor.stats", true, false));
        gateway.Reset();

        await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-2", "input-monitor.stats"));
        gateway.Reset();
        var second = await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-2", "input-monitor.stats"));

        Assert.True(second.Success);
        var request = Assert.Single(gateway.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/mpt-control/v1/invocations/inv-2", request.Path);
        Assert.Equal("running", ModuleHarness.Payload(second)["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFinishedInvocationIsAnsweredFromTheRecordedResultWithoutAnyRequest()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("input-monitor.stats", true, false));
        await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-3", "input-monitor.stats"));

        var terminal = new JsonObject
        {
            ["invocationId"] = "inv-3",
            ["commandId"] = "input-monitor.stats",
            ["state"] = "succeeded",
            ["message"] = "完成",
            ["terminal"] = true,
            ["result"] = new JsonObject
            {
                ["invocationId"] = "inv-3",
                ["state"] = "succeeded",
                ["summary"] = "今天使用电脑 2 小时",
                ["errorCode"] = "",
                ["retryable"] = false
            }
        };
        gateway.Invocations["inv-3"] = terminal;
        await harness.RunAsync(
            MobileToolControlOptions.CommandInvocationStatus,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentInvocationId] = "inv-3"
            });
        gateway.Reset();

        var repeat = await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-3", "input-monitor.stats"));

        Assert.True(repeat.Success);
        var payload = ModuleHarness.Payload(repeat);
        Assert.True(payload["terminal"]!.GetValue<bool>());
        Assert.Equal("succeeded", payload["state"]!.GetValue<string>());
        Assert.Equal("今天使用电脑 2 小时", payload["result"]!["summary"]!.GetValue<string>());
        Assert.Equal(0, gateway.RequestCount);
    }

    [Fact]
    public async Task ReusingAnInvocationIdForAnotherCommandIsRefused()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(
            gateway,
            ("input-monitor.stats", true, false),
            ("input-monitor.snapshot", true, false));
        await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-4", "input-monitor.stats"));

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvoke,
            InvokeArgs("inv-4", "input-monitor.snapshot"));

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.InvocationConflict, result.Error?.Code);
    }

    [Fact]
    public async Task CancelUsesTheCancelEndpointAndReturnsTheComputersAnswer()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("input-monitor.stats", true, false));
        await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-5", "input-monitor.stats"));
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvokeCancel,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentInvocationId] = "inv-5"
            });

        Assert.True(result.Success);
        var request = Assert.Single(gateway.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/mpt-control/v1/invocations/inv-5/cancel", request.Path);

        var payload = ModuleHarness.Payload(result);
        Assert.Equal("cancelled", payload["state"]!.GetValue<string>());
        Assert.True(payload["terminal"]!.GetValue<bool>());
        Assert.True(payload["cancelAccepted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnotherDevicesInvocationIsNotReadableAndNothingIsProbed()
    {
        await using var first = GatewayDouble.Start();
        await using var second = GatewayDouble.Start();
        second.Catalog = CatalogWith(("input-monitor.stats", true, false));
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(first, "grant-a", "电脑 A");
        await harness.ImportAsync(second, "grant-b", "电脑 B");
        await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-shared", "input-monitor.stats"));
        first.Reset();
        second.Reset();

        var status = await harness.RunAsync(
            MobileToolControlOptions.CommandInvocationStatus,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-b",
                [MobileToolControlOptions.ArgumentInvocationId] = "inv-shared"
            });
        var cancel = await harness.RunAsync(
            MobileToolControlOptions.CommandInvokeCancel,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-b",
                [MobileToolControlOptions.ArgumentInvocationId] = "inv-shared"
            });

        Assert.False(status.Success);
        Assert.Equal(MobileToolControlErrorCodes.NotFound, status.Error?.Code);
        Assert.False(cancel.Success);
        Assert.Equal(MobileToolControlErrorCodes.NotFound, cancel.Error?.Code);
        Assert.Equal(0, second.RequestCount);
    }

    [Fact]
    public async Task RemovingADeviceDropsItsRecordedInvocations()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("input-monitor.stats", true, false));
        await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-6", "input-monitor.stats"));
        await harness.RunAsync(
            MobileToolControlOptions.CommandDevicesRemove,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvocationStatus,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentInvocationId] = "inv-6"
            });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.UnknownDevice, result.Error?.Code);
        Assert.Equal(0, gateway.RequestCount);
    }

    [Fact]
    public async Task ACancelRefusedByTheComputerIsReportedAsRefused()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("input-monitor.stats", true, false));
        await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-refused", "input-monitor.stats"));
        gateway.Reset();
        gateway.Responder = request => request.Path.EndsWith("/cancel", StringComparison.Ordinal)
            ? new GatewayResponse(
                200,
                "{\"invocationId\":\"inv-refused\",\"commandId\":\"input-monitor.stats\",\"state\":\"running\"," +
                "\"message\":\"运行时拒绝了取消请求\",\"terminal\":false,\"accepted\":false,\"cancelAccepted\":false}")
            : new GatewayResponse(200, "{}");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvokeCancel,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentInvocationId] = "inv-refused"
            });

        Assert.True(result.Success);
        var payload = ModuleHarness.Payload(result);
        // A 200 does not mean "cancelled": the computer's own accepted flag is preserved.
        Assert.False(payload["cancelAccepted"]!.GetValue<bool>());
        Assert.False(payload["terminal"]!.GetValue<bool>());
        Assert.Equal("running", payload["state"]!.GetValue<string>());
        Assert.Equal("运行时拒绝了取消请求", payload["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnAcceptedCancelSurvivesALaterStatusReadThatOmitsTheFlag()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("input-monitor.stats", true, false));
        await harness.RunAsync(MobileToolControlOptions.CommandInvoke, InvokeArgs("inv-late", "input-monitor.stats"));
        gateway.Responder = request => request.Path.EndsWith("/cancel", StringComparison.Ordinal)
            ? new GatewayResponse(
                200,
                "{\"invocationId\":\"inv-late\",\"commandId\":\"input-monitor.stats\",\"state\":\"cancelling\"," +
                "\"message\":\"已请求取消\",\"terminal\":false,\"accepted\":true,\"cancelAccepted\":true}")
            : new GatewayResponse(
                200,
                "{\"invocationId\":\"inv-late\",\"commandId\":\"input-monitor.stats\",\"state\":\"running\"," +
                "\"message\":\"仍在执行\",\"terminal\":false,\"result\":null}");

        var cancelled = await harness.RunAsync(
            MobileToolControlOptions.CommandInvokeCancel,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentInvocationId] = "inv-late"
            });
        Assert.True(ModuleHarness.Payload(cancelled)["cancelAccepted"]!.GetValue<bool>());

        // A status document has no accepted flag at all; the module must not downgrade the answer.
        var status = await harness.RunAsync(
            MobileToolControlOptions.CommandInvocationStatus,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentInvocationId] = "inv-late"
            });

        var payload = ModuleHarness.Payload(status);
        Assert.True(payload["cancelAccepted"]!.GetValue<bool>());
        Assert.Equal("running", payload["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheAwaitingConfirmationStateIsKeptAsANonTerminalState()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("nssm-manager.restart", true, false));
        gateway.Invocations["inv-wait"] = new JsonObject
        {
            ["invocationId"] = "inv-wait",
            ["commandId"] = "nssm-manager.restart",
            ["state"] = "awaiting-confirmation",
            ["message"] = "此操作需要在电脑上确认。",
            ["terminal"] = false,
            ["result"] = null
        };
        // Only the invocation submit is overridden; the catalog read still answers normally.
        gateway.Responder = request => request.Path.EndsWith("/catalog", StringComparison.Ordinal)
            ? new GatewayResponse(200, gateway.Catalog.ToJsonString())
            : new GatewayResponse(202, gateway.Invocations["inv-wait"]!.ToJsonString());

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvoke,
            InvokeArgs("inv-wait", "nssm-manager.restart"));

        Assert.True(result.Success, result.Error?.Message);
        var payload = ModuleHarness.Payload(result);
        Assert.Equal("awaiting-confirmation", payload["state"]!.GetValue<string>());
        Assert.False(payload["terminal"]!.GetValue<bool>());
        Assert.True(ModuleHarness.Payload(await harness.RunAsync(MobileToolControlOptions.CommandStatus))
            ["activeInvocationId"]!.GetValue<string>() == "inv-wait");
    }

    [Fact]
    public async Task AMissingInvocationIdIsRejected()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await HarnessWithAsync(gateway, ("input-monitor.stats", true, false));

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandInvoke,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentCommandId] = "input-monitor.stats"
            });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.ValidationFailed, result.Error?.Code);
    }
}
