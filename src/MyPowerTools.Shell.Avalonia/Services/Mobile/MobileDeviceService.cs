using System.Text.Json;
using System.Text.Json.Nodes;
using Grpc.Core;
using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>
/// Runs one file-transfer command over the existing host control boundary and returns the
/// command's JSON payload. The production implementation uses <see cref="HostControlClient"/>;
/// tests inject their own executor, so no gRPC endpoint and no module is required.
/// </summary>
public delegate Task<string> MobileCommandExecutor(string commandId, JsonObject args, CancellationToken cancellationToken);

/// <summary>
/// Mobile presentation facade over the file-transfer module. It reads and writes only through
/// existing module commands, so it never references FileTransfer.Core, a private module assembly
/// or a module configuration file. Every value it reports comes from a real command answer:
/// a peer stays <see cref="MobilePeerConnectionState.Unknown"/> until the paired receiver itself
/// answered a check, and file pairing never grants remote control of the peer.
/// </summary>
public sealed class MobileDeviceService : IMobileDeviceService
{
    /// <summary>The module that owns pairing, transfer history and the relay configuration.</summary>
    public const string FileTransferModuleId = "file-transfer";

    /// <summary>How many recent transfer records one snapshot carries.</summary>
    public const int MaxActivities = 20;

    private const string InspectCommand = "file-transfer.inspect";
    private const string PairingCommand = "file-transfer.pairing";
    private const string PairImportCommand = "file-transfer.pair.import";
    private const string PeerCheckCommand = "file-transfer.peer.check";
    private const string PeerRemoveCommand = "file-transfer.peers.remove";

    private readonly MobileCommandExecutor _execute;

    /// <summary>Production constructor; uses the running host through HostControlClient.</summary>
    public MobileDeviceService() : this(ExecuteThroughHostControlAsync)
    {
    }

    /// <summary>Injectable constructor for tests and for an embedded host that supplies its own executor.</summary>
    public MobileDeviceService(MobileCommandExecutor executor) =>
        _execute = executor ?? throw new ArgumentNullException(nameof(executor));

    private static async Task<string> ExecuteThroughHostControlAsync(string commandId, JsonObject args, CancellationToken cancellationToken)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.ExecuteCommandAsync(Guid.NewGuid().ToString("N"), commandId, args, cancellationToken);
        if (!string.Equals(response.State, "succeeded", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(FailureMessage(response));
        return response.Summary;
    }

    private static string FailureMessage(HostProto.CommandExecutionResponse response)
    {
        var message = string.IsNullOrWhiteSpace(response.ErrorMessage) ? response.Summary?.Trim() : response.ErrorMessage.Trim();
        if (string.Equals(response.State, "permission-required", StringComparison.OrdinalIgnoreCase))
            return "该操作需要系统授权，请在电脑端确认后重试。" + (string.IsNullOrWhiteSpace(message) ? "" : $"（{message}）");
        if (string.Equals(response.State, "cancelled", StringComparison.OrdinalIgnoreCase)) return "操作已取消。";
        return string.IsNullOrWhiteSpace(message) ? "文件互传命令执行失败。" : message;
    }

    /// <summary>
    /// Reads one snapshot from a single <c>file-transfer.inspect</c> command. It performs no network
    /// call of its own: the relay state is whatever the module last measured, so opening a page never
    /// blocks on a relay check and "never checked" stays distinct from "unreachable". The explicit
    /// relay check belongs to the file-transfer settings entry.
    /// </summary>
    public async Task<MobileDeviceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        JsonObject state;
        try
        {
            state = await ReadAsync(InspectCommand, new JsonObject(), cancellationToken);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // An unavailable module is an empty state with a next step, never a diagnostic stack.
            return new MobileDeviceSnapshot("", false, [], false, false, null, [],
                Notice: "文件互传暂时不可用：" + ex.Message);
        }

        var peers = Peers(state["peers"] as JsonArray);
        // The assistant prefers the module's assistant-relay view: it knows the public default transport,
        // which the legacy OpenList summary cannot describe. Older modules only report the legacy view.
        var relay = RelayState(state["assistantRelay"] as JsonObject ?? state["cloud"] as JsonObject);
        var settings = state["settings"] as JsonObject;
        return new MobileDeviceSnapshot(
            Text(state, "localName"),
            Flag(state, "receiving"),
            peers,
            relay.Configured,
            relay.Running,
            relay.Description,
            Activities(state, peers),
            Notice: null,
            LocalAddress: LocalAddress(state, settings),
            RelayChecked: relay.Checked);
    }

    public async Task ImportPairingAsync(string code, CancellationToken cancellationToken = default)
    {
        var trimmed = (code ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("请粘贴对方 MPT 中复制的设备连接码。");
        await ReadAsync(PairImportCommand, new JsonObject { ["code"] = trimmed }, cancellationToken);
    }

    public async Task RemovePeerAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await ReadAsync(PeerRemoveCommand, new JsonObject { ["deviceId"] = RequiredDeviceId(deviceId) }, cancellationToken);
    }

    public async Task<string> GetPairingCodeAsync(CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync(PairingCommand, new JsonObject(), cancellationToken);
        var code = Text(result, "code");
        if (code.Length == 0) throw new InvalidOperationException("暂时无法生成设备连接码，请重试。");
        return code;
    }

    /// <summary>
    /// One explicit check of one paired device. There is no timer and no background loop: the
    /// caller decides when to ask.
    /// </summary>
    public async Task<MobilePeerInfo> CheckPeerAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync(PeerCheckCommand, new JsonObject { ["deviceId"] = RequiredDeviceId(deviceId) }, cancellationToken);
        return Peer(result);
    }

    private async Task<JsonObject> ReadAsync(string commandId, JsonObject args, CancellationToken cancellationToken)
    {
        var output = await _execute(commandId, args, cancellationToken);
        if (string.IsNullOrWhiteSpace(output)) return new JsonObject();
        try
        {
            return JsonNode.Parse(output) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"文件互传返回了无法识别的数据：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// Maps the module's last relay measurement. <c>reachable</c> is null until a real conversation
    /// with the relay happened, and that state is reported as "not checked yet" rather than offline.
    /// </summary>
    private static (bool Configured, bool Checked, bool Running, string Description) RelayState(JsonObject? relay)
    {
        var configured = Flag(relay, "configured");
        var state = Text(relay, "state");
        var message = Text(relay, "message");
        if (!configured) return (false, false, false, "发送后会自动开始同步。");

        var checkedNow = state is "available" or "unavailable";
        var running = state == "available";
        var description = state switch
        {
            "available" => "同步连接正常。",
            "unavailable" => message.Length > 0 ? message : "暂时无法同步，连接恢复后会继续。",
            _ => "等待同步。"
        };
        return (true, checkedNow, running, description);
    }

    private static IReadOnlyList<MobilePeerInfo> Peers(JsonArray? peers)
    {
        if (peers is null) return [];
        var result = new List<MobilePeerInfo>(peers.Count);
        foreach (var node in peers)
        {
            if (node is JsonObject peer) result.Add(Peer(peer));
        }
        return result;
    }

    private static MobilePeerInfo Peer(JsonObject peer)
    {
        var deviceId = Text(peer, "deviceId");
        var address = Text(peer, "address");
        var reported = Text(peer, "state");
        // An address alone is never online. Only the module's verified probe result, which requires
        // the paired receiver to have answered, changes this from Unknown.
        var connection = address.Length == 0
            ? MobilePeerConnectionState.Unknown
            : reported switch
            {
                "online" => MobilePeerConnectionState.Online,
                "offline" => MobilePeerConnectionState.Offline,
                _ => MobilePeerConnectionState.Unknown
            };
        var message = Text(peer, "message");
        if (message.Length == 0 && connection == MobilePeerConnectionState.Unknown)
            message = address.Length == 0 ? "尚未记录地址，请重新导入对方连接码。" : "已配对，尚未检查。";
        return new MobilePeerInfo(
            deviceId,
            Text(peer, "name") is { Length: > 0 } name ? name : deviceId,
            address,
            connection,
            Timestamp(peer, "checkedAt"),
            // File pairing is a file credential. Whatever a module or a forged payload claims,
            // pairing can never hand out control of the other computer.
            SupportsToolControl: false,
            Message: message);
    }

    private static IReadOnlyList<MobileTransferActivity> Activities(JsonObject state, IReadOnlyList<MobilePeerInfo> peers)
    {
        var activities = new List<MobileTransferActivity>();
        if (state["progress"] is JsonObject progress && IsActive(Text(progress, "state")))
            activities.Add(Activity(progress, activities.Count, peers));
        if (state["history"] is JsonArray history)
        {
            // Newest first: the module appends in completion order.
            for (var index = history.Count - 1; index >= 0 && activities.Count < MaxActivities; index--)
            {
                if (history[index] is JsonObject record) activities.Add(Activity(record, activities.Count, peers));
            }
        }
        return activities;
    }

    private static MobileTransferActivity Activity(JsonObject record, int index, IReadOnlyList<MobilePeerInfo> peers)
    {
        var name = Text(record, "name");
        var state = Text(record, "state");
        var delivery = Text(record, "delivery");
        var direction = Text(record, "direction");
        if (direction.Length == 0 && state is "received" or "receiving") direction = "receive";
        var time = Timestamp(record, "time");
        var peer = Text(record, "peer");
        // A relay upload is only "waiting": the storage accepted the payload, the recipient has not
        // confirmed anything. Direct sends and inbound receives are confirmed by the other side.
        var display = state switch
        {
            "started" => "active",
            "sending" or "uploading" or "downloading" or "receiving" => state,
            "completed" when delivery == "relay-uploaded" => "uploaded",
            "completed" when delivery is "direct" or "local" or "relay-downloaded" => "received",
            "completed" => "completed",
            "received" => "received",
            "cancelled" => "cancelled",
            "failed" => "failed",
            _ => "completed"
        };
        var total = ReadLong(record, "total");
        return new MobileTransferActivity(
            Id: $"{time?.ToString("O") ?? ""}|{name}|{index}",
            Name: name,
            State: display,
            Direction: direction,
            PeerName: peer.Length == 0 ? null : PeerName(peer, peers),
            Bytes: total > 0 ? total : ReadLong(record, "done"),
            Timestamp: time,
            Message: Text(record, "message") is { Length: > 0 } message ? message : null);
    }

    /// <summary>Prefers the paired device's display name, so a stored id is never shown as a raw key.</summary>
    private static string PeerName(string peer, IReadOnlyList<MobilePeerInfo> peers)
    {
        foreach (var known in peers)
        {
            if (string.Equals(known.DeviceId, peer, StringComparison.Ordinal)) return known.Name;
        }
        return peer;
    }

    private static bool IsActive(string state) => state is "started" or "sending" or "uploading" or "downloading" or "receiving";

    private static string LocalAddress(JsonObject state, JsonObject? settings)
    {
        if (state["addresses"] is JsonArray addresses && addresses.Count > 0 && addresses[0] is JsonValue value &&
            value.TryGetValue<string>(out var address)) return address;
        return settings is null ? "" : Text(settings, "listenAddress");
    }

    private static string RequiredDeviceId(string deviceId)
    {
        var trimmed = (deviceId ?? "").Trim();
        if (trimmed.Length == 0) throw new ArgumentException("请选择要操作的设备。");
        return trimmed;
    }

    private static string Text(JsonObject? node, string key)
    {
        if (node is null || !node.TryGetPropertyValue(key, out var value) || value is null) return "";
        try { return value.GetValue<string>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
    }

    private static bool Flag(JsonObject? node, string key)
    {
        if (node is null || !node.TryGetPropertyValue(key, out var value) || value is null) return false;
        try { return value.GetValue<bool>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return false; }
    }

    private static long ReadLong(JsonObject node, string key)
    {
        if (!node.TryGetPropertyValue(key, out var value) || value is null) return 0;
        try { return value.GetValue<long>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return 0; }
    }

    private static DateTimeOffset? Timestamp(JsonObject? node, string key)
    {
        var text = Text(node, key);
        return DateTimeOffset.TryParse(text, out var value) ? value : null;
    }

    /// <summary>Failures a mobile page can present as a message instead of an exception.</summary>
    private static bool IsExpected(Exception ex) =>
        ex is RpcException or IOException or InvalidOperationException or InvalidDataException or
            JsonException or NotSupportedException or ArgumentException or TimeoutException;
}
