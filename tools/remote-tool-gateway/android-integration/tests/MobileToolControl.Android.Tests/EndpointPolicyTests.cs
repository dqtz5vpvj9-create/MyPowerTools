using System.Net;
using MobileToolControl.Android;

namespace MobileToolControl.Android.Tests;

/// <summary>
/// The address boundary is the security property the contract names first: only a literal Tailnet IP
/// may be contacted, no host name, no LAN address, no public address, no other scheme.
/// </summary>
public sealed class EndpointPolicyTests
{
    private readonly TailnetEndpointPolicy _policy = TailnetEndpointPolicy.Instance;

    [Theory]
    [InlineData("100.64.0.2", true)]
    [InlineData("100.64.0.0", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.0", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fd7a:115c:a1e0:1234::9", true)]
    [InlineData("fd7a:115c:a1e1::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::1", false)]
    public void TailnetPolicyAcceptsOnlyTheTailscaleRanges(string address, bool expected)
    {
        Assert.Equal(expected, _policy.Allows(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("http://100.64.0.2:49541", true)]
    [InlineData("http://100.64.0.2:49541/", true)]
    [InlineData("http://[fd7a:115c:a1e0::1]:49541", true)]
    [InlineData("https://100.64.0.2:49541", false)] // only plain HTTP; the tunnel is the encryption
    [InlineData("http://work-pc:49541", false)] // a host name could resolve anywhere
    [InlineData("http://work-pc.tailnet.ts.net:49541", false)]
    [InlineData("http://100.64.0.2", false)] // no implicit port 80
    [InlineData("http://100.64.0.2:0", false)]
    [InlineData("http://100.64.0.2:70000", false)]
    [InlineData("http://100.64.0.2:49541/mpt-control/v1", false)] // no path in the endpoint
    [InlineData("http://user@100.64.0.2:49541", false)]
    [InlineData("http://100.64.0.2:49541?x=1", false)]
    [InlineData("http://192.168.1.10:49541", false)]
    [InlineData("http://8.8.8.8:49541", false)]
    [InlineData("ftp://100.64.0.2:49541", false)]
    public void EndpointParserAcceptsOnlyLiteralTailnetHttpOrigins(string endpoint, bool expected)
    {
        var ok = MobileToolEndpointParser.TryParse(endpoint, _policy, out var parsed, out var error);
        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.Equal("", error);
            Assert.Equal(49541, parsed.Port);
        }
        else
        {
            Assert.NotEqual("", error);
        }
    }

    [Theory]
    [InlineData("mpt://control/not-base64!!!")]
    [InlineData("https://control/abcd")]
    [InlineData("mpt://other/abcd")]
    [InlineData("mpt://control/")]
    public void MalformedConnectionCodesAreRejected(string code)
    {
        Assert.False(MobileToolConnectionCodeParser.TryParse(code, _policy, out _, out var error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public void ConnectionCodeWithNonTailnetEndpointIsRejected()
    {
        var code = ModuleHarness.BuildCode("http://192.168.1.20:49541", "token", "grant-1", "家里的电脑");
        Assert.False(MobileToolConnectionCodeParser.TryParse(code, _policy, out _, out var error));
        Assert.Contains("Tailnet", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionCodeWithHostNameEndpointIsRejected()
    {
        var code = ModuleHarness.BuildCode("http://desktop.local:49541", "token", "grant-1", "电脑");
        Assert.False(MobileToolConnectionCodeParser.TryParse(code, _policy, out _, out var error));
        Assert.Contains("主机名", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionCodeWithoutTokenIsRejected()
    {
        var code = ModuleHarness.BuildCode("http://100.64.0.2:49541", "", "grant-1", "电脑");
        Assert.False(MobileToolConnectionCodeParser.TryParse(code, _policy, out _, out var error));
        Assert.Contains("凭据", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectionCodeWithUnsupportedVersionIsRejected()
    {
        var json = "{\"version\":2,\"endpoint\":\"http://100.64.0.2:49541\",\"grantId\":\"g\",\"deviceName\":\"电脑\",\"token\":\"t\"}";
        var code = "mpt://control/" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(MobileToolConnectionCodeParser.TryParse(code, _policy, out _, out var error));
        Assert.Contains("版本", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsedConnectionCodeNeverPrintsItsToken()
    {
        var code = ModuleHarness.BuildCode("http://100.64.0.2:49541", "super-secret-token", "grant-1", "工作电脑");
        Assert.True(MobileToolConnectionCodeParser.TryParse(code, _policy, out var parsed, out _));
        Assert.DoesNotContain("super-secret-token", parsed.ToString(), StringComparison.Ordinal);
        Assert.Contains(MobileToolControlOptions.RedactedToken, parsed.ToString(), StringComparison.Ordinal);
    }
}
