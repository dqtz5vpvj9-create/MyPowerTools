using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using FileTransfer.Core.Discovery;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace FileTransfer.MyPowerTools;

/// <summary>
/// The assistant conversation: identity, durable timeline, queue scheduling, first-contact receiving
/// and delivery receipts. Everything here runs on the module lifecycle, so closing a Surface never
/// stops queued work and disabling the tool stops it completely.
/// </summary>
public sealed partial class FileTransferModule
{
    /// <summary>How often an enabled receiver polls the relay for new items while no push exists.</summary>
    private const int OfflineReceiveSyncSeconds = 60;
    /// <summary>Direct deliveries attempted in one sync round; the next round continues immediately.</summary>
    private const int DirectDeliveryLimit = 8;
    /// <summary>A device that already acknowledged an item is never offered the same item again.</summary>
    private static readonly TimeSpan DirectRetryDelay = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _directGate = new(1, 1);
    private readonly SemaphoreSlim _relayGate = new(1, 1);
    private Task<(AssistantSyncResult Result, string Error)>? _relayPass;
    private readonly SemaphoreSlim _assistantSignal = new(0, int.MaxValue);
    private readonly ReceiveAuthorization _receiveAuthorization = new();
    private readonly Dictionary<string, OwnDevice> _ownDevices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _itemCancellation = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssistantItemState> _userIntents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _directAttempts = new(StringComparer.Ordinal);
    private DiscoveryBeacon? _beacon;
    private IDisposable? _multicastLease;
    private string _beaconNote = "";
    private AssistantStore? _assistantStore;
    private AssistantState? _assistant;
    private Task? _assistantWorker;
    private string _conversationId = "";
    private string _conversationKey = "";
    private string _linked = "";

    /// <summary>An own device is one that proved it shares this conversation; nothing else may receive a self send.</summary>
    private sealed record OwnDevice(string DeviceId, string Name, string Address, DateTimeOffset ConfirmedAt);

    /// <summary>The conversation identity this device publishes with every item.</summary>
    private AssistantIdentity Identity() => new(Setting("deviceId"), DeviceName(), _conversationId);

    private bool Linked
    {
        get { lock (_stateLock) return _linked == "imported" || _ownDevices.Count > 0; }
    }

    private string LinkState => Linked ? "linked" : _linked.Length > 0 ? "waiting" : "none";

    private async Task InitializeAssistantAsync(CancellationToken token)
    {
        _assistantStore = new AssistantStore(Path.Combine(_data, "assistant"));
        _assistant = await _assistantStore.LoadAsync(token);
        await EnsureConversationAsync(token);
        await LoadOwnDevicesAsync(token);
        _assistant = await _assistantStore.ConfigureAsync(Identity(), token);
        StartAssistantWorker();
    }

    /// <summary>
    /// Loads or creates the shared conversation. The id names the relay namespace and the key is the
    /// shared secret of the user's own devices; both live only in the secret store.
    /// </summary>
    private async Task EnsureConversationAsync(CancellationToken token)
    {
        _conversationId = await SecretAsync("conversation-id", token) ?? "";
        _conversationKey = await SecretAsync("conversation-key", token) ?? "";
        _linked = await SecretAsync("conversation-linked", token) ?? "";
        if (_conversationId.Length > 0 && _conversationKey.Length == 64) return;
        _conversationId = "self-" + Guid.NewGuid().ToString("N")[..8];
        _conversationKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        await _secrets.SaveAsync(Id, "conversation-id", _conversationId, token);
        await _secrets.SaveAsync(Id, "conversation-key", _conversationKey, token);
    }

    // ---- own devices ------------------------------------------------------------------------------

    private string OwnDevicesPath => Path.Combine(_data, "own-devices.json");

    private async Task LoadOwnDevicesAsync(CancellationToken token)
    {
        _ownDevices.Clear();
        if (!File.Exists(OwnDevicesPath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<List<OwnDevice>>(await File.ReadAllTextAsync(OwnDevicesPath, token), DirectTransfer.Json) ?? [];
            foreach (var device in saved)
            {
                if (device.DeviceId.Length == 0 || device.DeviceId == Setting("deviceId")) continue;
                _ownDevices[device.DeviceId] = device;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            // A damaged membership file only means "no own device is known yet"; it is never trusted blindly.
            _ownDevices.Clear();
        }
    }

    private async Task SaveOwnDevicesAsync(CancellationToken token)
    {
        var temporary = OwnDevicesPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(_ownDevices.Values.ToArray(), DirectTransfer.Json), token);
        File.Move(temporary, OwnDevicesPath, true);
    }

    /// <summary>
    /// Records a device that proved it belongs to this conversation: a device that joined through the
    /// link code, one that authenticated an incoming item with the conversation key, or one whose
    /// receipt arrived in this conversation. A plain file pairing or a remembered first contact never
    /// becomes an own device, so it can never receive a self send.
    /// </summary>
    private async Task ConfirmOwnDeviceAsync(string deviceId, string name, string address, CancellationToken token)
    {
        if (deviceId.Length == 0 || deviceId == Setting("deviceId")) return;
        lock (_stateLock)
        {
            var existing = _ownDevices.GetValueOrDefault(deviceId);
            var resolvedName = name.Length > 0 ? name : existing?.Name ?? deviceId;
            // A paired address is the most reliable one; the address the device advertised comes next,
            // and the observed endpoint is only a fallback (it can be a router or loopback address).
            var peerAddress = FindPeer(deviceId) is { } peer ? PeerText(peer, "address") : "";
            var resolvedAddress = peerAddress.Length > 0 ? peerAddress
                : address.Length > 0 ? address : existing?.Address ?? "";
            if (existing is not null && existing.Name == resolvedName && existing.Address == resolvedAddress) return;
            _ownDevices[deviceId] = new OwnDevice(deviceId, resolvedName, resolvedAddress, DateTimeOffset.UtcNow);
        }
        await SaveOwnDevicesAsync(token);
        EmitAssistantChanged("own-device");
    }

    private OwnDevice[] OwnDevices()
    {
        lock (_stateLock) return _ownDevices.Values.ToArray();
    }

    /// <summary>
    /// Takes the platform's Wi-Fi multicast lease for as long as something is really listening.
    /// Without a registered platform lock the lease is a no-op, and Android is told honestly that
    /// nearby devices may not be heard instead of pretending discovery works.
    /// </summary>
    private void AcquireMulticast(string reason)
    {
        if (_multicastLease is not null) return;
        _multicastLease = MobileWifiMulticast.AcquireIfAvailable(reason);
        if (MobileWifiMulticast.Current is null && OperatingSystem.IsAndroid())
            _beaconNote = "本机还没有接入局域网发现，可能收不到附近的设备。";
    }

    private void ReleaseMulticast()
    {
        var lease = _multicastLease;
        _multicastLease = null;
        lease?.Dispose();
    }

    /// <summary>Starts the passive discovery service next to a running receiver.</summary>
    private async Task StartBeaconAsync(CancellationToken token)
    {
        if (_beacon is not null) return;
        var address = Setting("listenAddress");
        if (address.Length == 0)
        {
            _beaconNote = "尚未连接 Tailscale 网络，本机暂时不能被自动发现。";
            return;
        }
        var local = new DiscoveredDevice(Setting("deviceId"), DeviceName(), address, TransferFiles.Port, PlatformName());
        var beacon = new DiscoveryBeacon(DiscoveryOptions.Default with { LocalDevice = local });
        try
        {
            if (await beacon.StartAsync(local, token))
            {
                _beacon = beacon;
                _beaconNote = "";
                AcquireMulticast("文件助手正在等待局域网内的设备");
                return;
            }
            _beaconNote = beacon.Fault.Length > 0 ? beacon.Fault : "本机暂时不能被自动发现。";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or PlatformNotSupportedException or InvalidOperationException or ArgumentException)
        {
            _beaconNote = MptLogRedactor.Redact(ex.Message);
        }
        await beacon.DisposeAsync();
    }

    private async Task StopBeaconAsync()
    {
        var beacon = _beacon;
        _beacon = null;
        // Disabling the tool releases the wake-up source with the beacon.
        ReleaseMulticast();
        if (beacon is null) return;
        try { await beacon.StopAsync(); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or ObjectDisposedException or InvalidOperationException) { }
        await beacon.DisposeAsync();
    }

    /// <summary>Wakes the queue worker; there is no timer when nothing is queued and nothing is enabled.</summary>
    private void SignalAssistant()
    {
        if (_assistantWorker is null || _lifetime.IsCancellationRequested) return;
        _assistantSignal.Release();
    }

    private void StartAssistantWorker()
    {
        if (_assistantWorker is not null) return;
        _assistantWorker = Task.Run(AssistantLoopAsync);
    }

    /// <summary>
    /// One worker for the whole module lifetime. It blocks on a signal with no timeout while receiving
    /// is disabled, and only an enabled offline receiver waits with a bounded interval, because a
    /// WebDAV relay has no push channel.
    /// </summary>
    private async Task AssistantLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var wait = Volatile.Read(ref _session) is not null
                ? TimeSpan.FromSeconds(OfflineReceiveSyncSeconds)
                : Timeout.InfiniteTimeSpan;
            try { await _assistantSignal.WaitAsync(wait, _lifetime.Token); }
            catch (OperationCanceledException) { return; }
            if (_lifetime.IsCancellationRequested) return;
            while (_assistantSignal.CurrentCount > 0) await _assistantSignal.WaitAsync(_lifetime.Token);
            var more = false;
            try
            {
                // Only the direct leg is awaited here: a relay that is slow or stuck must not stop the
                // next direct delivery, and the durable relay copy continues in its own single flight.
                var direct = await RunDirectLegAsync(Identity(), _lifetime.Token);
                KickRelayLeg(Identity());
                more = direct.HasMore;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception) { EmitAssistantChanged("sync.failed"); }
            // Unfinished work continues immediately instead of waiting for the next bounded interval.
            if (more) _ = ContinueSoonAsync();
        }
    }

    private async Task ContinueSoonAsync()
    {
        try { await Task.Delay(250, _lifetime.Token); }
        catch (OperationCanceledException) { return; }
        SignalAssistant();
    }

    /// <summary>
    /// Runs one bounded pass. The relay is durable storage, the direct channel is only an accelerator,
    /// so a relay that is slow or broken never blocks a device that is reachable right now.
    /// </summary>
    /// <summary>The explicit command: both legs, direct first, then the durable relay pass.</summary>
    private async Task<JsonObject> RunAssistantSyncAsync(AssistantIdentity? identity, CancellationToken token)
    {
        identity ??= Identity();
        var direct = await RunDirectLegAsync(identity, token);
        var (relay, relayError) = await RunRelayLegAsync(identity, token);
        var answer = await AssistantInspectJsonAsync(token);
        answer["sync"] = new JsonObject
        {
            ["published"] = relay.Published,
            ["failed"] = relay.Failed,
            ["received"] = relay.Received,
            ["downloaded"] = relay.Downloaded,
            ["receiptsWritten"] = relay.ReceiptsWritten,
            ["receiptsRead"] = relay.ReceiptsRead,
            ["directDelivered"] = direct.Delivered,
            ["directPending"] = direct.Pending,
            ["hasMore"] = relay.HasMore || direct.HasMore || _userIntents.Count > 0,
            ["message"] = relayError.Length > 0 ? relayError : relay.Message ?? ""
        };
        EmitAssistantChanged("sync");
        return answer;
    }

    /// <summary>
    /// The direct leg, single flight. It never waits for the relay, and the user's pending cancel/retry
    /// decisions are settled here because this is the leg that changes their records.
    /// </summary>
    private async Task<DirectOutcome> RunDirectLegAsync(AssistantIdentity identity, CancellationToken token)
    {
        await _directGate.WaitAsync(token);
        try { return await DeliverDirectAsync(identity, token); }
        finally
        {
            try { await ApplyUserIntentsAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) { }
            try { PruneItemScopes(await _assistantStore!.LoadAsync(CancellationToken.None)); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
            _directGate.Release();
        }
    }

    /// <summary>The relay leg, single flight and reusable: a caller joins a pass that is already running.</summary>
    private async Task<(AssistantSyncResult Result, string Error)> RunRelayLegAsync(AssistantIdentity identity, CancellationToken token)
    {
        await _relayGate.WaitAsync(token);
        try
        {
            var store = _assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。");
            var outcome = await RunRelayPassAsync(store, identity, token);
            if (outcome.Result.ReceiptsRead > 0) await ConfirmReceiptSendersAsync(token);
            return outcome;
        }
        finally { _relayGate.Release(); }
    }

    /// <summary>Starts the relay leg in the background unless one is already in flight.</summary>
    private void KickRelayLeg(AssistantIdentity identity)
    {
        if (_relayPass is { IsCompleted: false }) return;
        _relayPass = RunRelayLegAsync(identity, _lifetime.Token)
            .ContinueWith(task =>
            {
                var outcome = task.IsCompletedSuccessfully ? task.Result : (new AssistantSyncResult(0, 0, 0, 0, 0, 0, false, null), "");
                if (outcome.Item1.HasMore && !_lifetime.IsCancellationRequested) _ = ContinueSoonAsync();
                return outcome;
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// One relay pass. The health it reports comes from the pass result: a relay that answered with a
    /// server error or asked for a retry is never recorded as reachable, and a slow pass is bounded by
    /// the caller's token rather than by the direct leg.
    /// </summary>
    private async Task<(AssistantSyncResult Result, string Error)> RunRelayPassAsync(AssistantStore store,
        AssistantIdentity identity, CancellationToken token)
    {
        if (!await RelayReadyAsync(token)) return (new AssistantSyncResult(0, 0, 0, 0, 0, 0, false, null), "");
        try
        {
            using var cloud = await CloudAsync(token);
            var sync = new AssistantSync(store, cloud) { ItemCancellation = ItemToken };
            var result = await sync.SyncAsync(identity, token);
            if (result.RetryAfter is not null)
            {
                // The relay answered with a failure it wants retried: that is not a healthy relay.
                var message = result.Message is { Length: > 0 } text ? text : "中转网盘暂时不可用，稍后会自动重试。";
                NoteRelayUnreachable(message);
                return (result, message);
            }
            NoteRelayReachable();
            return (result, "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = MptLogRedactor.Redact(ex.Message);
            NoteRelayUnreachable("连接中转网盘失败：" + message);
            return (new AssistantSyncResult(0, 0, 0, 0, 0, 0, false, null), message);
        }
    }

    /// <summary>A receipt that arrived in this conversation is proof the writer shares it.</summary>
    private async Task ConfirmReceiptSendersAsync(CancellationToken token)
    {
        var store = _assistantStore!;
        var state = await store.LoadAsync(token);
        foreach (var item in state.Outgoing(Setting("deviceId")).ToArray())
        {
            foreach (var receipt in item.Receipts)
            {
                if (receipt.DeviceId.Length == 0 || receipt.DeviceId == Setting("deviceId")) continue;
                await ConfirmOwnDeviceAsync(receipt.DeviceId, receipt.DeviceName, "", token);
            }
        }
    }

    /// <summary>
    /// The cancellation scope of one item for the current pass. Both transports link to it, so a user
    /// cancel aborts the active WebDAV request and the active TCP stream alike, and never a sibling.
    /// </summary>
    private CancellationTokenSource ItemScope(string itemId) =>
        _itemCancellation.GetOrAdd(itemId, _ => new CancellationTokenSource());

    private CancellationToken ItemToken(string itemId) => ItemScope(itemId).Token;

    /// <summary>Drops scopes whose item reached a terminal state; called only between passes.</summary>
    private void PruneItemScopes(AssistantState state)
    {
        foreach (var id in _itemCancellation.Keys.ToArray())
        {
            var item = state.Find(id);
            if (item is not null && item.State is not (AssistantItemState.Delivered or AssistantItemState.Cancelled
                or AssistantItemState.Available)) continue;
            if (_itemCancellation.TryRemove(id, out var scope)) scope.Dispose();
        }
    }

    private sealed record DirectOutcome(int Delivered, int Pending, bool HasMore);

    /// <summary>
    /// Tries a direct transfer for the outgoing items that still need one. A self send only ever goes
    /// to devices that proved they are part of this conversation, a targeted send only to the device
    /// the user picked, and a device that already acknowledged an item is never offered it again.
    /// </summary>
    private async Task<DirectOutcome> DeliverDirectAsync(AssistantIdentity identity, CancellationToken token)
    {
        var store = _assistantStore!;
        var state = await store.LoadAsync(token);
        var own = OwnDevices().Where(device => device.Address.Length > 0)
            .Select(device => new DiscoveredDevice(device.DeviceId, device.Name, device.Address, TransferFiles.Port, ""))
            .ToArray();
        var peers = KnownDevices().Where(device => device.Address.Length > 0).ToArray();
        var delivered = 0;
        var pending = 0;
        var attempted = 0;
        // A relay pass may have claimed an entry as "sending" at the same moment; that must not hide it
        // from the direct path, because the two transports are independent.
        foreach (var item in state.Items.Where(item => string.Equals(item.SenderDeviceId, identity.DeviceId, StringComparison.Ordinal)
            && item.State is AssistantItemState.Queued or AssistantItemState.Sending or AssistantItemState.Stored or AssistantItemState.Failed)
            .OrderBy(item => item.CreatedAt).ToArray())
        {
            if (attempted >= DirectDeliveryLimit) return new DirectOutcome(delivered, pending, true);
            var targets = item.TargetDeviceId is { Length: > 0 } target
                ? peers.Concat(own).Where(device => string.Equals(device.DeviceId, target, StringComparison.Ordinal)).DistinctBy(device => device.DeviceId).ToArray()
                : own;
            foreach (var device in targets)
            {
                token.ThrowIfCancellationRequested();
                if (item.Receipts.Any(receipt => receipt.DeviceId == device.DeviceId)) continue;
                var attemptKey = item.Id + "|" + device.DeviceId;
                lock (_stateLock)
                {
                    if (_directAttempts.TryGetValue(attemptKey, out var last) && DateTimeOffset.UtcNow - last < DirectRetryDelay) continue;
                    _directAttempts[attemptKey] = DateTimeOffset.UtcNow;
                }
                attempted++;
                var reply = await SendDirectItemAsync(device, item, identity, token);
                if (reply is null) continue;
                if (reply.Pending) pending++;
                if (!reply.Ok || !string.Equals(reply.State, AssistantWire.DeliveredState, StringComparison.Ordinal)) continue;
                delivered++;
                await store.MutateAsync(current =>
                {
                    var row = current.Find(item.Id);
                    if (row is null) return;
                    MergeReceipt(current, row, device);
                    if (row.TargetDeviceId is not null) row.State = AssistantItemState.Delivered;
                }, token);
            }
        }
        return new DirectOutcome(delivered, pending, false);
    }

    private async Task<AssistantWire.ItemReply?> SendDirectItemAsync(DiscoveredDevice device, AssistantItem item,
        AssistantIdentity identity, CancellationToken token)
    {
        var scope = ItemScope(item.Id);
        using var itemCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, scope.Token);
        try
        {
            var payload = _assistantStore!.GetPayloadPath(item);
            var frame = new AssistantWire.Frame(
                AssistantWire.Version,
                AssistantWire.ItemKind,
                await DirectTokenAsync(device.DeviceId, token),
                item.Name,
                item.Size,
                identity.DeviceId,
                item.Id,
                identity.ConversationId,
                item.Kind switch
                {
                    AssistantItemKind.Text => AssistantWire.TextItem,
                    AssistantItemKind.Image => AssistantWire.ImageItem,
                    _ => AssistantWire.FileItem
                },
                item.Text,
                identity.Name,
                item.TargetDeviceId,
                Address: Setting("listenAddress"));
            // A first contact may hold this connection while its user decides, so the budget covers it.
            // Progress goes through the store, so the shared snapshot is never written from this thread.
            return await AssistantWire.SendItemAsync(device.Address, device.Port, frame, payload,
                (done, _) => _assistantStore?.ReportProgress(item.Id, done), TimeSpan.FromMinutes(5), itemCancellation.Token);
        }
        catch (OperationCanceledException) when (itemCancellation.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // The user cancelled this one item; the rest of the pass continues.
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidDataException or ArgumentException or JsonException)
        {
            // Direct transfer is an accelerator: an unreachable device simply waits for the relay.
            return null;
        }
        finally { /* the pass owns the scope; pruning happens between passes. */ }
    }

    /// <summary>The credential presented to a device: a stored pairing token, or the conversation key.</summary>
    private async Task<string> DirectTokenAsync(string deviceId, CancellationToken token)
    {
        var peer = FindPeer(deviceId);
        if (peer is not null)
        {
            var stored = await SecretAsync("peer-" + deviceId, token);
            if (!string.IsNullOrEmpty(stored)) return stored;
        }
        return _conversationKey;
    }

    private static void MergeReceipt(AssistantState state, AssistantItem item, DiscoveredDevice device)
    {
        item.Receipts.RemoveAll(receipt => string.Equals(receipt.DeviceId, device.DeviceId, StringComparison.Ordinal));
        item.Receipts.Add(new AssistantReceipt
        {
            ItemId = item.Id,
            DeviceId = device.DeviceId,
            DeviceName = device.Name,
            SavedAt = DateTimeOffset.UtcNow,
            Bytes = item.Kind == AssistantItemKind.Text ? 0 : item.Size
        });
        state.Remember(item.Id);
    }

    /// <summary>Devices this conversation knows about: remembered peers plus own devices.</summary>
    private IReadOnlyList<DiscoveredDevice> KnownDevices()
    {
        var devices = new List<DiscoveredDevice>();
        foreach (var node in Peers())
        {
            if (node is not JsonObject peer) continue;
            var deviceId = PeerText(peer, "deviceId");
            var address = PeerText(peer, "address");
            if (deviceId.Length == 0) continue;
            devices.Add(new DiscoveredDevice(deviceId, PeerText(peer, "name"), address, TransferFiles.Port, ""));
        }
        return devices;
    }

    // ---- commands -------------------------------------------------------------------------------

    private async Task<object> AssistantInspectAsync(CancellationToken token) => await AssistantInspectJsonAsync(token);

    /// <summary>The frozen inspect shape. <c>receiving</c> is a bool; the detail lives next to it.</summary>
    private async Task<JsonObject> AssistantInspectJsonAsync(CancellationToken token)
    {
        var state = await AssistantStateAsync(token);
        var items = new JsonArray();
        foreach (var item in state.Items.OrderByDescending(item => item.CreatedAt).Take(200))
            items.Add(JsonSerializer.SerializeToNode(item, AssistantJson.Options));
        var pending = new JsonArray();
        foreach (var request in _receiveAuthorization.Pending())
            pending.Add(new JsonObject
            {
                ["requestId"] = request.RequestId,
                ["deviceId"] = request.DeviceId,
                ["name"] = request.Name,
                ["itemNames"] = new JsonArray(request.ItemNames.Select(name => (JsonNode?)name).ToArray()),
                ["expiresAt"] = request.ExpiresAt.ToString("O")
            });
        var session = Volatile.Read(ref _session);
        return new JsonObject
        {
            ["identity"] = new JsonObject
            {
                ["id"] = Setting("deviceId"),
                ["name"] = DeviceName(),
                ["linked"] = Linked,
                ["linkState"] = LinkState,
                ["ownDevices"] = OwnDevices().Length
            },
            ["items"] = items,
            ["pendingRequests"] = pending,
            ["relay"] = RelayJson(),
            ["receiving"] = session is not null,
            ["receivingDetails"] = ReceivingDetailsJson(session is not null)
        };
    }

    /// <summary>
    /// One send request may carry text and attachments together. Everything is persisted before the
    /// answer, and a failure rolls the whole request back, so the user never sees "sent" for half of
    /// it nor a retry that duplicates the half that did succeed.
    /// </summary>
    private async Task<object> AssistantSendAsync(JsonObject args, CancellationToken token)
    {
        var target = SettingsJson.ReadString(args, "targetDeviceId") ?? "";
        if (target.Length > 0 && target == Setting("deviceId")) target = "";
        if (target.Length > 0) TransferFiles.DeviceId(target);
        var text = SettingsJson.ReadString(args, "text");
        var paths = BatchSend.Paths(args);
        if (string.IsNullOrEmpty(text) && paths.Count == 0) throw new ArgumentException("请输入文字或选择要发送的文件。");
        if (text is { Length: > AssistantLimits.MaxTextCharacters }) throw new ArgumentException("文字太长了。");
        foreach (var path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new ArgumentException($"找不到文件：{Path.GetFileName(path)}。");
            if (info.Length > AssistantLimits.MaxPayloadBytes) throw new ArgumentException($"文件太大：{Path.GetFileName(path)}。");
        }
        var store = _assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。");
        var deviceTarget = target.Length == 0 ? null : target;
        // Text and attachments are one compose action: the store persists the whole batch or nothing,
        // so a partial send can never leave the user with a duplicate half after a retry.
        var created = await store.EnqueueAsync(Identity(),
            AssistantDraft.ForContent(text, paths.Count > 0 ? paths : null, deviceTarget), token);
        if (created.Count > 0)
        {
            EmitAssistantChanged("send");
            SignalAssistant();
        }
        return new
        {
            accepted = created.Count > 0,
            itemIds = created.Select(item => item.Id).ToArray(),
            targetDeviceId = deviceTarget
        };
    }

    private async Task<object> AssistantRetryAsync(JsonObject args, CancellationToken token)
    {
        var itemId = RequiredItemId(args);
        var store = _assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。");
        var retried = false;
        // The user's decision is applied at once and re-applied after a running pass, so a five minute
        // upload never delays it and the pass cannot write its own outcome over it.
        var state = await store.MutateAsync(current =>
        {
            var item = current.Find(itemId);
            if (item is null || item.State != AssistantItemState.Failed) return;
            item.State = AssistantItemState.Queued;
            item.Error = null;
            retried = true;
        }, token);
        if (retried)
        {
            lock (_stateLock) _userIntents[itemId] = AssistantItemState.Queued;
            EmitAssistantChanged("retry");
            SignalAssistant();
        }
        return new { itemId, retried, state = StateText(state.Find(itemId)?.State) };
    }

    private async Task<object> AssistantCancelAsync(JsonObject args, CancellationToken token)
    {
        var itemId = RequiredItemId(args);
        var store = _assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。");
        // Cancel must not wait for an in-flight upload: the item's own transfer is aborted, and a
        // message that already reached the target keeps its real confirmation.
        if (_itemCancellation.TryGetValue(itemId, out var cancellation))
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        var accepted = false;
        await store.MutateAsync(current => accepted = current.TryCancel(itemId), token);
        var cancelled = accepted;
        if (cancelled) lock (_stateLock) _userIntents[itemId] = AssistantItemState.Cancelled;
        EmitAssistantChanged("cancel");
        return new { itemId, cancelled };
    }

    /// <summary>Re-applies the user's cancel/retry decisions after a pass that may have overwritten them.</summary>
    private async Task ApplyUserIntentsAsync(CancellationToken token)
    {
        KeyValuePair<string, AssistantItemState>[] intents;
        lock (_stateLock)
        {
            if (_userIntents.Count == 0) return;
            intents = _userIntents.ToArray();
        }
        await _assistantStore!.MutateAsync(state =>
        {
            foreach (var (itemId, intent) in intents)
            {
                var item = state.Find(itemId);
                if (item is null) continue;
                if (intent == AssistantItemState.Cancelled && !item.CanCancel) continue;
                if (item.State != intent) item.State = intent;
                if (intent == AssistantItemState.Queued) item.Error = null;
            }
        }, token);
        lock (_stateLock)
        {
            foreach (var (itemId, _) in intents) _userIntents.Remove(itemId);
        }
    }

    private async Task<object> AssistantOpenAsync(JsonObject args, CancellationToken token)
    {
        var itemId = RequiredItemId(args);
        var state = await AssistantStateAsync(token);
        var item = state.Find(itemId) ?? throw new ArgumentException("未找到该条目。");
        var target = AssistantContent.OpenTarget(item);
        if (!target.NeedsDownload) return new { itemId, path = target.Path, text = target.Text, needsDownload = false };
        if (item.Kind == AssistantItemKind.Text) throw new InvalidOperationException("这条消息没有可打开的内容。");
        if (!await RelayReadyAsync(token)) throw new InvalidOperationException("本机还没有这条内容，且未配置中转网盘。");
        await _relayGate.WaitAsync(token);
        try
        {
            using var cloud = await CloudAsync(token);
            await new AssistantSync(_assistantStore!, cloud) { ItemCancellation = ItemToken }.EnsureLocalAsync(Identity(), itemId, token);
        }
        finally { _relayGate.Release(); }
        NoteRelayReachable();
        EmitAssistantChanged("open");
        var opened = AssistantContent.OpenTarget((await AssistantStateAsync(token)).Find(itemId)!);
        return new { itemId, path = opened.Path, text = opened.Text, needsDownload = opened.NeedsDownload };
    }

    private async Task<object> AssistantDevicesAsync(CancellationToken token)
    {
        var known = KnownDevices();
        var local = new DiscoveredDevice(Setting("deviceId"), DeviceName(),
            Setting("listenAddress"), TransferFiles.Port, PlatformName());
        var options = DiscoveryOptions.Default with { LocalDevice = local };
        DiscoveryReport report;
        // The lease lasts exactly as long as this bounded window: discovery is page scoped.
        using var multicast = MobileWifiMulticast.AcquireIfAvailable("正在查找附近的设备");
        try
        {
            report = await new DeviceDiscovery(options).DiscoverAsync(known, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or PlatformNotSupportedException or InvalidOperationException)
        {
            report = new DiscoveryReport([], DiscoveryOutcome.Unsupported, [MptLogRedactor.Redact(ex.Message)], TimeSpan.Zero);
        }
        var devices = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in report.Devices)
        {
            if (result.Device.DeviceId.Length == 0 || result.Device.DeviceId == Setting("deviceId")) continue;
            if (!seen.Add(result.Device.DeviceId)) continue;
            devices.Add(DeviceJson(result.Device, result.Paired, result.Available, result.Message));
        }
        // A remembered peer that this bounded window did not see stays listed, with its last real check.
        foreach (var device in known)
        {
            if (device.DeviceId.Length == 0 || device.DeviceId == Setting("deviceId") || !seen.Add(device.DeviceId)) continue;
            devices.Add(DeviceJson(device, true, PeerAvailable(device.DeviceId), ""));
        }
        foreach (var device in OwnDevices())
        {
            if (device.DeviceId == Setting("deviceId") || !seen.Add(device.DeviceId)) continue;
            devices.Add(DeviceJson(new DiscoveredDevice(device.DeviceId, device.Name, device.Address, TransferFiles.Port, ""), true, true, ""));
        }
        var message = report.Message;
        if (MobileWifiMulticast.Current is null && OperatingSystem.IsAndroid() && message.Length == 0)
            message = "本机还没有接入局域网发现，可能找不到附近的设备。";
        return new { devices, discoveryState = report.State, message };
    }

    private JsonObject DeviceJson(DiscoveredDevice device, bool paired, bool available, string message)
    {
        var json = new JsonObject
        {
            ["deviceId"] = device.DeviceId,
            ["name"] = device.Name.Length > 0 ? device.Name : device.DeviceId,
            ["address"] = device.Address,
            ["platform"] = device.Platform,
            ["paired"] = paired,
            ["available"] = available,
            ["own"] = OwnDevices().Any(own => own.DeviceId == device.DeviceId)
        };
        var known = FindPeer(device.DeviceId);
        if (known is not null)
        {
            var name = PeerText(known, "name");
            if (name.Length > 0) json["name"] = name;
            if (device.Address.Length == 0) json["address"] = PeerText(known, "address");
            json["paired"] = true;
        }
        if (message.Length > 0) json["message"] = message;
        return json;
    }

    /// <summary>Availability comes from the retained check result; a device list never probes by itself.</summary>
    private bool PeerAvailable(string deviceId)
    {
        lock (_stateLock) return _peerChecks.GetValueOrDefault(deviceId)?.State == "online";
    }

    private async Task<object> AssistantReceiveRespondAsync(JsonObject args, CancellationToken token)
    {
        var requestId = SettingsJson.ReadString(args, "requestId") ?? "";
        if (requestId.Length == 0) throw new ArgumentException("请选择要处理的接收请求。");
        var accept = ReadFlag(args, "accept");
        var remember = accept && ReadFlag(args, "remember");
        if (!_receiveAuthorization.Respond(requestId, accept, remember, out var request) || request is null)
            throw new ArgumentException("该接收请求不存在或已过期。");
        var stored = false;
        if (accept && remember && request.OfferedToken is { Length: >= 24 } offered)
        {
            // Permanent trust is only written after the user explicitly accepted and chose to remember.
            await _secrets.SaveAsync(Id, "peer-" + request.DeviceId, offered, token);
            stored = true;
        }
        // An accepted first contact is a file-transfer authorisation only; it never joins the
        // conversation, so it can never receive a self send.
        EmitAssistantChanged("receive.respond");
        if (accept) SignalAssistant();
        return new { requestId, accepted = accept, remembered = stored, deviceId = request.DeviceId };
    }

    private async Task<object> AssistantLinkExportAsync(CancellationToken token)
    {
        await EnsureConversationAsync(token);
        var address = Setting("listenAddress");
        if (address.Length == 0) address = TransferFiles.LocalAddresses().FirstOrDefault() ?? "";
        // One scan has to be enough: the code carries the relay configuration too, so the other device
        // can sync offline without a second setup. Secrets still only ever live in the secret store.
        var cloud = "";
        if (await RelayReadyAsync(token))
            cloud = new CloudConnection(Setting("webDavUrl"), Setting("username"), await SecretAsync("password", token) ?? "").Encode();
        var code = LinkCode.Encode(new LinkCode.Payload(1, _conversationId, _conversationKey,
            Setting("deviceId"), DeviceName(), address, TransferFiles.Port, PlatformName(), cloud));
        if (_linked.Length == 0)
        {
            // Showing a code is not a connection yet.
            _linked = "waiting";
            await _secrets.SaveAsync(Id, "conversation-linked", _linked, token);
        }
        return new { code, name = DeviceName(), address, relayIncluded = cloud.Length > 0, linked = Linked, linkState = LinkState };
    }

    private static object AssistantLinkPreviewAsync(JsonObject args)
    {
        var payload = LinkCode.Decode(SettingsJson.ReadString(args, "code") ?? "");
        // Preview never stores, never connects and never returns the key.
        return new
        {
            name = payload.Name,
            deviceId = payload.DeviceId,
            conversationId = payload.ConversationId,
            scope = "own-devices",
            relayIncluded = payload.Cloud is { Length: > 0 }
        };
    }

    private async Task<object> AssistantLinkImportAsync(JsonObject args, CancellationToken token)
    {
        var payload = LinkCode.Decode(SettingsJson.ReadString(args, "code") ?? "");
        if (payload.DeviceId == Setting("deviceId")) throw new ArgumentException("这是本机自己的连接码，请扫描另一台设备的码。");
        await _secrets.SaveAsync(Id, "conversation-id", payload.ConversationId, token);
        await _secrets.SaveAsync(Id, "conversation-key", payload.Key, token);
        _linked = "imported";
        await _secrets.SaveAsync(Id, "conversation-linked", _linked, token);
        await EnsureConversationAsync(token);
        var relayImported = false;
        if (payload.Cloud is { Length: > 0 } cloudCode)
        {
            // The relay account travels in the code, and its password goes straight to the secret store.
            var connection = CloudConnection.Decode(cloudCode);
            await _operations.WaitAsync(token);
            try
            {
                await _secrets.SaveAsync(Id, "password", connection.Password, token);
                _settings["webDavUrl"] = connection.Url;
                _settings["username"] = connection.Username;
                InvalidateRelay();
                await PersistSettingsAsync(token);
                relayImported = true;
            }
            finally { _operations.Release(); }
        }
        await _operations.WaitAsync(token);
        try
        {
            lock (_stateLock)
            {
                if (payload.Address.Length > 0) _settings["peers"] = WithPeer(payload.DeviceId, payload.Name, payload.Address);
                _peerChecks.Remove(payload.DeviceId);
            }
            if (payload.Address.Length > 0) await PersistSettingsAsync(token);
        }
        finally { _operations.Release(); }
        await ConfirmOwnDeviceAsync(payload.DeviceId, payload.Name, payload.Address, token);
        _assistant = await _assistantStore!.ConfigureAsync(Identity(), token);
        EmitAssistantChanged("link.import");
        SignalAssistant();
        return new
        {
            joined = true,
            conversationId = payload.ConversationId,
            deviceId = payload.DeviceId,
            name = payload.Name,
            relayImported,
            linked = Linked
        };
    }

    // ---- helpers --------------------------------------------------------------------------------

    private async Task<AssistantState> AssistantStateAsync(CancellationToken token)
    {
        _assistant ??= await (_assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。")).LoadAsync(token);
        return _assistant;
    }

    private static string RequiredItemId(JsonObject args)
    {
        var value = (SettingsJson.ReadString(args, "itemId") ?? "").Trim();
        if (value.Length == 0) throw new ArgumentException("请选择要操作的条目。");
        if (value.Length != 32 || !value.All(Uri.IsHexDigit)) throw new ArgumentException("条目编号无效。");
        return value;
    }

    private static bool ReadFlag(JsonObject args, string key)
    {
        try { return args[key]?.GetValue<bool>() ?? false; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return false; }
    }

    private static string StateText(AssistantItemState? state) => state switch
    {
        AssistantItemState.Queued => "queued",
        AssistantItemState.Sending => "sending",
        AssistantItemState.Stored => "stored",
        AssistantItemState.Delivered => "delivered",
        AssistantItemState.Downloading => "downloading",
        AssistantItemState.Available => "available",
        AssistantItemState.Failed => "failed",
        AssistantItemState.Cancelled => "cancelled",
        _ => ""
    };

    private static string PlatformName() =>
        OperatingSystem.IsAndroid() ? "android" : OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "";

    private async Task<bool> RelayReadyAsync(CancellationToken token) =>
        Setting("webDavUrl").Length > 0 && Setting("username").Length > 0
        && await SecretAsync("password", token) is { Length: > 0 };

    private JsonObject RelayJson()
    {
        var configured = Setting("webDavUrl").Length > 0 && Setting("username").Length > 0;
        string state;
        string message;
        lock (_stateLock)
        {
            state = !configured ? "unconfigured" : _cloudCheckedAt is null ? "unknown" : _cloudReachable ? "available" : "unavailable";
            message = configured && _cloudCheckedAt is null ? "尚未检查中转网盘连接。" : _cloudMessage;
        }
        return new JsonObject { ["configured"] = configured, ["state"] = state, ["message"] = message };
    }

    private JsonObject ReceivingDetailsJson(bool enabled)
    {
        var json = new JsonObject
        {
            ["enabled"] = enabled,
            ["address"] = Setting("listenAddress"),
            ["port"] = TransferFiles.Port,
            ["pendingRequests"] = _receiveAuthorization.Pending().Count,
            ["discoverable"] = _beacon?.IsRunning == true,
            ["note"] = _receiveNote.Length > 0 ? _receiveNote : _beaconNote
        };
        // A WebDAV relay has no push channel, so an enabled receiver is the only case that polls the
        // conversation on a bounded interval. Everything else waits for an explicit signal.
        if (enabled) json["syncSeconds"] = OfflineReceiveSyncSeconds;
        return json;
    }

    private void EmitAssistantChanged(string reason)
    {
        var payload = new JsonObject { ["reason"] = reason, ["at"] = DateTimeOffset.UtcNow.ToString("O") };
        _events.Writer.TryWrite(new(Id, (ulong)Interlocked.Increment(ref _seq), "file-transfer.assistant.changed", DateTimeOffset.UtcNow, payload));
    }

    /// <summary>The receiver callbacks that connect inbound items to the conversation store.</summary>
    private async Task<bool> IsTrustedTokenAsync(string token, string? deviceId, string? address, string? name, CancellationToken token2)
    {
        if (token.Length == 0) return false;
        if (token == _conversationKey)
        {
            // Only a device that holds the conversation key can authenticate this way, and the endpoint
            // it connected from is the address a later direct send has to use.
            if (deviceId is { Length: > 0 } member) await ConfirmOwnDeviceAsync(member, name ?? "", address ?? "", token2);
            return true;
        }
        if (deviceId is not { Length: > 0 } id) return false;
        var stored = await SecretAsync("peer-" + id, token2);
        return stored is { Length: > 0 } && stored == token;
    }

    /// <summary>
    /// Deduplication may only confirm content this device really holds. A known remote id whose payload
    /// was never downloaded, or whose local file is gone, is not an acknowledgement.
    /// </summary>
    private Task<bool> IsKnownItemAsync(string itemId, CancellationToken token)
    {
        var item = _assistant?.Find(itemId);
        var available = item is not null && item.State != AssistantItemState.Cancelled && AssistantContent.HasLocalContent(item);
        return Task.FromResult(available);
    }

    /// <summary>
    /// Persists an item the receiver already wrote to disk, before the sender is acknowledged. The
    /// durable copy lives in the conversation store; publishing to the system downloads folder uses a
    /// separate copy, because that publisher deletes the file it is given.
    /// </summary>
    private async Task OnReceivedItemAsync(ReceivedItem received, CancellationToken token)
    {
        var store = _assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。");
        var item = new AssistantItem
        {
            Id = received.ItemId,
            Kind = received.ItemKind switch
            {
                AssistantWire.TextItem => AssistantItemKind.Text,
                AssistantWire.ImageItem => AssistantItemKind.Image,
                _ => AssistantItemKind.File
            },
            Text = received.ItemKind == AssistantWire.TextItem ? received.Text : null,
            Name = received.Name,
            Size = received.Size,
            CreatedAt = received.At,
            SenderDeviceId = received.DeviceId,
            SenderName = received.SenderName,
            TargetDeviceId = received.TargetDeviceId,
            State = AssistantItemState.Available,
            BytesDone = received.Size
        };
        if (item.Kind != AssistantItemKind.Text)
        {
            // The adopted path must survive the platform publisher: move the spool file into the store.
            var directory = store.GetInboxDirectory(item);
            Directory.CreateDirectory(directory);
            var name = received.Path is { Length: > 0 } path ? Path.GetFileName(path) : TransferFiles.FileName(item.Name ?? "来件");
            item.LocalPath = received.Path is { Length: > 0 } spool
                ? TransferFiles.Commit(spool, directory, name)
                : store.GetInboxPath(item);
        }
        var adopted = await store.AdoptAsync(Identity(), item, token);
        if (adopted.Kind != AssistantItemKind.Text) await PublishCopyAsync(store, adopted, token);
        EmitAssistantChanged("received");
        SignalAssistant();
    }

    /// <summary>Publishes a throwaway copy to the downloads folder; the durable original stays untouched.</summary>
    private async Task PublishCopyAsync(AssistantStore store, AssistantItem item, CancellationToken token)
    {
        if (_downloads is null) return;
        var source = store.GetPayloadPath(item) ?? item.LocalPath;
        if (source is not { Length: > 0 } || !File.Exists(source)) return;
        var directory = Path.Combine(_data, "publish");
        try
        {
            Directory.CreateDirectory(directory);
            var copy = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(source));
            File.Copy(source, copy, true);
            await PublishAsync(copy, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            // The message is already durable and openable; a downloads copy is a convenience only.
        }
    }
}

/// <summary>
/// The one-time link code that joins another own device: it carries the conversation id, the shared
/// conversation key, the public identity needed to reach the owner and, when the relay is configured,
/// the relay connection so one scan is enough. Only a QR scan or a paste brings it in, and every
/// secret it contains goes straight into the secret store.
/// </summary>
public static class LinkCode
{
    /// <summary>The frozen activation prefix. <see cref="LegacyPrefix"/> is only decoded, never produced.</summary>
    public const string Prefix = "mpt://assistant/";
    public const string LegacyPrefix = "mpt://link/";
    public sealed record Payload(int Version, string ConversationId, string Key, string DeviceId, string Name,
        string Address, int Port, string Platform, string? Cloud = null);

    public static string Encode(Payload payload) =>
        Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload, DirectTransfer.Json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static Payload Decode(string code)
    {
        var trimmed = (code ?? "").Trim();
        var prefix = trimmed.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix
            : trimmed.StartsWith(LegacyPrefix, StringComparison.Ordinal) ? LegacyPrefix : "";
        if (prefix.Length == 0 || trimmed.Length > 16384)
            throw new ArgumentException("请粘贴或扫描“连接我的设备”里的连接码。");
        var base64 = trimmed[prefix.Length..].Replace('-', '+').Replace('_', '/');
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')), DirectTransfer.Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new ArgumentException("连接码无效。"); }
        if (payload is null || payload.Version != 1) throw new ArgumentException("连接码无效。");
        TransferFiles.DeviceId(payload.ConversationId);
        TransferFiles.DeviceId(payload.DeviceId);
        if (payload.Key.Length != 64 || !payload.Key.All(Uri.IsHexDigit)) throw new ArgumentException("连接码无效。");
        if (payload.Name.Length > 100 || payload.Name.Any(char.IsControl)) throw new ArgumentException("连接码无效。");
        if (payload.Address.Length > 0 && !System.Net.IPAddress.TryParse(payload.Address, out var address))
            throw new ArgumentException("连接码里的地址无效。");
        if (payload.Cloud is { Length: > 0 } cloud)
        {
            var connection = CloudConnection.Decode(cloud);
            payload = payload with { Cloud = new CloudConnection(connection.Url, connection.Username, connection.Password).Encode() };
        }
        return payload;
    }
}
