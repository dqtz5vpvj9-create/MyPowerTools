using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FileTransfer.Surface.Tests;

/// <summary>
/// A module stand-in that answers exactly the file-transfer commands the page may call and records
/// what it was asked to do. Nothing here invents a state the real module cannot produce: the
/// responses use the same JSON shape as <c>FileTransfer.MyPowerTools</c>, and a terminal state is
/// only reported when the test asks for it.
/// </summary>
internal sealed class FakeTransferModule
{
    private readonly List<(string Command, JsonObject? Args)> _calls = [];
    private readonly List<Action<MptSurfaceEvent>> _subscribers = [];

    public string DeviceId { get; set; } = "mpt-phone";
    public string ReceiveDirectory { get; set; } = "/data/user/0/com.mypowertools/files/Download";
    public string ListenAddress { get; set; } = "100.64.0.7";
    public string WebDavUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string LastPeer { get; set; } = "";
    public bool Receiving { get; set; }
    public bool Busy { get; set; }
    public bool OpenListRunning { get; set; }
    public JsonArray Peers { get; } = [];
    public JsonObject? Progress { get; set; }
    public List<string> FailCommands { get; } = [];

    /// <summary>Per-device reachability as the module reports it, keyed by device id.</summary>
    public Dictionary<string, string> PeerStates { get; } = new(StringComparer.Ordinal);

    /// <summary>Commands this module build does not implement, so the page's optional-capability
    /// path can be exercised exactly like an older installed module.</summary>
    public HashSet<string> MissingCommands { get; } = new(StringComparer.Ordinal);

    /// <summary>Null means "never checked"; the module only reports a real conversation.</summary>
    public bool? CloudReachable { get; set; }
    public string CloudMessage { get; set; } = "";

    // ---- file-assistant session (the FILE_ASSISTANT_CONTRACT JSON shapes) ---------------------

    /// <summary>The conversation as the module would report it, keyed by item id.</summary>
    public List<JsonObject> AssistantItems { get; } = [];
    public JsonObject DraftPreferences { get; private set; } = new();

    /// <summary>Inbound requests waiting for this device's answer.</summary>
    public List<JsonObject> AssistantRequests { get; } = [];

    /// <summary>Devices that discovery would find. Empty means a real empty result.</summary>
    public List<JsonObject> AssistantDevices { get; } = [];

    /// <summary>
    /// The code <c>link.export</c> returns. The default is a full-length assistant connection code —
    /// the module's assistant payload including relay configuration — because a short placeholder would
    /// hide a QR symbol that is too dense or too large for the first screen.
    /// </summary>
    public string LinkCode { get; set; } =
        FileTransfer.MyPowerTools.LinkCode.Encode(new FileTransfer.MyPowerTools.LinkCode.Payload(
            1, "conversation-0123456789abcdef", new string('a', 64), "mpt-phone-7f3a19c2",
            "我的手机", "100.64.0.7", 49540, "android",
            new FileTransfer.Core.CloudConnection("https://relay.example.com/dav/我的文件助手", "mpt-test-device",
                "synthetic-relay-password-for-qr-layout").Encode()));

    public string AssistantIdentityId { get; set; } = "mpt-phone";
    public string AssistantIdentityName { get; set; } = "我的手机";
    public bool AssistantLinked { get; set; }
    public string AssistantRelayState { get; set; } = "unconfigured";
    public string AssistantRelayMessage { get; set; } = "";
    public string AssistantDevicesMessage { get; set; } = "";

    /// <summary>The module's own discovery verdict: completed, partial or unsupported.</summary>
    public string AssistantDiscoveryState { get; set; } = "completed";

    /// <summary>Set to make assistant.send report accepted:false, the way a refused write behaves.</summary>
    public bool RefuseAssistantSend { get; set; }

    /// <summary>Commands this module build does not implement, exercised as an older install.</summary>
    public List<JsonObject> AssistantOpenCalls { get; } = [];

    public IReadOnlyList<(string Command, JsonObject? Args)> Calls => _calls;

    public int CountCalls(string command) => _calls.Count(call => call.Command == command);

    public JsonObject LastArgs(string command) =>
        _calls.LastOrDefault(call => call.Command == command).Args ?? new JsonObject();

    public void AddPeer(string deviceId, string name, string address = "")
    {
        Peers.Add(new JsonObject { ["deviceId"] = deviceId, ["name"] = name, ["address"] = address });
    }

    public void SetPeerState(string deviceId, string state, string message = "")
    {
        PeerStates[deviceId] = state;
        _peerMessages[deviceId] = message;
    }

    private readonly Dictionary<string, string> _peerMessages = new(StringComparer.Ordinal);

    /// <summary>Lets a test hold one command open, so a slow module can be exercised.</summary>
    public Func<string, Task?>? BeforeAnswer { get; set; }

    public MptAvaloniaSurfaceContext Context(
        string dataDirectory,
        string theme = "light",
        Func<CancellationToken, Task<string?>>? scanConnectionCode = null) => new(
        "file-transfer",
        "workspace",
        dataDirectory,
        theme,
        ExecuteAsync,
        (_, _, _) => Task.CompletedTask,
        null!,
        _ => { },
        Subscribe)
    {
        ScanConnectionCodeAsync = scanConnectionCode
    };

    private IDisposable Subscribe(Action<MptSurfaceEvent> handler)
    {
        _subscribers.Add(handler);
        return new Subscription(() => _subscribers.Remove(handler));
    }

    /// <summary>Raises a real <c>transfer.changed</c> event, the only way a transfer result appears.</summary>
    public void Emit(string name, string state, long done = 0, long total = 0, string message = "")
    {
        var payload = new JsonObject
        {
            ["name"] = name,
            ["state"] = state,
            ["done"] = done,
            ["total"] = total,
            ["message"] = message,
            ["time"] = DateTimeOffset.UtcNow.ToString("O")
        };
        foreach (var handler in _subscribers.ToArray())
            handler(new MptSurfaceEvent(1, "file-transfer", "transfer.changed", DateTimeOffset.UtcNow, payload));
    }

    private Task<CommandExecutionResult> ExecuteAsync(string command, JsonObject? args, CancellationToken token)
    {
        var name = command.StartsWith("file-transfer.", StringComparison.Ordinal) ? command["file-transfer.".Length..] : command;
        _calls.Add((name, args?.DeepClone().AsObject()));
        if (BeforeAnswer?.Invoke(name) is { } gate)
            return AwaitGateAsync(gate, command, args);
        if (MissingCommands.Contains(name))
            return Task.FromResult(new CommandExecutionResult(command, command, "failed", false, "",
                new MptRuntimeError("command.unknown", $"未知命令：{name}")));
        if (FailCommands.Contains(name))
            return Task.FromResult(new CommandExecutionResult(command, command, "failed", false, "", new MptRuntimeError("module.rejected", "模块拒绝了这次调用。")));

        if (name.StartsWith("assistant.", StringComparison.Ordinal))
            return Task.FromResult(new CommandExecutionResult(command, command, "completed", true, Assistant(name, args)));

        var output = name switch
        {
            "inspect" => Inspect(),
            "pairing" => """{"code":"mpt://pair/eyJuYW1lIjoiV29yayBQQyJ9"}""",
            "pair.preview" => """{"deviceId":"pc-new","name":"新电脑"}""",
            "pair.import" => PairImport(args),
            "cloud.import" => """{"connected":true}""",
            "cloud.export" => """{"code":"mpt://cloud/eyJ1cmwiOiJodHRwczovL29wZW5saXN0LmV4YW1wbGUudGVzdC9kYXYvdHJhbnNmZXIifQ=="}""",
            "cloud.check" => """{"connected":true}""",
            "cloud.list" => "[]",
            "peer.check" => PeerCheck(args),
            "peers.remove" => RemovePeer(args),
            "send.direct" or "send.cloud" or "cloud.download" => """{"started":true}""",
            "cancel" => """{"cancelled":true}""",
            "receive.start" => """{"receiving":true}""",
            "receive.stop" => """{"receiving":false}""",
            "configure" => """{"saved":true}""",
            _ => "{}"
        };
        return Task.FromResult(new CommandExecutionResult(command, command, "completed", true, output));
    }

    /// <summary>
    /// The file-assistant contract. Every answer uses the exact JSON the real module is specified to
    /// return, so a test can only pass by consuming that contract correctly; nothing here fabricates
    /// a state the module would not produce.
    /// </summary>
    /// <summary>Answers a held command once the test releases it, so a slow module is observable.</summary>
    private async Task<CommandExecutionResult> AwaitGateAsync(Task gate, string command, JsonObject? args)
    {
        await gate;
        var body = command.EndsWith("assistant.send", StringComparison.Ordinal)
            ? Assistant("assistant.send", args)
            : """{"accepted":true,"itemIds":[]}""";
        return new CommandExecutionResult(command, command, "completed", true, body);
    }

    private string Assistant(string command, JsonObject? args)
    {
        // The dispatcher above already stripped "file-transfer."; strip the group prefix too.
        var name = command.StartsWith("assistant.", StringComparison.Ordinal) ? command["assistant.".Length..] : command;
        switch (name)
        {
            case "preferences.inspect": return DraftPreferences.ToJsonString();
            case "preferences.update":
                var drafts = DraftPreferences["drafts"]?.DeepClone() as JsonObject;
                DraftPreferences = args!.DeepClone().AsObject();
                if (drafts is not null && args["conversationKey"]?.GetValue<string>() is { } conversationKey)
                {
                    drafts[conversationKey] = args.DeepClone();
                    DraftPreferences["drafts"] = drafts;
                }
                return DraftPreferences.ToJsonString();
            case "inspect":
            case "sync":
                return AssistantSession();
            case "send":
            {
                if (RefuseAssistantSend) return """{"accepted":false,"itemIds":[]}""";
                var id = "item-" + (AssistantItems.Count + 1);
                var paths = (args?["paths"] as JsonArray)?.Select(node => node?.GetValue<string>() ?? "").Where(path => path.Length > 0).ToArray() ?? [];
                var text = args?["text"]?.GetValue<string>();
                var target = args?["targetDeviceId"]?.GetValue<string>();
                // A send with no target is "to myself": it is queued for the shared session.
                AssistantItems.Add(new JsonObject
                {
                    ["id"] = id,
                    ["kind"] = paths.Length > 0 ? (IsImage(paths[0]) ? "image" : "file") : "text",
                    ["text"] = text,
                    ["name"] = paths.Length > 0 ? Path.GetFileName(paths[0]) : null,
                    ["size"] = paths.Length > 0 ? new FileInfo(paths[0]).Length : 0,
                    ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["senderDeviceId"] = AssistantIdentityId,
                    ["senderName"] = AssistantIdentityName,
                    ["targetDeviceId"] = target,
                    ["state"] = target is { Length: > 0 } ? "sending" : "queued",
                    ["bytesDone"] = 0,
                    ["receipts"] = new JsonArray()
                });
                return new JsonObject { ["accepted"] = true, ["itemIds"] = new JsonArray(id) }.ToJsonString();
            }
            case "retry":
            case "cancel":
            {
                var itemId = args?["itemId"]?.GetValue<string>() ?? "";
                var item = AssistantItems.FirstOrDefault(entry => entry["id"]?.GetValue<string>() == itemId);
                if (item is not null)
                    item["state"] = name == "retry"
                        ? item["senderDeviceId"]?.GetValue<string>() is { Length: > 0 } sender && sender != AssistantIdentityId ? "stored" : "queued"
                        : "cancelled";
                return """{"ok":true}""";
            }
            case "open":
            {
                var itemId = args?["itemId"]?.GetValue<string>() ?? "";
                var item = AssistantItems.FirstOrDefault(entry => entry["id"]?.GetValue<string>() == itemId);
                if (item is null) return """{"needsDownload":true}""";
                var kind = item["kind"]?.GetValue<string>();
                if (kind == "text")
                    return new JsonObject { ["text"] = item["text"]?.GetValue<string>() ?? "" }.ToJsonString();
                var local = item["localPath"]?.GetValue<string>();
                if (local is { Length: > 0 }) return new JsonObject { ["path"] = local }.ToJsonString();
                // No local payload yet: the module downloads it and returns the real path.
                local = Path.Combine(Path.GetTempPath(), "mpt-assistant-" + itemId + ".bin");
                File.WriteAllText(local, "downloaded");
                item["localPath"] = local;
                item["state"] = "available";
                return new JsonObject { ["path"] = local }.ToJsonString();
            }
            case "devices":
                return new JsonObject
                {
                    ["devices"] = new JsonArray(AssistantDevices.Select(device => (JsonNode?)device.DeepClone()).ToArray()),
                    ["discoveryState"] = AssistantDiscoveryState,
                    ["message"] = AssistantDevicesMessage
                }.ToJsonString();
            case "receive.respond":
            {
                var requestId = args?["requestId"]?.GetValue<string>() ?? "";
                AssistantRequests.RemoveAll(request => request["requestId"]?.GetValue<string>() == requestId);
                return """{"ok":true}""";
            }
            case "link.export":
                return new JsonObject { ["code"] = LinkCode }.ToJsonString();
            case "link.preview":
                return """{"name":"我的电脑","scope":"文件会话","deviceCount":2}""";
            case "link.import":
                AssistantLinked = true;
                return """{"ok":true}""";
            default:
                return "{}";
        }
    }

    private string AssistantSession() => new JsonObject
    {
        ["identity"] = new JsonObject
        {
            ["id"] = AssistantIdentityId,
            ["name"] = AssistantIdentityName,
            ["linked"] = AssistantLinked
        },
        ["items"] = new JsonArray(AssistantItems.Select(item => (JsonNode?)item.DeepClone()).ToArray()),
        ["pendingRequests"] = new JsonArray(AssistantRequests.Select(request => (JsonNode?)request.DeepClone()).ToArray()),
        ["relay"] = new JsonObject
        {
            ["configured"] = AssistantRelayState is not "unconfigured",
            ["state"] = AssistantRelayState,
            ["message"] = AssistantRelayMessage
        },
        ["receiving"] = Receiving
    }.ToJsonString();

    /// <summary>Raises the module's own session-changed signal.</summary>
    public void EmitAssistantChanged()
    {
        foreach (var handler in _subscribers.ToArray())
            handler(new MptSurfaceEvent(1, "file-transfer", "file-transfer.assistant.changed", DateTimeOffset.UtcNow, new JsonObject()));
    }

    private static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif";

    /// <summary>A peer check answers with the module's own verdict, not with an assumption.</summary>
    private string PeerCheck(JsonObject? args)
    {
        var peerId = args?["peerId"]?.GetValue<string>() ?? "";
        var state = PeerStates.TryGetValue(peerId, out var value) ? value : "unknown";
        SetPeerState(peerId, state, state == "online" ? "" : "没有收到对方应答。");
        return new JsonObject
        {
            ["deviceId"] = peerId,
            ["state"] = state,
            ["checkedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["message"] = _peerMessages.GetValueOrDefault(peerId, "")
        }.ToJsonString();
    }

    private string RemovePeer(JsonObject? args)
    {
        var peerId = args?["peerId"]?.GetValue<string>() ?? "";
        var match = Peers.FirstOrDefault(node => node?["deviceId"]?.GetValue<string>() == peerId);
        if (match is not null) Peers.Remove(match);
        PeerStates.Remove(peerId);
        return """{"removed":true}""";
    }

    private string PairImport(JsonObject? args)
    {
        var code = args?["code"]?.GetValue<string>() ?? "";
        AddPeer("peer-from-code", code.Length > 40 ? "Work PC" : "Shared PC", "100.64.0.9");
        return """{"paired":"Work PC"}""";
    }

    public string Inspect() => new JsonObject
    {
        ["settings"] = new JsonObject
        {
            ["deviceId"] = DeviceId,
            ["receiveDirectory"] = ReceiveDirectory,
            ["listenAddress"] = ListenAddress,
            ["webDavUrl"] = WebDavUrl,
            ["username"] = Username,
            ["lastPeer"] = LastPeer,
            ["peers"] = PeerStatesJson()
        },
        ["cloud"] = new JsonObject
        {
            ["configured"] = WebDavUrl.Length > 0 && Username.Length > 0,
            ["reachable"] = WebDavUrl.Length > 0 && Username.Length > 0 ? CloudReachable : false,
            ["checkedAt"] = CloudReachable is null ? null : DateTimeOffset.UtcNow.ToString("O"),
            ["message"] = CloudMessage
        },
        ["addresses"] = new JsonArray(ListenAddress),
        ["receiving"] = Receiving,
        ["openListRunning"] = OpenListRunning,
        ["adminUrl"] = OpenListRunning ? "http://127.0.0.1:15244/" : "",
        ["busy"] = Busy,
        ["progress"] = Progress?.DeepClone(),
        ["history"] = new JsonArray()
    }.ToJsonString();

    /// <summary>Peers with the module's optional reachability fields attached.</summary>
    private JsonArray PeerStatesJson()
    {
        var array = new JsonArray();
        foreach (var node in Peers)
        {
            if (node is not JsonObject peer) continue;
            var deviceId = peer["deviceId"]?.GetValue<string>() ?? "";
            var state = PeerStates.TryGetValue(deviceId, out var value) ? value : "unknown";
            array.Add(new JsonObject
            {
                ["deviceId"] = deviceId,
                ["name"] = peer["name"]?.GetValue<string>() ?? "",
                ["address"] = peer["address"]?.GetValue<string>() ?? "",
                ["state"] = state,
                ["checkedAt"] = state == "unknown" ? null : DateTimeOffset.UtcNow.ToString("O"),
                ["message"] = _peerMessages.GetValueOrDefault(deviceId, ""),
                ["supportsControl"] = false
            });
        }
        return array;
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

internal static class TestPump
{
    /// <summary>Runs queued dispatcher work so posted event/progress handlers have run.</summary>
    public static void Drain()
    {
        for (var pass = 0; pass < 6; pass++) Dispatcher.UIThread.RunJobs();
    }

    public static async Task RunAsync(Func<Task> action)
    {
        await action();
        Drain();
    }

    /// <summary>
    /// Waits out the module's short busy-confirmation delay and drains the dispatcher. A batch verdict
    /// is decided from the module's real busy flag, which it answers just after a file finishes, so a
    /// test that asserts a settled batch must let that one follow-up read happen.
    /// </summary>
    public static async Task SettleAsync(TransferCore? core = null)
    {
        Drain();
        if (core?.PendingSettle is { } pending)
        {
            await pending;
            Drain();
            return;
        }
        await Task.Delay(900);
        Drain();
    }
}
