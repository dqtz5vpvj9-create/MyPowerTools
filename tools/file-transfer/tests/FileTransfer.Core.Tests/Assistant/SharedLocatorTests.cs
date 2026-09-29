using System.Text.Json;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using Xunit.Abstractions;

namespace FileTransfer.Tests;

public sealed class SharedLocatorTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const string Conversation = "shared-locator-test";
    private static readonly string Key = new('a', 64);
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-locator-" + Guid.NewGuid().ToString("N"));
    private readonly List<SharedLocatorClient> _clients = [];
    private SharedLocatorRelayFixture _tail = null!;
    private SharedLocatorRelayFixture _public = null!;

    public async Task InitializeAsync()
    {
        _tail = new(Path.Combine(_root, "tail"));
        _public = new(Path.Combine(_root, "public"));
        await _tail.StartAsync();
        _public.ProxyEnabled = true;
        _public.TailOrigin = _tail.Address;
        await _public.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var client in _clients) client.Dispose();
        await _tail.DisposeAsync();
        await _public.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private SharedLocatorClient Client(SharedRelayRoute route, bool unavailableTail = false, string? key = null)
    {
        var endpoint = unavailableTail ? new Uri("http://127.0.0.1:1") : route == SharedRelayRoute.Tail ? _tail.Address : _public.Address;
        var client = new SharedLocatorClient(Conversation, key ?? Key, route, endpoint);
        _clients.Add(client);
        return client;
    }

    private (SharedLocatorTransfer Transfer, SharedLocatorClient Public, SharedLocatorClient Tail) Device(string name, bool noTail = false)
    {
        var publicClient = Client(SharedRelayRoute.Public);
        var tailClient = Client(SharedRelayRoute.Tail, noTail);
        return (new(publicClient, tailClient, Path.Combine(_root, name, "copy")), publicClient, tailClient);
    }

    private async Task<(AssistantManifest Message, string Payload)> AttachmentAsync(DateTimeOffset? at = null)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bin");
        var bytes = new byte[1024 * 1024];
        Random.Shared.NextBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
        return (new AssistantManifest { Id = Guid.NewGuid().ToString("N"), Kind = AssistantItemKind.File,
            Name = "test.bin", Size = bytes.Length, CreatedAt = at ?? DateTimeOffset.UtcNow,
            SenderDeviceId = "sender-a", SenderName = "A" }, path);
    }

    private Task<SharedReceiveResult> ReceiveAsync(SharedLocatorTransfer transfer, SharedLocator locator, string device, string? existing = null,
        Func<string, CancellationToken, Task>? commit = null) => transfer.ReceiveAsync(locator, device, device,
            Path.Combine(_root, device, "inbox"), existing,
            commit ?? ((path, token) => File.WriteAllTextAsync(Path.Combine(_root, device, "saved.json"), JsonSerializer.Serialize(new { id = locator.Message.Id, path }), token)), CancellationToken.None);

    [Fact]
    public async Task FirstReceiptDoesNotPreventPublicOnlyAndLateMembersFromReceivingAfterRestarts()
    {
        var sender = Device("sender");
        var b = Device("b");
        var c = Device("c", noTail: true);
        var (message, payload) = await AttachmentAsync();
        var published = await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        Assert.Equal(new SharedPublishResult(true, true, false), published);
        var locator = Assert.Single((await b.Public.ListLocatorsAsync(new(), CancellationToken.None)).Items);
        var receivedB = await ReceiveAsync(b.Transfer, locator, "b");
        Assert.False(receivedB.WaitingForPublicCopy);
        Assert.Equal(await File.ReadAllBytesAsync(payload), await File.ReadAllBytesAsync(receivedB.Path!));
        Assert.Single(await sender.Public.ListReceiptsAsync(message.Id, CancellationToken.None));
        Assert.Equal(message.Size, _tail.PayloadUploadBytes);
        Assert.Equal(0, _public.PayloadUploadBytes);
        Assert.Equal(0, _public.PayloadDownloads);
        output.WriteLine($"all-Tail: payload={message.Size}, public-payload-upload={_public.PayloadUploadBytes}, public-payload-GET={_public.PayloadDownloads}, public-metadata-upload={_public.MetadataUploadBytes}");

        await _public.RestartAsync();
        await _tail.RestartAsync();
        sender.Public.Dispose(); sender.Tail.Dispose();
        File.Delete(payload); // The original sender is absent; the relay's existing Tail copy remains readable.
        var receivedC = await ReceiveAsync(c.Transfer, locator, "c");
        Assert.False(receivedC.WaitingForPublicCopy);
        var late = Device("late", noTail: true);
        var receivedLate = await ReceiveAsync(late.Transfer, locator, "late");
        Assert.False(receivedLate.WaitingForPublicCopy);
        Assert.Equal(3, (await b.Public.ListReceiptsAsync(message.Id, CancellationToken.None)).Count);
        Assert.Empty((await b.Public.ListRequestsAsync(message.Id, CancellationToken.None)).Items);
        Assert.Equal(0, _public.PayloadUploadBytes);
        Assert.Equal(2, _public.PayloadDownloads);
        output.WriteLine($"mixed: public-payload-upload={_public.PayloadUploadBytes}, public-proxy-GET={_public.PayloadDownloads}, public-metadata-upload={_public.MetadataUploadBytes}");
        var downloads = _public.PayloadDownloads + _tail.PayloadDownloads;
        await ReceiveAsync(c.Transfer, locator, "c", receivedC.Path);
        Assert.Equal(downloads, _public.PayloadDownloads + _tail.PayloadDownloads);
    }

    [Fact]
    public async Task FailedLocatorPublicationRetriesOnlyMetadataAndRequestsRemainIdempotent()
    {
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        _public.Fail("locator", true);
        await Assert.ThrowsAsync<HttpRequestException>(() => sender.Transfer.PublishAsync(message, payload, CancellationToken.None));
        Assert.Equal(message.Size, _tail.PayloadUploadBytes);
        Assert.Null(await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None));
        _public.Fail("locator", false);
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        Assert.Equal(message.Size, _tail.PayloadUploadBytes);
        var revision = await sender.Public.ChangesAsync(null, CancellationToken.None);
        var changed = sender.Public.ChangesAsync(revision, CancellationToken.None);
        await sender.Public.RequestPublicCopyAsync(message.Id, "c", "tail-unreachable", CancellationToken.None);
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)) > revision);
        var after = await sender.Public.ChangesAsync(null, CancellationToken.None);
        await sender.Public.RequestPublicCopyAsync(message.Id, "c", "tail-unreachable", CancellationToken.None);
        Assert.Equal(after, await sender.Public.ChangesAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task InterruptedFallbackHasNoManifestAndConcurrentRequestsCopyTheSameItemOnce()
    {
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        await sender.Public.RequestPublicCopyAsync(message.Id, "c", "tail-unreachable", CancellationToken.None);
        await sender.Public.RequestPublicCopyAsync(message.Id, "d", "tail-unreachable", CancellationToken.None);
        _public.Fail("payload", true);
        await Assert.ThrowsAnyAsync<Exception>(() => sender.Transfer.ServeRequestsAsync(message, "sender-a", payload, CancellationToken.None));
        Assert.NotNull(await sender.Public.ReadContentManifestAsync(message.Id, CancellationToken.None));
        Assert.False(await sender.Public.HasPublicCopyAsync(message.Id, CancellationToken.None));
        Assert.Equal(2, (await sender.Public.ListRequestsAsync(message.Id, CancellationToken.None)).Items.Count);
        _public.Fail("payload", false);
        await _public.RestartAsync();
        sender = Device("sender-restarted");
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sender.Transfer.ServeRequestsAsync(message, "sender-a", payload, CancellationToken.None)));
        Assert.All(results, Assert.True);
        Assert.Equal(message.Size, _public.PayloadUploadBytes);
        Assert.NotNull(await sender.Public.ReadContentManifestAsync(message.Id, CancellationToken.None));
    }

    [Fact]
    public async Task OriginalV1ClientReadsAnImageThroughThePublicProxyWithoutSenderOrPublicPayloadCopy()
    {
        var sender = Device("sender");
        var receiver = Device("b");
        var (file, payload) = await AttachmentAsync();
        var message = file with { Kind = AssistantItemKind.Image, Name = "test.png" };
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        var locator = (await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None))!;
        await ReceiveAsync(receiver.Transfer, locator, "b");
        Assert.False(await sender.Transfer.ServeRequestsAsync(message, "sender-a", payload, CancellationToken.None));
        sender.Public.Dispose(); sender.Tail.Dispose();
        using var oldClient = new OpenListClient(new Uri(_public.Address, PublicRelayClient.DavPath).ToString(), Conversation, Key);
        Assert.Equal(message.Id, Assert.Single((await oldClient.ListAssistantAsync(Conversation, new(), CancellationToken.None)).Items).Id);
        var saved = await oldClient.DownloadAssistantAsync(Conversation, message, Path.Combine(_root, "old-client"), null, CancellationToken.None);
        Assert.Equal(await File.ReadAllBytesAsync(payload), await File.ReadAllBytesAsync(saved));
        Assert.Equal(0, _public.PayloadUploadBytes);
        Assert.False(_public.HealthReceivedCredentials);
    }

    [Fact]
    public async Task LocalAdoptionFailureNeverWritesAReceiptAndPrivateMessagesCannotCreateLocators()
    {
        var sender = Device("sender");
        var receiver = Device("b");
        var (message, payload) = await AttachmentAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => sender.Transfer.PublishAsync(message with { TargetDeviceId = "b" }, payload, CancellationToken.None));
        Assert.Equal(0, _tail.PayloadUploadBytes + _public.PayloadUploadBytes);
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        var locator = (await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None))!;
        await Assert.ThrowsAsync<IOException>(() => ReceiveAsync(receiver.Transfer, locator, "b", commit: (_, _) => throw new IOException("test store unavailable")));
        Assert.Empty(await sender.Public.ListReceiptsAsync(message.Id, CancellationToken.None));
    }

    [Fact]
    public async Task APublicOnlySenderStillPublishesACompleteV1Attachment()
    {
        var sender = Device("sender", noTail: true);
        var (message, payload) = await AttachmentAsync();
        Assert.Equal(new SharedPublishResult(false, false, true),
            await sender.Transfer.PublishAsync(message, payload, CancellationToken.None));
        Assert.Null(await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None));
        Assert.Equal(message.Size, _public.PayloadUploadBytes);
        Assert.Equal(0, _tail.PayloadUploadBytes);
        using var oldClient = new OpenListClient(new Uri(_public.Address, PublicRelayClient.DavPath).ToString(), Conversation, Key);
        Assert.Equal(message.Id, Assert.Single((await oldClient.ListAssistantAsync(Conversation, new(), CancellationToken.None)).Items).Id);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.Transfer.PublishAsync(message with { Id = Guid.NewGuid().ToString("N") }, payload, cancelled.Token));
        Assert.Equal(message.Size, _public.PayloadUploadBytes);
    }

    [Fact]
    public async Task ATruncatedTailPayloadRequestsPublicCopyWithoutAReceiptOrPartialFile()
    {
        var sender = Device("sender");
        var receiver = Device("b");
        var (message, payload) = await AttachmentAsync();
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        var locator = (await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None))!;
        await File.WriteAllBytesAsync(_tail.ContentPath(Conversation, "assistant", Conversation, message.Id, "payload"), [1, 2, 3]);
        var result = await ReceiveAsync(receiver.Transfer, locator, "b");
        Assert.True(result.WaitingForPublicCopy);
        Assert.Null(result.Path);
        Assert.Empty(await sender.Public.ListReceiptsAsync(message.Id, CancellationToken.None));
        Assert.Contains((await sender.Public.ListRequestsAsync(message.Id, CancellationToken.None)).Items,
            request => request.DeviceId == "b");
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "b", "inbox")));
    }

    [Fact]
    public async Task MetadataIsBoundedAndRequestsCannotChangeTheConversationOrEndpoint()
    {
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        var badId = Guid.NewGuid().ToString("N");
        var badPath = _public.ContentPath(Conversation, "assistant-locator", Conversation, badId, "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllTextAsync(badPath, new string('x', SharedLocatorRules.LocatorBytes + 1));
        Assert.Equal(badId, Assert.Single((await sender.Public.ListLocatorsAsync(new(), CancellationToken.None)).InvalidItemIds));
        for (var index = 0; index < AssistantLimits.MaxReceiptsPerItem + 1; index++)
        {
            var device = "member-" + index.ToString("D2");
            var requestPath = _public.ContentPath(Conversation, "assistant-locator", Conversation, message.Id, "requests", device + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new SharedPublicCopyRequest
                { ItemId = message.Id, DeviceId = device, RequestedAt = DateTimeOffset.UtcNow }, AssistantJson.Options));
        }
        var requests = await sender.Public.ListRequestsAsync(message.Id, CancellationToken.None);
        Assert.True(requests.LimitExceeded);
        Assert.Equal(AssistantLimits.MaxReceiptsPerItem, requests.Items.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => sender.Public.ReadLocatorAsync("../another-conversation", CancellationToken.None));
        Assert.Throws<ArgumentException>(() => new SharedLocatorClient(Conversation, Key, SharedRelayRoute.Tail, new Uri("http://untrusted.example")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.Tail.RequestPublicCopyAsync(message.Id, "b", "tail-unreachable", CancellationToken.None));
        using var wrong = Client(SharedRelayRoute.Public, key: new string('b', 64));
        await Assert.ThrowsAsync<PublicRelayAuthException>(() => wrong.ReadLocatorAsync(message.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AServerWithoutTheAdvertisedCapabilityKeepsCompleteV1Storage()
    {
        _public.ProxyEnabled = false;
        await _public.RestartAsync();
        var sender = Device("sender");
        Assert.False(await sender.Public.SupportsPayloadLocatorAsync(CancellationToken.None));
        var (message, payload) = await AttachmentAsync();
        var result = await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        Assert.Equal(new SharedPublishResult(false, false, true), result);
        Assert.Equal(0, _tail.PayloadUploadBytes);
        Assert.Equal(message.Size, _public.PayloadUploadBytes);
        Assert.Null(await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None));
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        Assert.Equal(message.Size, _public.PayloadUploadBytes);
        Assert.False(_public.HealthReceivedCredentials);
    }

    [Fact]
    public async Task ACommittedOriginalV1AttachmentIsNotUploadedAgainAfterAnUpgrade()
    {
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        await sender.Public.RegisterAsync(CancellationToken.None);
        using var oldClient = new OpenListClient(new Uri(_public.Address, PublicRelayClient.DavPath).ToString(), Conversation, Key);
        await oldClient.PublishAssistantAsync(Conversation, message, payload, null, CancellationToken.None);
        Assert.Equal(new SharedPublishResult(false, false, true), await sender.Transfer.PublishAsync(message, payload, CancellationToken.None));
        Assert.Equal(message.Size, _public.PayloadUploadBytes);
        Assert.Equal(0, _tail.PayloadUploadBytes);
        Assert.Null(await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AFailedFinalManifestCanResumeWithoutRepeatingTheTailUpload()
    {
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        _public.Fail("manifest", true);
        await Assert.ThrowsAsync<HttpRequestException>(() => sender.Transfer.PublishAsync(message, payload, CancellationToken.None));
        Assert.NotNull(await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None));
        Assert.Null(await sender.Public.ReadContentManifestAsync(message.Id, CancellationToken.None));
        _public.Fail("manifest", false);
        await _public.RestartAsync();
        await Device("sender-restarted").Transfer.PublishAsync(message, payload, CancellationToken.None);
        Assert.NotNull(await sender.Public.ReadContentManifestAsync(message.Id, CancellationToken.None));
        Assert.Equal(message.Size, _tail.PayloadUploadBytes);
        Assert.Equal(0, _public.PayloadUploadBytes);
    }

    [Fact]
    public async Task TailFailureRequestsASenderCopyWhichSurvivesPublicAndSenderRestart()
    {
        var sender = Device("sender");
        var b = Device("b");
        var c = Device("c", noTail: true);
        var (message, payload) = await AttachmentAsync();
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        var locator = (await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None))!;
        await ReceiveAsync(b.Transfer, locator, "b");
        await _tail.StopAsync();
        var waiting = await ReceiveAsync(c.Transfer, locator, "c");
        Assert.True(waiting.WaitingForPublicCopy);
        Assert.Single(await b.Public.ListReceiptsAsync(message.Id, CancellationToken.None));
        // The same namespace may also contain the public server's reserved request, never a receipt.
        await b.Public.RequestPublicCopyAsync(message.Id, "relay-public", "tail-unreachable", CancellationToken.None);
        await _public.RestartAsync();
        var restarted = Device("sender-restarted");
        Assert.Contains((await restarted.Public.ListRequestsAsync(message.Id, CancellationToken.None)).Items,
            request => request.DeviceId == "relay-public");
        Assert.True(await restarted.Transfer.ServeRequestsAsync(message, "sender-a", payload, CancellationToken.None));
        var received = await ReceiveAsync(c.Transfer, locator, "c");
        Assert.False(received.WaitingForPublicCopy);
        Assert.Equal(await File.ReadAllBytesAsync(payload), await File.ReadAllBytesAsync(received.Path!));
        Assert.Equal(2, (await b.Public.ListReceiptsAsync(message.Id, CancellationToken.None)).Count);
        Assert.True(await Device("sender-restarted-again").Transfer.ServeRequestsAsync(message, "sender-a", payload, CancellationToken.None));
        Assert.Equal(message.Size, _public.PayloadUploadBytes);
    }

    [Fact]
    public async Task SyncPersistsPlacementAndServicesLateRequestsAfterReceiptAndRestart()
    {
        var sender = Device("sender");
        var receiver = Device("receiver");
        using var senderDav = new OpenListClient(new Uri(_public.Address, PublicRelayClient.DavPath).ToString(), Conversation, Key);
        using var receiverDav = new OpenListClient(new Uri(_public.Address, PublicRelayClient.DavPath).ToString(), Conversation, Key);
        var senderIdentity = new AssistantIdentity("sender-a", "A", Conversation);
        var receiverIdentity = new AssistantIdentity("b", "B", Conversation);
        var senderDirectory = Path.Combine(_root, "sender-store");
        var senderStore = new AssistantStore(senderDirectory);
        var receiverStore = new AssistantStore(Path.Combine(_root, "receiver-store"));
        var (_, payload) = await AttachmentAsync();
        var item = Assert.Single(await senderStore.EnqueueAsync(senderIdentity, AssistantDraft.ForPaths([payload]), CancellationToken.None));
        var privateItem = Assert.Single(await senderStore.EnqueueAsync(senderIdentity, AssistantDraft.ForText("private", "b"), CancellationToken.None));
        var sentEvents = new List<AssistantItemState>();
        var senderState = await senderStore.LoadAsync(CancellationToken.None);
        var senderSync = new AssistantSync(senderStore, senderDav) { SharedTransport = sender.Transfer,
            Changed = _ => sentEvents.Add(senderState.Find(item.Id)!.State) };
        Assert.Equal(1, (await senderSync.SyncAsync(senderIdentity, CancellationToken.None)).Published);
        Assert.Contains(AssistantItemState.Stored, sentEvents);
        Assert.Equal(new SharedPublishResult(true, true, false), (await senderStore.LoadAsync(CancellationToken.None)).Find(item.Id)!.SharedStorage);
        Assert.Null(await sender.Public.ReadContentManifestAsync(privateItem.Id, CancellationToken.None));
        var receiverSync = new AssistantSync(receiverStore, receiverDav) { SharedTransport = receiver.Transfer };
        var pulled = await receiverSync.SyncAsync(receiverIdentity, CancellationToken.None);
        Assert.Equal(1, pulled.Downloaded);
        Assert.Equal(1, pulled.ReceiptsWritten);
        Assert.Equal(AssistantItemState.Available, (await receiverStore.LoadAsync(CancellationToken.None)).Find(item.Id)!.State);
        await senderSync.SyncAsync(senderIdentity, CancellationToken.None);
        Assert.Single((await senderStore.LoadAsync(CancellationToken.None)).Find(item.Id)!.Receipts);
        await receiver.Public.RequestPublicCopyAsync(item.Id, "relay-public", "tail-unreachable", CancellationToken.None);
        await _public.RestartAsync();
        await _tail.StopAsync();
        senderStore = new AssistantStore(senderDirectory);
        senderSync = new(senderStore, senderDav) { SharedTransport = Device("restarted").Transfer };
        var result = await senderSync.SyncAsync(senderIdentity, CancellationToken.None);
        Assert.Null(result.RetryAfter);
        var after = (await senderStore.LoadAsync(CancellationToken.None)).Find(item.Id)!;
        Assert.True(after.SharedStorage!.PublicStored);
        Assert.Equal(AssistantItemState.Stored, after.State);
        Assert.Single(after.Receipts);
        Assert.Equal(item.Size, _public.PayloadUploadBytes);
        await senderSync.SyncAsync(senderIdentity, CancellationToken.None);
        Assert.Equal(item.Size, _public.PayloadUploadBytes);
    }

    [Fact]
    public async Task OriginalV1DownloadFailureCreatesAnIdempotentRequestThatTheRestartedSenderServes()
    {
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        await _tail.StopAsync();
        using var oldClient = new OpenListClient(new Uri(_public.Address, PublicRelayClient.DavPath).ToString(), Conversation, Key);
        await Assert.ThrowsAsync<IOException>(() => oldClient.DownloadAssistantAsync(Conversation, message,
            Path.Combine(_root, "old-failed"), null, CancellationToken.None));
        var request = Assert.Single((await sender.Public.ListRequestsAsync(message.Id, CancellationToken.None)).Items);
        Assert.Equal("relay-public", request.DeviceId);
        Assert.Equal("tail-unreachable", request.Reason);
        var revision = await sender.Public.ChangesAsync(null, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => oldClient.DownloadAssistantAsync(Conversation, message,
            Path.Combine(_root, "old-failed"), null, CancellationToken.None));
        Assert.Equal(revision, await sender.Public.ChangesAsync(null, CancellationToken.None));
        Assert.Empty(await sender.Public.ListReceiptsAsync(message.Id, CancellationToken.None));
        await _public.RestartAsync();
        Assert.True(await Device("sender-restarted").Transfer.ServeRequestsAsync(message, "sender-a", payload, CancellationToken.None));
        var saved = await oldClient.DownloadAssistantAsync(Conversation, message, Path.Combine(_root, "old-recovered"), null, CancellationToken.None);
        Assert.Equal(await File.ReadAllBytesAsync(payload), await File.ReadAllBytesAsync(saved));
        Assert.Equal(message.Size, _public.PayloadUploadBytes);
        Assert.Empty(await sender.Public.ListReceiptsAsync(message.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ARequestBeyondThePerPassBudgetIsScannedToCompletionWithoutIdleSpinning()
    {
        var sender = Device("sender");
        using var dav = new OpenListClient(new Uri(_public.Address, PublicRelayClient.DavPath).ToString(), Conversation, Key);
        var store = new AssistantStore(Path.Combine(_root, "bounded-store"));
        var identity = new AssistantIdentity("sender-a", "A", Conversation);
        var (_, payload) = await AttachmentAsync();
        for (var index = 0; index < 3; index++)
            await store.EnqueueAsync(identity, AssistantDraft.ForPaths([payload]), CancellationToken.None);
        await new AssistantSync(store, dav, new(Receipts: 0)) { SharedTransport = sender.Transfer }.SyncAsync(identity, CancellationToken.None);
        var state = await store.LoadAsync(CancellationToken.None);
        var last = state.Items.OrderBy(item => item.SharedRequestCheckedAt).ThenBy(item => item.CreatedAt).Last();
        await sender.Public.RequestPublicCopyAsync(last.Id, "relay-public", "tail-unreachable", CancellationToken.None);
        var bounded = new AssistantSync(store, dav, new(Publish: 1, Receipts: 0)) { SharedTransport = sender.Transfer };
        var result = await bounded.SyncAsync(identity, CancellationToken.None);
        Assert.True(result.HasMore);
        Assert.False(last.SharedStorage!.PublicStored);
        var rounds = 1;
        while (result.HasMore && rounds++ < 10) result = await bounded.SyncAsync(identity, CancellationToken.None);
        Assert.False(result.HasMore);
        Assert.True(last.SharedStorage!.PublicStored);
        Assert.Equal(last.Size, _public.PayloadUploadBytes);
        Assert.False((await bounded.SyncAsync(identity, CancellationToken.None)).HasMore);
        Assert.Equal(last.Size, _public.PayloadUploadBytes);
    }

    [Fact]
    public async Task ARejectedTailNamespaceNeverFallsBackToAPublicPayloadUpload()
    {
        using var conflictingTail = Client(SharedRelayRoute.Tail, key: new string('b', 64));
        await conflictingTail.RegisterAsync(CancellationToken.None);
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        await Assert.ThrowsAsync<PublicRelayAuthException>(() => sender.Transfer.PublishAsync(message, payload, CancellationToken.None));
        Assert.Equal(0, _public.PayloadUploadBytes);
        Assert.Null(await sender.Public.ReadContentManifestAsync(message.Id, CancellationToken.None));
        Assert.Null(await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ARejectedTailCredentialStopsReceiveBeforeAnyPublicPayloadOrCopyRequest()
    {
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        var locator = (await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None))!;
        var receiver = new SharedLocatorTransfer(Client(SharedRelayRoute.Public),
            Client(SharedRelayRoute.Tail, key: new string('b', 64)), Path.Combine(_root, "rejected"));
        await Assert.ThrowsAsync<PublicRelayAuthException>(() => ReceiveAsync(receiver, locator, "b"));
        Assert.Equal(0, _public.PayloadDownloads);
        Assert.Equal(0, _public.PayloadUploadBytes);
        Assert.Empty((await sender.Public.ListRequestsAsync(message.Id, CancellationToken.None)).Items);
        Assert.Empty(await sender.Public.ListReceiptsAsync(message.Id, CancellationToken.None));
    }

    [Fact]
    public async Task AProxyReportedTailAuthRejectionDoesNotCreateAPublicCopyRequest()
    {
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        var locator = (await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None))!;
        _tail.Fail("payload-auth", true);
        var receiver = Device("b", noTail: true);
        await Assert.ThrowsAsync<PublicRelayAuthException>(() => ReceiveAsync(receiver.Transfer, locator, "b"));
        Assert.Equal(0, _public.PayloadUploadBytes);
        Assert.Empty((await sender.Public.ListRequestsAsync(message.Id, CancellationToken.None)).Items);
        Assert.Empty(await sender.Public.ListReceiptsAsync(message.Id, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedLocatorsAndRequestsAreReportedAndConflictingMessagesAreNotMerged()
    {
        var sender = Device("sender");
        var (message, payload) = await AttachmentAsync();
        await sender.Transfer.PublishAsync(message, payload, CancellationToken.None);
        var locator = (await sender.Public.ReadLocatorAsync(message.Id, CancellationToken.None))!;
        var badId = Guid.NewGuid().ToString("N");
        var badPath = _public.ContentPath(Conversation, "assistant-locator", Conversation, badId, "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(badPath)!);
        await File.WriteAllTextAsync(badPath, JsonSerializer.Serialize(locator with { Message = message with { Id = badId }, PayloadRoute = "http://untrusted.example/payload" }, AssistantJson.Options));
        var page = await sender.Public.ListLocatorsAsync(new(), CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Equal(badId, Assert.Single(page.InvalidItemIds));
        var requestPath = _public.ContentPath(Conversation, "assistant-locator", Conversation, message.Id, "requests", "c.json");
        Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new SharedPublicCopyRequest { ItemId = message.Id, DeviceId = "different-device", RequestedAt = DateTimeOffset.UtcNow }, AssistantJson.Options));
        Assert.Equal("c.json", Assert.Single((await sender.Public.ListRequestsAsync(message.Id, CancellationToken.None)).InvalidNames));
        await File.WriteAllTextAsync(_public.ContentPath(Conversation, "assistant", Conversation, message.Id, "manifest.json"),
            JsonSerializer.Serialize(message with { Name = "conflict.bin" }, AssistantJson.Options));
        var receiver = Device("c", noTail: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReceiveAsync(receiver.Transfer, locator, "c"));
        Assert.Empty(await sender.Public.ListReceiptsAsync(message.Id, CancellationToken.None));
    }
}
