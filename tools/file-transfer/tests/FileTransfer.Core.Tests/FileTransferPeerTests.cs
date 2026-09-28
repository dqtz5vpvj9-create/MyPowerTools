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
/// Module-level contract for the mobile device page: a paired device with an address stays
/// "unknown" until its receiver really answers, removing a device removes its secret, pairing
/// cannot be escalated to remote control, and a relay upload is never recorded as received.
/// </summary>
public sealed class FileTransferPeerTests : IAsyncDisposable
{
    private const string PeerKey = "module-peer-key-0123456789abcdef";
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-peer-test-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _secrets = new();
    private readonly List<DirectReceiver> _receivers = [];
    private readonly List<WebDavStub> _servers = [];
    private FileTransferModule? _module;

    public FileTransferPeerTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        foreach (var receiver in _receivers) await receiver.DisposeAsync();
        foreach (var server in _servers) await server.DisposeAsync();
        if (_module is not null) await _module.DisposeAsync(CancellationToken.None);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task<FileTransferModule> StartAsync(JsonObject? preferences = null)
    {
        if (preferences is not null)
            await File.WriteAllTextAsync(Path.Combine(_root, "preferences.json"), preferences.ToJsonString());
        _module = await StartModuleAsync(_root, _secrets, null);
        return _module;
    }

    private static async Task<FileTransferModule> StartModuleAsync(string root, InMemorySecretStore secrets, JsonObject? preferences)
    {
        if (preferences is not null)
            await File.WriteAllTextAsync(Path.Combine(root, "preferences.json"), preferences.ToJsonString());
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", root, root, root, "linux",
            ["secret.store"], new Dictionary<string, object> { ["secret.store"] = secrets });
        var module = new FileTransferModule();
        var initialized = await module.InitializeAsync(context, CancellationToken.None);
        Assert.True(initialized.Ok);
        return module;
    }

    private static JsonObject Read(CommandExecutionResult result)
    {
        Assert.True(result.Success, result.Output);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    private static async Task<JsonObject> CallAsync(FileTransferModule module, string invocation, string command, JsonObject? args = null) =>
        Read(await module.ExecuteCommandAsync(new CommandRequest(invocation, command, args ?? new JsonObject()), CancellationToken.None));

    /// <summary>
    /// Each test binds the production transfer port on its own loopback address. The receiver is the
    /// active closer of every connection, so a port it used stays in TIME_WAIT for about a minute and
    /// cannot be bound again in the same run; separate 127.0.0.x addresses keep the tests independent
    /// without changing how the module binds its real listener.
    /// </summary>
    /// <summary>
    /// A receiver that is enabled with the tool binds its listen address at start, so a test that
    /// needs the loopback address for the peer gives this module a tailnet listen address instead:
    /// it cannot bind here, the receiver reports why, and the peer keeps the loopback port.
    /// </summary>
    private JsonObject PeerPreferences(string deviceId, string address, string peerId = "pc-peer", string peerName = "Peer PC",
        string? listenAddress = null) =>
        RootPeerPreferences(_root, deviceId, address, peerId, peerName, listenAddress);

    /// <summary>A separate name keeps the five argument call unambiguous with the device-root overload.</summary>
    private static JsonObject RootPeerPreferences(string root, string deviceId, string address, string peerId, string peerName,
        string? listenAddress = null) => new()
    {
        ["deviceId"] = deviceId,
        ["listenAddress"] = listenAddress ?? address,
        ["receiveDirectory"] = Path.Combine(root, "inbox"),
        ["peers"] = new JsonArray(new JsonObject { ["deviceId"] = peerId, ["name"] = peerName, ["address"] = address })
    };

    private DirectReceiver PeerReceiver(string address, string deviceId = "pc-peer", string name = "Peer PC", string token = PeerKey, string? directory = null)
    {
        var receiver = new DirectReceiver(address, TransferFiles.Port, token, directory ?? Path.Combine(_root, "peer-inbox"),
            64L * 1024 * 1024, (_, _, _, _) => { }, deviceId: deviceId, deviceName: name);
        _receivers.Add(receiver);
        return receiver;
    }

    private WebDavStub Relay()
    {
        var server = new WebDavStub();
        _servers.Add(server);
        return server;
    }

    [Fact]
    public async Task APairedAddressStaysUnknownUntilThePairedReceiverAnswers()
    {
        await _secrets.SaveAsync("file-transfer", "peer-pc-peer", PeerKey, CancellationToken.None);
        var module = await StartAsync(PeerPreferences("phone-local", "127.0.0.31", listenAddress: "100.64.0.31"));

        var before = (await CallAsync(module, "1", "file-transfer.inspect"))["peers"]!.AsArray();
        Assert.Single(before);
        Assert.Equal("unknown", before[0]!["state"]!.GetValue<string>());
        Assert.Null(before[0]!["checkedAt"]);
        Assert.False(before[0]!["supportsControl"]!.GetValue<bool>());

        // Nothing is listening yet, so the explicit check must report offline, never online.
        var offline = await CallAsync(module, "2", "file-transfer.peer.check", new JsonObject { ["deviceId"] = "pc-peer" });
        Assert.Equal("offline", offline["state"]!.GetValue<string>());
        Assert.NotEqual("", offline["message"]!.GetValue<string>());
        Assert.False(offline["supportsControl"]!.GetValue<bool>());
        var checkedState = (await CallAsync(module, "3", "file-transfer.inspect"))["peers"]!.AsArray()[0]!;
        Assert.Equal("offline", checkedState["state"]!.GetValue<string>());
        Assert.NotNull(checkedState["checkedAt"]);

        // Now the paired receiver is up and answers: this is the only path to "online".
        PeerReceiver("127.0.0.31");
        var online = await CallAsync(module, "4", "file-transfer.peer.check", new JsonObject { ["deviceId"] = "pc-peer" });
        Assert.Equal("online", online["state"]!.GetValue<string>());
        Assert.Equal("pc-peer", online["deviceId"]!.GetValue<string>());
        Assert.False(online["supportsControl"]!.GetValue<bool>());
        Assert.Equal("online", (await CallAsync(module, "5", "file-transfer.inspect"))["peers"]!.AsArray()[0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task AReachableEndpointThatDoesNotProveItsIdentityStaysUnknown()
    {
        await _secrets.SaveAsync("file-transfer", "peer-pc-peer", PeerKey, CancellationToken.None);
        var module = await StartAsync(PeerPreferences("phone-local", "127.0.0.42", listenAddress: "100.64.0.42"));
        // The endpoint answers, but with a different pairing secret, so it never proves the identity.
        PeerReceiver("127.0.0.42", token: "another-pairing-key-0123456789");

        var check = await CallAsync(module, "1", "file-transfer.peer.check", new JsonObject { ["deviceId"] = "pc-peer" });
        // A peer that answered is not offline; the state stays unverified until it proves who it is.
        Assert.Equal("unknown", check["state"]!.GetValue<string>());
        Assert.NotEqual("", check["message"]!.GetValue<string>());
        Assert.Equal("unknown", (await CallAsync(module, "2", "file-transfer.inspect"))["peers"]!.AsArray()[0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task APeerWithoutAnAddressOrSecretStaysUnknown()
    {
        var module = await StartAsync(new JsonObject
        {
            ["deviceId"] = "phone-local",
            ["listenAddress"] = "127.0.0.33",
            ["receiveDirectory"] = Path.Combine(_root, "inbox"),
            ["peers"] = new JsonArray(
                new JsonObject { ["deviceId"] = "pc-no-address", ["name"] = "无地址", ["address"] = "" },
                new JsonObject { ["deviceId"] = "pc-no-secret", ["name"] = "无密钥", ["address"] = "127.0.0.33" })
        });

        foreach (var deviceId in new[] { "pc-no-address", "pc-no-secret" })
        {
            var check = await CallAsync(module, "1", "file-transfer.peer.check", new JsonObject { ["deviceId"] = deviceId });
            Assert.Equal("unknown", check["state"]!.GetValue<string>());
            Assert.Contains("重新导入", check["message"]!.GetValue<string>());
        }
        var states = (await CallAsync(module, "2", "file-transfer.inspect"))["peers"]!.AsArray();
        Assert.All(states, peer => Assert.Equal("unknown", peer!["state"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ADeviceWithNoReceiverAnswerIsNeverReportedAsOnlineInAnySnapshot()
    {
        await _secrets.SaveAsync("file-transfer", "peer-pc-peer", PeerKey, CancellationToken.None);
        var module = await StartAsync(PeerPreferences("phone-local", "127.0.0.34", listenAddress: "100.64.0.34"));
        // Prove the peer port is free on that address right before the check, so "offline" cannot be
        // an accident of another listener and the assertion stays deterministic under load.
        var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Parse("127.0.0.34"), TransferFiles.Port);
        free.Start();
        free.Stop();
        var check = await CallAsync(module, "1", "file-transfer.peer.check", new JsonObject { ["deviceId"] = "pc-peer" });
        // Nothing is listening on that address, which is a real connection failure: offline.
        Assert.Equal("offline", check["state"]!.GetValue<string>());
        for (var i = 0; i < 2; i++)
        {
            var peer = (await CallAsync(module, "i" + i, "file-transfer.inspect"))["peers"]!.AsArray()[0]!;
            Assert.NotEqual("online", peer["state"]!.GetValue<string>());
            Assert.Equal("127.0.0.34", peer["address"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task RemovingADeviceAlsoRemovesItsSecretAndProbeState()
    {
        var module = await StartAsync(PeerPreferences("phone-local", "127.0.0.35"));
        var code = new Pairing("pc-peer", "Peer PC", "100.64.0.9", PeerKey).Encode();
        await CallAsync(module, "1", "file-transfer.pair.import", new JsonObject { ["code"] = code });
        Assert.Equal(PeerKey, await _secrets.ReadAsync(SecretReference.Create("file-transfer", "peer-pc-peer"), CancellationToken.None));

        var removed = await CallAsync(module, "2", "file-transfer.peers.remove", new JsonObject { ["deviceId"] = "pc-peer" });
        Assert.Equal("pc-peer", removed["removed"]!.GetValue<string>());
        Assert.Equal(0, removed["peers"]!.GetValue<int>());
        // The credential must not outlive the device entry.
        Assert.Null(await _secrets.ReadAsync(SecretReference.Create("file-transfer", "peer-pc-peer"), CancellationToken.None));
        Assert.Empty((await CallAsync(module, "3", "file-transfer.inspect"))["peers"]!.AsArray());

        var again = await module.ExecuteCommandAsync(new CommandRequest("4", "file-transfer.peers.remove", new JsonObject { ["deviceId"] = "pc-peer" }), CancellationToken.None);
        Assert.False(again.Success);
        Assert.Contains("未找到要移除的设备", again.Output);
    }

    [Fact]
    public async Task ImportingOurOwnCodeAndShortNumericCodesAreRejected()
    {
        var module = await StartAsync(PeerPreferences("pc-local", "127.0.0.36"));
        var own = await module.ExecuteCommandAsync(new CommandRequest("1", "file-transfer.pair.import",
            new JsonObject { ["code"] = new Pairing("pc-local", "This device", "100.64.0.9", PeerKey).Encode() }), CancellationToken.None);
        Assert.False(own.Success);
        Assert.Contains("本机自己", own.Output);

        // A six digit code is not the existing secure connection code and is never accepted as one.
        var numeric = await module.ExecuteCommandAsync(new CommandRequest("2", "file-transfer.pair.import",
            new JsonObject { ["code"] = "123456" }), CancellationToken.None);
        Assert.False(numeric.Success);
        Assert.Contains("连接码", numeric.Output);
        Assert.Throws<ArgumentException>(() => Pairing.Decode("123456"));
        // The rejected imports changed nothing: only the peer that was already configured remains.
        var peers = (await CallAsync(module, "3", "file-transfer.inspect"))["peers"]!.AsArray();
        Assert.Single(peers);
        Assert.Equal("pc-peer", peers[0]!["deviceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task APairwiseTransferConfirmsTheReceiptOnBothSidesAndIsNotARemoteControlGrant()
    {
        // One test owns the fixed transfer port: a receiving module and, later, a raw peer receiver.
        var receivingRoot = Path.Combine(_root, "desktop");
        Directory.CreateDirectory(receivingRoot);
        var receivingSecrets = new InMemorySecretStore();
        var desktop = await StartModuleAsync(receivingRoot, receivingSecrets, RootPeerPreferences(receivingRoot, "pc-peer", "127.0.0.32", "phone-local", "The Phone"));
        try
        {
            await CallAsync(desktop, "1", "file-transfer.receive.start");
            var receiverToken = await receivingSecrets.ReadAsync(SecretReference.Create("file-transfer", "receiver-token"), CancellationToken.None);
            Assert.False(string.IsNullOrEmpty(receiverToken));

            await _secrets.SaveAsync("file-transfer", "peer-pc-peer", receiverToken!, CancellationToken.None);
            var module = await StartAsync(PeerPreferences("phone-local", "127.0.0.32"));
            var file = Path.Combine(_root, "report.txt");
            await File.WriteAllTextAsync(file, "hello");

            await CallAsync(module, "1", "file-transfer.send.direct",
                new JsonObject { ["peerId"] = "pc-peer", ["paths"] = new JsonArray(file) });
            var sent = await WaitForIdleAsync(module);
            var outbound = sent["history"]!.AsArray()[^1]!.AsObject();
            Assert.Equal("completed", outbound["state"]!.GetValue<string>());
            Assert.Equal("send", outbound["direction"]!.GetValue<string>());
            Assert.Equal("Peer PC", outbound["peer"]!.GetValue<string>());
            // The receiver acknowledged the committed file, so the sender can report a confirmed receipt.
            Assert.Equal("direct", outbound["delivery"]!.GetValue<string>());

            var received = await WaitForIdleAsync(desktop);
            var inbound = received["history"]!.AsArray()[^1]!.AsObject();
            Assert.Equal("received", inbound["state"]!.GetValue<string>());
            Assert.Equal("receive", inbound["direction"]!.GetValue<string>());
            Assert.Equal("The Phone", inbound["peer"]!.GetValue<string>());
            Assert.Equal("local", inbound["delivery"]!.GetValue<string>());
            Assert.True(File.Exists(Path.Combine(receivingRoot, "inbox", "report.txt")));

            // The peer answered a real file, so the explicit check may now report it online.
            var online = await CallAsync(module, "2", "file-transfer.peer.check", new JsonObject { ["deviceId"] = "pc-peer" });
            Assert.Equal("online", online["state"]!.GetValue<string>());
            // Nothing about file pairing may hand out control of the other computer.
            Assert.False(online["supportsControl"]!.GetValue<bool>());
        }
        finally
        {
            await desktop.DisposeAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ARelayUploadIsWaitingForTheRecipientInsteadOfReceived()
    {
        var relay = Relay();
        await _secrets.SaveAsync("file-transfer", "password", "relay-password", CancellationToken.None);
        var preferences = PeerPreferences("phone-local", "127.0.0.37");
        preferences["webDavUrl"] = $"http://127.0.0.1:{relay.Port}/dav";
        preferences["username"] = "mpt-relay";
        var module = await StartAsync(preferences);
        var file = Path.Combine(_root, "relay.txt");
        await File.WriteAllTextAsync(file, "waiting for pickup");

        await CallAsync(module, "1", "file-transfer.send.cloud",
            new JsonObject { ["peerId"] = "pc-peer", ["paths"] = new JsonArray(file) });
        var state = await WaitForIdleAsync(module);
        var record = state["history"]!.AsArray()[^1]!.AsObject();
        Assert.Equal("completed", record["state"]!.GetValue<string>());
        // The storage accepted the payload; the recipient has confirmed nothing.
        Assert.Equal("relay-uploaded", record["delivery"]!.GetValue<string>());
        Assert.Equal("send", record["direction"]!.GetValue<string>());
        Assert.Equal("Peer PC", record["peer"]!.GetValue<string>());

        // A real relay conversation is the only thing that may report the relay as reachable.
        var cloud = (await CallAsync(module, "2", "file-transfer.inspect"))["cloud"]!.AsObject();
        Assert.True(cloud["configured"]!.GetValue<bool>());
        Assert.True(cloud["reachable"]!.GetValue<bool>());
        Assert.NotNull(cloud["checkedAt"]);
    }

    [Fact]
    public async Task TheRelayIsNotReportedAsReachableBeforeItWasEverContacted()
    {
        var relay = Relay();
        await _secrets.SaveAsync("file-transfer", "password", "relay-password", CancellationToken.None);
        var preferences = PeerPreferences("phone-local", "127.0.0.38");
        preferences["webDavUrl"] = $"http://127.0.0.1:{relay.Port}/dav";
        preferences["username"] = "mpt-relay";
        var module = await StartAsync(preferences);
        var cloud = (await CallAsync(module, "1", "file-transfer.inspect"))["cloud"]!.AsObject();
        Assert.True(cloud["configured"]!.GetValue<bool>());
        // "Never checked" is null, not false: the page must not claim the relay is down.
        Assert.Null(cloud["reachable"]);
        Assert.Null(cloud["checkedAt"]);

        // A relay that does not answer is reported as unreachable with a message.
        await relay.DisposeAsync();
        var failed = await module.ExecuteCommandAsync(new CommandRequest("2", "file-transfer.cloud.check", new JsonObject()), CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Contains("中转", failed.Output);
        var after = (await CallAsync(module, "3", "file-transfer.inspect"))["cloud"]!.AsObject();
        Assert.False(after["reachable"]!.GetValue<bool>());
        Assert.NotEqual("", after["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ChangingTheRelayAddressClearsTheOldReachabilityResult()
    {
        var relay = Relay();
        await _secrets.SaveAsync("file-transfer", "password", "relay-password", CancellationToken.None);
        var preferences = PeerPreferences("phone-local", "127.0.0.40");
        // Saving settings re-validates the listen address, so keep it a real Tailscale address.
        preferences["listenAddress"] = "100.64.0.40";
        preferences["webDavUrl"] = $"http://127.0.0.1:{relay.Port}/dav";
        preferences["username"] = "mpt-relay";
        var module = await StartAsync(preferences);
        // Receiving is parked so no background pass re-checks the relay while the test asserts the cache.
        await CallAsync(module, "0", "file-transfer.receive.stop");
        await CallAsync(module, "1", "file-transfer.cloud.check");
        Assert.True((await CallAsync(module, "2", "file-transfer.inspect"))["cloud"]!["reachable"]!.GetValue<bool>());

        // A different relay address means the previous answer described a different server.
        await CallAsync(module, "3", "file-transfer.configure", new JsonObject { ["webDavUrl"] = $"http://127.0.0.1:{relay.Port}/dav2" });
        var cloud = (await CallAsync(module, "4", "file-transfer.inspect"))["cloud"]!.AsObject();
        Assert.Null(cloud["checkedAt"]);
        // "Never checked" must stay null instead of turning into "unreachable".
        Assert.Null(cloud["reachable"]);
        Assert.Equal("", cloud["message"]!.GetValue<string>());

        // A new password is part of the relay identity as well.
        await CallAsync(module, "5", "file-transfer.cloud.check");
        await CallAsync(module, "6", "file-transfer.configure", new JsonObject { ["password"] = "another-relay-password" });
        Assert.Null((await CallAsync(module, "7", "file-transfer.inspect"))["cloud"]!["checkedAt"]);
    }

    [Fact]
    public async Task ALocalFailureNeverMarksTheRelayUnreachable()
    {
        var relay = Relay();
        await _secrets.SaveAsync("file-transfer", "password", "relay-password", CancellationToken.None);
        var preferences = PeerPreferences("phone-local", "127.0.0.41");
        preferences["listenAddress"] = "100.64.0.41";
        preferences["webDavUrl"] = $"http://127.0.0.1:{relay.Port}/dav";
        preferences["username"] = "mpt-relay";
        var module = await StartAsync(preferences);
        await CallAsync(module, "1", "file-transfer.cloud.check");
        var reachable = (await CallAsync(module, "2", "file-transfer.inspect"))["cloud"]!.AsObject();
        Assert.True(reachable["reachable"]!.GetValue<bool>());
        var checkedAt = reachable["checkedAt"]!.GetValue<string>();

        // A pasted code that cannot even be parsed is a local error, not a relay failure.
        var parse = await module.ExecuteCommandAsync(new CommandRequest("3", "file-transfer.cloud.import",
            new JsonObject { ["code"] = "not-a-connection-code" }), CancellationToken.None);
        Assert.False(parse.Success);
        // A rejected local setting is a local error too.
        var invalid = await module.ExecuteCommandAsync(new CommandRequest("4", "file-transfer.configure",
            new JsonObject { ["listenAddress"] = "8.8.8.8" }), CancellationToken.None);
        Assert.False(invalid.Success);

        var after = (await CallAsync(module, "5", "file-transfer.inspect"))["cloud"]!.AsObject();
        Assert.True(after["reachable"]!.GetValue<bool>());
        Assert.Equal(checkedAt, after["checkedAt"]!.GetValue<string>());
        Assert.Equal("", after["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheNewCommandsArePublishedInTheCommandList()
    {
        var module = await StartAsync(PeerPreferences("phone-local", "127.0.0.39"));
        var commands = await module.ListCommandsAsync(CancellationToken.None);
        Assert.Contains(commands, command => command.Id == "file-transfer.peer.check");
        Assert.Contains(commands, command => command.Id == "file-transfer.peers.remove");
    }

    /// <summary>Waits for the module's single transfer slot to finish; the test's own bounded poll, not production code.</summary>
    private static async Task<JsonObject> WaitForIdleAsync(FileTransferModule module, bool expectActivity = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var state = Read(await module.ExecuteCommandAsync(new CommandRequest("wait", "file-transfer.inspect", new JsonObject()), timeout.Token));
            var history = state["history"]!.AsArray();
            if (!state["busy"]!.GetValue<bool>() && (!expectActivity || history.Count > 0) && state["progress"]?["state"]?.GetValue<string>() is not "receiving")
                return state;
            await Task.Delay(50, timeout.Token);
        }
    }

    /// <summary>A minimal WebDAV endpoint: it drains the request body and answers 200 to everything.</summary>
    private sealed class WebDavStub : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<string> _requests = [];
        private readonly Task _loop;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public WebDavStub()
        {
            _listener.Start();
            _loop = RunAsync();
        }

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
                    lock (_requests) _requests.Add(request.Split("\r\n")[0]);
                    var length = 0;
                    foreach (var line in request.Split("\r\n"))
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out length);
                    var body = new byte[length];
                    var read = 0;
                    while (read < length)
                    {
                        var count = await stream.ReadAsync(body.AsMemory(read, length - read), _lifetime.Token);
                        if (count == 0) break;
                        read += count;
                    }
                    await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _lifetime.Token);
                    await stream.FlushAsync(_lifetime.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch (Exception) { }
            _lifetime.Cancel();
        }
    }
}
