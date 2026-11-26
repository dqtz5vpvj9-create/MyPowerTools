using System.Text.Json.Nodes;
using MobileToolControl.Android;

namespace MobileToolControl.Android.Tests;

/// <summary>
/// Wire behaviour against a real HTTP gateway double: the exact request shape, the bearer header, the
/// refusal to follow redirects and the mapping of the contract's error envelope.
/// </summary>
public sealed class GatewayWireTests
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
                ["title"] = "查看使用统计",
                ["subtitle"] = "只读",
                ["dangerLevel"] = "",
                ["requiresElevation"] = elevation,
                ["supportsProgress"] = false,
                ["supportsCancellation"] = false,
                ["allowed"] = allowed,
                ["notAllowedReason"] = allowed ? "" : "此电脑未授权该命令。",
                ["parameters"] = new JsonArray(
                    new JsonObject
                    {
                        ["id"] = "category",
                        ["label"] = "分类",
                        ["type"] = "string",
                        ["required"] = false,
                        ["defaultValue"] = "all"
                    })
            });
        }

        return new JsonObject
        {
            ["device"] = new JsonObject { ["name"] = "工作电脑", ["platform"] = "windows" },
            ["tools"] = new JsonArray(
                new JsonObject
                {
                    ["toolId"] = "input-monitor",
                    ["moduleId"] = "input-monitor",
                    ["title"] = "输入监测",
                    ["description"] = "查看使用习惯",
                    ["category"] = "Wellbeing",
                    ["state"] = "ready",
                    ["availability"] = "available"
                }),
            ["commands"] = array
        };
    }

    [Fact]
    public async Task CatalogUsesTheContractPathAndBearerHeader()
    {
        await using var gateway = GatewayDouble.Start();
        gateway.Catalog = CatalogWith(("input-monitor.stats", true, false));
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentDeviceId] = "grant-1",
                [MobileToolControlOptions.ArgumentRefresh] = true
            });

        Assert.True(result.Success);
        var request = Assert.Single(gateway.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("/mpt-control/v1/catalog", request.Path);
        Assert.Equal("Bearer " + gateway.Token, request.Authorization);

        var payload = ModuleHarness.Payload(result);
        Assert.Equal("工作电脑", payload["device"]!["name"]!.GetValue<string>());
        Assert.Equal("windows", payload["device"]!["platform"]!.GetValue<string>());
        Assert.Equal("输入监测", payload["tools"]!.AsArray()[0]!["title"]!.GetValue<string>());

        var command = payload["commands"]!.AsArray()[0]!;
        Assert.Equal("input-monitor.stats", command["commandId"]!.GetValue<string>());
        Assert.True(command["allowed"]!.GetValue<bool>());
        Assert.Equal("分类", command["parameters"]!.AsArray()[0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWrongTokenIsReportedAsUnauthorized()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Token = "another-token";
        gateway.Reset();

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.Unauthorized, result.Error?.Code);
    }

    [Fact]
    public async Task ARedirectIsRefusedAndNeverFollowed()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Reset();
        gateway.Responder = _ => new GatewayResponse(
            302,
            "",
            "http://127.0.0.1:1/mpt-control/v1/catalog");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.RedirectRefused, result.Error?.Code);
        // Exactly one request: the redirect target was never contacted.
        Assert.Single(gateway.Requests);
    }

    [Fact]
    public async Task TheServerErrorEnvelopeIsPreserved()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Reset();
        gateway.Responder = _ => new GatewayResponse(
            409,
            "{\"error\":{\"code\":\"command-revoked\",\"message\":\"授权已被撤销\"}}");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.Rejected, result.Error?.Code);
        Assert.Equal("授权已被撤销", result.Error?.Message);
        Assert.Equal("command-revoked", result.Error?.Details?["serverCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task AServerFailureIsMarkedRetryable()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Reset();
        gateway.Responder = _ => new GatewayResponse(503, "{\"error\":{\"code\":\"busy\",\"message\":\"temporarily unavailable\"}}");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.Unavailable, result.Error?.Code);
        Assert.True(result.Error?.Retryable);
    }

    [Theory]
    [InlineData(413, "payload-too-large", "调用内容过大")]
    [InlineData(405, "method-not-allowed", "方法不被接受")]
    [InlineData(429, "busy", "请求过于频繁")]
    public async Task NormalClientRefusalsMapToRejectedAndKeepTheServerReason(
        int status,
        string serverCode,
        string message)
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Reset();
        gateway.Responder = _ => new GatewayResponse(
            status,
            $"{{\"error\":{{\"code\":\"{serverCode}\",\"message\":\"{message}\"}}}}");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.Rejected, result.Error?.Code);
        Assert.Equal(message, result.Error?.Message);
        Assert.Equal(serverCode, result.Error?.Details?["serverCode"]!.GetValue<string>());
        Assert.Equal(status, result.Error?.Details?["status"]!.GetValue<int>());
    }

    [Fact]
    public async Task APayloadTooLargeWithoutAMessageExplainsTheSize()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Reset();
        gateway.OverrideResponse = new GatewayResponse(413, "");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.Rejected, result.Error?.Code);
        Assert.Contains("过大", result.Error?.Message ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonJsonBodyIsAProtocolErrorInsteadOfASilentSuccess()
    {
        await using var gateway = GatewayDouble.Start();
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        await harness.ImportAsync(gateway);
        gateway.Reset();
        gateway.Responder = _ => new GatewayResponse(200, "<html>not the contract</html>", ContentType: "text/html");

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandCatalog,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-1" });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.Protocol, result.Error?.Code);
    }

    [Fact]
    public async Task AnUnreachableComputerIsReportedAsUnreachable()
    {
        await using var harness = await ModuleHarness.CreateAsync(new LoopbackEndpointPolicy());
        // A code the loopback policy accepts but nothing listens on: bind and release a port first.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var code = ModuleHarness.BuildCode($"http://127.0.0.1:{port}", "token", "grant-x", "离线电脑");

        Assert.True((await harness.RunAsync(
            MobileToolControlOptions.CommandImportConfirm,
            new JsonObject
            {
                [MobileToolControlOptions.ArgumentCode] = code,
                [MobileToolControlOptions.ArgumentAccepted] = true
            })).Success);

        var result = await harness.RunAsync(
            MobileToolControlOptions.CommandDevicesCheck,
            new JsonObject { [MobileToolControlOptions.ArgumentDeviceId] = "grant-x" });

        Assert.False(result.Success);
        Assert.Equal(MobileToolControlErrorCodes.Unreachable, result.Error?.Code);
        Assert.True(result.Error?.Retryable);

        var status = ModuleHarness.Payload(await harness.RunAsync(MobileToolControlOptions.CommandStatus));
        var device = status["devices"]!.AsArray()[0]!;
        Assert.Equal("unreachable", device["lastState"]!.GetValue<string>());
    }
}
