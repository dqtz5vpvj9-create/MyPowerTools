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
/// Ordinary device pairing over the public relay's deposit inbox (PROTOCOL.md §6). A pair code carries
/// only file-delivery permission; the receiving device keeps its owner key and its conversation key, and
/// the sender's entry only becomes delivered after the receiver really saved the payload and wrote a receipt.
/// </summary>
[CollectionDefinition(InboxCollection.Name, DisableParallelization = true)]
public sealed class InboxCollection
{
    public const string Name = "public-inbox";
}

[Collection(InboxCollection.Name)]
public sealed class PublicInboxPairingTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-inbox-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileTransferModule> _modules = [];
    private readonly List<FakeInboxRelay> _relays = [];

    public PublicInboxPairingTests()
    {
        Directory.CreateDirectory(_root);
        TransferFiles.LocalAddressesOverride = () => [];
    }

    public async ValueTask DisposeAsync()
    {
        PublicRelayClient.BaseAddressOverride = null;
        TransferFiles.LocalAddressesOverride = null;
        foreach (var module in _modules) await module.DisposeAsync(CancellationToken.None);
        foreach (var relay in _relays) await relay.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task<FileTransferModule> StartAsync(string name, string deviceId)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        var preferences = new JsonObject
        {
            ["deviceId"] = deviceId,
            ["listenAddress"] = "",
            ["receiveDirectory"] = Path.Combine(root, "inbox"),
            ["peers"] = new JsonArray()
        };
        await File.WriteAllTextAsync(Path.Combine(root, "preferences.json"), preferences.ToJsonString());
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", root, root, root, "linux",
            ["secret.store"], new Dictionary<string, object> { ["secret.store"] = new InMemorySecretStore() });
        var module = new FileTransferModule();
        _modules.Add(module);
        Assert.True((await module.InitializeAsync(context, CancellationToken.None)).Ok);
        return module;
    }

    private FakeInboxRelay Relay()
    {
        var relay = new FakeInboxRelay();
        _relays.Add(relay);
        PublicRelayClient.BaseAddressOverride = () => relay.BaseAddress;
        return relay;
    }

    private static async Task<JsonObject> CallAsync(FileTransferModule module, string command, JsonObject? args = null)
    {
        var result = await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString("N"), command, args ?? new JsonObject()), CancellationToken.None);
        Assert.True(result.Success, result.Output);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    private static JsonArray Items(JsonObject inspect) => inspect["items"]!.AsArray();

    /// <summary>A compact per-item summary for a failing assertion, without dumping whole payloads.</summary>
    private static string Summary(JsonObject inspect) => string.Join("; ", Items(inspect).Select(item =>
        $"{item!["kind"]!.GetValue<string>()}:{item["state"]!.GetValue<string>()}:{item["error"]?.GetValue<string>() ?? ""}"));

    private static async Task<JsonObject> WaitForItemAsync(FileTransferModule module, string itemId, Func<JsonObject, bool> ready,
        string? detail = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        JsonObject? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = Items(await CallAsync(module, "file-transfer.assistant.inspect"))
                .FirstOrDefault(item => item!["id"]!.GetValue<string>() == itemId) as JsonObject;
            if (last is not null && ready(last)) return last;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("条目状态没有稳定：" + (last?.ToJsonString() ?? "(条目尚未出现)") + " " + detail);
    }

    [Fact]
    public async Task TheDepositClientPutsAnItemAndReadsItsReceiptBack()
    {
        var relay = Relay();
        var pairing = new PublicInboxPairing("inbox-" + new string('f', 32), new string('a', 64));
        var identity = new PublicInboxIdentity(pairing.InboxId, new string('b', 64), pairing.DepositKey);
        using (var owner = PublicInboxClient.Owner(identity)) await owner.RegisterAsync();
        using (var sender = PublicInboxClient.Deposit(pairing))
        {
            var itemId = PublicInboxIds.NewItemId();
            var deposit = await sender.DepositTextAsync(itemId, "直接投递", "laptop-a", "电脑");
            Assert.Equal(itemId, deposit.ItemId);
            Assert.False(deposit.Duplicate);
            var receipt = await sender.GetReceiptAsync(itemId);
            Assert.False(receipt.Saved);
        }
        // The owner sees exactly one pending item and can save + acknowledge it.
        using (var owner = PublicInboxClient.Owner(identity))
        {
            var page = await owner.PollAsync();
            var item = Assert.Single(page.Items);
            using var buffer = new MemoryStream();
            var payload = await owner.DownloadAsync(item.ItemId, buffer);
            Assert.Equal("直接投递", Encoding.UTF8.GetString(buffer.ToArray()));
            var ack = await owner.AcknowledgeAsync(item.ItemId, payload.Bytes, deviceId: "phone-b", deviceName: "手机");
            Assert.True(ack.Saved);
        }
        using (var sender = PublicInboxClient.Deposit(pairing))
        {
            // An id that was never deposited is unknown to the relay: the client reports 404, not "not saved".
            await Assert.ThrowsAsync<PublicInboxNotFoundException>(
                () => sender.GetReceiptAsync(PublicInboxIds.NewItemId()));
        }
    }

    // ---- the real path: a paired device receives a file and a text over the public relay ----------

    [Fact]
    public async Task APairedDeviceReceivesAFileAndTextThroughItsDepositInbox()
    {
        var relay = Relay();
        var sender = await StartAsync("laptop", "laptop-a");
        var receiver = await StartAsync("phone", "phone-b");

        // The receiver's code is the ordinary stable pair code: identity plus an optional deposit inbox.
        var pairing = await CallAsync(receiver, "file-transfer.pairing");
        var code = pairing["code"]!.GetValue<string>();
        Assert.StartsWith(Pairing.Prefix, code);
        Assert.StartsWith("inbox-", pairing["inboxId"]!.GetValue<string>());
        // The exported inbox is the very inbox this device receives on; a mismatch would send files nowhere.
        var receiverInbox = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.AsObject();
        Assert.Equal(receiverInbox["id"]!.GetValue<string>(), pairing["inboxId"]!.GetValue<string>());
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = code });

        var file = Path.Combine(_root, "report.txt");
        await File.WriteAllTextAsync(file, "inbox payload bytes");
        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["paths"] = new JsonArray(file), ["targetDeviceId"] = "phone-b" });
        var fileId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        var text = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "投递一句话", ["targetDeviceId"] = "phone-b" });
        var textId = text["itemIds"]!.AsArray()[0]!.GetValue<string>();

        // The receiver really saves the file, and the sender confirms from the real receipt only.
        var received = await WaitForItemAsync(receiver, fileId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("report.txt", received["name"]!.GetValue<string>());
        var saved = received["localPath"]!.GetValue<string>();
        Assert.True(File.Exists(saved), saved);
        Assert.Equal("inbox payload bytes", await File.ReadAllTextAsync(saved));
        var receivedText = await WaitForItemAsync(receiver, textId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("投递一句话", receivedText["text"]!.GetValue<string>());

        var delivered = await WaitForItemAsync(sender, fileId, item => item["state"]!.GetValue<string>() == "delivered");
        var receipt = delivered["receipts"]!.AsArray().Single()!.AsObject();
        Assert.Equal("phone-b", receipt["deviceId"]!.GetValue<string>());
        Assert.True(receipt["bytes"]!.GetValue<long>() > 0);
        var deliveredText = await WaitForItemAsync(sender, textId, item => item["state"]!.GetValue<string>() == "delivered");
        Assert.Equal("phone-b", deliveredText["receipts"]!.AsArray()[0]!["deviceId"]!.GetValue<string>());

        // The item is not deleted on acknowledge: the sender could still read its receipt.
        Assert.True(relay.HasItem(receiverInbox["id"]!.GetValue<string>(), fileId), "回执后条目不应被删除，否则发送端读不到回执");
        Assert.True(relay.ReceiptWrites >= 2, "收件端必须真的写过回执");
    }

    [Fact]
    public async Task ThePairingCodeCarriesOnlyFileDeliveryPermission()
    {
        Relay();
        var sender = await StartAsync("laptop", "laptop-a");
        var receiver = await StartAsync("phone", "phone-b");
        var pairing = await CallAsync(receiver, "file-transfer.pairing");
        var code = pairing["code"]!.GetValue<string>();
        var owner = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
        var conversationCode = (await CallAsync(receiver, "file-transfer.assistant.link.export"))["code"]!.GetValue<string>();

        // The deposit key is in the code; the owner key and the conversation credentials are not.
        var decoded = Pairing.Decode(code);
        Assert.NotNull(decoded.Inbox);
        var depositKey = decoded.Inbox!.DepositKey;
        Assert.Equal(64, depositKey.Length);
        Assert.DoesNotContain(depositKey, conversationCode);
        var conversation = LinkCode.Decode(conversationCode);
        Assert.DoesNotContain(conversation.Key, code);
        Assert.DoesNotContain(conversation.ConversationId, code);
        var inboxState = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.AsObject();
        Assert.True(inboxState["configured"]!.GetValue<bool>());
        Assert.DoesNotContain(depositKey, inboxState.ToJsonString());
        Assert.Equal(0, owner["ownDevices"]!.GetValue<int>());

        // Preview shows the inbox id but never the deposit key.
        var preview = await CallAsync(sender, "file-transfer.pair.preview", new JsonObject { ["code"] = code });
        Assert.True(preview["hasInbox"]!.GetValue<bool>());
        Assert.DoesNotContain(depositKey, preview.ToJsonString());

        // Importing gives file permission only: the sender does not join the receiver's conversation.
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = code });
        var senderIdentity = (await CallAsync(sender, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
        Assert.Equal(0, senderIdentity["ownDevices"]!.GetValue<int>());
        Assert.False(senderIdentity["linked"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnOldPairingCodeWithoutAnInboxSaysToScanAgainAndNeverFakesDelivery()
    {
        var relay = Relay();
        var sender = await StartAsync("laptop", "laptop-a");
        // A legacy code: identity only, no deposit permission.
        var legacy = new Pairing("phone-b", "旧手机", "", "legacy-token-0123456789abcdef").Encode();
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = legacy });

        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "旧码不能公网投递", ["targetDeviceId"] = "phone-b" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        var item = await WaitForItemAsync(sender, itemId, row => row["error"] is JsonValue value && value.GetValue<string>().Length > 0);
        Assert.Contains("重新扫码", item["error"]!.GetValue<string>());
        Assert.DoesNotContain(item["state"]!.GetValue<string>(), new[] { "stored", "delivered", "available" });
        Assert.Equal(0, relay.Deposits);
    }

    [Fact]
    public async Task AnUnregisteredInboxIsRetriedUntilItIsReady()
    {
        var relay = Relay();
        var sender = await StartAsync("laptop", "laptop-a");
        var receiver = await StartAsync("phone", "phone-b");
        // The relay has not seen this inbox yet, so a deposit answers 503 until the owner registers.
        // That is the real "收件端还没注册" state, not a wrong key.
        relay.NotReady = true;
        var pairing = await CallAsync(receiver, "file-transfer.pairing");
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = pairing["code"]!.GetValue<string>() });
        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "等收件端上线", ["targetDeviceId"] = "phone-b" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        var waiting = await WaitForItemAsync(sender, itemId, row => row["state"]!.GetValue<string>() == "queued" && row["error"] is JsonValue,
            "item=" + Summary(await CallAsync(sender, "file-transfer.assistant.inspect")));
        Assert.True(waiting["error"]!.GetValue<string>().Length > 0, "503 必须留下可重试的真实原因");
        Assert.DoesNotContain(waiting["state"]!.GetValue<string>(), new[] { "stored", "delivered" });

        // Once the relay is ready the owner's own retry registers it, and the sender's next pass deposits
        // and confirms without anyone calling a retry command.
        relay.NotReady = false;
        var stored = await WaitForItemAsync(sender, itemId, row => row["state"]!.GetValue<string>() is "stored" or "delivered");
        Assert.Contains(stored["state"]!.GetValue<string>(), new[] { "stored", "delivered" });
        var received = await WaitForItemAsync(receiver, itemId, row => row["state"]!.GetValue<string>() == "available");
        Assert.Equal("等收件端上线", received["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task DisablingReceivingStopsTheInboxLongPollAndDisposeDoesNotHang()
    {
        var relay = Relay();
        var receiver = await StartAsync("phone", "phone-b");
        var receiverInbox = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.AsObject();
        var inboxId = receiverInbox["id"]!.GetValue<string>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (relay.PollsFor(inboxId) == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        var inboxState = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.AsObject();
        Assert.True(relay.PollsFor(inboxId) > 0, "收件端没有进入 owner 长轮询：" + inboxState.ToJsonString());

        await CallAsync(receiver, "file-transfer.receive.stop");
        // A request already on the wire when the stop happened is not a new poll: let it land before
        // measuring what the disabled receiver issues afterwards.
        await Task.Delay(300);
        var held = relay.PollsFor(inboxId);
        await Task.Delay(700);
        Assert.True(relay.PollsFor(inboxId) - held <= 0, $"禁用后仍在轮询：{relay.PollsFor(inboxId) - held}");

        var started = await CallAsync(receiver, "file-transfer.receive.start");
        Assert.True(started["receiving"]!.GetValue<bool>());
        var resumed = DateTimeOffset.UtcNow.AddSeconds(20);
        while (relay.PollsFor(inboxId) <= held && DateTimeOffset.UtcNow < resumed) await Task.Delay(50);
        Assert.True(relay.PollsFor(inboxId) > held);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await receiver.DisposeAsync(CancellationToken.None);
        watch.Stop();
        _modules.Remove(receiver);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Dispose 耗时 {watch.Elapsed}");
    }

    /// <summary>
    /// An ordinary paired device and a third device in the conversation must never be confused:
    /// what arrives through the deposit inbox (and its receipt) is stored locally, but it is not proof
    /// of conversation membership, so ownDevices must not grow and a later self send must not go direct.
    /// </summary>
    [Fact]
    public async Task APairedDeviceNeverBecomesAConversationMember()
    {
        var relay = Relay();
        var sender = await StartAsync("laptop", "laptop-a");
        var receiver = await StartAsync("phone", "phone-b");
        var third = await StartAsync("tablet", "tablet-c");

        // The receiver and the third device share one conversation; the laptop is only paired.
        var conversation = await CallAsync(third, "file-transfer.assistant.link.export");
        await CallAsync(receiver, "file-transfer.assistant.link.import", new JsonObject { ["code"] = conversation["code"]!.GetValue<string>() });
        var before = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
        var ownBefore = before["ownDevices"]!.GetValue<int>();
        Assert.True(ownBefore >= 1, "同会话设备应互相可见：" + before.ToJsonString());

        var pairing = await CallAsync(receiver, "file-transfer.pairing");
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = pairing["code"]!.GetValue<string>() });
        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "普通配对不该成为成员", ["targetDeviceId"] = "phone-b" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await WaitForItemAsync(receiver, itemId, item => item["state"]!.GetValue<string>() == "available");
        var delivered = await WaitForItemAsync(sender, itemId, item => item["state"]!.GetValue<string>() == "delivered");
        Assert.Single(delivered["receipts"]!.AsArray());

        // The inbox item and its receipt are in the local store, yet the laptop is still not a member.
        var after = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
        Assert.Equal(ownBefore, after["ownDevices"]!.GetValue<int>());
        Assert.DoesNotContain("laptop-a", after.ToJsonString());

        // A self send therefore stays inside the conversation: it must not be handed to the paired device.
        var depositsBefore = relay.Deposits;
        var selfSend = await CallAsync(receiver, "file-transfer.assistant.send", new JsonObject { ["text"] = "只发自己的设备" });
        var selfId = selfSend["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await Task.Delay(700);
        Assert.Equal(depositsBefore, relay.Deposits);
        var fromLaptop = Items(await CallAsync(sender, "file-transfer.assistant.inspect"));
        Assert.DoesNotContain(fromLaptop, row => row!["id"]!.GetValue<string>() == selfId);
    }

    /// <summary>Receiving off stops sockets and the owner poll, never the outgoing queue.</summary>
    [Fact]
    public async Task SendingAndReceiptConfirmationContinueWhileReceivingIsDisabled()
    {
        var relay = Relay();
        var sender = await StartAsync("laptop", "laptop-a");
        var receiver = await StartAsync("phone", "phone-b");
        var pairing = await CallAsync(receiver, "file-transfer.pairing");
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = pairing["code"]!.GetValue<string>() });

        // The receiver is not receiving at all; it is the sender that has to keep working.
        await CallAsync(sender, "file-transfer.receive.stop");
        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "禁用接收也要发出", ["targetDeviceId"] = "phone-b" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        var received = await WaitForItemAsync(receiver, itemId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("禁用接收也要发出", received["text"]!.GetValue<string>());
        // And the receipt still comes back without the sender receiving anything.
        var delivered = await WaitForItemAsync(sender, itemId, item => item["state"]!.GetValue<string>() == "delivered",
            "senderInbox=" + (await CallAsync(sender, "file-transfer.assistant.inspect"))["inbox"]!.ToJsonString()
            + " receiverInbox=" + (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.ToJsonString()
            + " receipts=" + relay.ReceiptReads + " deposits=" + relay.Deposits + " log=" + relay.Log);
        Assert.Single(delivered["receipts"]!.AsArray());
        Assert.False((await CallAsync(sender, "file-transfer.assistant.inspect"))["receiving"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AFullBatchWaitingForReceiptsDoesNotBlockTheNextUpload()
    {
        var relay = Relay();
        var sender = await StartAsync("batch-sender", "batch-sender");
        var receiver = await StartAsync("batch-receiver", "batch-receiver");
        var pairing = await CallAsync(receiver, "file-transfer.pairing");
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = pairing["code"]!.GetValue<string>() });
        // The receiver's own inbox must be registered before it goes offline, exactly as a real device
        // would be: an inbox that was never registered answers 503, which is a retry, not a permanent
        // failure. The sender polls its own inbox too, so the guard is per-inbox, never a global counter.
        var receiverInboxId = Pairing.Decode(pairing["code"]!.GetValue<string>()).Inbox!.InboxId;
        var registered = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!relay.IsRegistered(receiverInboxId) && DateTimeOffset.UtcNow < registered) await Task.Delay(50);
        Assert.True(relay.IsRegistered(receiverInboxId),
            $"收件端自己的收件箱必须先注册再模拟离线：{receiverInboxId} relay={relay.Dump()}");
        await CallAsync(receiver, "file-transfer.receive.stop");
        var ids = new List<string>();
        for (var index = 0; index < 12; index++)
        {
            var sent = await CallAsync(sender, "file-transfer.assistant.send", new JsonObject
            { ["text"] = $"batch {index}", ["targetDeviceId"] = "batch-receiver" });
            ids.Add(sent["itemIds"]!.AsArray()[0]!.GetValue<string>());
        }

        // Fairness while the receiver is away: the last upload still completes even though the first
        // entries are already waiting for receipts that cannot exist yet.
        await WaitForItemAsync(sender, ids[^1], item => item["state"]!.GetValue<string>() == "stored");
        var stillWaiting = Items(await CallAsync(sender, "file-transfer.assistant.inspect"));
        Assert.All(ids, id => Assert.NotEqual("delivered",
            stillWaiting.First(item => item!["id"]!.GetValue<string>() == id)!["state"]!.GetValue<string>()));
        Assert.Equal(0, relay.ReceiptWrites);

        // The receiver comes back and is never poked again: no manual sync, no extra send. Its own loop
        // must pull the pending page, save the twelve entries and write the real receipts.
        var pollsBeforeStart = relay.PollsFor(receiverInboxId);
        await CallAsync(receiver, "file-transfer.receive.start");
        var resumed = DateTimeOffset.UtcNow.AddSeconds(20);
        JsonObject receiverAfterStart;
        do
        {
            receiverAfterStart = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.AsObject();
            if (relay.PollsFor(receiverInboxId) > pollsBeforeStart && receiverAfterStart["lastItems"]!.GetValue<int>() > 0) break;
            await Task.Delay(50);
        }
        while (DateTimeOffset.UtcNow < resumed);
        Assert.True(relay.PollsFor(receiverInboxId) > pollsBeforeStart && receiverAfterStart["lastItems"]!.GetValue<int>() > 0,
            $"恢复接收后收件端必须真的拉取：polls {pollsBeforeStart}->{relay.PollsFor(receiverInboxId)} inbox={receiverAfterStart.ToJsonString()} relay={relay.Dump()}");

        var saved = DateTimeOffset.UtcNow.AddSeconds(20);
        while (relay.ReceiptWrites < ids.Count && DateTimeOffset.UtcNow < saved) await Task.Delay(50);
        Assert.True(relay.ReceiptWrites >= ids.Count, $"收件端必须保存并回执整批：writes={relay.ReceiptWrites} relay={relay.Dump()}");

        // The twelve become delivered through the sender's own receipt polling: nothing else is called here.
        var trace = new List<string>();
        using var probe = new CancellationTokenSource();
        var sampler = ProbeAsync(sender, relay, trace, probe.Token);
        try
        {
            // The detail is read at failure time, so the trace really shows the whole confirmation window.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            var allDelivered = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var items = Items(await CallAsync(sender, "file-transfer.assistant.inspect"));
                allDelivered = ids.All(id => items.Any(item => item!["id"]!.GetValue<string>() == id
                    && item["state"]!.GetValue<string>() == "delivered"));
                if (allDelivered) break;
                await Task.Delay(200);
            }
            Assert.True(allDelivered, "整批必须在无人干预下自动确认：trace=" + string.Join(" ; ", trace.TakeLast(30)));
        }
        finally
        {
            probe.Cancel();
            try { await sampler; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>Samples the real counters while the batch is being confirmed, so a timeout shows the cause.</summary>
    private static async Task ProbeAsync(FileTransferModule sender, FakeInboxRelay relay, List<string> trace,
        CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var inspect = await CallAsync(sender, "file-transfer.assistant.inspect");
                var items = Items(inspect);
                var delivered = items.Count(item => item!["state"]!.GetValue<string>() == "delivered");
                var stored = items.Count(item => item!["state"]!.GetValue<string>() == "stored");
                trace.Add($"t={(int)(DateTimeOffset.UtcNow - started).TotalSeconds}s delivered={delivered} stored={stored}"
                    + $" confirms={inspect["inbox"]!["confirmReads"]!.GetValue<int>()}"
                    + $" writes={relay.ReceiptWrites} payloads={relay.PayloadReads}");
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// A text larger than the conversation limit is refused without unbounded buffering, and the next
    /// item in the same inbox is still delivered.
    /// </summary>
    [Fact]
    public async Task ATooLargeTextIsRejectedAndDoesNotBlockTheNextItem()
    {
        var relay = Relay();
        var receiver = await StartAsync("phone", "phone-b");
        var pairing = await CallAsync(receiver, "file-transfer.pairing");
        var inbox = Pairing.Decode(pairing["code"]!.GetValue<string>()).Inbox!;
        var receiverInbox = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.AsObject();
        Assert.Equal(inbox.InboxId, receiverInbox["id"]!.GetValue<string>());

        // Depositing needs this inbox's own registration to exist, exactly like the real relay's 503 gate.
        var registered = DateTimeOffset.UtcNow.AddSeconds(20);
        while (!relay.IsRegistered(inbox.InboxId) && DateTimeOffset.UtcNow < registered) await Task.Delay(50);
        Assert.True(relay.IsRegistered(inbox.InboxId), $"收件端必须先真实注册：{inbox.InboxId} relay={relay.Dump()}");
        using var sender = PublicInboxClient.Deposit(inbox);

        // An entry beyond the conversation's text limit is refused without unbounded buffering, and the
        // refusal is recorded so the same bad entry is never downloaded again.
        var oversized = new string('x', AssistantLimits.MaxTextCharacters * 4 + 1024);
        var bigId = PublicInboxIds.NewItemId();
        await sender.DepositTextAsync(bigId, oversized, "laptop-a", "电脑");
        var refused = DateTimeOffset.UtcNow.AddSeconds(20);
        JsonObject inboxAfterRefusal;
        do
        {
            inboxAfterRefusal = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.AsObject();
            if (inboxAfterRefusal["rejected"]!.GetValue<int>() >= 1) break;
            await Task.Delay(50);
        }
        while (DateTimeOffset.UtcNow < refused);
        Assert.True(inboxAfterRefusal["rejected"]!.GetValue<int>() >= 1,
            $"超长文本必须被拒且留下记录：inbox={inboxAfterRefusal.ToJsonString()} relay={relay.Dump()} polls={relay.PollsFor(inbox.InboxId)} log={relay.Log}");

        // A refused entry must not block the queue: the next entry in the same inbox is still saved.
        var smallId = PublicInboxIds.NewItemId();
        await sender.DepositTextAsync(smallId, "正常大小", "laptop-a", "电脑");
        var small = await WaitForItemAsync(receiver, smallId, item => item["state"]!.GetValue<string>() == "available",
            "receiverInbox=" + (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.ToJsonString()
            + " relay=" + relay.Dump() + " polls=" + relay.PollsFor(inbox.InboxId) + " log=" + relay.Log);
        Assert.Equal("正常大小", small["text"]!.GetValue<string>());
        var items = Items(await CallAsync(receiver, "file-transfer.assistant.inspect"));
        Assert.DoesNotContain(items, row => row!["id"]!.GetValue<string>() == bigId && row["state"]!.GetValue<string>() == "available");
    }

    /// <summary>
    /// The deposit inbox routes of the fixed public relay, plus the conversation endpoints a module
    /// still needs for its own receive loop. Real HTTP, real files, real receipts.
    /// </summary>
    /// <summary>
    /// The deposit inbox routes of the fixed public relay (PROTOCOL.md §6), modelled per inbox namespace
    /// exactly like the deployed service: credentials, items, payloads, revisions and long-poll waiters all
    /// belong to one inbox id, an unknown inbox answers 503 (not a credential error), and a repeated PUT
    /// answers the protocol JSON instead of an empty body.
    /// </summary>
    private sealed class FakeInboxRelay : IAsyncDisposable
    {
        private sealed record StoredItem(byte[] Payload, string Kind, string? Name, string? SenderId, string? SenderName, DateTimeOffset At,
            string Fingerprint);

        // Every dictionary is keyed by inbox id first: nothing can leak across namespaces.
        private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _depositKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, StoredItem>> _items = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _receipts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _revisions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _inboxPollsFor = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<TaskCompletionSource>> _inboxWaiters = new(StringComparer.Ordinal);
        private readonly List<TaskCompletionSource> _waiters = [];
        private readonly object _gate = new();
        private TcpListener? _listener;
        private CancellationTokenSource? _lifetime;
        private Task? _loop;
        private long _revision;
        private int _deposits;
        private int _receiptReads;
        private int _inboxPolls;
        private readonly List<string> _log = [];

        /// <summary>When set, registration answers 503 so a caller exercises the retry path.</summary>
        public bool NotReady { get; set; }

        public int Port { get; private set; }
        public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
        public int Deposits => Volatile.Read(ref _deposits);
        public int ReceiptReads => Volatile.Read(ref _receiptReads);
        public int InboxPolls => Volatile.Read(ref _inboxPolls);

        /// <summary>True once that exact inbox registered (owner + deposit key), not merely any inbox.</summary>
        public bool IsRegistered(string inboxId)
        {
            lock (_gate) return _depositKeys.ContainsKey(inboxId);
        }

        /// <summary>Long polls that asked for that exact inbox's item list.</summary>
        public int PollsFor(string inboxId)
        {
            lock (_gate) return _inboxPollsFor.GetValueOrDefault(inboxId);
        }

        /// <summary>True only inside that inbox's namespace; a cross-namespace read can never see it.</summary>
        public bool HasItem(string inboxId, string itemId)
        {
            lock (_gate) return _items.TryGetValue(inboxId, out var items) && items.ContainsKey(itemId);
        }

        private int _payloadReads;
        private int _receiptWrites;
        public int PayloadReads => Volatile.Read(ref _payloadReads);
        public int ReceiptWrites => Volatile.Read(ref _receiptWrites);

        /// <summary>The most recent requests, which is where a stalled flow actually stops.</summary>
        public string Log { get { lock (_gate) return string.Join(" | ", _log.TakeLast(16)); } }

        /// <summary>Which inboxes really hold items, for diagnosing a delivery that did not arrive.</summary>
        public string Dump()
        {
            lock (_gate)
            {
                var items = string.Join(",", _items.Where(pair => pair.Value.Count > 0).Select(pair => $"{pair.Key}:{pair.Value.Count}"));
                var receipts = string.Join(",", _receipts.Where(pair => pair.Value.Count > 0).Select(pair => $"{pair.Key}:{pair.Value.Count}"));
                return $"inboxes=[{string.Join(",", _depositKeys.Keys)}] items=[{items}] receipts=[{receipts}] owners=[{string.Join(",", _owners.Keys)}]";
            }
        }

        public FakeInboxRelay() => Start();

        public void Start()
        {
            _lifetime = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => RunAsync(_lifetime.Token));
        }

        private async Task RunAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener!.AcceptTcpClientAsync(token); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = HandleAsync(client, token);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken token)
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
                        if (await stream.ReadAsync(buffer, token) == 0) return;
                        header.Append((char)buffer[0]);
                    }
                    var lines = header.ToString().Split("\r\n");
                    var parts = lines[0].Split(' ');
                    var method = parts[0];
                    var path = parts.Length > 1 ? parts[1] : "/";
                    string? Header(string name) => lines.FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase));
                    var length = 0;
                    foreach (var line in lines)
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out length);
                    var body = new byte[length];
                    var read = 0;
                    while (read < length)
                    {
                        var count = await stream.ReadAsync(body.AsMemory(read, length - read), token);
                        if (count == 0) break;
                        read += count;
                    }
                    var response = await RespondAsync(method, path, Header("Authorization"), Header("X-MPT-Kind"), Header("X-MPT-Name"),
                        Header("X-MPT-Sender-Id"), Header("X-MPT-Sender-Name"), body, token);
                    await stream.WriteAsync(response, token);
                    await stream.FlushAsync(token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    lock (_gate)
                    {
                        if (_log.Count < 60) _log.Add($"write-failed {ex.GetType().Name}");
                    }
                }
            }
        }

        private async Task<byte[]> RespondAsync(string method, string path, string? authorization, string? kindHeader,
            string? nameHeader, string? senderIdHeader, string? senderNameHeader, byte[] body, CancellationToken token)
        {
            var route = path.Split('?')[0];
            var credentials = Decode(authorization);
            if (credentials is null) return Status("401 Unauthorized");
            var (id, key) = credentials.Value;
            lock (_gate)
            {
                if (_log.Count < 60) _log.Add($"{method} {route} {id}");
            }

            // Conversation endpoints: a module still needs them for its own receive loop.
            if (method == "POST" && route == PublicRelayClient.ConversationsPath)
            {
                lock (_gate) _owners[id] = key;
                return Status("201 Created");
            }
            if (method == "GET" && route == PublicRelayClient.ChangesPath)
            {
                if (Query(path, "since") is { } sinceConversation && sinceConversation == Current(ref _revision))
                {
                    var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_gate) _waiters.Add(waiter);
                    try { await waiter.Task.WaitAsync(TimeSpan.FromMilliseconds(300), token); }
                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
                    finally { lock (_gate) _waiters.Remove(waiter); }
                }
                return Json(new JsonObject { ["revision"] = Current(ref _revision) });
            }

            // Registration: owner key only; the deposit key of an existing inbox cannot be replaced.
            if (method == "POST" && route == PublicInboxClient.InboxesPath)
            {
                string owner;
                lock (_gate)
                {
                    if (!_owners.TryGetValue(id, out owner!)) _owners[id] = owner = key;
                }
                if (owner != key) return Status("401 Unauthorized");
                if (NotReady) return Status("503 Service Unavailable", "Retry-After: 1");
                var deposit = JsonNode.Parse(Encoding.UTF8.GetString(body))?["depositKey"]?.GetValue<string>() ?? "";
                bool created;
                lock (_gate)
                {
                    if (_depositKeys.TryGetValue(id, out var existing) && existing != deposit) return Status("409 Conflict");
                    created = !_depositKeys.ContainsKey(id);
                    _depositKeys[id] = deposit;
                    _revisions.TryAdd(id, 0);
                }
                return Json(new JsonObject
                {
                    ["inboxId"] = id,
                    ["created"] = created,
                    ["revision"] = Revision(id),
                    ["itemsPath"] = PublicInboxClient.ItemsPath
                });
            }

            var isItemRoute = route.StartsWith(PublicInboxClient.ItemsPath + "/", StringComparison.Ordinal);
            var segments = isItemRoute ? route[(PublicInboxClient.ItemsPath.Length + 1)..].Split('/') : [];
            var itemId = segments.Length > 0 ? segments[0] : "";

            // Deposit: the inbox must exist (otherwise 503, exactly like the deployed server), the caller
            // must present the deposit key, and a repeat of the same item is an idempotent 200 with JSON.
            if (method == "PUT" && isItemRoute && segments.Length == 1)
            {
                if (NotReady) return Status("503 Service Unavailable", "Retry-After: 1");
                string depositKey;
                lock (_gate)
                {
                    if (!_depositKeys.TryGetValue(id, out depositKey!))
                        return Status("503 Service Unavailable", "Retry-After: 1");
                }
                if (depositKey != key)
                {
                    return _owners.TryGetValue(id, out var owner) && owner == key
                        ? Status("403 Forbidden")   // owner credentials never use the deposit route
                        : Status("401 Unauthorized");
                }
                var kind = kindHeader is null ? "file" : kindHeader[(kindHeader.IndexOf(':') + 1)..].Trim();
                var name = nameHeader is null ? null : PublicInboxHeader.Decode(nameHeader[(nameHeader.IndexOf(':') + 1)..].Trim());
                var senderId = senderIdHeader?[(senderIdHeader.IndexOf(':') + 1)..].Trim();
                var senderName = senderNameHeader is null ? null : PublicInboxHeader.Decode(senderNameHeader[(senderNameHeader.IndexOf(':') + 1)..].Trim());
                var fingerprint = $"{kind}|{name}|{body.Length}|{senderId}|{senderName}";
                bool duplicate;
                long revision;
                lock (_gate)
                {
                    var items = Items(id);
                    if (items.TryGetValue(itemId, out var existing))
                    {
                        if (existing.Fingerprint != fingerprint) return Status("409 Conflict");
                        duplicate = true;
                    }
                    else
                    {
                        items[itemId] = new StoredItem(body, kind, name, senderId, senderName, DateTimeOffset.UtcNow, fingerprint);
                        _revisions[id] = _revisions.GetValueOrDefault(id) + 1;
                        foreach (var waiter in Waiters(id)) waiter.TrySetResult();
                        Waiters(id).Clear();
                        duplicate = false;
                    }
                    revision = _revisions[id];
                }
                if (!duplicate) Interlocked.Increment(ref _deposits);
                return Json(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["size"] = body.Length,
                    ["revision"] = revision,
                    ["duplicate"] = duplicate
                });
            }

            // Owner listing: only this inbox's items that have no receipt yet; deposit credentials get 403.
            if (method == "GET" && route == PublicInboxClient.ItemsPath)
            {
                Interlocked.Increment(ref _inboxPolls);
                lock (_gate) _inboxPollsFor[id] = _inboxPollsFor.GetValueOrDefault(id) + 1;
                if (!_owners.TryGetValue(id, out var owner) || owner != key) return Status("403 Forbidden");
                if (Query(path, "since") is { } since && since == Revision(id))
                {
                    var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_gate) Waiters(id).Add(waiter);
                    try { await waiter.Task.WaitAsync(TimeSpan.FromSeconds(2), token); }
                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
                    finally { lock (_gate) Waiters(id).Remove(waiter); }
                }
                lock (_gate)
                {
                    var items = new JsonArray();
                    foreach (var (storedId, value) in Items(id).Where(pair => !Receipts(id).ContainsKey(pair.Key)))
                    {
                        items.Add(new JsonObject
                        {
                            ["itemId"] = storedId,
                            ["kind"] = value.Kind,
                            ["name"] = value.Name,
                            ["size"] = value.Payload.Length,
                            ["createdAt"] = value.At.ToString("O"),
                            ["senderDeviceId"] = value.SenderId,
                            ["senderName"] = value.SenderName
                        });
                    }
                    if (_log.Count < 60) _log.Add($"-> items={items.Count} rev={_revisions.GetValueOrDefault(id)} for {id}");
                    return Json(new JsonObject { ["revision"] = _revisions.GetValueOrDefault(id), ["items"] = items, ["hasMore"] = false });
                }
            }

            // Payload: owner only, and only an item that really lives in this inbox.
            if (method == "GET" && isItemRoute && segments.Length == 1)
            {
                Interlocked.Increment(ref _payloadReads);
                if (!_owners.TryGetValue(id, out var owner) || owner != key) return Status("403 Forbidden");
                StoredItem? stored = null;
                lock (_gate) Items(id).TryGetValue(itemId, out stored);
                if (stored is null) return Status("404 Not Found");
                var headers = new StringBuilder();
                headers.Append($"X-MPT-Kind: {stored.Kind}\r\n");
                if (stored.Name is { Length: > 0 }) headers.Append($"X-MPT-Name: {PublicInboxHeader.Encode(stored.Name)}\r\n");
                if (stored.SenderId is { Length: > 0 }) headers.Append($"X-MPT-Sender-Id: {stored.SenderId}\r\n");
                if (stored.SenderName is { Length: > 0 }) headers.Append($"X-MPT-Sender-Name: {PublicInboxHeader.Encode(stored.SenderName)}\r\n");
                return Body(stored.Payload, headers.ToString());
            }

            // Owner receipt: marks the item saved in this inbox only, never deletes it.
            if (method == "POST" && isItemRoute && segments.Length == 2 && segments[1] == "receipt")
            {
                Interlocked.Increment(ref _receiptWrites);
                if (!_owners.TryGetValue(id, out var owner) || owner != key) return Status("403 Forbidden");
                bool known;
                lock (_gate)
                {
                    known = Items(id).ContainsKey(itemId);
                    if (known) Receipts(id)[itemId] = Encoding.UTF8.GetString(body);
                }
                if (!known) return Status("404 Not Found");
                return Json(new JsonObject { ["itemId"] = itemId, ["saved"] = true, ["duplicate"] = false });
            }

            // Owner delete: frees the entry without waking the long poll (PROTOCOL.md §6.4). The product
            // uses it for an entry it refused, never for one it already acknowledged.
            if (method == "DELETE" && isItemRoute && segments.Length == 1)
            {
                bool known;
                lock (_gate)
                {
                    known = Items(id).Remove(itemId);
                    Receipts(id).Remove(itemId);
                }
                return known ? Status("204 No Content") : Status("404 Not Found");
            }

            // Deposit (or owner) receipt read: an item that was never deposited is 404, not "not saved".
            if (method == "GET" && isItemRoute && segments.Length == 2 && segments[1] == "receipt")
            {
                Interlocked.Increment(ref _receiptReads);
                StoredItem? stored = null;
                string? receipt;
                lock (_gate)
                {
                    Items(id).TryGetValue(itemId, out stored);
                    Receipts(id).TryGetValue(itemId, out receipt);
                }
                if (_log.Count < 60) _log.Add($"-> receipt {itemId[..8]} saved={receipt is not null} inbox={id}");
                if (stored is null) return Status("404 Not Found");
                if (receipt is null) return Json(new JsonObject { ["itemId"] = itemId, ["saved"] = false, ["size"] = stored.Payload.Length });
                return Json(new JsonObject
                {
                    ["itemId"] = itemId,
                    ["saved"] = true,
                    ["savedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["bytes"] = stored.Payload.Length,
                    ["deviceId"] = "phone-b",
                    ["deviceName"] = "手机"
                });
            }

            return Status("404 Not Found");
        }

        private Dictionary<string, StoredItem> Items(string inboxId)
        {
            if (!_items.TryGetValue(inboxId, out var items)) _items[inboxId] = items = new(StringComparer.Ordinal);
            return items;
        }

        private Dictionary<string, string> Receipts(string inboxId)
        {
            if (!_receipts.TryGetValue(inboxId, out var receipts)) _receipts[inboxId] = receipts = new(StringComparer.Ordinal);
            return receipts;
        }

        private List<TaskCompletionSource> Waiters(string inboxId)
        {
            if (!_inboxWaiters.TryGetValue(inboxId, out var waiters)) _inboxWaiters[inboxId] = waiters = [];
            return waiters;
        }

        private long Revision(string inboxId)
        {
            lock (_gate) return _revisions.GetValueOrDefault(inboxId);
        }

        private static long? Query(string path, string name)
        {
            var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "";
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == name && long.TryParse(kv[1], out var value)) return value;
            }
            return null;
        }

        private long Current(ref long value)
        {
            lock (_gate) return value;
        }

        private static (string Id, string Key)? Decode(string? headerLine)
        {
            if (headerLine is null) return null;
            var value = headerLine[(headerLine.IndexOf(':') + 1)..].Trim();
            if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(value[6..].Trim()));
                var separator = text.IndexOf(':');
                return separator <= 0 ? null : (text[..separator], text[(separator + 1)..]);
            }
            catch (FormatException) { return null; }
        }

        private static byte[] Json(JsonObject payload) => Body(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        private static byte[] Status(string status, string? extra = null) =>
            Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\n{extra ?? ""}Content-Length: 0\r\nConnection: close\r\n\r\n");

        private static byte[] Body(byte[] content, string headers = "") =>
            [.. Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\n{headers}Content-Length: {content.Length}\r\nConnection: close\r\n\r\n"), .. content];

        public async ValueTask DisposeAsync()
        {
            _lifetime?.Cancel();
            _listener?.Stop();
            try { if (_loop is not null) await _loop.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
        }
    }
}
