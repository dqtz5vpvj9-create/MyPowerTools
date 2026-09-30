using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FileTransfer.Core.Cloud;

namespace FileTransfer.Tests;

public sealed class OpenListCloudAccountClientTests
{
    [Fact]
    public async Task PreparesThroughRealHttpAndRemovesOnlyItsProbe()
    {
        var listener = new HttpListener();
        var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        using var stop = new CancellationTokenSource();
        var calls = new List<string>(); byte[]? payload = null; string? probe = null;
        var server = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().WaitAsync(stop.Token); }
                catch (OperationCanceledException) { break; }
                var path = ctx.Request.Url!.AbsolutePath;
                calls.Add(ctx.Request.HttpMethod + " " + path);
                object? data = null;
                if (path == "/api/auth/login") data = new { token = "admin-test" };
                else if (path == "/api/admin/storage/create")
                {
                    using var body = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                    Assert.Equal("Quark", body.RootElement.GetProperty("driver").GetString());
                    Assert.Contains("test-cookie", body.RootElement.GetProperty("addition").GetString());
                    data = new { id = 8 };
                }
                else if (path == "/api/fs/list") data = new { content = Array.Empty<object>() };
                else if (path == "/api/fs/put")
                {
                    probe = Uri.UnescapeDataString(ctx.Request.Headers["File-Path"]!);
                    Assert.StartsWith("/mount/MPT/.mpt-check-", probe);
                    using var bytes = new MemoryStream(); await ctx.Request.InputStream.CopyToAsync(bytes); payload = bytes.ToArray();
                }
                else if (path == "/api/fs/get") data = new { raw_url = $"http://127.0.0.1:{port}/p" + probe };
                else if (path.StartsWith("/p/", StringComparison.Ordinal))
                {
                    Assert.Equal("/p" + probe, path); await ctx.Response.OutputStream.WriteAsync(payload!);
                    ctx.Response.Close(); continue;
                }
                else if (path == "/api/fs/remove")
                {
                    using var body = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                    Assert.Equal("/mount/MPT", body.RootElement.GetProperty("dir").GetString());
                    Assert.Equal(probe!.Split('/').Last(), body.RootElement.GetProperty("names")[0].GetString());
                }
                else Assert.Contains(path, new[] { "/api/fs/mkdir", "/api/admin/storage/delete" });
                var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { code = 200, data }));
                await ctx.Response.OutputStream.WriteAsync(reply); ctx.Response.Close();
            }
        });
        try
        {
            using var api = new OpenListCloudAccountClient(new Uri($"http://127.0.0.1:{port}/"), "test-password");
            await api.LoginAsync(default);
            Assert.Equal(8, await api.MountAsync("quark", "test-cookie", "/mount", default));
            await api.PrepareAsync("/mount", "/mount/MPT", default);
            await api.UnmountAsync(8, default);
            Assert.Contains("POST /api/fs/mkdir", calls);
            Assert.Single(calls.Where(c => c == "POST /api/fs/remove"));
        }
        finally { stop.Cancel(); await server; listener.Close(); }
    }

    [Fact]
    public void AdminApiRefusesNonlocalOrigin() =>
        Assert.Throws<ArgumentException>(() => new OpenListCloudAccountClient(new Uri("https://example.com"), "secret"));
}
