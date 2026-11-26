using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using FileTransfer.MyPowerTools;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace FileTransfer.Tests;

[CollectionDefinition(PreferencesCollection.Name, DisableParallelization = true)]
public sealed class PreferencesCollection
{
    public const string Name = "assistant-preferences";
}

/// <summary>
/// The durable composer draft: one bounded, atomic, local-only snapshot that survives a restart,
/// keeps a cleared field cleared, restores only attachment references that still exist while naming
/// the rest, treats a target as usable only after a confirmed relationship, and never touches the
/// relay or the receiver. The draft is a preference, not a conversation entry.
/// </summary>
[Collection(PreferencesCollection.Name)]
public sealed class AssistantPreferencesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-assistant-preferences-" + Guid.NewGuid().ToString("N"));
    private readonly List<FileTransferModule> _modules = [];
    private readonly List<HangingEndpoint> _endpoints = [];

    public AssistantPreferencesTests() => Directory.CreateDirectory(_root);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var module in _modules) await module.DisposeAsync(CancellationToken.None);
        foreach (var endpoint in _endpoints) await endpoint.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string DeviceRoot(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonArray Peers(params (string Id, string Name)[] peers)
    {
        var array = new JsonArray();
        foreach (var (id, name) in peers)
            array.Add(new JsonObject { ["deviceId"] = id, ["name"] = name, ["address"] = "" });
        return array;
    }

    /// <summary>
    /// Every module here gets its own loopback relay, so no test depends on the process-wide public
    /// relay seam and a test module can never dial the real endpoint.
    /// </summary>
    private async Task<FileTransferModule> StartAsync(string name, string deviceId, string address, JsonArray? peers = null,
        string relayUrl = "http://127.0.0.1:1/dav")
    {
        var root = DeviceRoot(name);
        var preferences = new JsonObject
        {
            ["deviceId"] = deviceId,
            ["listenAddress"] = address,
            ["receiveDirectory"] = Path.Combine(root, "inbox"),
            ["peers"] = peers ?? new JsonArray(),
            ["webDavUrl"] = relayUrl,
            ["username"] = "mpt-relay"
        };
        await File.WriteAllTextAsync(Path.Combine(root, "preferences.json"), preferences.ToJsonString());
        var secrets = new InMemorySecretStore();
        await secrets.SaveAsync("file-transfer", "password", "relay-password", CancellationToken.None);
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", root, root, root, "linux",
            ["secret.store"], new Dictionary<string, object> { ["secret.store"] = secrets });
        var module = new FileTransferModule();
        _modules.Add(module);
        Assert.True((await module.InitializeAsync(context, CancellationToken.None)).Ok);
        return module;
    }

    private async Task<FileTransferModule> RestartAsync(FileTransferModule module, string name, string deviceId,
        string address, JsonArray? peers = null)
    {
        _modules.Remove(module);
        await module.DisposeAsync(CancellationToken.None);
        return await StartAsync(name, deviceId, address, peers);
    }

    private static async Task<JsonObject> CallAsync(FileTransferModule module, string command, JsonObject? args = null)
    {
        var result = await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString("N"), command, args ?? new JsonObject()), CancellationToken.None);
        Assert.True(result.Success, result.Output);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    private static async Task<CommandExecutionResult> TryAsync(FileTransferModule module, string command, JsonObject args) =>
        await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString("N"), command, args), CancellationToken.None);

    private static string? Text(JsonObject answer, string key) => answer[key]?.GetValue<string>();

    private static string[] Paths(JsonObject answer) =>
        answer["attachmentPaths"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();

    private static string[] Missing(JsonObject answer) =>
        answer["missingAttachments"]!.AsArray().Select(node => node!["path"]!.GetValue<string>()).ToArray();

    private string SourceFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, name);
        return path;
    }

    [Fact]
    public async Task ConversationDraftsStayIndependentAcrossSwitchRestartAndRejectedUpdates()
    {
        var module = await StartAsync("map", "phone-a", "127.0.0.88", Peers(("phone-b", "Phone B")));
        var shared = (await CallAsync(module, "file-transfer.assistant.inspect"))["identity"]!["conversationKey"]!.GetValue<string>();
        var attachment = SourceFile("draft-map.txt");
        await CallAsync(module, "file-transfer.assistant.preferences.update", new JsonObject
        { ["conversationKey"] = shared, ["draftText"] = "shared draft", ["scrollOffset"] = 12.5,
            ["attachmentPaths"] = new JsonArray(attachment), ["lastReadAt"] = "2026-09-29T00:00:00Z" });
        var second = await CallAsync(module, "file-transfer.assistant.preferences.update", new JsonObject
        { ["conversationKey"] = "device:phone-b", ["draftText"] = "private draft", ["scrollOffset"] = 30 });
        Assert.Equal("device:phone-b", Text(second, "conversationKey"));
        Assert.Equal("phone-b", Text(second, "targetDeviceId"));
        Assert.Equal("shared draft", second["drafts"]![shared]!["draftText"]!.GetValue<string>());
        Assert.Empty(Paths(second));
        var switched = await CallAsync(module, "file-transfer.assistant.preferences.update", new JsonObject { ["conversationKey"] = shared });
        Assert.Equal("shared draft", Text(switched, "draftText"));
        Assert.Equal(new[] { attachment }, Paths(switched));
        Assert.Equal(12.5, switched["scrollOffset"]!.GetValue<double>());
        Assert.Equal("private draft", switched["drafts"]!["device:phone-b"]!["draftText"]!.GetValue<string>());
        var rejected = await module.ExecuteCommandAsync(new CommandRequest("bad-map", "file-transfer.assistant.preferences.update",
            new JsonObject { ["conversationKey"] = "device:phone-b", ["draftText"] = "must roll back", ["scrollOffset"] = -1 }), CancellationToken.None);
        Assert.False(rejected.Success);
        var unchanged = await CallAsync(module, "file-transfer.assistant.preferences.inspect");
        Assert.Equal(shared, Text(unchanged, "conversationKey"));
        Assert.Equal("private draft", unchanged["drafts"]!["device:phone-b"]!["draftText"]!.GetValue<string>());
        var restored = await new AssistantStore(Path.Combine(_root, "map", "assistant")).LoadAsync(CancellationToken.None);
        Assert.Equal("shared draft", restored.Drafts[shared].DraftText);
        Assert.Equal("private draft", restored.Drafts["device:phone-b"].DraftText);
        Assert.Equal(12.5, restored.Drafts[shared].ScrollOffset);
    }

    [Fact]
    public async Task TheDraftSurvivesARestartAndOnlyClearsWhenExplicitlyCleared()
    {
        var first = SourceFile("first.txt");
        var second = SourceFile("second.txt");
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.81", Peers(("phone-b", "Phone B")));

        // No target means "send to myself", which the contract labels with the tool's own name.
        var fresh = await CallAsync(module, "file-transfer.assistant.preferences.inspect");
        Assert.Null(fresh["draftText"]);
        Assert.Null(fresh["targetDeviceId"]);
        Assert.Equal("文件传输助手", Text(fresh, "targetName"));
        Assert.True(fresh["targetUsable"]!.GetValue<bool>());
        Assert.Null(fresh["savedAt"]);

        var saved = await CallAsync(module, "file-transfer.assistant.preferences.update", new JsonObject
        {
            ["draftText"] = "还没写完",
            ["attachmentPaths"] = new JsonArray(first, second),
            ["targetDeviceId"] = "phone-b"
        });
        Assert.Equal("还没写完", Text(saved, "draftText"));
        Assert.Equal(new[] { first, second }, Paths(saved));
        Assert.Empty(Missing(saved));
        Assert.Equal("phone-b", Text(saved, "targetDeviceId"));
        Assert.Equal("Phone B", Text(saved, "targetName"));
        Assert.True(saved["targetUsable"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(Text(saved, "savedAt")));
        // The wire answer always carries the full snapshot, including explicit nulls, so the page can
        // distinguish "cleared" from "the module did not answer".
        foreach (var key in new[] { "draftText", "attachmentPaths", "missingAttachments", "targetDeviceId", "targetName", "targetUsable", "savedAt" })
            Assert.True(saved.ContainsKey(key), key);
        // It lives in the one assistant state file next to the conversation, not in a second store.
        var stateFile = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "phone-a", "assistant", "assistant.json")))!.AsObject();
        var persisted = stateFile["preferences"]!.AsObject();
        Assert.Equal("还没写完", Text(persisted, "draftText"));
        Assert.Contains(first, persisted["attachmentPaths"]!.AsArray().Select(node => node!.GetValue<string>()));

        // A field absent from the request keeps its stored value; only the mentioned field changes.
        var partial = await CallAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["draftText"] = "改了一下" });
        Assert.Equal("改了一下", Text(partial, "draftText"));
        Assert.Equal(new[] { first, second }, Paths(partial));
        Assert.Equal("phone-b", Text(partial, "targetDeviceId"));
        // Re-saving the identical snapshot is not a new write: the save time does not move, so a UI
        // that re-saves on every tick cannot churn the whole conversation state file.
        await Task.Delay(30);
        var again = await CallAsync(module, "file-transfer.assistant.preferences.update", new JsonObject
        {
            ["draftText"] = "改了一下",
            ["attachmentPaths"] = new JsonArray(first, second),
            ["targetDeviceId"] = "phone-b"
        });
        Assert.Equal(Text(partial, "savedAt"), Text(again, "savedAt"));
        var inspect = await CallAsync(module, "file-transfer.assistant.preferences.inspect");
        Assert.Equal(partial.ToJsonString(), inspect.ToJsonString());

        // A draft is not a message: nothing was enqueued and nothing is in the timeline.
        Assert.Empty((await CallAsync(module, "file-transfer.assistant.inspect"))["items"]!.AsArray());

        // A restart reloads the same snapshot from the store, including the stable target id.
        var restarted = await RestartAsync(module, "phone-a", "phone-a", "127.0.0.81", Peers(("phone-b", "Phone B")));
        var afterRestart = await CallAsync(restarted, "file-transfer.assistant.preferences.inspect");
        Assert.Equal("改了一下", Text(afterRestart, "draftText"));
        Assert.Equal(new[] { first, second }, Paths(afterRestart));
        Assert.Equal("phone-b", Text(afterRestart, "targetDeviceId"));
        Assert.Equal("Phone B", Text(afterRestart, "targetName"));
        Assert.True(afterRestart["targetUsable"]!.GetValue<bool>());

        // A file that disappeared is reported, never restored as if it were usable; the remaining
        // reference is what an update sends back, which prunes the stored one.
        File.Delete(second);
        var missing = await CallAsync(restarted, "file-transfer.assistant.preferences.inspect");
        Assert.Equal(new[] { first }, Paths(missing));
        var gone = Assert.Single(missing["missingAttachments"]!.AsArray())!.AsObject();
        Assert.Equal(second, Text(gone, "path"));
        Assert.Equal("second.txt", Text(gone, "name"));
        var pruned = await CallAsync(restarted, "file-transfer.assistant.preferences.update",
            new JsonObject { ["attachmentPaths"] = new JsonArray(first) });
        Assert.Equal(new[] { first }, Paths(pruned));
        Assert.Empty(Missing(pruned));

        // Explicitly cleared values are written as cleared: a later read cannot resurrect them.
        var cleared = await CallAsync(restarted, "file-transfer.assistant.preferences.update", new JsonObject
        {
            ["draftText"] = null,
            ["attachmentPaths"] = new JsonArray(),
            ["targetDeviceId"] = null
        });
        Assert.Null(cleared["draftText"]);
        Assert.Empty(Paths(cleared));
        Assert.Null(cleared["targetDeviceId"]);
        Assert.Equal("文件传输助手", Text(cleared, "targetName"));
        Assert.True(cleared["targetUsable"]!.GetValue<bool>());
        // A cleared field is an explicit null on the wire, not an omitted key the page could misread.
        Assert.True(cleared.ContainsKey("draftText"));
        Assert.True(cleared.ContainsKey("targetDeviceId"));
        Assert.True(cleared.ContainsKey("savedAt"));
        var third = await RestartAsync(restarted, "phone-a", "phone-a", "127.0.0.81", Peers(("phone-b", "Phone B")));
        var final = await CallAsync(third, "file-transfer.assistant.preferences.inspect");
        Assert.Null(final["draftText"]);
        Assert.Empty(Paths(final));
        Assert.Null(final["targetDeviceId"]);
        Assert.Equal("文件传输助手", Text(final, "targetName"));
    }

    [Fact]
    public async Task UnusableOrRejectedSnapshotsNeverChangeTheStoredDraft()
    {
        var kept = SourceFile("kept.txt");
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.83");
        await CallAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["draftText"] = "保留", ["attachmentPaths"] = new JsonArray(kept) });

        // A vanished file is named in the answer and dropped from the stored snapshot, while the rest
        // of the request is still saved: one unusable reference must not fail the whole draft.
        var gone = Path.Combine(_root, "gone.bin");
        var vanished = await CallAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["draftText"] = "还在", ["attachmentPaths"] = new JsonArray(gone) });
        Assert.Equal("还在", Text(vanished, "draftText"));
        Assert.Empty(Paths(vanished));
        var missing = Assert.Single(vanished["missingAttachments"]!.AsArray())!.AsObject();
        Assert.Equal(gone, Text(missing, "path"));
        Assert.Equal("gone.bin", Text(missing, "name"));
        var afterVanished = await CallAsync(module, "file-transfer.assistant.preferences.inspect");
        Assert.Equal("还在", Text(afterVanished, "draftText"));
        Assert.Empty(Paths(afterVanished));
        Assert.Empty(Missing(afterVanished));

        // Restore a good snapshot, then prove every rejected request is one atomic no-op.
        await CallAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["draftText"] = "保留", ["attachmentPaths"] = new JsonArray(kept) });
        var relative = await TryAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["draftText"] = "被拒绝", ["attachmentPaths"] = new JsonArray("relative.txt") });
        Assert.False(relative.Success, relative.Output);
        var wrongType = await TryAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["draftText"] = 42 });
        Assert.False(wrongType.Success, wrongType.Output);
        var unknownKey = await TryAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["draft"] = "拼错的名字" });
        Assert.False(unknownKey.Success, unknownKey.Output);

        var after = await CallAsync(module, "file-transfer.assistant.preferences.inspect");
        Assert.Equal("保留", Text(after, "draftText"));
        Assert.Equal(new[] { kept }, Paths(after));
        Assert.Empty(Missing(after));
    }

    [Fact]
    public async Task OnlyAuthorizedTargetsAreUsable()
    {
        // The peer needs a real candidate address for a discovery window to remember it; a loopback
        // address is a valid candidate but not a valid Tailnet probe target, which is exactly the
        // "seen but not authorized" case.
        var peers = new JsonArray(new JsonObject { ["deviceId"] = "phone-b", ["name"] = "Phone B", ["address"] = "127.0.0.87" });
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.84", peers);
        // A discovery window remembers the candidate the way the send protocol does.
        await CallAsync(module, "file-transfer.assistant.devices");

        var saved = await CallAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["targetDeviceId"] = "phone-b" });
        Assert.Equal("phone-b", Text(saved, "targetDeviceId"));
        Assert.Equal("Phone B", Text(saved, "targetName"));
        Assert.True(saved["targetUsable"]!.GetValue<bool>());

        // Removing the pairing must not silently turn a targeted draft into a self draft.
        await CallAsync(module, "file-transfer.peers.remove", new JsonObject { ["deviceId"] = "phone-b" });
        var removed = await CallAsync(module, "file-transfer.assistant.preferences.inspect");
        Assert.Equal("phone-b", Text(removed, "targetDeviceId"));
        Assert.False(removed["targetUsable"]!.GetValue<bool>());
        Assert.Equal("", Text(removed, "targetName"));

        // Discovery is not pairing and cannot silently enable private delivery.
        var sent = await module.ExecuteCommandAsync(new CommandRequest("private-unpaired", "file-transfer.assistant.send",
            new JsonObject { ["text"] = "发现不等于授权", ["targetDeviceId"] = "phone-b" }), CancellationToken.None);
        Assert.False(sent.Success);
        Assert.Contains("配对", sent.Output);

        // The page's explicit choice is what restores self; the local device id also means self.
        var self = await CallAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["targetDeviceId"] = null });
        Assert.Null(self["targetDeviceId"]);
        Assert.True(self["targetUsable"]!.GetValue<bool>());
        Assert.Equal("文件传输助手", Text(self, "targetName"));
        var local = await CallAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["targetDeviceId"] = "phone-a" });
        Assert.Null(local["targetDeviceId"]);
        Assert.Equal("文件传输助手", Text(local, "targetName"));
    }

    [Fact]
    public async Task AttachmentDeduplicationFollowsTheFileSystemCaseRule()
    {
        var first = SourceFile("Case-Draft.txt");
        var second = Path.Combine(_root, "case-draft.txt");
        // On Windows the two spellings are one file; on Linux and Android they are two real files.
        File.WriteAllText(second, OperatingSystem.IsWindows() ? "Case-Draft.txt" : "second");
        var expected = OperatingSystem.IsWindows() ? new[] { first } : new[] { first, second };
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.86");

        var saved = await CallAsync(module, "file-transfer.assistant.preferences.update",
            new JsonObject { ["attachmentPaths"] = new JsonArray(first, second) });
        Assert.Empty(Missing(saved));
        Assert.Equal(expected, Paths(saved));

        // A state file that already holds both spellings is repaired on load, so a hand-edited or
        // older file can never offer the same file twice either.
        _modules.Remove(module);
        await module.DisposeAsync(CancellationToken.None);
        var store = new AssistantStore(Path.Combine(_root, "phone-a", "assistant"));
        await store.MutateAsync(state => state.Preferences = new AssistantPreferences
        {
            AttachmentPaths = [first, second],
            SavedAt = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var reloaded = await StartAsync("phone-a", "phone-a", "127.0.0.86");
        var restored = await CallAsync(reloaded, "file-transfer.assistant.preferences.inspect");
        Assert.Equal(expected, Paths(restored));
        Assert.Empty(Missing(restored));
    }

    [Fact]
    public async Task PreferencesWritesNeedNoRelayAndNeverRestartTheReceiver()
    {
        await using var endpoint = new HangingEndpoint();
        _endpoints.Add(endpoint);
        var module = await StartAsync("phone-a", "phone-a", "127.0.0.85", Peers(("phone-b", "Phone B")),
            endpoint.BaseAddress.ToString());
        var file = SourceFile("draft.txt");

        // Receiving keeps the relay path busy; stop it so this window measures only the commands.
        await CallAsync(module, "file-transfer.receive.stop");
        // The module's own relay pass must already be parked on the endpoint: otherwise "no new
        // connection" would be vacuous instead of evidence.
        await WaitForFirstConnectionAsync(endpoint);
        var idleAt = await IdleConnectionCountAsync(endpoint);
        Assert.True(idleAt >= 1, "relay path was never exercised");

        var before = await CallAsync(module, "file-transfer.assistant.inspect");
        await CallAsync(module, "file-transfer.assistant.preferences.inspect");
        await CallAsync(module, "file-transfer.assistant.preferences.update", new JsonObject
        {
            ["draftText"] = "断网也能保存",
            ["attachmentPaths"] = new JsonArray(file),
            ["targetDeviceId"] = "phone-b"
        });
        await Task.Delay(300);

        // No relay connection was opened and the receiver was not switched back on or restarted.
        Assert.Equal(idleAt, endpoint.Connections);
        var after = await CallAsync(module, "file-transfer.assistant.inspect");
        Assert.False(after["receiving"]!.GetValue<bool>());
        Assert.Equal(before["receiving"]!.ToJsonString(), after["receiving"]!.ToJsonString());
        Assert.Equal(before["receivingDetails"]!.ToJsonString(), after["receivingDetails"]!.ToJsonString());
        var settings = await CallAsync(module, "file-transfer.inspect");
        Assert.False(settings["busy"]!.GetValue<bool>());
        Assert.Null(settings["progress"]);
        // The write produced no conversation entry, so it cannot be mistaken for a sent message.
        Assert.Empty(after["items"]!.AsArray());
        // The draft really was saved even though the relay never answered.
        Assert.Equal("断网也能保存", Text(await CallAsync(module, "file-transfer.assistant.preferences.inspect"), "draftText"));
    }

    private static async Task WaitForFirstConnectionAsync(HangingEndpoint endpoint)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (endpoint.Connections == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(25);
    }

    /// <summary>Stops when the endpoint has seen no new connection for two full intervals.</summary>
    private static async Task<int> IdleConnectionCountAsync(HangingEndpoint endpoint)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var stable = 0;
        var last = endpoint.Connections;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(400);
            var current = endpoint.Connections;
            if (current == last)
            {
                if (++stable >= 2) return current;
            }
            else
            {
                stable = 0;
                last = current;
            }
        }
        return last;
    }

    /// <summary>
    /// A loopback relay that counts every connection and then holds it open without answering. A
    /// background relay pass parks here instead of failing and retrying, so any connection opened while
    /// the preferences commands run is attributable to those commands.
    /// </summary>
    private sealed class HangingEndpoint : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly List<TcpClient> _clients = [];
        private readonly Task _loop;
        private int _connections;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public Uri BaseAddress => new($"http://127.0.0.1:{Port}/dav");
        public int Connections => Volatile.Read(ref _connections);

        public HangingEndpoint()
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
                Interlocked.Increment(ref _connections);
                // Deliberately never read or answer: the caller stays blocked in its request.
                lock (_clients) _clients.Add(client);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Stop();
            lock (_clients)
            {
                foreach (var client in _clients) client.Dispose();
                _clients.Clear();
            }
            try { await _loop; } catch (Exception) { }
        }
    }
}
