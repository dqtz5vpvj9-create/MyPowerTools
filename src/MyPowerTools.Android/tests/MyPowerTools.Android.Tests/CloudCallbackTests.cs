using System.Text;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.Android.Tests;

public class CloudCallbackTests
{
    private static string Payload(string driver = "baiduyun_go") => Convert.ToBase64String(
        Encoding.UTF8.GetBytes("{\"driver_txt\":\"" + driver + "\",\"refresh_token\":\"test-only-credential\"}"));

    [Fact]
    public void Expected_callback_returns_credential_without_rendering_it() =>
        Assert.Equal("test-only-credential", CloudAuthorizationCallback.BaiduRefreshToken("https://api.oplist.org/#" + Payload()));

    [Theory]
    [InlineData("http://api.oplist.org/")]
    [InlineData("https://api.oplist.org.evil.example/")]
    [InlineData("https://api.oplist.org:444/")]
    [InlineData("https://user@api.oplist.org/")]
    [InlineData("https://api.oplist.org/other")]
    public void Other_origins_or_routes_cannot_supply_credentials(string origin) =>
        Assert.Null(CloudAuthorizationCallback.BaiduRefreshToken(origin + "#" + Payload()));

    [Fact]
    public void Another_provider_callback_cannot_connect_a_baidu_account() =>
        Assert.Null(CloudAuthorizationCallback.BaiduRefreshToken("https://api.oplist.org/#" + Payload("quarkyun_go")));

    [Theory]
    [InlineData("not-base64")]
    [InlineData("e30=")]
    [InlineData("bnVsbA==")]
    public void Invalid_provider_response_is_rejected_without_echoing_credentials(string payload) =>
        Assert.Null(CloudAuthorizationCallback.BaiduRefreshToken("https://api.oplist.org/#" + payload));
}
