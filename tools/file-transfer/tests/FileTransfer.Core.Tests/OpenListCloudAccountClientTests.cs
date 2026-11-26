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

    [Fact]
    public async Task Healthy_upload_is_not_limited_by_the_administration_timeout()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-upload-budget-" + Guid.NewGuid().ToString("N"));
        await using var server = new AssistantWebDavServer(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reply = Task.CompletedTask;
        server.Intercept = (context, request) =>
        {
            if (request.Path != "/api/fs/put") return false;
            context.Request.InputStream.CopyTo(Stream.Null);
            entered.TrySetResult();
            reply = Task.Run(async () =>
            {
                await release.Task;
                AssistantWebDavServer.Write(context, 200, Encoding.UTF8.GetBytes("{\"code\":200}"));
            });
            return true;
        };
        using var api = new OpenListCloudAccountClient(new Uri(server.Url), "test-password", TimeSpan.FromMilliseconds(50));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var upload = api.UploadAsync("/mount", "/mount/large.bin", new MemoryStream(new byte[1024]), 1024, stop.Token);
        try
        {
            await entered.Task.WaitAsync(stop.Token);
            await Task.Delay(150, stop.Token); // Provider upload is still progressing after the local request body was read.
            Assert.False(upload.IsCompleted);
        }
        finally { release.TrySetResult(); await reply; Directory.Delete(root, true); }
        await upload;
    }

    [Fact]
    public async Task Upload_waiting_on_the_provider_can_still_be_cancelled()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-upload-cancel-" + Guid.NewGuid().ToString("N"));
        await using var server = new AssistantWebDavServer(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reply = Task.CompletedTask;
        server.Intercept = (context, request) =>
        {
            if (request.Path != "/api/fs/put") return false;
            context.Request.InputStream.CopyTo(Stream.Null);
            entered.TrySetResult();
            reply = Task.Run(async () => { await release.Task; context.Response.Close(); });
            return true;
        };
        using var api = new OpenListCloudAccountClient(new Uri(server.Url), "test-password");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var upload = api.UploadAsync("/mount", "/mount/large.bin", new MemoryStream(new byte[1]), 1, stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upload);
        }
        finally { release.TrySetResult(); await reply; Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Administration_timeout_also_covers_a_stalled_response_body()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-admin-budget-" + Guid.NewGuid().ToString("N"));
        await using var server = new AssistantWebDavServer(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Intercept = (context, request) =>
        {
            if (request.Path != "/api/auth/login") return false;
            context.Request.InputStream.CopyTo(Stream.Null);
            context.Response.ContentLength64 = 2;
            context.Response.OutputStream.WriteByte((byte)'{');
            context.Response.OutputStream.Flush();
            entered.TrySetResult();
            return true;
        };
        using var api = new OpenListCloudAccountClient(new Uri(server.Url), "test-password", TimeSpan.FromMilliseconds(200));
        try
        {
            var login = api.LoginAsync(default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { Directory.Delete(root, true); }
    }
}
