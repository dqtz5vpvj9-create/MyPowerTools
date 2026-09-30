using System.Net;
using System.Text;
using System.Text.Json;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using FileTransfer.Core.Cloud;

namespace FileTransfer.Tests;

/// <summary>
/// Real loopback HTTP faults at the OpenList API and assistant payload boundaries. These tests use
/// synthetic provider responses; they do not authorize a real account or start the embedded runtime.
/// </summary>
public sealed class CloudFailureTests : IAsyncDisposable
{
    private const string Conversation = "cloud-failure-test";
    private const string PrivateError = "cookie=DO_NOT_EXPOSE https://private.example/download?token=secret";
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-cloud-failure-" + Guid.NewGuid().ToString("N"));
    private readonly List<AssistantWebDavServer> _servers = [];

    private AssistantWebDavServer Server()
    {
        var server = new AssistantWebDavServer(Path.Combine(_root, "http-" + _servers.Count));
        _servers.Add(server);
        return server;
    }

    private static void Reply(HttpListenerContext context, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentLength64 = bytes.Length;
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
    }

    [Theory]
    [InlineData("quark", 401, 200)]
    [InlineData("baidu", 401, 200)]
    [InlineData("quark", 507, 200)]
    [InlineData("baidu", 507, 200)]
    [InlineData("quark", 200, 403)]
    [InlineData("baidu", 200, 403)]
    [InlineData("quark", 200, 507)]
    [InlineData("baidu", 200, 507)]
    public async Task Upload_auth_and_quota_failures_keep_the_durable_batch_for_retry(string provider, int httpStatus, int apiCode)
    {
        var server = Server();
        var reject = true;
        string? driver = null;
        server.Intercept = (context, request) =>
        {
            if (!request.Path.StartsWith("/api/", StringComparison.Ordinal)) return false;
            if (request.Path == "/api/auth/login") Reply(context, 200, new { code = 200, data = new { token = "test-admin" } });
            else if (request.Path == "/api/admin/storage/create")
            {
                using var body = JsonDocument.Parse(context.Request.InputStream);
                driver = body.RootElement.GetProperty("driver").GetString();
                Reply(context, 200, new { code = 200, data = new { id = 8 } });
            }
            else if (request.Path == "/api/fs/put")
            {
                context.Request.InputStream.CopyTo(Stream.Null);
                Reply(context, reject ? httpStatus : 200, new { code = reject ? apiCode : 200, message = PrivateError });
            }
            else Reply(context, 404, new { code = 404 });
            return true;
        };
        using var api = new OpenListCloudAccountClient(new Uri(server.Url), "test-password");
        await api.LoginAsync(default);
        await api.MountAsync(provider, "synthetic-provider-credential", "/mount", default);
        Assert.Equal(provider == "quark" ? "Quark" : "BaiduNetdisk", driver);

        var identity = new AssistantIdentity("sender", "Sender", Conversation);
        var storePath = Path.Combine(_root, "sender");
        var store = new AssistantStore(storePath);
        var paths = new[] { Path.Combine(_root, "first.txt"), Path.Combine(_root, "second.txt") };
        foreach (var path in paths) await File.WriteAllTextAsync(path, "synthetic cloud payload " + Path.GetFileName(path));
        var accepted = await store.EnqueueAsync(identity, AssistantDraft.ForPaths(paths), default);
        var ids = accepted.Select(item => item.Id).Order().ToArray();
        using var relay = new OpenListClient(server.Url, AssistantWebDavServer.UserName, AssistantWebDavServer.Password);
        var sync = new AssistantSync(store, relay)
        {
            CloudPublisher = async (message, payload, token) =>
            {
                await api.UploadAsync("/mount", "/mount/" + message.Id, File.OpenRead(payload!), message.Size, token);
                return true;
            }
        };
        var failed = await sync.SyncAsync(identity, default);
        Assert.Equal(1, failed.Failed);
        var persisted = await new AssistantStore(storePath).LoadAsync(default);
        Assert.Equal(ids, persisted.Items.Select(item => item.Id).Order());
        var rejected = Assert.Single(persisted.Items.Where(item => item.State == AssistantItemState.Failed));
        Assert.Contains("网盘", rejected.Error);
        Assert.DoesNotContain("DO_NOT_EXPOSE", rejected.Error);
        Assert.DoesNotContain("private.example", rejected.Error);
        Assert.All(persisted.Items, item => Assert.True(File.Exists(store.GetPayloadPath(item))));
        Assert.Empty(server.Requests.Where(request => request.Method == "PUT" && request.Path.EndsWith("/payload", StringComparison.Ordinal)));

        reject = false;
        Assert.Equal(2, (await sync.SyncAsync(identity, default)).Published);
        persisted = await new AssistantStore(storePath).LoadAsync(default);
        Assert.Equal(ids, persisted.Items.Select(item => item.Id).Order());
        Assert.All(persisted.Items, item => { Assert.Equal(AssistantItemState.Stored, item.State); Assert.Null(item.Error); });
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Rejected_cloud_payload_keeps_one_incoming_record_and_retry_saves_that_same_item(int status)
    {
        var server = Server();
        const string content = "synthetic incoming cloud attachment";
        var source = Path.Combine(_root, "incoming.txt");
        await File.WriteAllTextAsync(source, content);
        var message = new AssistantManifest { Id = Guid.NewGuid().ToString("N"), Kind = AssistantItemKind.File,
            Name = "incoming.txt", Size = Encoding.UTF8.GetByteCount(content), CreatedAt = DateTimeOffset.UtcNow,
            SenderDeviceId = "sender", SenderName = "Sender" };
        using var relay = new OpenListClient(server.Url, AssistantWebDavServer.UserName, AssistantWebDavServer.Password);
        await relay.PublishAssistantAsync(Conversation, message, source, null, default);
        var reject = true;
        server.Intercept = (context, request) =>
        {
            if (request.Path.Contains("/cloud-locator/", StringComparison.Ordinal))
            { Reply(context, 200, CloudAttachmentOffer.Create(Conversation, message)); return true; }
            if (request.Path.EndsWith("/payload", StringComparison.Ordinal) && request.Method == "PROPFIND")
            { AssistantWebDavServer.Write(context, 404); return true; }
            if (reject && request.Path.EndsWith("/payload", StringComparison.Ordinal) && request.Method == "GET")
            { Reply(context, status, new { error = PrivateError }); return true; }
            return false;
        };
        var identity = new AssistantIdentity("receiver", "Receiver", Conversation);
        var storePath = Path.Combine(_root, "receiver");
        var store = new AssistantStore(storePath);
        using var cloud = new CloudRelayClient(Conversation, new string('a', 64), new Uri(server.Url));
        var sync = new AssistantSync(store, relay) { IncomingTransportRoute = cloud.ReadPayloadRouteAsync };
        Assert.Equal(1, (await sync.SyncAsync(identity, default)).Failed);
        var failed = Assert.Single((await new AssistantStore(storePath).LoadAsync(default)).Items);
        Assert.Equal(message.Id, failed.Id);
        Assert.Equal(AssistantItemState.Failed, failed.State);
        Assert.Equal("sender", failed.SenderDeviceId);
        Assert.Equal("cloud", failed.TransportRoute);
        Assert.Null(failed.ReceiptAt);
        Assert.False(File.Exists(failed.LocalPath));
        Assert.DoesNotContain("DO_NOT_EXPOSE", failed.Error);
        Assert.DoesNotContain("private.example", failed.Error);

        reject = false;
        Assert.Equal(1, (await sync.SyncAsync(identity, default)).Downloaded);
        var received = Assert.Single((await new AssistantStore(storePath).LoadAsync(default)).Items);
        Assert.Equal(message.Id, received.Id);
        Assert.Equal(AssistantItemState.Available, received.State);
        Assert.Equal(content, await File.ReadAllTextAsync(received.LocalPath!));
        Assert.NotNull(received.ReceiptAt);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(410)]
    public async Task Expired_provider_link_is_refreshed_when_read_is_retried(int status)
    {
        var server = Server();
        var read = 0;
        server.Intercept = (context, request) =>
        {
            if (request.Path == "/api/fs/get")
                Reply(context, 200, new { code = 200, data = new { raw_url = $"http://127.0.0.1:{server.Port}/p/object-{++read}" } });
            else if (request.Path == "/p/object-1") Reply(context, status, new { error = PrivateError });
            else if (request.Path == "/p/object-2") AssistantWebDavServer.Write(context, 200, Encoding.UTF8.GetBytes("fresh body"));
            else return false;
            return true;
        };
        using var api = new OpenListCloudAccountClient(new Uri(server.Url), "test-password");
        var error = await Assert.ThrowsAsync<IOException>(() => api.OpenReadAsync("/mount", "/mount/object", default));
        Assert.Contains(status.ToString(), error.Message);
        Assert.DoesNotContain("DO_NOT_EXPOSE", error.Message);
        using var response = await api.OpenReadAsync("/mount", "/mount/object", default);
        Assert.Equal("fresh body", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, server.Count("POST", "/api/fs/get"));
        Assert.Equal(1, server.Count("GET", "/p/object-1"));
        Assert.Equal(1, server.Count("GET", "/p/object-2"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers) await server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
