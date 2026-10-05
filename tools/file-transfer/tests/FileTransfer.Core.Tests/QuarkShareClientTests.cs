using System.Net;
using System.Text;
using System.Text.Json;
using FileTransfer.Core.Cloud;

namespace FileTransfer.Tests;

public sealed class QuarkShareClientTests
{
    [Fact]
    public async Task Share_task_keeps_waiting_until_provider_finishes_after_twenty_polls()
    {
        var polls = 0;
        var creates = 0;
        using var handler = new ReplyHandler(request =>
        {
            var result = request.RequestUri!.AbsolutePath.Split('/')[^1] switch
            {
                "sort" => Json(new { list = new[] { new { fid = "file", file_name = "large.bin" } } }),
                "share" => Json(new { task_id = "task-" + ++creates }),
                "task" => Json(new { status = ++polls > 20 ? 2 : 1, share_id = "share" }),
                "password" => Json(new { pwd_id = "public-share" }),
                _ => throw new InvalidOperationException()
            };
            return Task.FromResult(result);
        });
        using var client = new QuarkShareClient(handler);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var result = await client.CreateAsync("test-cookie", "/large.bin", DateTimeOffset.UtcNow.AddDays(6), stop.Token);
        Assert.Equal("public-share", result.ShareId);
        Assert.Equal(21, polls);
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task Receiver_refreshes_the_share_capability_without_any_owner_credential()
    {
        var tokenCalls = 0;
        using var handler = new ReplyHandler(async request =>
        {
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Null(request.Headers.Authorization);
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/token")) { tokenCalls++; return Json(new { stoken = "share-token-" + tokenCalls }); }
            if (path.EndsWith("/detail")) return Json(new { list = new[] { new { fid = "one-file", share_fid_token = "single-file-token" } } });
            if (path.EndsWith("/download"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("one-file", body.RootElement.GetProperty("fids")[0].GetString());
                Assert.Equal("single-file-token", body.RootElement.GetProperty("fids_token")[0].GetString());
                return Json(new[] { new { download_url = "https://cdn.example.test/object" } });
            }
            Assert.Equal("cdn.example.test", request.RequestUri.Host);
            return new(HttpStatusCode.OK) { Content = new StringContent("file body") };
        });
        using var client = new QuarkShareClient(handler);
        for (var i = 0; i < 2; i++)
        {
            using var response = await client.OpenReadAsync(new("share-only", "one-file"), default);
            Assert.Equal("file body", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(2, tokenCalls);
    }

    [Fact]
    public async Task Owner_cookie_is_sent_only_to_owner_requests_even_when_client_is_reused_to_receive()
    {
        using var handler = new ReplyHandler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "drive-pc.quark.cn") Assert.Equal("private-owner-cookie", request.Headers.GetValues("Cookie").Single());
            else Assert.False(request.Headers.Contains("Cookie"));
            if (path.EndsWith("/share"))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal(3, body.RootElement.GetProperty("expired_type").GetInt32());
                Assert.False(body.RootElement.TryGetProperty("expired_at", out _));
            }
            var response = path.Split('/')[^1] switch
            {
                "sort" => Json(new { list = new[] { new { fid = "one-file", file_name = "probe.txt" } } }),
                "share" => Json(new { task_id = "task" }),
                "task" => Json(new { status = 2, share_id = "owner-share-id" }),
                "password" => Json(new { pwd_id = "public-share-id" }),
                "token" => Json(new { stoken = "share-token" }),
                "detail" => Json(new { list = new[] { new { fid = "one-file", share_fid_token = "single-file-token" } } }),
                "download" => Json(new[] { new { download_url = "https://cdn.example.test/object" } }),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("file body") }
            };
            return response;
        });
        using var client = new QuarkShareClient(handler);
        var share = await client.CreateAsync("private-owner-cookie", "/probe.txt", DateTimeOffset.UtcNow.AddDays(6), default);
        Assert.Equal(new QuarkShareDescriptor("public-share-id", "one-file"), share);
        using var received = await client.OpenReadAsync(share, default);
        Assert.Equal("file body", await received.Content.ReadAsStringAsync());
        Assert.DoesNotContain("private-owner-cookie", JsonSerializer.Serialize(share));
    }

    [Fact]
    public async Task Revoked_share_does_not_report_provider_body_or_credentials()
    {
        using var handler = new ReplyHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{\"status\":400,\"code\":41001,\"message\":\"private-owner-cookie\"}") }));
        using var client = new QuarkShareClient(handler);
        var error = await Assert.ThrowsAsync<IOException>(() => client.OpenReadAsync(new("revoked", "file"), default));
        Assert.Contains("41001", error.Message);
        Assert.DoesNotContain("private-owner-cookie", error.Message);
    }

    private static HttpResponseMessage Json(object data) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { status = 200, code = 0, data }), Encoding.UTF8, "application/json") };
    private sealed class ReplyHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => reply(request);
    }
}
