using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.MyPowerTools;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace FileTransfer.Tests;

/// <summary>
/// The file assistant's default transport: two modules with no Tailscale address and no WebDAV
/// settings exchange text and files through the public relay over real HTTP, recover from a network
/// blip without a manual retry, reject a wrong conversation key without falling back, and stop their
/// long poll the moment receiving is disabled.
/// </summary>
[CollectionDefinition(RelayCollection.Name, DisableParallelization = true)]
public sealed class RelayCollection
{
    public const string Name = "public-relay";
}

/// <summary>
/// These tests run alone: each of them starts several modules with real long-poll loops against one
/// simulated relay, and letting other collections run beside them only adds scheduling noise to
/// assertions about delivery timing.
/// </summary>
[Collection(RelayCollection.Name)]
public sealed class PublicRelayTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-public-relay-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileTransferModule> _modules = [];
    private readonly List<FakePublicRelay> _relays = [];

    public PublicRelayTests()
    {
        Directory.CreateDirectory(_root);
        // These tests model a phone/CI host without any Tailnet interface: a real state, injected
        // instead of inventing an unreachable 100.x address.
        TransferFiles.LocalAddressesOverride = () => [];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var module in _modules) await module.DisposeAsync(CancellationToken.None);
        PublicRelayClient.BaseAddressOverride = null;
        TransferFiles.LocalAddressesOverride = null;
        foreach (var relay in _relays) await relay.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string DeviceRoot(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Starts a device with no listen address and no relay settings: the public default only.</summary>
    private async Task<FileTransferModule> StartAsync(string name, string deviceId, string? conversationId = null,
        string? conversationKey = null, string listenAddress = "", bool customWithoutPassword = false)
    {
        var root = DeviceRoot(name);
        var secrets = new InMemorySecretStore();
        if (conversationId is not null) await secrets.SaveAsync("file-transfer", "conversation-id", conversationId, CancellationToken.None);
        if (conversationKey is not null) await secrets.SaveAsync("file-transfer", "conversation-key", conversationKey, CancellationToken.None);
        var preferences = new JsonObject
        {
            ["deviceId"] = deviceId,
            ["listenAddress"] = listenAddress,
            ["receiveDirectory"] = Path.Combine(root, "inbox"),
            ["peers"] = new JsonArray()
        };
        if (customWithoutPassword)
        {
            preferences["webDavUrl"] = "http://127.0.0.1:9/dav/";
            preferences["username"] = "private-storage-user";
        }
        await File.WriteAllTextAsync(Path.Combine(root, "preferences.json"), preferences.ToJsonString());
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", root, root, root, "linux",
            ["secret.store"], new Dictionary<string, object> { ["secret.store"] = secrets });
        var module = new FileTransferModule();
        _modules.Add(module);
        Assert.True((await module.InitializeAsync(context, CancellationToken.None)).Ok);
        return module;
    }

    private FakePublicRelay Relay()
    {
        var relay = new FakePublicRelay();
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

    private static async Task<CommandExecutionResult> TryAsync(FileTransferModule module, string command, JsonObject? args = null) =>
        await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString("N"), command, args ?? new JsonObject()), CancellationToken.None);

    private static JsonArray Items(JsonObject inspect) => inspect["items"]!.AsArray();

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
        throw new InvalidOperationException("条目状态没有稳定：" + (last?.ToJsonString() ?? "(条目尚未出现)") + " 同步：" + detail);
    }

    [Fact]
    public async Task TheRelayClientSendsTheConversationCredentialsOnRegistration()
    {
        var relay = Relay();
        using var client = new PublicRelayClient("conv-client", new string('a', 64));
        var error = await Record.ExceptionAsync(() => client.RegisterAsync(CancellationToken.None));
        Assert.True(error is null, $"{error?.Message} log={string.Join(" | ", relay.RequestLog)}");
        Assert.True(relay.RequestLog.Count > 0, "no request reached the relay");
        Assert.Contains("auth=conv-client/aaaa", relay.RequestLog[0]);
    }

    // ---- the default path without Tailscale or settings --------------------------------------

    [Fact]
    public async Task WithoutTailscaleOrSettingsTextAndFilesTravelBothWays()
    {
        var relay = Relay();
        // No Tailnet interface on either device: the public relay is the only path here.
        var first = await StartAsync("phone-a", "phone-a");
        var second = await StartAsync("phone-b", "phone-b");
        var link = await CallAsync(first, "file-transfer.assistant.link.export");
        Assert.StartsWith(LinkCode.Prefix, link["code"]!.GetValue<string>());
        // The code of a device with no Tailscale address must still join.
        await CallAsync(second, "file-transfer.assistant.link.import", new JsonObject { ["code"] = link["code"]!.GetValue<string>() });

        var text = await CallAsync(first, "file-transfer.assistant.send", new JsonObject { ["text"] = "公网也能发" });
        var textId = text["itemIds"]!.AsArray()[0]!.GetValue<string>();
        var file = Path.Combine(_root, "relay-note.txt");
        await File.WriteAllTextAsync(file, "public relay payload");
        var attachment = await CallAsync(first, "file-transfer.assistant.send", new JsonObject { ["paths"] = new JsonArray(file) });
        var fileId = attachment["itemIds"]!.AsArray()[0]!.GetValue<string>();

        // No settings anywhere: the default relay carries both messages to the other device.
        var diagnostic = "first=" + (await CallAsync(first, "file-transfer.assistant.inspect")).ToJsonString()
            + " second=" + (await CallAsync(second, "file-transfer.assistant.inspect")).ToJsonString()
            + " stored=" + relay.DumpStored()
            + " req=" + string.Join(" | ", relay.RequestLog.TakeLast(14));
        var receivedText = await WaitForItemAsync(second, textId, item => item["state"]!.GetValue<string>() == "available", diagnostic);
        Assert.Equal("公网也能发", receivedText["text"]!.GetValue<string>());
        var receivedFile = await WaitForItemAsync(second, fileId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("relay-note.txt", receivedFile["name"]!.GetValue<string>());
        Assert.True(File.Exists(receivedFile["localPath"]!.GetValue<string>()));
        Assert.Equal("public relay payload", await File.ReadAllTextAsync(receivedFile["localPath"]!.GetValue<string>()));

        // The receiving side can answer over the same default transport.
        var back = await CallAsync(second, "file-transfer.assistant.send", new JsonObject { ["text"] = "收到了" });
        var backId = back["itemIds"]!.AsArray()[0]!.GetValue<string>();
        var receivedBack = await WaitForItemAsync(first, backId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("收到了", receivedBack["text"]!.GetValue<string>());

        // Both ends know the conversation is live without any user configuration.
        var relayState = (await CallAsync(first, "file-transfer.assistant.inspect"))["relay"]!.AsObject();
        Assert.True(relayState["configured"]!.GetValue<bool>());
        Assert.True(relayState["public"]!.GetValue<bool>());
        Assert.True(relayState["revision"]!.GetValue<long>() > 0);
        Assert.True(relay.Requests > 0);
    }

    [Fact]
    public async Task AnEmptyConversationBlocksInALongPollInsteadOfSpinning()
    {
        var relay = Relay();
        await StartAsync("idle", "idle");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (relay.ChangesRequests < 2 && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
        Assert.True(relay.ChangesRequests >= 2);
        var before = relay.ChangesRequests;
        await Task.Delay(500);
        Assert.True(relay.ChangesRequests - before <= 1, "revision zero must be a known revision, not a new poll each time");
    }

    [Fact]
    public async Task SendsDuringAnActivePassAreNotLost()
    {
        Relay();
        var sender = await StartAsync("sender", "sender");
        var receiver = await StartAsync("receiver", "receiver");
        var link = await CallAsync(sender, "file-transfer.assistant.link.export");
        await CallAsync(receiver, "file-transfer.assistant.link.import", new JsonObject { ["code"] = link["code"]!.GetValue<string>() });
        var ids = new List<string>();
        for (var index = 0; index < 12; index++)
        {
            var sent = await CallAsync(sender, "file-transfer.assistant.send", new JsonObject { ["text"] = $"burst {index}" });
            ids.Add(sent["itemIds"]!.AsArray()[0]!.GetValue<string>());
        }
        foreach (var id in ids)
            await WaitForItemAsync(receiver, id, item => item["state"]!.GetValue<string>() == "available");
    }

    [Fact]
    public async Task BothEndsSeeTheJoinedDevice()
    {
        Relay();
        var owner = await StartAsync("phone-a", "phone-a");
        var joiner = await StartAsync("phone-b", "phone-b");
        var link = await CallAsync(owner, "file-transfer.assistant.link.export");
        var joined = await CallAsync(joiner, "file-transfer.assistant.link.import", new JsonObject { ["code"] = link["code"]!.GetValue<string>() });
        Assert.True(joined["membershipNotice"]!.GetValue<bool>());

        // The joiner knows the owner from the code, and announces itself so the owner sees it too.
        var joinerIdentity = (await CallAsync(joiner, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
        Assert.True(joinerIdentity["ownDevices"]!.GetValue<int>() >= 1);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var ownerIdentity = (await CallAsync(owner, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
            if (ownerIdentity["ownDevices"]!.GetValue<int>() >= 1) return;
            await Task.Delay(100);
        }
        Assert.Fail("加入端没有出现在拥有者的设备列表里");
    }

    [Fact]
    public async Task AnOfflineDeviceCatchesUpWhenItComesOnline()
    {
        Relay();
        var relay = Relay();
        var first = await StartAsync("phone-a", "phone-a");
        var link = await CallAsync(first, "file-transfer.assistant.link.export");
        var code = link["code"]!.GetValue<string>();
        var sent = await CallAsync(first, "file-transfer.assistant.send", new JsonObject { ["text"] = "离线时发的" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();

        // The other device only exists later; the durable relay copy is what it catches up on.
        var second = await StartAsync("phone-b", "phone-b");
        await CallAsync(second, "file-transfer.assistant.link.import", new JsonObject { ["code"] = code });
        var diagnostic = "owner=" + (await CallAsync(first, "file-transfer.assistant.inspect")).ToJsonString()
            + " joiner=" + (await CallAsync(second, "file-transfer.assistant.inspect")).ToJsonString()
            + " stored=" + relay.DumpStored()
            + " req=" + string.Join(" | ", relay.RequestLog.Take(24));
        var received = await WaitForItemAsync(second, itemId, item => item["state"]!.GetValue<string>() == "available", diagnostic);
        Assert.Equal("离线时发的", received["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task AFailedDirectAttemptStillDeliversThroughTheRelay()
    {
        var relay = Relay();
        // The owner advertises a direct candidate that is not reachable from this host, so the joiner's
        // direct attempt fails while the durable relay copy must still arrive. The local interface list
        // stays empty: this is the peer's advertised path, not a fabricated local interface.
        var first = await StartAsync("phone-a", "phone-a", listenAddress: "100.64.0.99");
        var second = await StartAsync("phone-b", "phone-b");
        var link = await CallAsync(first, "file-transfer.assistant.link.export");
        await CallAsync(second, "file-transfer.assistant.link.import", new JsonObject { ["code"] = link["code"]!.GetValue<string>() });
        var sent = await CallAsync(second, "file-transfer.assistant.send", new JsonObject { ["text"] = "直传失败也要到" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        var diagnostic = "sender=" + (await CallAsync(second, "file-transfer.assistant.inspect")).ToJsonString()
            + " owner=" + (await CallAsync(first, "file-transfer.assistant.inspect")).ToJsonString()
            + " stored=" + relay.DumpStored();
        var received = await WaitForItemAsync(first, itemId, item => item["state"]!.GetValue<string>() == "available", diagnostic);
        Assert.Equal("直传失败也要到", received["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ANetworkBlipRecoversWithoutAManualRetry()
    {
        var relay = Relay();
        var first = await StartAsync("phone-a", "phone-a");
        var second = await StartAsync("phone-b", "phone-b");
        var link = await CallAsync(first, "file-transfer.assistant.link.export");
        await CallAsync(second, "file-transfer.assistant.link.import", new JsonObject { ["code"] = link["code"]!.GetValue<string>() });

        var port = relay.Port;
        relay.Stop();
        var sent = await CallAsync(first, "file-transfer.assistant.send", new JsonObject { ["text"] = "断网期间" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        // While the relay is away nothing may claim delivery; the entry is queued or failed, never sent.
        var offline = await WaitForItemAsync(first, itemId,
            item => item["state"]!.GetValue<string>() is "failed" or "queued");
        Assert.DoesNotContain(offline["state"]!.GetValue<string>(), new[] { "stored", "delivered", "available" });

        relay.Start(port);
        // The receive loop's bounded backoff notices the relay is back and the queue recovers by itself.
        var stored = await WaitForItemAsync(first, itemId, item => item["state"]!.GetValue<string>() == "stored");
        Assert.Null(stored["error"]);
        var received = await WaitForItemAsync(second, itemId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("断网期间", received["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWrongConversationKeyIsReportedAndNeverFallsBack()
    {
        var relay = Relay();
        // The conversation exists on the server with another key.
        relay.Register("conv-guard", new string('a', 64));
        var module = await StartAsync("phone-a", "phone-a", "conv-guard", new string('b', 64));
        await CallAsync(module, "file-transfer.assistant.send", new JsonObject { ["text"] = "不该被接受" });
        var sync = await TryAsync(module, "file-transfer.assistant.sync");

        var relayState = (await CallAsync(module, "file-transfer.assistant.inspect"))["relay"]!.AsObject();
        Assert.Equal("unavailable", relayState["state"]!.GetValue<string>());
        Assert.Contains("密钥", relayState["message"]!.GetValue<string>());
        // A rejected key is not retried into a spin, and no credential fallback is attempted.
        var before = relay.ChangesRequests;
        await Task.Delay(500);
        Assert.True(relay.ChangesRequests - before <= 1, $"轮询次数 {relay.ChangesRequests - before}");
        Assert.False(sync.Success && sync.Output.Contains("\"published\":1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReceiveStopEndsTheLongPollAndStartResumesIt()
    {
        var relay = Relay();
        var module = await StartAsync("phone-a", "phone-a");
        // Wait until the module is really parked in the server's long poll.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (relay.ChangesRequests == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(50);
        Assert.True(relay.ChangesRequests > 0);
        Assert.True((await CallAsync(module, "file-transfer.assistant.inspect"))["receiving"]!.GetValue<bool>());

        var stopAt = DateTimeOffset.UtcNow;
        var stopped = await CallAsync(module, "file-transfer.receive.stop");
        Assert.False(stopped["receiving"]!.GetValue<bool>());
        var held = relay.ChangesRequests;
        // Correct lifecycle observation: a request already in flight when the stop happened is not a new
        // poll, so only arrivals clearly after the stop count. Anything later means the loop kept polling.
        await Task.Delay(1500);
        var late = relay.ChangesAfter(stopAt.AddSeconds(1));
        Assert.True(late == 0, $"停止 1 秒后仍在轮询：{late}（总请求 {relay.ChangesRequests}）");
        // Receiving really is off in the module state, not just in the reply.
        var stoppedState = (await CallAsync(module, "file-transfer.assistant.inspect"))["receivingDetails"]!.AsObject();
        Assert.False(stoppedState["enabled"]!.GetValue<bool>());

        var started = await CallAsync(module, "file-transfer.receive.start");
        Assert.True(started["receiving"]!.GetValue<bool>());
        // No Tailscale address here: starting must not fail, it only leaves the direct listener off.
        Assert.False(started["tailnetListener"]!.GetValue<bool>());
        // No Tailscale prompt and no claim that the relay is already delivering: the note stays empty
        // unless there is a real listener error to report.
        Assert.DoesNotContain("Tailscale", started["note"]!.GetValue<string>());
        var resumed = DateTimeOffset.UtcNow.AddSeconds(20);
        while (relay.ChangesRequests <= held && DateTimeOffset.UtcNow < resumed) await Task.Delay(50);
        Assert.True(relay.ChangesRequests > held);
        Assert.True((await CallAsync(module, "file-transfer.assistant.inspect"))["receiving"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DisposeEndsTheLongPollWithoutHanging()
    {
        Relay();
        var module = await StartAsync("phone-a", "phone-a");
        await Task.Delay(300);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await module.DisposeAsync(CancellationToken.None);
        started.Stop();
        _modules.Remove(module);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"Dispose 耗时 {started.Elapsed}");
    }

    [Fact]
    public async Task MissingCustomPasswordDoesNotPublishSharedContentToBuiltInRelays()
    {
        var relay = Relay();
        var module = await StartAsync("custom", "phone-private", customWithoutPassword: true);
        var file = Path.Combine(DeviceRoot("custom"), "private.txt");
        await File.WriteAllTextAsync(file, "private storage content");
        await CallAsync(module, "file-transfer.assistant.send", new JsonObject
        {
            ["text"] = "private storage message",
            ["paths"] = new JsonArray(file)
        });
        // Explicit synchronization exercises the shared relay selection with a missing secret.
        // The command can report the configuration error; it must never select the public default.
        await TryAsync(module, "file-transfer.assistant.sync");
        await relay.WaitUntilIdleAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, relay.Requests);
    }

    // ---- compatibility of the classic commands ------------------------------------------------

    [Fact]
    public async Task IdentityAndPairingCodesWorkOfflineWithoutAnyNetworkCall()
    {
        var relay = Relay();
        var first = await StartAsync("phone-a", "phone-a");
        var second = await StartAsync("phone-b", "phone-b");
        // Receiving is parked so the only traffic left would come from the commands under test. The
        // barrier waits for the transport to be really idle: nothing in flight can be mistaken for a
        // request that a command caused, and no blind delay is needed.
        await CallAsync(first, "file-transfer.receive.stop");
        await CallAsync(second, "file-transfer.receive.stop");
        await relay.WaitUntilIdleAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, relay.ActiveRequests);
        var before = relay.Requests;

        var pairing = await CallAsync(first, "file-transfer.pairing");
        Assert.Equal("pair", pairing["kind"]!.GetValue<string>());
        Assert.StartsWith(Pairing.Prefix, pairing["code"]!.GetValue<string>());
        // The address is only an optional direct candidate, never the identity.
        Assert.Equal("", pairing["address"]!.GetValue<string>());

        var preview = await CallAsync(second, "file-transfer.pair.preview", new JsonObject { ["code"] = pairing["code"]!.GetValue<string>() });
        Assert.Equal("phone-a", preview["deviceId"]!.GetValue<string>());
        Assert.Equal("", preview["address"]!.GetValue<string>());

        // An address-less target is a valid pairing target: identity is the key, not an IP.
        var imported = await CallAsync(second, "file-transfer.pair.import", new JsonObject { ["code"] = pairing["code"]!.GetValue<string>() });
        Assert.Equal("phone-a", imported["deviceId"]!.GetValue<string>());
        var importedPeers = (await CallAsync(second, "file-transfer.inspect"))["peers"]!.AsArray();
        Assert.Single(importedPeers);
        Assert.Equal("", importedPeers[0]!["address"]!.GetValue<string>());

        // A code with no address at all is still a complete identity: the stable id is the key.
        var addressless = new Pairing("phone-c", "无网卡设备", "", "addressless-token-0123456789ab").Encode();
        var addresslessPreview = await CallAsync(second, "file-transfer.pair.preview", new JsonObject { ["code"] = addressless });
        Assert.Equal("phone-c", addresslessPreview["deviceId"]!.GetValue<string>());
        Assert.Equal("", addresslessPreview["address"]!.GetValue<string>());
        await CallAsync(second, "file-transfer.pair.import", new JsonObject { ["code"] = addressless });
        var allPeers = (await CallAsync(second, "file-transfer.inspect"))["peers"]!.AsArray();
        var stored = allPeers.Single(peer => peer!["deviceId"]!.GetValue<string>() == "phone-c")!.AsObject();
        Assert.True(stored["address"]!.GetValue<string>() == "", allPeers.ToJsonString());

        // The assistant connection code is equally offline, and identity export never registers.
        var export = await CallAsync(first, "file-transfer.assistant.link.export");
        Assert.StartsWith(LinkCode.Prefix, export["code"]!.GetValue<string>());
        var linkPreview = await CallAsync(first, "file-transfer.assistant.link.preview", new JsonObject { ["code"] = export["code"]!.GetValue<string>() });
        Assert.Equal("phone-a", linkPreview["deviceId"]!.GetValue<string>());
        // The barrier is re-established after the commands: an in-flight call started before the snapshot
        // would also be counted, so the count is only meaningful on a quiet transport.
        await relay.WaitUntilIdleAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(before, relay.Requests);
    }

    [Fact]
    public async Task AnUnknownTargetDeviceIsRefused()
    {
        Relay();
        var module = await StartAsync("phone-a", "phone-a");
        var result = await TryAsync(module, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "发给陌生人", ["targetDeviceId"] = "never-seen-device" });
        Assert.False(result.Success);
        Assert.Contains("配对", result.Output);
    }

    /// <summary>
    /// A real HTTP public relay: conversation registration, Basic auth per conversation, a private
    /// WebDAV namespace and a long poll that a manifest or receipt write wakes.
    /// </summary>
    private sealed class FakePublicRelay : IAsyncDisposable
    {
        private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private readonly HashSet<string> _collections = new(StringComparer.Ordinal) { "/" };
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _waiters = [];
        private TcpListener? _listener;
        private CancellationTokenSource? _lifetime;
        private Task? _loop;
        private long _revision;
        private int _requests;
        private int _active;
        private int _changes;
        private readonly List<DateTimeOffset> _changesAt = [];
        private static readonly byte[] Empty = [];

        public int Port { get; private set; }
        public Uri BaseAddress => new($"http://127.0.0.1:{Port}/");
        public int Requests => Volatile.Read(ref _requests);

        /// <summary>Requests currently being handled, the basis for a real quiescence barrier.</summary>
        public int ActiveRequests => Volatile.Read(ref _active);

        /// <summary>
        /// Waits until no request is being handled (and none arrives for a short settle), so a test can
        /// snapshot the counter without racing an in-flight background call. This observes the transport
        /// instead of sleeping for a guessed delay.
        /// </summary>
        public async Task WaitUntilIdleAsync(TimeSpan budget)
        {
            var deadline = DateTimeOffset.UtcNow + budget;
            var quietSince = DateTimeOffset.UtcNow;
            var seen = Requests;
            while (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20);
                if (ActiveRequests != 0 || Requests != seen)
                {
                    seen = Requests;
                    quietSince = DateTimeOffset.UtcNow;
                    continue;
                }
                if (DateTimeOffset.UtcNow - quietSince >= TimeSpan.FromMilliseconds(150)) return;
            }
        }
        public int ChangesRequests => Volatile.Read(ref _changes);

        /// <summary>How many long polls arrived after a moment; a stopped receiver must issue none.</summary>
        public int ChangesAfter(DateTimeOffset moment)
        {
            lock (_gate) return _changesAt.Count(at => at > moment);
        }
        /// <summary>Diagnostics: the credentials the server saw, and why it rejected them.</summary>
        public List<string> AuthLog { get; } = [];
        public List<string> RequestLog { get; } = [];

        /// <summary>What the relay really stores, so a timeout can be diagnosed from facts.</summary>
        public string DumpStored()
        {
            lock (_gate) return $"rev={_revision} files=[{string.Join(",", _files.Keys.Select(key => key.Replace(PublicRelayClient.DavPath, "")))}]";
        }

        public FakePublicRelay() => Start(0);

        public void Register(string conversationId, string key)
        {
            lock (_gate) _keys[conversationId] = key;
        }

        public void Start(int port)
        {
            _lifetime = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Task.Run(() => RunAsync(_lifetime.Token));
        }

        public void Stop()
        {
            _lifetime?.Cancel();
            _listener?.Stop();
            try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            lock (_gate)
            {
                // A relay that went away answers nothing: pending long polls end like a dropped connection.
                foreach (var waiter in _waiters) waiter.TrySetResult();
                _waiters.Clear();
            }
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
            var activeRequest = false;
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
                    var request = header.ToString();
                    // HttpClient may open a pooled socket before a cancelled request sends headers.
                    // An idle accepted socket is not an in-flight HTTP request.
                    Interlocked.Increment(ref _active);
                    activeRequest = true;
                    Interlocked.Increment(ref _requests);
                    var lines = request.Split("\r\n");
                    var parts = lines[0].Split(' ');
                    var method = parts[0];
                    var path = parts.Length > 1 ? parts[1] : "/";
                    var authorization = lines.FirstOrDefault(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
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
                    var headerNames = string.Join(",", lines
                        .Where(line => line.Contains(':') && !line.StartsWith("HTTP/", StringComparison.Ordinal))
                        .Select(line => line.Split(':')[0]).Take(12));
                    var response = await RespondAsync(method, path, authorization, body, headerNames, token);
                    await stream.WriteAsync(response, token);
                    await stream.FlushAsync(token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
                finally { if (activeRequest) Interlocked.Decrement(ref _active); }
            }
        }

        private async Task<byte[]> RespondAsync(string method, string path, string? authorization, byte[] body,
            string headerNames, CancellationToken token)
        {
            var credentials = Decode(authorization);
            var route = path.Split('?')[0];
            lock (_gate)
            {
                if (RequestLog.Count < 40)
                    RequestLog.Add($"{method} {route} auth={(credentials is null ? "none" : credentials.Value.ConversationId + "/" + credentials.Value.Key[..4])} headers=[{headerNames}] authRaw='{authorization}'");
            }
            if (credentials is null) return Status("401 Unauthorized");
            var (conversationId, key) = credentials.Value;
            lock (_gate)
            {
                if (!_keys.TryGetValue(conversationId, out var expected)) _keys[conversationId] = key;
                else if (expected != key)
                {
                    AuthLog.Add($"{conversationId}: expected {expected[..6]} got {key[..6]} ({method} {route})");
                    return Status("401 Unauthorized");
                }
            }
            if (method == "POST" && route == PublicRelayClient.ConversationsPath)
            {
                lock (_gate) _keys[conversationId] = key;
                return Status("201 Created");
            }
            if (method == "GET" && route == PublicRelayClient.ChangesPath)
            {
                Interlocked.Increment(ref _changes);
                lock (_gate) _changesAt.Add(DateTimeOffset.UtcNow);
                var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "";
                long? since = null;
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = pair.Split('=', 2);
                    if (kv.Length == 2 && kv[0] == "since" && long.TryParse(kv[1], out var value)) since = value;
                }
                if (since is null) return Json(new JsonObject { ["revision"] = CurrentRevision() });
                if (since > CurrentRevision()) return Json(new JsonObject { ["revision"] = CurrentRevision() });
                if (since == CurrentRevision())
                {
                    var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_gate) _waiters.Add(waiter);
                    try { await waiter.Task.WaitAsync(TimeSpan.FromSeconds(2), token); }
                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
                    finally { lock (_gate) _waiters.Remove(waiter); }
                }
                return Json(new JsonObject { ["revision"] = CurrentRevision() });
            }
            if (!route.StartsWith(PublicRelayClient.DavPath, StringComparison.Ordinal)) return Status("404 Not Found");
            var normalized = Uri.UnescapeDataString(route).TrimEnd('/');
            // The server scopes every authenticated request to that conversation's private namespace:
            // only the assistant tree is reachable and traversal out of it is refused.
            if (!normalized.StartsWith(PublicRelayClient.DavPath + "assistant", StringComparison.Ordinal)
                || normalized.Contains("..", StringComparison.Ordinal))
                return Status("403 Forbidden");
            switch (method)
            {
                case "MKCOL":
                    lock (_gate) _collections.Add(normalized);
                    return Status("201 Created");
                case "PUT":
                    lock (_gate)
                    {
                        _files[normalized] = body;
                        _revision++;
                        foreach (var waiter in _waiters) waiter.TrySetResult();
                        _waiters.Clear();
                    }
                    return Status("201 Created");
                case "GET":
                    lock (_gate) return _files.TryGetValue(normalized, out var content) ? Body(content) : Status("404 Not Found");
                case "PROPFIND":
                    lock (_gate) return Body(Encoding.UTF8.GetBytes(MultiStatus(normalized)));
                default:
                    return Status("200 OK");
            }
        }

        private long CurrentRevision()
        {
            lock (_gate) return _revision;
        }

        private string MultiStatus(string path)
        {
            var builder = new StringBuilder();
            builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?><D:multistatus xmlns:D=\"DAV:\">");
            lock (_gate)
            {
                foreach (var file in _files.Keys.Where(key => Parent(key) == path))
                    builder.Append($"<D:response><D:href>{file}</D:href><D:propstat><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
                foreach (var collection in _collections.Where(key => key.Length > 0 && Parent(key) == path))
                    builder.Append($"<D:response><D:href>{collection}</D:href><D:propstat><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>");
            }
            builder.Append("</D:multistatus>");
            return builder.ToString();
        }

        private static string Parent(string path)
        {
            var index = path.LastIndexOf('/');
            return index <= 0 ? "" : path[..index];
        }

        private static (string ConversationId, string Key)? Decode(string? headerLine)
        {
            if (headerLine is null) return null;
            var value = headerLine[(headerLine.IndexOf(':') + 1)..].Trim();
            if (!value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(value[6..].Trim()));
                var separator = text.IndexOf(':');
                if (separator <= 0) return null;
                return (text[..separator], text[(separator + 1)..]);
            }
            catch (FormatException) { return null; }
        }

        private static byte[] Json(JsonObject payload) => Body(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        private static byte[] Status(string status) =>
            Encoding.UTF8.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        private static byte[] Body(byte[] content) =>
            [.. Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"), .. content];

        public async ValueTask DisposeAsync()
        {
            Stop();
            await Task.CompletedTask;
        }
    }
}
