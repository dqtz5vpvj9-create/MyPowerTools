using System.Net;
using System.Text;
using System.Text.Json;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;

namespace FileTransfer.Tests;

/// <summary>
/// End-to-end assistant behaviour against a real loopback WebDAV server: publish ordering, credentials,
/// redirect rules, retry/dedupe, two independent device stores, offline queueing and restart recovery.
/// The relay is a real HTTP server on disk, not a mocked client.
/// </summary>
public sealed class AssistantRelayTests : IAsyncDisposable
{
    private const string Conversation = "conv-1";

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-assistant-relay-" + Guid.NewGuid().ToString("N"));

    private readonly List<AssistantWebDavServer> _servers = [];
    private readonly List<OpenListClient> _clients = [];

    public AssistantRelayTests() => System.IO.Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients) client.Dispose();
        foreach (var server in _servers) await server.DisposeAsync();
        try { if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private sealed class Device(AssistantIdentity identity, AssistantStore store, OpenListClient client)
    {
        public AssistantIdentity Identity { get; } = identity;
        public AssistantStore Store { get; } = store;
        public OpenListClient Client { get; } = client;
        public AssistantSync Sync { get; } = new(store, client);
        public async Task<AssistantState> StateAsync(CancellationToken token) => await Store.LoadAsync(token);
        public async Task<IReadOnlyList<AssistantItem>> ItemsAsync(CancellationToken token) => (await Store.LoadAsync(token)).Items;
    }

    private AssistantWebDavServer StartServer(int? port = null)
    {
        var server = new AssistantWebDavServer(Path.Combine(_root, "relay-" + Guid.NewGuid().ToString("N")), port);
        _servers.Add(server);
        return server;
    }

    private OpenListClient Connect(string url)
    {
        var client = new OpenListClient(url, AssistantWebDavServer.UserName, AssistantWebDavServer.Password);
        _clients.Add(client);
        return client;
    }

    private Device NewDevice(string name, string url, string conversation = Conversation)
    {
        var store = new AssistantStore(Path.Combine(_root, "devices", name));
        return new Device(new AssistantIdentity(name, name, conversation), store, Connect(url));
    }

    private static AssistantIdentity Identity(string name, string conversation = Conversation) => new(name, name, conversation);

    [Fact]
    public async Task VisibleTransitionsNotifyAfterCommitButUnchangedReceiptRoundsStaySilent()
    {
        var server = StartServer();
        var sender = NewDevice("event-sender", server.Url);
        var receiver = NewDevice("event-receiver", server.Url);
        var source = SourceFile("event-image.png", new string('x', 128 * 1024));
        var item = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForPaths([source]), CancellationToken.None));
        var sent = new List<AssistantItem>();
        var arrived = new List<AssistantItem>();
        var progress = 0;
        var senderState = await sender.Store.LoadAsync(CancellationToken.None);
        var receiverState = await receiver.Store.ConfigureAsync(receiver.Identity, CancellationToken.None);
        sender.Sync.Changed = isProgress =>
        {
            // A callback may immediately inspect, so a callback under the store lock would deadlock.
            Assert.True(sender.Store.LoadAsync(CancellationToken.None).IsCompletedSuccessfully);
            var row = senderState.Find(item.Id)!;
            if (isProgress) { Assert.True(row.BytesDone > 0); progress++; }
            else sent.Add(row.Copy());
        };
        receiver.Sync.Changed = isProgress =>
        {
            if (!isProgress)
            {
                Assert.True(receiver.Store.LoadAsync(CancellationToken.None).IsCompletedSuccessfully);
                arrived.Add(receiverState.Find(item.Id)!.Copy());
            }
        };
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        Assert.Contains(sent, row => row.State == AssistantItemState.Sending);
        Assert.Contains(sent, row => row.State == AssistantItemState.Stored && row.BytesDone == row.Size);
        Assert.True(progress > 0);
        await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);
        Assert.Contains(arrived, row => row.State == AssistantItemState.Stored);
        Assert.Contains(arrived, row => row.State == AssistantItemState.Downloading);
        Assert.Contains(arrived, row => row.State == AssistantItemState.Available && File.Exists(row.LocalPath));
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        Assert.Contains(sent, row => row.Receipts.Count == 1);
        sent.Clear();
        arrived.Clear();
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);
        Assert.Empty(sent); // ReceiptCheckedAt and the same receipt do not refresh the UI repeatedly.
        Assert.Empty(arrived);
    }

    private string SourceFile(string name, string content, string folder = "source")
    {
        var path = Path.Combine(_root, folder, name);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static AssistantManifest Manifest(string id, string name, long size, string sender = "desktop-1") => new()
    {
        Version = 1,
        Id = id,
        Kind = AssistantItemKind.File,
        Name = name,
        Size = size,
        CreatedAt = DateTimeOffset.UtcNow,
        SenderDeviceId = sender,
        SenderName = sender
    };

    /// <summary>Publishes one file entry straight through the client, for tests that need existing relay content.</summary>
    private async Task<AssistantManifest> PublishAsync(AssistantWebDavServer server, string name, string content, string sender = "desktop-1")
    {
        var path = Path.Combine(_root, "published", Guid.NewGuid().ToString("N"), name);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        var manifest = Manifest(Guid.NewGuid().ToString("N"), name, Encoding.UTF8.GetByteCount(content), sender);
        var client = Connect(server.Url);
        return await client.PublishAssistantAsync(Conversation, manifest, path, null, CancellationToken.None);
    }

    [Fact]
    public async Task PublishListAndDownloadRoundTripOverRealHttp()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        const string content = "合同正文\nsecond line";
        var item = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForPaths([SourceFile("合同 v2.pdf", content)]), CancellationToken.None));

        var result = await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        Assert.Equal(1, result.Published);
        Assert.Equal(0, result.Failed);
        Assert.Equal(AssistantItemState.Stored, item.State);

        // The payload reaches storage before the manifest, so an interrupted publish is not a readable entry.
        var puts = server.Requests.Where(request => request.Method == "PUT").Select(request => request.Path).ToArray();
        Assert.Equal(2, puts.Length);
        Assert.EndsWith("/payload", puts[0]);
        Assert.EndsWith("/manifest.json", puts[1]);
        Assert.True(server.Exists($"assistant/{Conversation}/{item.Id}/payload"));
        Assert.True(server.Exists($"assistant/{Conversation}/{item.Id}/manifest.json"));
        Assert.All(server.Requests.Where(request => request.Path.StartsWith("/dav", StringComparison.Ordinal)),
            request => Assert.True(request.Authorized));

        var receiver = NewDevice("phone-1", server.Url);
        Assert.Equal(1, (await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None)).Received);
        var incoming = Assert.Single(await receiver.ItemsAsync(CancellationToken.None));
        Assert.Equal(item.Id, incoming.Id);
        Assert.Equal(AssistantItemState.Available, incoming.State);
        Assert.Equal(content, await File.ReadAllTextAsync(incoming.LocalPath!));
        Assert.NotNull(incoming.ReceiptAt);

        // The receipt states that this device saved the file; there is no read flag anywhere.
        var receipt = Assert.Single(await receiver.Client.ListAssistantReceiptsAsync(Conversation, item.Id, CancellationToken.None));
        Assert.Equal("phone-1", receipt.DeviceId);
        Assert.Equal(new FileInfo(incoming.LocalPath!).Length, receipt.Bytes);

        // A self send stays "stored" (synced to the relay) and shows the concrete device receipts.
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        var mine = (await sender.StateAsync(CancellationToken.None)).Find(item.Id)!;
        Assert.Equal(AssistantItemState.Stored, mine.State);
        Assert.Equal("phone-1", Assert.Single(mine.Receipts).DeviceId);
    }

    [Fact]
    public async Task InterruptedPayloadUploadNeverBecomesAReadableEntry()
    {
        var server = StartServer();
        server.Intercept = (context, request) =>
        {
            if (request.Method == "PUT" && request.Path.EndsWith("/payload", StringComparison.Ordinal))
            {
                AssistantWebDavServer.Write(context, 500);
                return true;
            }
            return false;
        };
        var sender = NewDevice("desktop-1", server.Url);
        var item = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForPaths([SourceFile("big.bin", "payload")]), CancellationToken.None));

        var result = await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        Assert.Equal(0, result.Published);
        Assert.Equal(1, result.Failed);
        Assert.Equal(AssistantItemState.Failed, item.State);
        Assert.False(string.IsNullOrWhiteSpace(item.Error));
        Assert.False(server.Exists($"assistant/{Conversation}/{item.Id}/payload"));
        Assert.False(server.Exists($"assistant/{Conversation}/{item.Id}/manifest.json"));

        // The peer sees nothing while the publish is incomplete.
        server.Intercept = null;
        var receiver = NewDevice("phone-1", server.Url);
        Assert.Equal(0, (await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None)).Received);

        // Retrying reuses the same id, so the peer ends up with exactly one message.
        Assert.Equal(1, (await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None)).Published);
        Assert.Equal(AssistantItemState.Stored, item.State);
        Assert.Equal(1, (await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None)).Received);
        Assert.Single(await receiver.ItemsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RePublishingTheSameIdAndRepeatedSyncsDoNotDuplicateAnything()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        var item = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForPaths([SourceFile("a.bin", "data")]), CancellationToken.None));
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);

        // A crash between "manifest published" and "state saved" leaves the entry queued with the same id.
        var state = await sender.StateAsync(CancellationToken.None);
        state.Find(item.Id)!.State = AssistantItemState.Queued;
        await sender.Store.SaveAsync(state, CancellationToken.None);

        Assert.Equal(1, (await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None)).Published);
        Assert.Equal(1, server.Count("PUT", "/payload"));
        Assert.Equal(1, server.Count("PUT", "/manifest.json"));
        Assert.Equal(AssistantItemState.Stored, item.State);

        var receiver = NewDevice("phone-1", server.Url);
        Assert.Equal(1, (await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None)).Received);
        var fetches = server.Count("GET", "/manifest.json");
        Assert.Equal(0, (await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None)).Received);
        Assert.Equal(fetches, server.Count("GET", "/manifest.json"));   // remembered ids are never fetched twice
    }

    [Fact]
    public async Task SameAuthorityRedirectIsFollowedWithoutCredentials()
    {
        var server = StartServer();
        const string content = "redirected-body";
        var id = Guid.NewGuid().ToString("N");
        var relative = $"assistant/{Conversation}/{id}/payload";
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(server.DiskPath(relative))!);
        await File.WriteAllTextAsync(server.DiskPath(relative), content);
        server.Intercept = (context, request) =>
        {
            if (request.Method == "GET" && request.Path.EndsWith("/payload", StringComparison.Ordinal))
            {
                AssistantWebDavServer.Write(context, 302, location: $"http://127.0.0.1:{server.Port}/public/copy.bin");
                return true;
            }
            if (request.Path.StartsWith("/public/", StringComparison.Ordinal))
            {
                AssistantWebDavServer.Write(context, 200, Encoding.UTF8.GetBytes(content));
                return true;
            }
            return false;
        };

        var client = Connect(server.Url);
        var saved = await client.DownloadAssistantAsync(Conversation, Manifest(id, "copy.bin", content.Length),
            Path.Combine(_root, "downloads"), null, CancellationToken.None);
        Assert.Equal(content, await File.ReadAllTextAsync(saved));
        Assert.True(server.Requests.Single(request => request.Method == "GET" && request.Path.EndsWith("/payload", StringComparison.Ordinal)).Authorized);
        Assert.False(server.Requests.Single(request => request.Path.StartsWith("/public/", StringComparison.Ordinal)).Authorized);
    }

    [Fact]
    public async Task ForeignPlainHttpRedirectIsRefusedBeforeAnyRequest()
    {
        var server = StartServer();
        var foreign = StartServer();
        const string content = "secret";
        var id = Guid.NewGuid().ToString("N");
        var relative = $"assistant/{Conversation}/{id}/payload";
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(server.DiskPath(relative))!);
        await File.WriteAllTextAsync(server.DiskPath(relative), content);
        server.Intercept = (context, request) =>
        {
            if (request.Method == "GET" && request.Path.EndsWith("/payload", StringComparison.Ordinal))
            {
                AssistantWebDavServer.Write(context, 302, location: $"{foreign.Url}/stolen");
                return true;
            }
            return false;
        };

        var client = Connect(server.Url);
        var error = await Assert.ThrowsAsync<IOException>(() => client.DownloadAssistantAsync(Conversation,
            Manifest(id, "copy.bin", content.Length), Path.Combine(_root, "downloads"), null, CancellationToken.None));
        Assert.Contains("HTTPS", error.Message);
        Assert.Empty(foreign.Requests);
    }

    [Fact]
    public async Task OfflineQueueSurvivesRestartAndPublishesWhenTheRelayReturns()
    {
        var port = AssistantWebDavServer.ReservePort();
        var storeDirectory = Path.Combine(_root, "devices", "desktop-1");
        var identity = Identity("desktop-1");
        var store = new AssistantStore(storeDirectory);
        var source = SourceFile("离线发送.bin", "queued-offline");
        var item = Assert.Single(await store.EnqueueAsync(identity, AssistantDraft.ForPaths([source]), CancellationToken.None));

        // Nothing is listening on that port: the send is queued, never reported as sent.
        var offline = new AssistantSync(store, Connect($"http://127.0.0.1:{port}/dav"));
        var failed = await offline.SyncAsync(identity, CancellationToken.None);
        Assert.Equal(0, failed.Published);
        Assert.Equal(1, failed.Failed);
        Assert.Equal(AssistantItemState.Failed, item.State);
        Assert.False(failed.HasMore);          // nothing can start right now
        Assert.NotNull(failed.RetryAfter);     // the scheduler is told to wait instead of spinning

        // A restart must not require the user to pick the file again.
        var restarted = new AssistantStore(storeDirectory);
        var restored = (await restarted.LoadAsync(CancellationToken.None)).Find(item.Id)!;
        Assert.Equal("queued-offline", await File.ReadAllTextAsync(restarted.GetPayloadPath(restored)!));

        var relay = StartServer(port);
        var online = new AssistantSync(restarted, Connect(relay.Url));
        var recovered = await online.SyncAsync(identity, CancellationToken.None);
        Assert.Equal(1, recovered.Published);
        Assert.Equal(AssistantItemState.Stored, restored.State);
        Assert.Equal(1, relay.Count("PUT", "/payload"));
        Assert.Single((await restarted.LoadAsync(CancellationToken.None)).Items);   // no duplicate message
    }

    [Fact]
    public async Task TextEntriesSyncWithoutDownloadingAnyPayload()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        var receiver = NewDevice("phone-1", server.Url);
        var text = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForText("从电脑发来的文字 🎉"), CancellationToken.None));
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        var payloadGets = server.Count("GET", "/payload");

        Assert.Equal(1, (await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None)).Received);
        var incoming = Assert.Single(await receiver.ItemsAsync(CancellationToken.None));
        Assert.Equal(AssistantItemKind.Text, incoming.Kind);
        Assert.Equal("从电脑发来的文字 🎉", incoming.Text);
        Assert.Equal(AssistantItemState.Available, incoming.State);
        Assert.Null(incoming.LocalPath);
        Assert.False(server.Exists($"assistant/{Conversation}/{text.Id}/payload"));
        Assert.Equal(payloadGets, server.Count("GET", "/payload"));
        Assert.NotNull(incoming.ReceiptAt);
        Assert.Equal(0, Assert.Single(await receiver.Client.ListAssistantReceiptsAsync(Conversation, text.Id, CancellationToken.None)).Bytes);
    }

    [Fact]
    public async Task SameNameAttachmentsStaySeparateOnBothDevices()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        var receiver = NewDevice("phone-1", server.Url);
        var items = await sender.Store.EnqueueAsync(sender.Identity, AssistantDraft.ForPaths([
            SourceFile("报告.pdf", "first-report", "one"),
            SourceFile("报告.pdf", "second-report", "two")
        ]), CancellationToken.None);
        Assert.Equal(2, items.Count);
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);

        var incoming = await receiver.ItemsAsync(CancellationToken.None);
        Assert.Equal(2, incoming.Count);
        Assert.All(incoming, entry => Assert.Equal("报告.pdf", entry.Name));
        Assert.Equal(2, incoming.Select(entry => entry.LocalPath).Distinct().Count());
        Assert.Equal("first-report", await File.ReadAllTextAsync(incoming.Single(entry => entry.Id == items[0].Id).LocalPath!));
        Assert.Equal("second-report", await File.ReadAllTextAsync(incoming.Single(entry => entry.Id == items[1].Id).LocalPath!));
    }

    [Fact]
    public async Task PrivateSendNeverEntersSharedNamespaceEvenWithAPermissivePublishFilter()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        var target = NewDevice("phone-1", server.Url);
        var bystander = NewDevice("tablet-1", server.Url);
        var item = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForPaths([SourceFile("private.bin", "private payload")], "phone-1"), CancellationToken.None));
        sender.Sync.PublishFilter = _ => true;
        var result = await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        Assert.Equal(0, result.Published);
        Assert.Equal(AssistantItemState.Queued, item.State);
        Assert.False(File.Exists(server.DiskPath($"assistant/{Conversation}/{item.Id}/manifest.json")));
        Assert.False(File.Exists(server.DiskPath($"assistant/{Conversation}/{item.Id}/payload")));
        await target.Sync.SyncAsync(target.Identity, CancellationToken.None);
        await bystander.Sync.SyncAsync(bystander.Identity, CancellationToken.None);
        Assert.Empty(await target.ItemsAsync(CancellationToken.None));
        Assert.Empty(await bystander.ItemsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PrivateReceiptsAndOldSharedNamespaceNeverLeakIntoCurrentSharedSync()
    {
        var server = StartServer();
        var device = NewDevice("phone-1", server.Url);
        var old = Assert.Single(await device.Store.EnqueueAsync(device.Identity with { ConversationId = "old-conversation" },
            AssistantDraft.ForText("old namespace"), CancellationToken.None));
        var incoming = new AssistantItem { Id = Guid.NewGuid().ToString("N"), Kind = AssistantItemKind.Text,
            Text = "secret", Size = 6, CreatedAt = DateTimeOffset.UtcNow, SenderDeviceId = "desktop-1", SenderName = "desktop-1",
            TargetDeviceId = "phone-1", Provenance = AssistantConversations.PairedInbox };
        await device.Store.AdoptAsync(device.Identity, incoming, CancellationToken.None);
        var result = await device.Sync.SyncAsync(device.Identity, CancellationToken.None);
        Assert.Equal(0, result.Published);
        Assert.Equal(0, result.ReceiptsWritten);
        Assert.Equal(0, result.ReceiptsChecked);
        Assert.Equal(AssistantItemState.Queued, old.State);
        Assert.DoesNotContain(server.Requests, request => request.Method == "PUT");
    }

    [Fact]
    public async Task CancelledEntriesAreNeverPublished()
    {
        var server = StartServer();
        var device = NewDevice("desktop-1", server.Url);
        var item = Assert.Single(await device.Store.EnqueueAsync(device.Identity,
            AssistantDraft.ForPaths([SourceFile("c.bin", "c")]), CancellationToken.None));
        var state = await device.StateAsync(CancellationToken.None);
        Assert.True(state.TryCancel(item.Id));
        await device.Store.SaveAsync(state, CancellationToken.None);

        var result = await device.Sync.SyncAsync(device.Identity, CancellationToken.None);
        Assert.Equal(0, result.Published);
        Assert.Equal(0, server.Count("PUT", "/manifest.json"));
        Assert.False(System.IO.Directory.Exists(server.DiskPath($"assistant/{Conversation}/{item.Id}")));
    }

    [Fact]
    public async Task EnsureLocalDownloadsAgainAfterTheLocalCopyWasRemoved()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        var receiver = NewDevice("phone-1", server.Url);
        const string content = "re-openable";
        var item = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForPaths([SourceFile("reopen.bin", content)]), CancellationToken.None));
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);
        var local = Assert.Single(await receiver.ItemsAsync(CancellationToken.None)).LocalPath!;
        File.Delete(local);

        var target = await receiver.Sync.EnsureLocalAsync(receiver.Identity, item.Id, CancellationToken.None);
        Assert.False(target.NeedsDownload);
        Assert.NotNull(target.Path);
        Assert.Equal(content, await File.ReadAllTextAsync(target.Path!));

        var textSender = NewDevice("laptop-1", server.Url);
        var text = Assert.Single(await textSender.Store.EnqueueAsync(textSender.Identity, AssistantDraft.ForText("内联文字"), CancellationToken.None));
        await textSender.Sync.SyncAsync(textSender.Identity, CancellationToken.None);
        await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);
        var open = await receiver.Sync.EnsureLocalAsync(receiver.Identity, text.Id, CancellationToken.None);
        Assert.Equal("内联文字", open.Text);
        Assert.Null(open.Path);
        Assert.False(open.NeedsDownload);
    }

    [Fact]
    public async Task InvalidManifestIsReportedWithoutPoisoningTheTimeline()
    {
        var server = StartServer();
        var published = await PublishAsync(server, "good.bin", "good");
        var badId = Guid.NewGuid().ToString("N");
        var badDirectory = Path.GetDirectoryName(server.DiskPath($"assistant/{Conversation}/{badId}/manifest.json"))!;
        System.IO.Directory.CreateDirectory(badDirectory);
        await File.WriteAllTextAsync(Path.Combine(badDirectory, "manifest.json"), JsonSerializer.Serialize(
            Manifest(badId, "../escape.bin", 3), AssistantJson.Options));

        var receiver = NewDevice("phone-1", server.Url);
        var page = await receiver.Client.ListAssistantAsync(Conversation, new AssistantListRequest(), CancellationToken.None);
        Assert.Equal(2, page.DiscoveredCount);
        Assert.Equal(badId, Assert.Single(page.InvalidItemIds));
        Assert.Equal(published.Id, Assert.Single(page.Items).Id);

        // The sync remembers a definitively invalid id, so later passes do not keep fetching the broken record.
        await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);
        var fetches = server.Count("GET", "/manifest.json");
        await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);
        Assert.Equal(fetches, server.Count("GET", "/manifest.json"));
    }

    [Fact]
    public async Task InterruptedDownloadWritesNoReceiptAndNeverReportsDelivery()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        var receiver = NewDevice("phone-1", server.Url);
        const string content = "must actually arrive";
        var item = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForPaths([SourceFile("arrive.bin", content)]), CancellationToken.None));
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);

        server.Intercept = (context, request) =>
        {
            if (request.Method == "GET" && request.Path.EndsWith("/payload", StringComparison.Ordinal))
            {
                AssistantWebDavServer.Write(context, 500);
                return true;
            }
            return false;
        };
        var failed = await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);
        Assert.Equal(0, failed.Downloaded);
        Assert.Equal(1, failed.Failed);
        var incoming = Assert.Single(await receiver.ItemsAsync(CancellationToken.None));
        Assert.Equal(AssistantItemState.Failed, incoming.State);
        Assert.Null(incoming.LocalPath);
        Assert.Null(incoming.ReceiptAt);
        // No atomic local save happened, so no receipt may exist and the sender must not see "delivered".
        Assert.Empty(await receiver.Client.ListAssistantReceiptsAsync(Conversation, item.Id, CancellationToken.None));
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        Assert.Equal(AssistantItemState.Stored, (await sender.StateAsync(CancellationToken.None)).Find(item.Id)!.State);

        // Recovery keeps the same shared entry and adds the receiver's real saved receipt.
        server.Intercept = null;
        Assert.Equal(1, (await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None)).Downloaded);
        Assert.Equal(AssistantItemState.Available, Assert.Single(await receiver.ItemsAsync(CancellationToken.None)).State);
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        var confirmed = (await sender.StateAsync(CancellationToken.None)).Find(item.Id)!;
        Assert.Equal(AssistantItemState.Stored, confirmed.State);
        Assert.Equal("phone-1", Assert.Single(confirmed.Receipts).DeviceId);
    }

    [Fact]
    public async Task ListingIsBoundedAndContinuesFromACursor()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        for (var index = 0; index < 3; index++)
            await sender.Store.EnqueueAsync(sender.Identity, AssistantDraft.ForPaths([SourceFile($"p{index}.bin", $"payload-{index}")]), CancellationToken.None);
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);

        var receiver = NewDevice("phone-1", server.Url);
        var fetches = server.Count("GET", "/manifest.json");
        var first = await receiver.Client.ListAssistantAsync(Conversation, new AssistantListRequest(1), CancellationToken.None);
        Assert.Equal(3, first.DiscoveredCount);
        Assert.Single(first.Items);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);

        var second = await receiver.Client.ListAssistantAsync(Conversation, new AssistantListRequest(1, Cursor: first.NextCursor), CancellationToken.None);
        var third = await receiver.Client.ListAssistantAsync(Conversation, new AssistantListRequest(2, Cursor: second.NextCursor), CancellationToken.None);
        Assert.Single(second.Items);
        Assert.Single(third.Items);
        Assert.False(third.HasMore);
        Assert.Equal(3, new[] { first.Items[0].Id, second.Items[0].Id, third.Items[0].Id }.Distinct().Count());
        Assert.Equal(fetches + 3, server.Count("GET", "/manifest.json"));   // one manifest fetch per entry, no repeats
    }

    [Fact]
    public async Task DirectlyReceivedEntryJoinsTheTimelineAndGetsItsReceiptOnTheNextSync()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        const string content = "arrived over the direct transport";
        var item = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForPaths([SourceFile("direct.bin", content)]), CancellationToken.None));
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);

        // M4's direct transport already saved the file; the receiver adopts it into the same durable timeline.
        var receiver = NewDevice("phone-1", server.Url);
        var saved = Path.Combine(_root, "direct-inbox", "direct.bin");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        await File.WriteAllTextAsync(saved, content);
        var adopted = await receiver.Store.AdoptAsync(receiver.Identity, new AssistantItem
        {
            Id = item.Id,
            Kind = AssistantItemKind.File,
            Name = "direct.bin",
            Size = Encoding.UTF8.GetByteCount(content),
            CreatedAt = item.CreatedAt,
            SenderDeviceId = sender.Identity.DeviceId,
            SenderName = sender.Identity.Name,
            TargetDeviceId = null,
            ConversationId = Conversation,
            Provenance = AssistantConversations.DirectShared,
            LocalPath = saved
        }, CancellationToken.None);

        var payloadGets = server.Count("GET", "/payload");
        await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);
        Assert.Equal(payloadGets, server.Count("GET", "/payload"));   // adopted content is never downloaded again
        Assert.Single(await receiver.ItemsAsync(CancellationToken.None));
        Assert.Equal(adopted.Id, Assert.Single(await receiver.Client.ListAssistantReceiptsAsync(Conversation, item.Id, CancellationToken.None)).ItemId);

        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        var mine = (await sender.StateAsync(CancellationToken.None)).Find(item.Id)!;
        Assert.Equal(AssistantItemState.Stored, mine.State);
        Assert.Equal("phone-1", Assert.Single(mine.Receipts).DeviceId);
    }

    [Fact]
    public async Task DirectDeliveryUpgradesAKnownRelayEntryAndStillYieldsARealReceipt()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        const string content = "direct arrival after the manifest";
        var item = Assert.Single(await sender.Store.EnqueueAsync(sender.Identity,
            AssistantDraft.ForPaths([SourceFile("late.bin", content)]), CancellationToken.None));
        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);

        // The receiver knew the entry from the relay manifest only: metadata, no local content, not "saved".
        var receiver = NewDevice("phone-1", server.Url);
        var known = Assert.Single((await receiver.Client.ListAssistantAsync(Conversation, new AssistantListRequest(), CancellationToken.None)).Items);
        var state = await receiver.StateAsync(CancellationToken.None);
        state.Add(new AssistantItem
        {
            Id = known.Id,
            Kind = known.Kind,
            Name = known.Name,
            Size = known.Size,
            CreatedAt = known.CreatedAt,
            SenderDeviceId = known.SenderDeviceId,
            SenderName = known.SenderName,
            TargetDeviceId = known.TargetDeviceId,
            ConversationId = Conversation,
            Provenance = AssistantConversations.SharedRelay,
            State = AssistantItemState.Stored
        });
        await receiver.Store.SaveAsync(state, CancellationToken.None);
        Assert.False(AssistantContent.HasLocalContent(state.Find(known.Id)!));

        // The file then arrives over M4's direct transport and upgrades the very same entry.
        var saved = Path.Combine(_root, "direct-late", "late.bin");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        await File.WriteAllTextAsync(saved, content);
        var adopted = await receiver.Store.AdoptAsync(receiver.Identity, new AssistantItem
        {
            Id = known.Id,
            Kind = known.Kind,
            Name = known.Name,
            Size = Encoding.UTF8.GetByteCount(content),
            CreatedAt = known.CreatedAt,
            SenderDeviceId = "desktop-1",
            SenderName = "desktop-1",
            TargetDeviceId = null,
            ConversationId = Conversation,
            Provenance = AssistantConversations.DirectShared,
            LocalPath = saved
        }, CancellationToken.None);
        Assert.Equal(AssistantItemState.Available, adopted.State);
        Assert.Equal(saved, adopted.LocalPath);

        var payloadGets = server.Count("GET", "/payload");
        await receiver.Sync.SyncAsync(receiver.Identity, CancellationToken.None);
        Assert.Equal(payloadGets, server.Count("GET", "/payload"));   // an adopted entry is never re-downloaded
        Assert.Single(await receiver.ItemsAsync(CancellationToken.None));   // upgraded, not duplicated
        Assert.Equal("phone-1", Assert.Single(await receiver.Client.ListAssistantReceiptsAsync(Conversation, item.Id, CancellationToken.None)).DeviceId);

        await sender.Sync.SyncAsync(sender.Identity, CancellationToken.None);
        Assert.Equal(AssistantItemState.Stored, (await sender.StateAsync(CancellationToken.None)).Find(item.Id)!.State);
    }

    [Fact]
    public async Task ReceiptChecksRotateFairlySoEveryConfirmedEntryEventuallyGetsItsReceipt()
    {
        var server = StartServer();
        var sender = NewDevice("desktop-1", server.Url);
        var receiver = NewDevice("phone-1", server.Url);
        var limits = new AssistantSyncLimits(Publish: 25, Pull: 50, Download: 4, Receipts: 12);
        for (var index = 0; index < 25; index++)
            await sender.Store.EnqueueAsync(sender.Identity, AssistantDraft.ForText($"批量消息 {index}"), CancellationToken.None);

        for (var round = 0; round < 4; round++)
            await new AssistantSync(sender.Store, sender.Client, limits).SyncAsync(sender.Identity, CancellationToken.None);
        var published = await sender.ItemsAsync(CancellationToken.None);
        Assert.Equal(25, published.Count);
        Assert.All(published, entry => Assert.Equal(AssistantItemState.Stored, entry.State));

        // The peer receives and confirms every entry, itself bounded per round.
        for (var round = 0; round < 6; round++)
        {
            await new AssistantSync(receiver.Store, receiver.Client, limits).SyncAsync(receiver.Identity, CancellationToken.None);
            if ((await receiver.ItemsAsync(CancellationToken.None)).All(entry => entry.ReceiptAt is not null)) break;
        }
        Assert.All(await receiver.ItemsAsync(CancellationToken.None), entry => Assert.NotNull(entry.ReceiptAt));

        // The sender now learns all 25 real receipts over bounded, fair rounds: 12 at most per round.
        var perRound = new List<int>();
        for (var round = 0; round < 6; round++)
        {
            var start = server.Count("PROPFIND", "/receipts");
            var result = await new AssistantSync(sender.Store, sender.Client, limits).SyncAsync(sender.Identity, CancellationToken.None);
            perRound.Add(server.Count("PROPFIND", "/receipts") - start);
            if ((await sender.ItemsAsync(CancellationToken.None)).All(entry => entry.Receipts.Any(receipt => receipt.DeviceId == "phone-1"))) break;
        }
        Assert.All(perRound, count => Assert.InRange(count, 0, 12));
        Assert.True(perRound.Sum() <= 36, $"回执检查总次数应有界，实际 {perRound.Sum()}");
        var mine = await sender.StateAsync(CancellationToken.None);
        Assert.All(mine.Items, entry => Assert.Contains(entry.Receipts, receipt => receipt.DeviceId == "phone-1"));
        Assert.All(mine.Items, entry => Assert.NotNull(entry.ReceiptCheckedAt));   // the rotation visited every entry
    }

    [Fact]
    public async Task UnreachableRelayReportsRetryAfterAndStopsAfterTheFirstFailedRequest()
    {
        var server = StartServer();
        var device = NewDevice("desktop-1", server.Url);
        for (var index = 0; index < 3; index++)
            await device.Store.EnqueueAsync(device.Identity, AssistantDraft.ForText($"排队 {index}"), CancellationToken.None);

        server.Intercept = (context, request) =>
        {
            if (request.Method == "PUT") { AssistantWebDavServer.Write(context, 503); return true; }
            return false;
        };
        var result = await device.Sync.SyncAsync(device.Identity, CancellationToken.None);
        Assert.Equal(0, result.Published);
        Assert.Equal(1, result.Failed);
        Assert.False(result.HasMore);       // an unusable relay must not claim immediately startable work
        Assert.NotNull(result.RetryAfter);
        Assert.Equal(1, server.Count("PUT"));   // bounded: the round stops after the first relay-level failure
        var blocked = await device.ItemsAsync(CancellationToken.None);
        Assert.Equal(AssistantItemState.Failed, blocked[0].State);
        Assert.All(blocked.Skip(1), item => Assert.Equal(AssistantItemState.Queued, item.State));   // untouched, not failed
        Assert.Equal(1, blocked[0].Attempts);

        // Recovery continues the same entries without duplicates.
        server.Intercept = null;
        var recovered = await device.Sync.SyncAsync(device.Identity, CancellationToken.None);
        Assert.Equal(3, recovered.Published);
        Assert.False(recovered.HasMore);
        Assert.Null(recovered.RetryAfter);
        Assert.All(await device.ItemsAsync(CancellationToken.None), item => Assert.Equal(AssistantItemState.Stored, item.State));
        Assert.Equal(4, server.Count("PUT"));   // one refused manifest attempt + three published manifests
    }

    [Fact]
    public async Task TwoSyncInstancesShareOneStoreWithoutLosingMessagesOrRevivingCancelledEntries()
    {
        var server = StartServer();
        var identity = Identity("desktop-1");
        var store = new AssistantStore(Path.Combine(_root, "devices", "desktop-1-shared"));
        var client = Connect(server.Url);
        var item = Assert.Single(await store.EnqueueAsync(identity,
            AssistantDraft.ForPaths([SourceFile("slow.bin", "slow-payload")]), CancellationToken.None));

        // Hold the first payload upload open so the test can act while a sync is mid-flight.
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        server.Intercept = (context, request) =>
        {
            if (request.Method == "PUT" && request.Path.EndsWith("/payload", StringComparison.Ordinal))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
            return false;
        };

        var pending = new AssistantSync(store, client).SyncAsync(identity, CancellationToken.None);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
        // A second sync instance started while the first is mid-flight must wait for the store's sync gate
        // instead of racing inside the same snapshot.
        var second = new AssistantSync(store, client).SyncAsync(identity, CancellationToken.None);
        // Mid-flight: another command enqueues a message and cancels the entry that is being uploaded.
        var queued = Assert.Single(await store.EnqueueAsync(identity, AssistantDraft.ForText("抢跑消息"), CancellationToken.None));
        var cancelled = false;
        await store.MutateAsync(state => cancelled = state.TryCancel(item.Id), CancellationToken.None);
        Assert.True(cancelled);
        release.Set();
        var firstResult = await pending;
        var secondResult = await second;
        Assert.False(secondResult.HasMore);

        // The upload finished, but the local record stays cancelled and the new message was not lost.
        var state = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(2, state.Items.Count);
        Assert.Equal(AssistantItemState.Cancelled, state.Find(item.Id)!.State);
        Assert.Equal(AssistantItemState.Stored, state.Find(queued.Id)!.State);
        Assert.Equal(1, secondResult.Published);   // the message that arrived mid-flight went out exactly once
        Assert.Equal(1, server.Count("PUT", "/payload"));
        Assert.Equal(2, server.Count("PUT", "/manifest.json"));
        Assert.Equal(0, state.Find(item.Id)!.Attempts);   // a cancel is not a failure, and the second round skipped it

        // The cancelled entry is never uploaded again.
        var payloads = server.Count("PUT", "/payload");
        await new AssistantSync(store, client).SyncAsync(identity, CancellationToken.None);
        Assert.Equal(payloads, server.Count("PUT", "/payload"));
    }

    [Fact]
    public async Task IncompletePublishNeverSpinsTheScheduler()
    {
        var server = StartServer();
        // Two item directories that only ever got a payload: a publisher that died before the manifest.
        for (var index = 0; index < 2; index++)
        {
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.GetDirectoryName(server.DiskPath($"assistant/{Conversation}/{id}/payload"))!;
            System.IO.Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "payload"), "half");
        }
        var device = NewDevice("desktop-1", server.Url);
        var sync = new AssistantSync(device.Store, device.Client, new AssistantSyncLimits(Pull: 1));

        var first = await sync.SyncAsync(device.Identity, CancellationToken.None);
        Assert.Equal(0, first.Received);
        Assert.Empty(await device.ItemsAsync(CancellationToken.None));
        Assert.False(first.HasMore);      // nothing startable: not an entry, and the page could not advance
        Assert.Null(first.RetryAfter);    // the relay itself is fine, so the normal cadence applies

        var fetches = server.Count("GET", "/manifest.json");
        await sync.SyncAsync(device.Identity, CancellationToken.None);
        Assert.Equal(fetches + 1, server.Count("GET", "/manifest.json"));   // bounded per round, never a fast loop
    }

    [Fact]
    public async Task FailedLiveSaveAfterASyncKeepsTheCommittedGeneration()
    {
        var server = StartServer();
        var identity = Identity("desktop-1");
        var store = new AssistantStore(Path.Combine(_root, "devices", "save-after-sync"));
        var client = Connect(server.Url);
        var item = Assert.Single(await store.EnqueueAsync(identity, AssistantDraft.ForText("同步完成"), CancellationToken.None));
        var published = await new AssistantSync(store, client).SyncAsync(identity, CancellationToken.None);
        Assert.Equal(1, published.Published);
        Assert.Equal(AssistantItemState.Stored, item.State);

        // A later live edit that cannot be written must fall back to exactly what the sync committed.
        var blocker = Path.Combine(store.Directory, "assistant.json.tmp");
        System.IO.Directory.CreateDirectory(blocker);
        var state = await store.LoadAsync(CancellationToken.None);
        state.Find(item.Id)!.State = AssistantItemState.Queued;
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(state, CancellationToken.None));

        Assert.Equal(AssistantItemState.Stored, item.State);
        Assert.Equal(AssistantItemState.Stored,
            (await new AssistantStore(store.Directory).LoadAsync(CancellationToken.None)).Find(item.Id)!.State);
        System.IO.Directory.Delete(blocker);
    }

    [Fact]
    public async Task CancellingTheInFlightUploadNeverReanimatesACancelledEntry()
    {
        var server = StartServer();
        var identity = Identity("desktop-1");
        var store = new AssistantStore(Path.Combine(_root, "devices", "cancel-upload"));
        var client = Connect(server.Url);
        var item = Assert.Single(await store.EnqueueAsync(identity,
            AssistantDraft.ForPaths([SourceFile("cancel-me.bin", "payload")]), CancellationToken.None));

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        server.Intercept = (context, request) =>
        {
            if (request.Method == "PUT" && request.Path.EndsWith("/payload", StringComparison.Ordinal))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
            return false;
        };

        using var cancellation = new CancellationTokenSource();
        var pending = new AssistantSync(store, client).SyncAsync(identity, cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
        var cancelled = false;
        await store.MutateAsync(state => cancelled = state.TryCancel(item.Id), CancellationToken.None);
        Assert.True(cancelled);

        // The user cancelled first; the network call then trips. Its cleanup must not revive the entry.
        cancellation.Cancel();
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(AssistantItemState.Cancelled, (await store.LoadAsync(CancellationToken.None)).Find(item.Id)!.State);
        Assert.Equal(AssistantItemState.Cancelled,
            (await new AssistantStore(store.Directory).LoadAsync(CancellationToken.None)).Find(item.Id)!.State);
        // A later round must not re-upload it either.
        var payloads = server.Count("PUT", "/payload");
        await new AssistantSync(store, client).SyncAsync(identity, CancellationToken.None);
        Assert.Equal(payloads, server.Count("PUT", "/payload"));
    }

    [Fact]
    public async Task CancellingASlowDownloadKeepsTheCancelledEntryAndNeverMarksItAvailable()
    {
        var server = StartServer();
        var identity = Identity("desktop-1");
        var store = new AssistantStore(Path.Combine(_root, "devices", "cancel-download"));
        var client = Connect(server.Url);
        const string content = "slow-payload";
        var id = SeedIncomingAttachment(server, store, identity, content);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        server.Intercept = (context, request) =>
        {
            if (request.Method == "GET" && request.Path.EndsWith("/payload", StringComparison.Ordinal))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
            return false;
        };

        using var cancellation = new CancellationTokenSource();
        var pending = new AssistantSync(store, client).SyncAsync(identity, cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));
        var cancelled = false;
        await store.MutateAsync(state => cancelled = state.TryCancel(id), CancellationToken.None);
        Assert.True(cancelled);
        cancellation.Cancel();
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var live = (await store.LoadAsync(CancellationToken.None)).Find(id)!;
        Assert.Equal(AssistantItemState.Cancelled, live.State);   // not reanimated to stored, never available
        Assert.Null(live.LocalPath);
        var payloadGets = server.Count("GET", "/payload");
        await new AssistantSync(store, client).SyncAsync(identity, CancellationToken.None);
        Assert.Equal(payloadGets, server.Count("GET", "/payload"));   // a cancelled entry is never downloaded later
    }

    [Fact]
    public async Task EnsureLocalNeverOverwritesADirectReceiveThatWinsTheRace()
    {
        var server = StartServer();
        var identity = Identity("desktop-1");
        var store = new AssistantStore(Path.Combine(_root, "devices", "ensure-race"));
        var client = Connect(server.Url);
        const string content = "raced-payload";
        var id = SeedIncomingAttachment(server, store, identity, content);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        server.Intercept = (context, request) =>
        {
            if (request.Method == "GET" && request.Path.EndsWith("/payload", StringComparison.Ordinal))
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
            return false;
        };

        using var cancellation = new CancellationTokenSource();
        var pending = new AssistantSync(store, client).EnsureLocalAsync(identity, id, cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(15)));

        // M4's direct transport delivers the same entry while the relay download is still in flight.
        var saved = Path.Combine(_root, "direct-race", "slow.bin");
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
        await File.WriteAllTextAsync(saved, content);
        var adopted = await store.AdoptAsync(identity, new AssistantItem
        {
            Id = id,
            Kind = AssistantItemKind.File,
            Name = "slow.bin",
            Size = Encoding.UTF8.GetByteCount(content),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            SenderDeviceId = "phone-1",
            SenderName = "phone-1",
            TargetDeviceId = identity.DeviceId,
            LocalPath = saved
        }, CancellationToken.None);
        Assert.Equal(AssistantItemState.Available, adopted.State);

        cancellation.Cancel();
        release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        var live = (await store.LoadAsync(CancellationToken.None)).Find(id)!;
        Assert.Equal(AssistantItemState.Available, live.State);   // the direct receive is not downgraded
        Assert.Equal(saved, live.LocalPath);
    }

    [Fact]
    public async Task RelayFailureStopsEveryLaterStageInsteadOfOnlyTheUpload()
    {
        var server = StartServer();
        var identity = Identity("desktop-1");
        var store = new AssistantStore(Path.Combine(_root, "devices", "stop-all"));
        var client = Connect(server.Url);
        var queued = Assert.Single(await store.EnqueueAsync(identity, AssistantDraft.ForText("会失败"), CancellationToken.None));

        // Pre-existing work for every later stage: a receipt to check, an attachment to download, an ack to write.
        const string content = "incoming";
        var payloadId = SeedIncomingAttachment(server, store, identity, content);
        var state = await store.LoadAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        state.Add(new AssistantItem
        {
            Id = Guid.NewGuid().ToString("N"), Kind = AssistantItemKind.Text, Text = "已发布", Size = 9,
            CreatedAt = now.AddMinutes(-5), SenderDeviceId = identity.DeviceId, SenderName = identity.Name,
            ConversationId = Conversation,
            Provenance = AssistantConversations.SharedRelay,
            State = AssistantItemState.Stored
        });
        state.Add(new AssistantItem
        {
            Id = Guid.NewGuid().ToString("N"), Kind = AssistantItemKind.Text, Text = "待回执", Size = 9,
            CreatedAt = now.AddMinutes(-3), SenderDeviceId = "phone-1", SenderName = "phone-1",
            TargetDeviceId = identity.DeviceId, ConversationId = Conversation, Provenance = AssistantConversations.SharedRelay, State = AssistantItemState.Available
        });
        await store.SaveAsync(state, CancellationToken.None);

        server.Intercept = (context, request) =>
        {
            if (request.Method == "PUT") { AssistantWebDavServer.Write(context, 503); return true; }
            return false;
        };
        var before = server.Requests.Count;
        var result = await new AssistantSync(store, client, new AssistantSyncLimits(Publish: 1)).SyncAsync(identity, CancellationToken.None);
        var delta = server.Requests.Skip(before).ToArray();

        Assert.Equal(0, result.Published);
        Assert.False(result.HasMore);
        Assert.NotNull(result.RetryAfter);
        // Exactly the first publish attempt: 3 MKCOL + the manifest probe + the refused PUT. Nothing after it.
        Assert.Equal(5, delta.Length);
        Assert.Equal(1, delta.Count(request => request.Method == "PUT"));
        Assert.Equal(1, delta.Count(request => request.Method == "GET"));
        Assert.Equal(0, delta.Count(request => request.Method == "PROPFIND"));
        Assert.Equal(3, delta.Count(request => request.Method == "MKCOL"));

        // Local bookkeeping is preserved and nothing was falsely advanced.
        var after = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(AssistantItemState.Failed, after.Find(queued.Id)!.State);
        Assert.Equal(AssistantItemState.Stored, after.Find(payloadId)!.State);
        Assert.Null(after.Items.Single(item => item.Text == "待回执").ReceiptAt);

        // Once the relay recovers, every stage continues from that preserved state.
        server.Intercept = null;
        var recovered = await new AssistantSync(store, client, new AssistantSyncLimits(Publish: 1)).SyncAsync(identity, CancellationToken.None);
        Assert.Equal(1, recovered.Published);         // the entry the failed round could not publish
        Assert.Equal(1, recovered.Downloaded);        // the attachment that stayed Stored through the outage
        Assert.Equal(2, recovered.ReceiptsWritten);   // its acknowledgement plus the one still pending
        Assert.Equal(2, recovered.ReceiptsChecked);   // the seeded entry and the one just published
        Assert.False(recovered.HasMore);
        Assert.Null(recovered.RetryAfter);
        var final = await store.LoadAsync(CancellationToken.None);
        Assert.All(final.Incoming(identity.DeviceId).Where(item => item.State == AssistantItemState.Available),
            item => Assert.NotNull(item.ReceiptAt));
    }

    /// <summary>Publishes a payload on the relay and records the matching incoming entry in the local store.</summary>
    private string SeedIncomingAttachment(AssistantWebDavServer server, AssistantStore store, AssistantIdentity identity, string content)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.GetDirectoryName(server.DiskPath($"assistant/{Conversation}/{id}/payload"))!;
        System.IO.Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "payload"), content);
        var state = store.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
        state.Add(new AssistantItem
        {
            Id = id,
            Kind = AssistantItemKind.File,
            Name = "slow.bin",
            Size = Encoding.UTF8.GetByteCount(content),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            SenderDeviceId = "phone-1",
            SenderName = "phone-1",
            TargetDeviceId = identity.DeviceId,
            ConversationId = Conversation,
            Provenance = AssistantConversations.SharedRelay,
            State = AssistantItemState.Stored
        });
        store.SaveAsync(state, CancellationToken.None).GetAwaiter().GetResult();
        return id;
    }

    [Fact]
    public async Task WebDavRequestsRequireTheAccountCredentials()
    {
        var server = StartServer();
        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync($"{server.Url}/assistant/{Conversation}/");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var device = NewDevice("desktop-1", server.Url);
        await device.Store.EnqueueAsync(device.Identity, AssistantDraft.ForText("hello"), CancellationToken.None);
        var baseline = server.Requests.Count;   // the anonymous probe above is expected to be unauthenticated
        await device.Sync.SyncAsync(device.Identity, CancellationToken.None);
        var requests = server.Requests.Skip(baseline).ToArray();
        Assert.NotEmpty(requests);
        Assert.All(requests.Where(request => request.Path.StartsWith("/dav", StringComparison.Ordinal)),
            request => Assert.True(request.Authorized));
    }
}
