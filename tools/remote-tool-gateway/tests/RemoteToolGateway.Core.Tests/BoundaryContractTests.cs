using System.Net;
using System.Text.Json.Nodes;
using RemoteToolGateway.Core;

namespace RemoteToolGateway.Core.Tests;

/// <summary>
/// Boundary rules that do not need a listener: the connection-code format, the Tailnet address
/// policy and the elevation/confirmation classification.
/// </summary>
public sealed class BoundaryContractTests
{
    [Fact]
    public void ConnectionCode_RoundTrips_AndKeepsTheTokenOutOfThePreview()
    {
        var token = Base64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var connection = new ControlConnection(1, "http://100.64.0.2:49541", "g-0123456789abcdef", "工作电脑", token);
        var code = ControlConnectionCode.Encode(connection);

        Assert.StartsWith("mpt://control/", code);
        Assert.DoesNotContain("+", code);
        Assert.DoesNotContain("/", code[(ControlConnectionCode.Scheme.Length)..]);
        Assert.True(ControlConnectionCode.TryDecode(code, out var decoded, out var error), error);
        Assert.Equal(connection.Endpoint, decoded.Endpoint);
        Assert.Equal(connection.GrantId, decoded.GrantId);
        Assert.Equal(connection.DeviceName, decoded.DeviceName);
        Assert.Equal(token, decoded.Token);

        var preview = ControlConnectionCode.Describe(code);
        Assert.NotNull(preview);
        Assert.Contains("工作电脑", preview);
        Assert.DoesNotContain(token, preview);
    }

    [Theory]
    [InlineData("mpt://pair/AAAA", "文件互传")]
    [InlineData("mpt://cloud/AAAA", "文件互传")]
    [InlineData("https://100.64.0.2:49541/", "mpt://control/")]
    [InlineData("mpt://control/not-base64!!", "解析")]
    public void ConnectionCode_RejectsForeignAndMalformedCodes(string code, string expectedFragment)
    {
        Assert.False(ControlConnectionCode.TryDecode(code, out _, out var error));
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void ConnectionCode_RejectsNonTailnetAndRedirectingEndpoints()
    {
        var token = Base64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        foreach (var endpoint in new[]
                 {
                     "http://192.168.1.10:49541", "http://10.0.0.5:49541", "http://8.8.8.8:49541",
                     "https://100.64.0.2:49541", "http://example.com:49541", "http://0.0.0.0:49541"
                 })
        {
            var code = ControlConnectionCode.Encode(new ControlConnection(1, endpoint, "g-0123456789abcdef", "电脑", token));
            Assert.False(ControlConnectionCode.TryDecode(code, out _, out var error));
            Assert.Contains("Tailscale", error);
        }
    }

    [Fact]
    public void ConnectionCode_RejectsWrongVersionAndShortToken()
    {
        var code = ControlConnectionCode.Encode(new ControlConnection(2, "http://100.64.0.2:49541", "g-0123456789abcdef", "电脑",
            Base64Url.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))));
        Assert.False(ControlConnectionCode.TryDecode(code, out _, out var versionError));
        Assert.Contains("版本", versionError);

        var shortToken = ControlConnectionCode.Encode(new ControlConnection(1, "http://100.64.0.2:49541", "g-0123456789abcdef", "电脑", "short"));
        Assert.False(ControlConnectionCode.TryDecode(shortToken, out _, out var tokenError));
        Assert.Contains("凭据", tokenError);
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("fd7a:115c:a1e0::5", true)]
    [InlineData("fd00::1", false)]
    public void Tailnet_Classification_MatchesTheTailscaleRanges(string address, bool expected)
    {
        Assert.Equal(expected, TailnetBinding.IsTailnet(IPAddress.Parse(address)));
    }

    [Fact]
    public void PeerGate_AllowsTailnetAlways_AndLoopbackOnlyForInjectedTransports()
    {
        Assert.True(TailnetBinding.IsPeerAllowed(IPAddress.Parse("100.64.0.9"), allowLoopbackPeers: false));
        Assert.False(TailnetBinding.IsPeerAllowed(IPAddress.Loopback, allowLoopbackPeers: false));
        Assert.True(TailnetBinding.IsPeerAllowed(IPAddress.Loopback, allowLoopbackPeers: true));
        Assert.False(TailnetBinding.IsPeerAllowed(IPAddress.Parse("192.168.1.4"), allowLoopbackPeers: true));
        Assert.False(TailnetBinding.IsPeerAllowed(IPAddress.Parse("8.8.8.8"), allowLoopbackPeers: true));
    }

    [Fact]
    public void TailnetBindAddress_RejectsWildcardAndHostNames()
    {
        foreach (var value in new[] { "", "0.0.0.0", "::", "*", "+", "any", "my-laptop", "100.64.0.2.example.com", "100.200.1.1" })
        {
            Assert.False(TailnetBinding.TryParseTailnet(value, out _));
        }

        Assert.True(TailnetBinding.TryParseTailnet("100.64.0.2", out var address));
        Assert.Equal("100.64.0.2", address.ToString());
    }

    [Fact]
    public void CommandPolicy_ClassifiesElevationAndConfirmation()
    {
        var plain = new HostCommandDescriptor("demo.status", "demo", "状态", "", "", false, false, true, [], [], null);
        Assert.False(CommandPolicy.IsElevated(plain));
        Assert.Equal(ConfirmationRequirement.None, CommandPolicy.Evaluate(plain).Requirement);

        var elevated = plain with { RequiresElevation = true };
        Assert.True(CommandPolicy.IsElevated(elevated));
        Assert.Equal(ConfirmationRequirement.Desktop, CommandPolicy.Evaluate(elevated).Requirement);

        var constrained = plain with { Constraints = ["requiresElevatedWrites"] };
        Assert.True(CommandPolicy.IsElevated(constrained));

        var broker = plain with { Execution = new JsonObject { ["type"] = "broker.request", ["actionId"] = "svc.start" } };
        Assert.True(CommandPolicy.IsElevated(broker));
        Assert.Equal("broker", CommandPolicy.Evaluate(broker).Kind);

        var dangerous = plain with { DangerLevel = "danger" };
        Assert.Equal(ConfirmationRequirement.Desktop, CommandPolicy.Evaluate(dangerous).Requirement);

        var unsupported = plain with { Execution = new JsonObject { ["approval"] = "uac-prompt" } };
        Assert.Equal(ConfirmationRequirement.Unsupported, CommandPolicy.Evaluate(unsupported).Requirement);
        Assert.Contains("uac-prompt", CommandPolicy.Evaluate(unsupported).Kind);
    }

    [Theory]
    [InlineData("desktop", "desktop")]
    [InlineData("danger", "danger")]
    [InlineData("elevated", "elevated")]
    [InlineData("broker", "broker")]
    public void CommandPolicy_HonoursEverySupportedApprovalKind(string approval, string expectedKind)
    {
        var command = new HostCommandDescriptor("demo.op", "demo", "操作", "", "", false, false, true, [], [],
            new JsonObject { ["approval"] = approval });
        var decision = CommandPolicy.Evaluate(command);
        Assert.Equal(ConfirmationRequirement.Desktop, decision.Requirement);
        Assert.Equal(expectedKind, decision.Kind);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Fact]
    public void CommandPolicy_TreatsAnExplicitElevatedApprovalAsElevated()
    {
        var explicitApproval = new HostCommandDescriptor("demo.op", "demo", "操作", "", "", false, false, true, [], [],
            new JsonObject { ["approval"] = "elevated" });
        Assert.True(CommandPolicy.IsElevated(explicitApproval));

        // desktop/danger approvals are confirmations, not elevation requirements.
        var desktop = explicitApproval with { Execution = new JsonObject { ["approval"] = "desktop" } };
        Assert.False(CommandPolicy.IsElevated(desktop));
        var dangerous = explicitApproval with { Execution = new JsonObject { ["approval"] = "danger" } };
        Assert.False(CommandPolicy.IsElevated(dangerous));
    }

    [Fact]
    public void CommandPolicy_NoneApprovalDoesNotDisableDangerOrElevationChecks()
    {
        var none = new HostCommandDescriptor("demo.op", "demo", "操作", "", "danger", false, false, true, [], [],
            new JsonObject { ["approval"] = "none" });
        Assert.Equal(ConfirmationRequirement.Desktop, CommandPolicy.Evaluate(none).Requirement);
        Assert.True(CommandPolicy.IsSupportedApprovalKind("none"));
        Assert.False(CommandPolicy.IsSupportedApprovalKind("interactive-uac"));
    }

    [Fact]
    public void CommandPolicy_TreatsGatewayCommandsAsManagementOnly()
    {
        Assert.True(CommandPolicy.IsGatewayManagementCommand("remote-tool-gateway.grant.create", "remote-tool-gateway", "remote-tool-gateway"));
        Assert.True(CommandPolicy.IsGatewayManagementCommand("remote-tool-gateway.inspect", "other", "remote-tool-gateway"));
        Assert.False(CommandPolicy.IsGatewayManagementCommand("input-monitor.status", "input-monitor", "remote-tool-gateway"));
    }

    [Fact]
    public void ArgumentSummary_RedactsCredentialShapedValues()
    {
        var args = new JsonObject
        {
            ["commandId"] = "svc.restart",
            ["adminPassword"] = "hunter2",
            ["peer_token"] = "abcdef0123456789",
            ["nested"] = new JsonObject { ["a"] = 1, ["b"] = 2 }
        };
        var summary = ControlRedaction.SummarizeArgs(args);
        Assert.Contains("commandId=svc.restart", summary);
        Assert.Contains("****", summary);
        Assert.DoesNotContain("hunter2", summary);
        Assert.DoesNotContain("abcdef0123456789", summary);
        Assert.Contains("{2 项}", summary);
    }

    [Fact]
    public void ErrorEnvelope_IsJson_WithCodeAndMessage()
    {
        var body = ControlWire.ErrorBody("unauthorized", new string('x', ControlWire.MaxMessageLength * 4));
        var json = JsonNode.Parse(body)!.AsObject();
        var message = json["error"]!["message"]!.GetValue<string>();
        Assert.Equal("unauthorized", json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(ControlWire.MaxMessageLength, message.Length);
    }
}
