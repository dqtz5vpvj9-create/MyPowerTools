using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using FileTransfer.MyPowerTools;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace FileTransfer.Tests;

/// <summary>
/// The assistant conversation end to end over loopback and a simulated WebDAV relay: a send is
/// durable before it is accepted, two devices of one conversation exchange items and receipts, and a
/// first contact only delivers after the receiving device's user accepted it.
/// </summary>
public sealed class AssistantModuleTests : IAsyncLifetime
{
    private const string RelayPassword = "relay-password";
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-assistant-module-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileTransferModule> _modules = [];
    private readonly List<FakeWebDav> _relays = [];

    public AssistantModuleTests()
    {
        Directory.CreateDirectory(_root);
        // A module with no custom relay would otherwise dial the real public endpoint (no network in
        // tests) or, worse, the simulated relay another test class installed. A dead loopback port
        // keeps this class independent of both.
        PublicRelayClient.BaseAddressOverride = () => new Uri("http://127.0.0.1:1/");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var module in _modules) await module.DisposeAsync(CancellationToken.None);
        PublicRelayClient.BaseAddressOverride = null;
        foreach (var relay in _relays) await relay.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string DeviceRoot(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private FakeWebDav Relay()
    {
        var relay = new FakeWebDav();
        _relays.Add(relay);
        return relay;
    }

    private async Task<FileTransferModule> StartAsync(string name, string deviceId, string address, string relayUrl = "",
        JsonArray? peers = null, bool withPassword = true, IDownloadsService? downloads = null)
    {
        var root = DeviceRoot(name);
        var secrets = new InMemorySecretStore();
        if (withPassword) await secrets.SaveAsync("file-transfer", "password", RelayPassword, CancellationToken.None);
        var preferences = new JsonObject
        {
            ["deviceId"] = deviceId,
            ["listenAddress"] = address,
            ["receiveDirectory"] = Path.Combine(root, "inbox"),
            ["peers"] = peers ?? new JsonArray()
        };
        if (relayUrl.Length > 0)
        {
            preferences["webDavUrl"] = relayUrl;
            preferences["username"] = "mpt-relay";
        }
        await File.WriteAllTextAsync(Path.Combine(root, "preferences.json"), preferences.ToJsonString());
        var capabilities = new Dictionary<string, object> { ["secret.store"] = secrets };
        if (downloads is not null) capabilities["files.downloads"] = downloads;
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", root, root, root, "linux",
            ["secret.store", "files.downloads"], capabilities);
        var module = new FileTransferModule();
        _modules.Add(module);
        Assert.True((await module.InitializeAsync(context, CancellationToken.None)).Ok);
        return module;
    }

    private static async Task<JsonObject> CallAsync(FileTransferModule module, string command, JsonObject? args = null)
    {
        var result = await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString("N"), command, args ?? new JsonObject()), CancellationToken.None);
        Assert.True(result.Success, result.Output);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    private static async Task<CommandExecutionResult> TryAsync(FileTransferModule module, string command, JsonObject? args = null) =>
        await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString("N"), command, args ?? new JsonObject()), CancellationToken.None);

    private static JsonArray Items(JsonObject inspect) => inspect["items"]!.AsArray();

    private static async Task<JsonObject> ObserveChangedItemAsync(FileTransferModule module, string reason,
        Func<JsonObject, bool> ready, CancellationToken token, List<MptModuleEvent>? observations = null)
    {
        await foreach (var change in module.SubscribeEventsAsync(new EventCursor(0), token))
        {
            observations?.Add(change);
            if (change.Type != "file-transfer.assistant.changed" || change.Payload["reason"]?.GetValue<string>() != reason) continue;
            // Like the Surface, inspect only after a notification. Polling inspect would conceal a
            // missing event, which was the original last-image-stays-at-zero regression.
            var inspect = await CallAsync(module, "file-transfer.assistant.inspect");
            if (Items(inspect).OfType<JsonObject>().FirstOrDefault(ready) is { } item) return item;
        }
        throw new InvalidOperationException("Event stream ended before the visible transition.");
    }

    [Theory]
    [InlineData(false, "stored")]
    [InlineData(true, "failed")]
    public async Task SharedImagePublishesItsCommittedStateBeforeTheRestOfTheRelayPass(bool reject, string expected)
    {
        var relay = new FakeWebDav(reject ? RelayMode.Unavailable : RelayMode.Normal) { BlockReceiptReads = !reject };
        _relays.Add(relay);
        var sender = await StartAsync("event-image", "event-image", "127.0.0.91", $"http://127.0.0.1:{relay.Port}/dav");
        var path = Path.Combine(_root, "last-image.png");
        await File.WriteAllBytesAsync(path, new byte[4 * 1024 * 1024]);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var observations = new List<MptModuleEvent>();
        var observed = ObserveChangedItemAsync(sender, "transfer.changed",
            item => item["name"]?.GetValue<string>() == "last-image.png" && item["state"]?.GetValue<string>() == expected, timeout.Token, observations);
        try
        {
            var sent = await CallAsync(sender, "file-transfer.assistant.send", new JsonObject { ["paths"] = new JsonArray(path) });
            var item = await observed;
            Assert.Equal(sent["itemIds"]![0]!.GetValue<string>(), item["id"]!.GetValue<string>());
            if (reject) Assert.NotNull(item["error"]);
            else
            {
                Assert.Equal(item["size"]!.GetValue<long>(), item["bytesDone"]!.GetValue<long>());
                Assert.False(relay.Released); // a blocked receipt/list stage cannot hide upload completion
                var progress = observations.Where(e => e.Payload["reason"]?.GetValue<string>() == "transfer.progress").ToArray();
                Assert.NotEmpty(progress);
                for (var i = 1; i < progress.Length; i++)
                    Assert.True(progress[i].Time - progress[i - 1].Time >= TimeSpan.FromMilliseconds(200),
                        "A chunk burst must not become a refresh burst; progress is limited to approximately four events per second.");
            }
        }
        finally { relay.Release(); timeout.Cancel(); }
    }

    /// <summary>The queue worker runs on its own, so a test waits for the state it expects to settle.</summary>
    private static async Task<JsonObject> WaitForItemAsync(FileTransferModule module, string itemId, Func<JsonObject, bool> ready,
        string? detail = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        JsonObject? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            // A peer's item may not have arrived yet, which is simply "not ready" rather than a failure.
            last = Items(await CallAsync(module, "file-transfer.assistant.inspect"))
                .FirstOrDefault(item => item!["id"]!.GetValue<string>() == itemId) as JsonObject;
            if (last is not null && ready(last)) return last;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("条目状态没有稳定：" + (last?.ToJsonString() ?? "(条目尚未出现)") + " 同步结果：" + detail);
    }

    private static JsonObject ItemById(JsonObject inspect, string itemId) =>
        Items(inspect).First(item => item!["id"]!.GetValue<string>() == itemId)!.AsObject();

    [Theory]
    [InlineData("retry")]
    [InlineData("open")]
    public async Task RetryOrOpenOfFailedIncomingAttachmentAutomaticallyDownloadsAndAcknowledgesIt(string action)
    {
        await using var relay = new AssistantWebDavServer(Path.Combine(_root, "incoming-retry-relay"));
        var sender = await StartAsync("retry-sender", "retry-sender", "127.0.0.94", relay.Url);
        var receiver = await StartAsync("retry-receiver", "retry-receiver", "127.0.0.95", relay.Url);
        var link = await CallAsync(sender, "file-transfer.assistant.link.export");
        await CallAsync(receiver, "file-transfer.assistant.link.import", new() { ["code"] = link["code"]!.GetValue<string>() });
        await CallAsync(receiver, "file-transfer.receive.stop"); // No direct accelerator can bypass the injected HTTP failure.
        relay.Intercept = (context, request) =>
        {
            if (request.Method == "GET" && request.Path.EndsWith("/payload", StringComparison.Ordinal))
            { AssistantWebDavServer.Write(context, 503); return true; }
            return false;
        };
        var payload = Path.Combine(_root, "incoming-retry.bin");
        await File.WriteAllTextAsync(payload, "recover exactly this original attachment");
        var sent = await CallAsync(sender, "file-transfer.assistant.send", new() { ["paths"] = new JsonArray(payload) });
        var id = sent["itemIds"]![0]!.GetValue<string>();
        await WaitForItemAsync(sender, id, item => item["state"]!.GetValue<string>() == "stored");
        await CallAsync(receiver, "file-transfer.assistant.sync");
        await WaitForItemAsync(receiver, id, item => item["state"]!.GetValue<string>() == "failed");
        relay.Intercept = null;
        var retry = await CallAsync(receiver, "file-transfer.assistant." + action, new() { ["itemId"] = id });
        if (action == "retry") Assert.True(retry["retried"]!.GetValue<bool>());
        var received = await WaitForItemAsync(receiver, id, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal(await File.ReadAllTextAsync(payload), await File.ReadAllTextAsync(received["localPath"]!.GetValue<string>()));
        using var client = new OpenListClient(relay.Url, AssistantWebDavServer.UserName, AssistantWebDavServer.Password);
        var state = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(DeviceRoot("retry-receiver"), "assistant", "assistant.json")))!;
        var conversation = state["identity"]!["conversationId"]!.GetValue<string>();
        using var receiptDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while ((await client.ListAssistantReceiptsAsync(conversation, id, receiptDeadline.Token)).Count == 0)
            await Task.Delay(25, receiptDeadline.Token);
        Assert.Single(await client.ListAssistantReceiptsAsync(conversation, id, default));
        File.Delete(received["localPath"]!.GetValue<string>());
        var restoredResult = await CallAsync(receiver, "file-transfer.assistant." + action, new() { ["itemId"] = id });
        if (action == "retry") Assert.True(restoredResult["retried"]!.GetValue<bool>());
        var restored = await WaitForItemAsync(receiver, id, row => row["state"]!.GetValue<string>() == "available" &&
            File.Exists(row["localPath"]?.GetValue<string>()));
        Assert.Equal(await File.ReadAllTextAsync(payload), await File.ReadAllTextAsync(restored["localPath"]!.GetValue<string>()));
        Assert.Single(Items(await CallAsync(receiver, "file-transfer.assistant.inspect")), row => row!["id"]!.GetValue<string>() == id);
    }

    [Fact]
    public async Task ASendIsDurableBeforeItIsAcceptedAndSurvivesARestartWithoutARelay()
    {
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.51");
        var file = Path.Combine(_root, "note.txt");
        await File.WriteAllTextAsync(file, "attached");
        // One accepted request may carry text and attachments together; every input is persisted first.
        var sent = await CallAsync(module, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "第一条", ["paths"] = new JsonArray(file) });
        Assert.True(sent["accepted"]!.GetValue<bool>());
        var ids = sent["itemIds"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
        Assert.Equal(2, ids.Length);

        // A request whose attachment cannot be read is refused whole: no half-sent text to duplicate.
        var missing = await TryAsync(module, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "caption", ["paths"] = new JsonArray(Path.Combine(_root, "missing.bin")) });
        Assert.False(missing.Success);
        Assert.Single(Items(await CallAsync(module, "file-transfer.assistant.inspect")),
            item => item!["text"]?.GetValue<string>() == "第一条");

        var inspect = await CallAsync(module, "file-transfer.assistant.inspect");
        Assert.False(inspect["identity"]!["linked"]!.GetValue<bool>());
        Assert.Equal("phone-a", inspect["identity"]!["id"]!.GetValue<string>());
        // Persistence is local: nothing is claimed as delivered, and the default public relay is the
        // transport (unreachable in this sandbox, so an item may honestly be queued or failed).
        Assert.All(Items(inspect), item => Assert.Contains(item!["state"]!.GetValue<string>(), new[] { "queued", "failed" }));
        Assert.DoesNotContain(Items(inspect), item => item!["state"]!.GetValue<string>() is "stored" or "delivered" or "available");
        Assert.True(inspect["relay"]!["configured"]!.GetValue<bool>());
        Assert.True(inspect["relay"]!["public"]!.GetValue<bool>());

        // A restart reloads the same durable rows with their payload copies.
        var root = Path.Combine(_root, "phone-a");
        var secrets = new InMemorySecretStore();
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", root, root, root, "linux",
            ["secret.store"], new Dictionary<string, object> { ["secret.store"] = secrets });
        var restarted = new FileTransferModule();
        _modules.Add(restarted);
        Assert.True((await restarted.InitializeAsync(context, CancellationToken.None)).Ok);
        var after = await CallAsync(restarted, "file-transfer.assistant.inspect");
        var fileItem = ItemById(after, ids[1]);
        Assert.Equal("note.txt", fileItem["name"]!.GetValue<string>());
        Assert.True(File.Exists(fileItem["localPath"]!.GetValue<string>()));
        Assert.Equal("queued", fileItem["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task TwoLinkedDevicesExchangeItemsAndReceiptsThroughTheRelay()
    {
        var relay = Relay();
        var url = $"http://127.0.0.1:{relay.Port}/dav";
        var first = await StartAsync("phone-a", "phone-a", "127.0.0.52", url);
        var second = await StartAsync("phone-b", "phone-b", "127.0.0.53", url);

        var link = await CallAsync(first, "file-transfer.assistant.link.export");
        var code = link["code"]!.GetValue<string>();
        var preview = await CallAsync(second, "file-transfer.assistant.link.preview", new JsonObject { ["code"] = code });
        Assert.Equal("phone-a", preview["deviceId"]!.GetValue<string>());
        Assert.False(preview.ContainsKey("key"));
        var joined = await CallAsync(second, "file-transfer.assistant.link.import", new JsonObject { ["code"] = code });
        Assert.True(joined["joined"]!.GetValue<bool>());
        Assert.True((await CallAsync(second, "file-transfer.assistant.inspect"))["identity"]!["linked"]!.GetValue<bool>());

        var sent = await CallAsync(first, "file-transfer.assistant.send", new JsonObject { ["text"] = "跨设备文本" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        // The explicit sync and the background worker share one implementation, so the durable state is
        // what the test waits for instead of a per-call counter.
        await CallAsync(first, "file-transfer.assistant.sync");
        var stored = await WaitForItemAsync(first, itemId, item => item["state"]!.GetValue<string>() == "stored");
        Assert.Equal("跨设备文本", stored["text"]!.GetValue<string>());

        // The second device pulls the same conversation and can read the text without a download.
        var pull = await TryAsync(second, "file-transfer.assistant.sync");
        Assert.True(pull.Success && pull.Output.Length > 20,
            $"state={pull.State} success={pull.Success} output='{pull.Output}' error={pull.Error?.Message}");
        var received = await WaitForItemAsync(second, itemId, item => item["state"]!.GetValue<string>() == "available",
            pull.Output);
        Assert.Equal("跨设备文本", received["text"]!.GetValue<string>());

        // It then writes its own receipt, and the sender merges it: a real peer confirmation.
        var acknowledge = await TryAsync(second, "file-transfer.assistant.sync");
        Assert.True(acknowledge.Success, acknowledge.Output);
        var onSecond = ItemById(await CallAsync(second, "file-transfer.assistant.inspect"), itemId);
        Assert.True(onSecond["error"] is null, onSecond.ToJsonString());
        await CallAsync(first, "file-transfer.assistant.sync");
        var confirmed = await WaitForItemAsync(first, itemId,
            item => item["receipts"]!.AsArray().Any(receipt => receipt!["deviceId"]!.GetValue<string>() == "phone-b"),
            onSecond.ToJsonString());
        // A self send has no single target, so the relay copy stays "synced" and the receipt is display data.
        Assert.Equal("stored", confirmed["state"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadsPublisherReceivesTheOriginalNameAndKeepsFailedCopies(bool failPublication)
    {
        var publisher = new RecordingDownloads(failPublication);
        var sender = await StartAsync("publish-sender", "publish-sender", "127.0.0.96",
            peers: new JsonArray(new JsonObject { ["deviceId"] = "publish-receiver", ["name"] = "Receiver", ["address"] = "127.0.0.97" }));
        var receiver = await StartAsync("publish-receiver", "publish-receiver", "127.0.0.97", downloads: publisher);
        var source = Path.Combine(_root, "测试 report.pdf");
        const string contents = "original attachment contents";
        await File.WriteAllTextAsync(source, contents);
        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new() { ["paths"] = new JsonArray(source), ["targetDeviceId"] = "publish-receiver" });
        var id = sent["itemIds"]![0]!.GetValue<string>();
        await WaitUntilAsync(async () => (await CallAsync(receiver, "file-transfer.assistant.inspect"))["pendingRequests"]!.AsArray().Count == 1);
        var pending = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["pendingRequests"]![0]!;
        await CallAsync(receiver, "file-transfer.assistant.receive.respond",
            new() { ["requestId"] = pending["requestId"]!.GetValue<string>(), ["accept"] = true });
        await WaitForItemAsync(sender, id, row => row["state"]!.GetValue<string>() == "delivered");
        var received = await WaitForItemAsync(receiver, id, row => row["state"]!.GetValue<string>() == "available");
        var published = await publisher.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("测试 report.pdf", Path.GetFileName(published.Path));
        Assert.Equal(contents, published.Contents);
        Assert.NotEqual(received["localPath"]!.GetValue<string>(), published.Path);
        Assert.Equal(contents, await File.ReadAllTextAsync(received["localPath"]!.GetValue<string>()));
        if (failPublication)
        {
            Assert.Equal(contents, await File.ReadAllTextAsync(published.Path));
            Assert.True(Directory.Exists(Path.GetDirectoryName(published.Path)));
        }
        else
        {
            await WaitUntilAsync(() => Task.FromResult(!Directory.Exists(Path.GetDirectoryName(published.Path))));
            Assert.False(File.Exists(published.Path));
        }
    }

    private sealed class RecordingDownloads(bool fail) : IDownloadsService
    {
        public TaskCompletionSource<(string Path, string Contents)> Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PublishAsync(string path, CancellationToken cancellationToken)
        {
            var contents = await File.ReadAllTextAsync(path, cancellationToken);
            Published.TrySetResult((path, contents));
            if (fail) throw new IOException("Injected downloads publication failure");
            File.Delete(path); // Android consumes only the throwaway copy after MediaStore publication.
        }
    }

    [Fact]
    public async Task AFirstContactFileWaitsForTheReceiverAndRemembersOnlyWhenAsked()
    {
        var sender = await StartAsync("phone-a", "phone-a", "127.0.0.54",
            peers: new JsonArray(new JsonObject { ["deviceId"] = "phone-b", ["name"] = "Phone B", ["address"] = "127.0.0.55" }));
        var receiver = await StartAsync("phone-b", "phone-b", "127.0.0.55");
        var file = Path.Combine(_root, "photo.txt");
        await File.WriteAllTextAsync(file, "first contact payload");

        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["paths"] = new JsonArray(file), ["targetDeviceId"] = "phone-b" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        // The sender's queue holds the connection while the other user decides: nothing is delivered yet.
        await WaitUntilAsync(async () => (await CallAsync(receiver, "file-transfer.assistant.inspect"))["pendingRequests"]!.AsArray().Count == 1);
        Assert.NotEqual("delivered", ItemById(await CallAsync(sender, "file-transfer.assistant.inspect"), itemId)["state"]!.GetValue<string>());

        var pending = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["pendingRequests"]!.AsArray();
        var request = Assert.Single(pending)!.AsObject();
        Assert.Equal("phone-a", request["deviceId"]!.GetValue<string>());
        Assert.Contains("photo.txt", request["itemNames"]!.AsArray().Select(node => node!.GetValue<string>()));
        // The pending projection carries no credential at all.
        Assert.DoesNotContain("conversation", pending.ToJsonString());
        var requestId = request["requestId"]!.GetValue<string>();

        var accepted = await CallAsync(receiver, "file-transfer.assistant.receive.respond",
            new JsonObject { ["requestId"] = requestId, ["accept"] = true, ["remember"] = false });
        Assert.True(accepted["accepted"]!.GetValue<bool>());
        Assert.False(accepted["remembered"]!.GetValue<bool>());
        Assert.Empty((await CallAsync(receiver, "file-transfer.assistant.inspect"))["pendingRequests"]!.AsArray());

        // Accepting delivers on the held connection: no manual sync is needed on either side.
        var confirmed = await WaitForItemAsync(sender, itemId, item => item["state"]!.GetValue<string>() == "delivered");
        Assert.Contains("phone-b", confirmed["receipts"]!.AsArray().Select(node => node!["deviceId"]!.GetValue<string>()));

        var inbound = await WaitForItemAsync(receiver, itemId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("phone-a", inbound["senderDeviceId"]!.GetValue<string>());
        Assert.Equal("device:phone-a", inbound["conversationKey"]!.GetValue<string>());
        Assert.Equal("direct-device", inbound["provenance"]!.GetValue<string>());
        Assert.Equal("device:phone-b", confirmed["conversationKey"]!.GetValue<string>());
        // The durable copy is what the receiver opens, and it is not the file the publisher may delete.
        Assert.True(File.Exists(inbound["localPath"]!.GetValue<string>()));

        // A second first contact is asked again: a one-time acceptance is not a permanent pairing.
        var second = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "第二次", ["targetDeviceId"] = "phone-b" });
        var secondId = second["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await WaitUntilAsync(async () => (await CallAsync(receiver, "file-transfer.assistant.inspect"))["pendingRequests"]!.AsArray().Count == 1);
        Assert.NotEqual("delivered", ItemById(await CallAsync(sender, "file-transfer.assistant.inspect"), secondId)["state"]!.GetValue<string>());
        // Refusing ends the held connection with a real failure, and no trust is kept.
        var refuseId = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["pendingRequests"]!.AsArray()[0]!["requestId"]!.GetValue<string>();
        var refused = await CallAsync(receiver, "file-transfer.assistant.receive.respond",
            new JsonObject { ["requestId"] = refuseId, ["accept"] = false });
        Assert.False(refused["accepted"]!.GetValue<bool>());
        Assert.NotEqual("delivered", ItemById(await CallAsync(sender, "file-transfer.assistant.inspect"), secondId)["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task CancelRetryAndReceiptsOnlyTouchTheSelectedItem()
    {
        var relay = Relay();
        var url = $"http://127.0.0.1:{relay.Port}/dav";
        var module = await StartAsync("phone-a", "phone-a", "100.64.0.56", url);

        var first = await CallAsync(module, "file-transfer.assistant.send", new JsonObject { ["text"] = "一" });
        var keep = first["itemIds"]!.AsArray()[0]!.GetValue<string>();
        var second = await CallAsync(module, "file-transfer.assistant.send", new JsonObject { ["text"] = "二" });
        var drop = second["itemIds"]!.AsArray()[0]!.GetValue<string>();

        var cancelled = await CallAsync(module, "file-transfer.assistant.cancel", new JsonObject { ["itemId"] = drop });
        Assert.True(cancelled["cancelled"]!.GetValue<bool>());
        // Cancelling twice is refused instead of reported as success.
        Assert.False((await CallAsync(module, "file-transfer.assistant.cancel", new JsonObject { ["itemId"] = drop }))["cancelled"]!.GetValue<bool>());
        var inspect = await CallAsync(module, "file-transfer.assistant.inspect");
        Assert.Equal("cancelled", ItemById(inspect, drop)["state"]!.GetValue<string>());
        // The other message keeps going: cancelling never touches a sibling.
        Assert.NotEqual("cancelled", ItemById(inspect, keep)["state"]!.GetValue<string>());

        // A message queued while the relay is gone cannot be published, and retry reuses its id.
        await relay.DisposeAsync();
        var third = await CallAsync(module, "file-transfer.assistant.send", new JsonObject { ["text"] = "三" });
        var failedId = third["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await TryAsync(module, "file-transfer.assistant.sync");
        // The publish attempt has to settle before retry can see a failed item, whichever pass ran it.
        await WaitForItemAsync(module, failedId, item => item["state"]!.GetValue<string>() == "failed");
        Assert.True((await CallAsync(module, "file-transfer.assistant.retry", new JsonObject { ["itemId"] = failedId }))["retried"]!.GetValue<bool>());
        // A queued item is not a failed one, so retry has nothing to do.
        Assert.False((await CallAsync(module, "file-transfer.assistant.retry", new JsonObject { ["itemId"] = drop }))["retried"]!.GetValue<bool>());

        var recovered = Relay();
        var settings = await TryAsync(module, "file-transfer.configure",
            new JsonObject { ["webDavUrl"] = $"http://127.0.0.1:{recovered.Port}/dav" });
        Assert.True(settings.Success, settings.Output);
        await CallAsync(module, "file-transfer.assistant.sync");
        await WaitForItemAsync(module, failedId, item => item["state"]!.GetValue<string>() == "stored");
        var done = await CallAsync(module, "file-transfer.assistant.inspect");
        Assert.Equal("cancelled", ItemById(done, drop)["state"]!.GetValue<string>());
        // The retried message kept its identity instead of becoming a duplicate.
        Assert.Single(Items(done), item => item!["text"]?.GetValue<string>() == "三");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!await ready()) await Task.Delay(50, timeout.Token);
    }

    [Fact]
    public async Task ASelfSendNeverGoesToADeviceThatOnlyPairedWithUs()
    {
        var sender = await StartAsync("phone-a", "phone-a", "127.0.0.61",
            peers: new JsonArray(new JsonObject { ["deviceId"] = "phone-b", ["name"] = "Phone B", ["address"] = "127.0.0.62" }));
        var stranger = await StartAsync("phone-b", "phone-b", "127.0.0.62");
        var sent = await CallAsync(sender, "file-transfer.assistant.send", new JsonObject { ["text"] = "只给我自己" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await CallAsync(sender, "file-transfer.assistant.sync");
        await Task.Delay(300);

        // A file pairing is not conversation membership: the self send must not reach that device.
        var strangerItems = Items(await CallAsync(stranger, "file-transfer.assistant.inspect"));
        Assert.DoesNotContain(strangerItems, item => item!["id"]!.GetValue<string>() == itemId);
        var mine = ItemById(await CallAsync(sender, "file-transfer.assistant.inspect"), itemId);
        Assert.Empty(mine["receipts"]!.AsArray());
        Assert.NotEqual("delivered", mine["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task AJoinedDeviceCarriesTheRelayAndBecomesAnOwnDeviceAfterARealReceipt()
    {
        var relay = Relay();
        var url = $"http://127.0.0.1:{relay.Port}/dav";
        var owner = await StartAsync("phone-a", "phone-a", "127.0.0.64", url);
        // The joining device starts with no relay configuration at all.
        var joiner = await StartAsync("phone-b", "phone-b", "127.0.0.65", withPassword: false);

        var link = await CallAsync(owner, "file-transfer.assistant.link.export");
        Assert.True(link["relayIncluded"]!.GetValue<bool>());
        // Showing a code is not a connection: the owner still reports "waiting", not linked.
        Assert.False(link["linked"]!.GetValue<bool>());
        var joined = await CallAsync(joiner, "file-transfer.assistant.link.import",
            new JsonObject { ["code"] = link["code"]!.GetValue<string>() });
        Assert.True(joined["joined"]!.GetValue<bool>());
        Assert.True(joined["relayImported"]!.GetValue<bool>());

        // One scan was enough: the joiner can sync offline through the imported relay.
        var sent = await CallAsync(owner, "file-transfer.assistant.send", new JsonObject { ["text"] = "一次加入" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await CallAsync(owner, "file-transfer.assistant.sync");
        await CallAsync(joiner, "file-transfer.assistant.sync");
        var received = await WaitForItemAsync(joiner, itemId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("一次加入", received["text"]!.GetValue<string>());
        // The joiner writes its receipt, and that real receipt makes it an own device on the owner.
        await CallAsync(joiner, "file-transfer.assistant.sync");
        await CallAsync(owner, "file-transfer.assistant.sync");
        var ownerItem = await WaitForItemAsync(owner, itemId,
            item => item["receipts"]!.AsArray().Any(receipt => receipt!["deviceId"]!.GetValue<string>() == "phone-b"));
        Assert.Equal("stored", ownerItem["state"]!.GetValue<string>());
        var identity = (await CallAsync(owner, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
        Assert.True(identity["linked"]!.GetValue<bool>());
        Assert.True(identity["ownDevices"]!.GetValue<int>() >= 1);
    }

    [Fact]
    public async Task ARelayFailureDoesNotBlockAReachableDirectDevice()
    {
        var relay = Relay();
        var url = $"http://127.0.0.1:{relay.Port}/dav";
        var first = await StartAsync("phone-a", "phone-a", "127.0.0.66", url);
        var second = await StartAsync("phone-b", "phone-b", "127.0.0.67", withPassword: false);
        var link = await CallAsync(first, "file-transfer.assistant.link.export");
        await CallAsync(second, "file-transfer.assistant.link.import", new JsonObject { ["code"] = link["code"]!.GetValue<string>() });

        // The joiner sends first: that authenticated message makes it an own device on the first phone.
        var hello = await CallAsync(second, "file-transfer.assistant.send", new JsonObject { ["text"] = "我加入了" });
        var helloId = hello["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await CallAsync(second, "file-transfer.assistant.sync");
        await WaitForItemAsync(first, helloId, item => item["state"]!.GetValue<string>() == "available");

        // The relay is gone, but the direct path to a reachable own device still works.
        await relay.DisposeAsync();
        var sent = await CallAsync(first, "file-transfer.assistant.send", new JsonObject { ["text"] = "直传也要通" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        // The authenticated message from the joiner made it an own device of the first phone.
        var membership = (await CallAsync(first, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
        Assert.True(membership["ownDevices"]!.GetValue<int>() >= 1, membership.ToJsonString());
        var memberList = (await CallAsync(first, "file-transfer.assistant.devices"))["devices"]!.AsArray();
        var member = memberList.Single(item => item!["deviceId"]!.GetValue<string>() == "phone-b")!.AsObject();
        Assert.True(member["own"]!.GetValue<bool>(), memberList.ToJsonString());
        Assert.Equal("127.0.0.67", member["address"]!.GetValue<string>());
        var sync = await CallAsync(first, "file-transfer.assistant.sync");
        Assert.NotEqual("", sync["sync"]!["message"]!.GetValue<string>());
        // The relay is broken, yet the reachable own device acknowledged the content: a real receipt.
        var confirmed = await WaitForItemAsync(first, itemId,
            item => item["receipts"]!.AsArray().Any(receipt => receipt!["deviceId"]!.GetValue<string>() == "phone-b"),
            membership + " " + sync["sync"]!.ToJsonString());
        Assert.Contains("phone-b", confirmed["receipts"]!.AsArray().Select(node => node!["deviceId"]!.GetValue<string>()));
        var received = await WaitForItemAsync(second, itemId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("直传也要通", received["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ABlockedRelayDoesNotDelayAReachableOwnDevice()
    {
        var relay = new FakeWebDav(RelayMode.Block, TimeSpan.FromSeconds(40));
        _relays.Add(relay);
        var url = $"http://127.0.0.1:{relay.Port}/dav";
        var owner = await StartAsync("phone-a", "phone-a", "127.0.0.73", url);
        var member = await StartAsync("phone-b", "phone-b", "127.0.0.74", withPassword: false);
        var link = await CallAsync(owner, "file-transfer.assistant.link.export");
        await CallAsync(member, "file-transfer.assistant.link.import", new JsonObject { ["code"] = link["code"]!.GetValue<string>() });
        // The joiner's first authenticated message makes it an own device on the owner; that message
        // travels over the direct channel even though the relay leg of both passes is blocked.
        var hello = await CallAsync(member, "file-transfer.assistant.send", new JsonObject { ["text"] = "我加入了" });
        var helloId = hello["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await WaitForItemAsync(owner, helloId, item => item["state"]!.GetValue<string>() == "available");

        using var eventTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var receiptEvent = ObserveChangedItemAsync(owner, "direct.receipt",
            item => item["text"]?.GetValue<string>() == "直传不等中转" && item["receipts"]!.AsArray().Count > 0,
            eventTimeout.Token);
        var sent = await CallAsync(owner, "file-transfer.assistant.send", new JsonObject { ["text"] = "直传不等中转" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        // The durable relay copy is stuck inside the barrier, yet the reachable own device receives now.
        var received = await WaitForItemAsync(member, itemId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("直传不等中转", received["text"]!.GetValue<string>());
        Assert.True(relay.RequestsSeen > 0, "relay leg was never attempted");
        Assert.False(relay.Released, "the barrier must still be closed when the direct delivery lands");
        var notified = await receiptEvent;
        Assert.Equal(itemId, notified["id"]!.GetValue<string>());
        relay.Release();
    }

    [Fact]
    public async Task CancellingARelayUploadStopsQuicklyAndLeavesSiblingsAlone()
    {
        var relay = new FakeWebDav(RelayMode.Block, TimeSpan.FromSeconds(40));
        _relays.Add(relay);
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.75", $"http://127.0.0.1:{relay.Port}/dav");
        var big = Path.Combine(_root, "relay-big.bin");
        await File.WriteAllBytesAsync(big, new byte[4 * 1024 * 1024]);
        var uploading = await CallAsync(module, "file-transfer.assistant.send", new JsonObject { ["paths"] = new JsonArray(big) });
        var uploadingId = uploading["itemIds"]!.AsArray()[0]!.GetValue<string>();
        var sibling = await CallAsync(module, "file-transfer.assistant.send", new JsonObject { ["text"] = "兄弟条目" });
        var siblingId = sibling["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await WaitUntilAsync(async () => relay.RequestsSeen > 0);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = await CallAsync(module, "file-transfer.assistant.cancel", new JsonObject { ["itemId"] = uploadingId });
        started.Stop();
        Assert.True(cancelled["cancelled"]!.GetValue<bool>(), cancelled.ToJsonString());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"取消耗时 {started.Elapsed}");
        Assert.Equal("cancelled", ItemById(await CallAsync(module, "file-transfer.assistant.inspect"), uploadingId)["state"]!.GetValue<string>());

        relay.Release();
        await Task.Delay(500);
        var after = await CallAsync(module, "file-transfer.assistant.inspect");
        // The cancelled upload stays cancelled, and the sibling item still runs to its own outcome.
        Assert.Equal("cancelled", ItemById(after, uploadingId)["state"]!.GetValue<string>());
        Assert.NotEqual("cancelled", ItemById(after, siblingId)["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARelayServerErrorIsNotReportedAsReachable()
    {
        var relay = new FakeWebDav(RelayMode.Unavailable);
        _relays.Add(relay);
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.76", $"http://127.0.0.1:{relay.Port}/dav");
        await CallAsync(module, "file-transfer.assistant.send", new JsonObject { ["text"] = "会被中转拒绝" });
        var sync = await CallAsync(module, "file-transfer.assistant.sync");
        Assert.NotEqual("", sync["sync"]!["message"]!.GetValue<string>());
        var relayState = (await CallAsync(module, "file-transfer.assistant.inspect"))["relay"]!.AsObject();
        // A relay that answered 503 is not healthy, so it must not be recorded as available.
        Assert.Equal("unavailable", relayState["state"]!.GetValue<string>());
        Assert.NotEqual("", relayState["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task SyncReturnsTheInspectShapeAndCancelInterruptsASlowUpload()
    {
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.68",
            peers: new JsonArray(new JsonObject { ["deviceId"] = "phone-slow", ["name"] = "Slow", ["address"] = "127.0.0.69" }));
        var answer = await CallAsync(module, "file-transfer.assistant.sync");
        // The frozen inspect shape, plus the sync statistics; receiving stays a bool.
        Assert.True(answer.ContainsKey("identity"));
        Assert.True(answer.ContainsKey("items"));
        Assert.True(answer.ContainsKey("pendingRequests"));
        Assert.True(answer.ContainsKey("relay"));
        Assert.True(answer["receiving"] is JsonValue value && value.TryGetValue<bool>(out _));
        Assert.True(answer.ContainsKey("receivingDetails"));
        Assert.True(answer.ContainsKey("sync"));

        // A receiver that never acknowledges must not keep the cancel waiting: the item aborts.
        await using var slow = new StallingReceiver("127.0.0.69");
        var big = Path.Combine(_root, "big.bin");
        await File.WriteAllBytesAsync(big, new byte[8 * 1024 * 1024]);
        var sent = await CallAsync(module, "file-transfer.assistant.send",
            new JsonObject { ["paths"] = new JsonArray(big), ["targetDeviceId"] = "phone-slow" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await WaitUntilAsync(async () => slow.Connected);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var cancelled = await CallAsync(module, "file-transfer.assistant.cancel", new JsonObject { ["itemId"] = itemId });
        started.Stop();
        Assert.True(cancelled["cancelled"]!.GetValue<bool>(), cancelled.ToJsonString());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"取消耗时 {started.Elapsed}");
        Assert.Equal("cancelled", ItemById(await CallAsync(module, "file-transfer.assistant.inspect"), itemId)["state"]!.GetValue<string>());
        await Task.Delay(500);
        Assert.Equal("cancelled", ItemById(await CallAsync(module, "file-transfer.assistant.inspect"), itemId)["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task AZeroByteAttachmentIsDelivered()
    {
        var sender = await StartAsync("phone-a", "phone-a", "127.0.0.70",
            peers: new JsonArray(new JsonObject { ["deviceId"] = "phone-b", ["name"] = "Phone B", ["address"] = "127.0.0.71" }));
        var receiver = await StartAsync("phone-b", "phone-b", "127.0.0.71");
        var empty = Path.Combine(_root, "empty.txt");
        await File.WriteAllBytesAsync(empty, []);
        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["paths"] = new JsonArray(empty), ["targetDeviceId"] = "phone-b" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await WaitUntilAsync(async () => (await CallAsync(receiver, "file-transfer.assistant.inspect"))["pendingRequests"]!.AsArray().Count == 1);
        var requestId = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["pendingRequests"]!.AsArray()[0]!["requestId"]!.GetValue<string>();
        await CallAsync(receiver, "file-transfer.assistant.receive.respond", new JsonObject { ["requestId"] = requestId, ["accept"] = true });
        await WaitForItemAsync(sender, itemId, item => item["state"]!.GetValue<string>() == "delivered");
        var inbound = await WaitForItemAsync(receiver, itemId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal(0, inbound["size"]!.GetValue<long>());
        Assert.True(File.Exists(inbound["localPath"]!.GetValue<string>()));
    }

    [Fact]
    public async Task TheMulticastLeaseLastsForTheWindowAndIsReleasedWhenReceivingStops()
    {
        var platform = new CountingMulticast();
        MobileWifiMulticast.Current = platform;
        try
        {
            var module = await StartAsync("phone-a", "phone-a", "127.0.0.72");
            // The platform lock is process-wide, so the assertions stay delta based: this module must
            // take a lease for its discovery window and must release leases when receiving stops.
            var takenBefore = platform.Taken;
            var releasedBefore = platform.Released;
            await CallAsync(module, "file-transfer.assistant.devices");
            Assert.True(platform.Taken > takenBefore, platform.Describe());

            // Stopping the receiver releases the wake source instead of leaving a lease behind.
            await CallAsync(module, "file-transfer.receive.stop");
            Assert.True(platform.Released > releasedBefore, platform.Describe());
            var details = (await CallAsync(module, "file-transfer.assistant.inspect"))["receivingDetails"]!.AsObject();
            Assert.False(details["discoverable"]!.GetValue<bool>());
        }
        finally { MobileWifiMulticast.Current = null; }
    }

    private sealed class CountingMulticast : IMobileWifiMulticast
    {
        private readonly object _gate = new();
        private int _outstanding;
        public int Taken { get; private set; }
        public int Released { get; private set; }
        public int Outstanding { get { lock (_gate) return _outstanding; } }
        public string Describe() => $"taken={Taken} released={Released} outstanding={Outstanding}";

        public IDisposable Acquire(string reason)
        {
            lock (_gate) { _outstanding++; Taken++; }
            return new Lease(this);
        }

        private sealed class Lease(CountingMulticast owner) : IDisposable
        {
            private int _done;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 1) return;
                lock (owner._gate) { owner._outstanding--; owner.Released++; }
            }
        }
    }

    [Fact]
    public async Task TheDeviceListReportsKnownDevicesWithoutProbingThem()
    {
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.57",
            peers: new JsonArray(new JsonObject { ["deviceId"] = "phone-b", ["name"] = "Phone B", ["address"] = "127.0.0.58" }));
        var devices = await CallAsync(module, "file-transfer.assistant.devices");
        Assert.True(devices.ContainsKey("devices"));
        Assert.True(devices.ContainsKey("discoveryState"));
        var known = devices["devices"]!.AsArray().Single(item => item!["deviceId"]!.GetValue<string>() == "phone-b")!.AsObject();
        Assert.True(known["paired"]!.GetValue<bool>());
        // Nothing answered on that address, so it must not be offered as available.
        Assert.False(known["available"]!.GetValue<bool>());
    }

    /// <summary>A receiver that reads the frame, admits it and then never acknowledges or drains.</summary>
    private sealed class StallingReceiver : IAsyncDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _loop;
        private int _connected;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public bool Connected => Volatile.Read(ref _connected) == 1;

        /// <summary>The peer address in the test points at this listener's transfer port.</summary>
        public StallingReceiver(string address)
        {
            _listener = new System.Net.Sockets.TcpListener(IPAddress.Parse(address), TransferFiles.Port);
            _listener.Start();
            _loop = RunAsync();
        }

        private async Task RunAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                System.Net.Sockets.TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_lifetime.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                Interlocked.Exchange(ref _connected, 1);
                _ = Task.Run(async () =>
                {
                    // Read the frame, admit it, then stop reading so the sender blocks on the payload.
                    try
                    {
                        var stream = client.GetStream();
                        await DirectTransfer.ReadJsonAsync<AssistantWire.Frame>(stream, _lifetime.Token);
                        await DirectTransfer.WriteJsonAsync(stream, new AssistantWire.ItemReply(true, "ready"), _lifetime.Token);
                        await Task.Delay(Timeout.Infinite, _lifetime.Token);
                    }
                    catch (Exception) { }
                }, _lifetime.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch (Exception) { }
        }
    }

    /// <summary>
    /// A small stateful WebDAV endpoint: it stores PUT bodies, answers PROPFIND with the children of a
    /// collection and serves GET. That is exactly the surface the assistant namespace uses.
    /// </summary>
    private enum RelayMode
    {
        Normal,
        /// <summary>Reads every request and then waits for <see cref="FakeWebDav.Release"/> before answering.</summary>
        Block,
        /// <summary>Answers 503 to every request, which is a relay that answered but is not healthy.</summary>
        Unavailable
    }

    private sealed class FakeWebDav : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _collections = new(StringComparer.Ordinal) { "/" };
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RelayMode _mode;
        private readonly TimeSpan _blockTimeout;
        private readonly Task _loop;
        private int _requests;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int RequestsSeen => Volatile.Read(ref _requests);
        public bool Released => _release.Task.IsCompleted;
        public bool BlockReceiptReads { get; init; }

        public FakeWebDav(RelayMode mode = RelayMode.Normal, TimeSpan? blockTimeout = null)
        {
            _mode = mode;
            _blockTimeout = blockTimeout ?? TimeSpan.FromSeconds(30);
            _listener.Start();
            _loop = RunAsync();
        }

        /// <summary>Opens the barrier a blocked relay is waiting on.</summary>
        public void Release() => _release.TrySetResult();

        private async Task RunAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_lifetime.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var header = new StringBuilder();
                    var buffer = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(buffer, _lifetime.Token) == 0) return;
                        header.Append((char)buffer[0]);
                    }
                    var request = header.ToString();
                    var lines = request.Split("\r\n");
                    var parts = lines[0].Split(' ');
                    var method = parts[0];
                    var path = parts.Length > 1 ? parts[1] : "/";
                    var length = 0;
                    foreach (var line in lines)
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out length);
                    var body = new byte[length];
                    var read = 0;
                    while (read < length)
                    {
                        var count = await stream.ReadAsync(body.AsMemory(read, length - read), _lifetime.Token);
                        if (count == 0) break;
                        read += count;
                    }
                    Interlocked.Increment(ref _requests);
                    if (_mode == RelayMode.Block || (BlockReceiptReads && method == "PROPFIND" && path.TrimEnd('/').EndsWith("/receipts", StringComparison.Ordinal)))
                    {
                        try { await _release.Task.WaitAsync(_blockTimeout, _lifetime.Token); }
                        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { return; }
                    }
                    var response = Respond(method, path, body);
                    await stream.WriteAsync(response, _lifetime.Token);
                    await stream.FlushAsync(_lifetime.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
        }

        private byte[] Respond(string method, string path, byte[] body)
        {
            if (_mode == RelayMode.Unavailable) return Status("503 Service Unavailable");
            var normalized = Uri.UnescapeDataString(path).TrimEnd('/');
            switch (method)
            {
                case "MKCOL":
                    _collections.Add(normalized);
                    return Status("201 Created");
                case "PUT":
                    _files[normalized] = body;
                    return Status("201 Created");
                case "GET":
                    return _files.TryGetValue(normalized, out var content) ? Body(content) : Status("404 Not Found");
                case "PROPFIND":
                    return Body(Encoding.UTF8.GetBytes(MultiStatus(normalized)));
                default:
                    return Status("200 OK");
            }
        }

        private string MultiStatus(string path)
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
            foreach (var file in _files.Keys.Where(key => Parent(key) == path))
                builder.Append($"<D:response><D:href>{file}</D:href><D:propstat><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
            foreach (var collection in _collections.Where(key => key.Length > 0 && Parent(key) == path))
                builder.Append($"<D:response><D:href>{collection}</D:href><D:propstat><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
            builder.Append("</D:multistatus>");
            return builder.ToString();
        }

        private static string Parent(string path)
        {
            var index = path.LastIndexOf('/');
            return index <= 0 ? "" : path[..index];
        }

        private static byte[] Status(string status) =>
            Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        private static byte[] Body(byte[] content) =>
            [.. Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), .. content];

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch (Exception) { }
        }
    }
}
