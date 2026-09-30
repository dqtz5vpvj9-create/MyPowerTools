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
    /// <summary>The display name of the "send to myself" target in the draft contract.</summary>
    private const string SelfTargetName = "文件传输助手";
    /// <summary>Direct deliveries attempted in one sync round; the next round continues immediately.</summary>
    private const int DirectDeliveryLimit = 8;
    /// <summary>A device that already acknowledged an item is never offered the same item again.</summary>
    private static readonly TimeSpan DirectRetryDelay = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _receiveSignal = new(0, 1);
    private readonly SemaphoreSlim _publicRelayInitGate = new(1, 1);
    private readonly object _publicRelayLock = new();
    private int _publicRelayGeneration;
    private readonly HashSet<string> _discovered = new(StringComparer.Ordinal);
    private PublicRelayClient? _publicRelay;
    private string _publicRelayIdentity = "";
    private string _relayAuthError = "";
    // The public relay keeps its own health: a default URL is not a verified relay, and it must not
    // overwrite the state of a user's own OpenList either.
    private bool _assistantRelayReachable;
    private DateTimeOffset? _assistantRelayCheckedAt;
    private string _assistantRelayMessage = "";
    private bool _receivingEnabled = true;
    private CancellationTokenSource? _receiveCts;
    private Task? _relayReceiveLoop;
    private long _relayRevision = -1;
    private readonly SemaphoreSlim _directGate = new(1, 1);
    private readonly SemaphoreSlim _relayGate = new(1, 1);
    private Task? _relayPass;
    private readonly SemaphoreSlim _relaySignal = new(0, 1);
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
        StartCloudPayloadWorker();
        StartAssistantWorker();
        // Enabled module means receiving: the public relay carries it with no Tailscale and no settings.
        StartRelayReceive();
        StartInboxReceive();
        StartDepositLoop();
        SignalAssistant();
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

    private bool IsConversationMember(string deviceId) => OwnDevices().Any(device => device.DeviceId == deviceId);

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

    /// <summary>
    /// Receives through the public relay. It blocks in the server's long poll instead of polling on a
    /// timer, backs off in bounded steps when the network fails, and lets a new send interrupt that
    /// backoff so a queue recovers on its own without a manual retry.
    /// </summary>
    private void StartRelayReceive()
    {
        if (_relayReceiveLoop is not null) return;
        _relayReceiveLoop = Task.Run(RelayReceiveLoopAsync);
    }

    private async Task RelayReceiveLoopAsync()
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!_lifetime.IsCancellationRequested)
        {
            if (!_receivingEnabled)
            {
                await WaitForReceiveEventAsync(Timeout.InfiniteTimeSpan);
                continue;
            }
            if (CustomRelayConfigured)
            {
                await WaitForReceiveEventAsync(TimeSpan.FromSeconds(OfflineReceiveSyncSeconds));
                if (_receivingEnabled) KickRelayLeg();
                continue;
            }
            var poll = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _receiveCts = poll;
            try
            {
                var relay = await PublicRelayAsync(poll.Token);
                if (relay is null)
                {
                    // No identity yet, or a rejected key: wait for a signal instead of spinning.
                    await WaitForReceiveEventAsync(TimeSpan.FromSeconds(30));
                    continue;
                }
                var conversation = _conversationId;
                var revision = await relay.ChangesAsync(_relayRevision < 0 ? null : _relayRevision, poll.Token);
                if (conversation != _conversationId) continue;
                if (revision != _relayRevision)
                {
                    _relayRevision = revision;
                    // New conversation data: run one sync pass (durable relay + direct accelerator).
                    SignalAssistant();
                }
                else if (HasPendingOutgoing())
                {
                    // The relay is reachable again and something is still waiting: recover by itself.
                    SignalAssistant();
                }
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { /* receive.stop cancelled this poll */ }
            catch (PublicRelayAuthException) { await WaitForReceiveEventAsync(TimeSpan.FromMinutes(1)); }
            catch (Exception)
            {
                await WaitForReceiveEventAsync(backoff);
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromMinutes(2).Ticks));
            }
            finally
            {
                if (ReferenceEquals(_receiveCts, poll)) _receiveCts = null;
                poll.Dispose();
            }
        }
    }

    /// <summary>Cheap in-memory check for queued or failed outgoing work; never touches the network.</summary>
    private bool HasPendingOutgoing()
    {
        var state = _assistant;
        if (state is null) return false;
        return state.Items.Any(item => item.SenderDeviceId == Setting("deviceId")
            && item.State is AssistantItemState.Queued or AssistantItemState.Failed);
    }

    /// <summary>A bounded wait that a new send or a receive change interrupts immediately.</summary>
    private async Task WaitForReceiveEventAsync(TimeSpan wait)
    {
        try { await _receiveSignal.WaitAsync(wait, _lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Wakes the queue worker; there is no timer when nothing is queued and nothing is enabled.</summary>
    private void SignalAssistant()
    {
        if (_lifetime.IsCancellationRequested) return;
        _assistantSignal.Release();
        KickRelayLeg();
        KickDeposit();
        // A new send is also the moment to leave a backoff: the queue must recover by itself.
        WakeReceive();
    }

    private void WakeReceive()
    {
        try { _receiveSignal.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void StartAssistantWorker()
    {
        if (_assistantWorker is not null) return;
        _assistantWorker = Task.Run(AssistantLoopAsync);
        _relayPass = Task.Run(RelayWorkLoopAsync);
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
                var direct = await RunDirectLegAsync(Identity(), _lifetime.Token);
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
            identity = Identity(); // a queued pass may have waited while another device joined
            var store = _assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。");
            var outcome = await RunRelayPassAsync(store, identity, token);
            // Any item or receipt that arrived proves its writer holds the conversation key, so the
            // membership scan runs after every pass instead of only when receipts were read.
            await ConfirmConversationMembersAsync(outcome.Result, token);
            return outcome;
        }
        finally { _relayGate.Release(); }
    }

    // A bounded signal retains a request that arrives during a pass. Checking IsCompleted and
    // dropping the request loses this race: the previous pass may have already read the queue.
    private void KickRelayLeg()
    {
        try { _relaySignal.Release(); }
        catch (SemaphoreFullException) { /* one pending pass already covers this wake-up */ }
    }

    private async Task RelayWorkLoopAsync()
    {
        var wait = Timeout.InfiniteTimeSpan;
        var backoff = TimeSpan.FromSeconds(2);
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await _relaySignal.WaitAsync(wait, _lifetime.Token);
                var (result, error) = await RunRelayLegAsync(Identity(), _lifetime.Token);
                if ((result.RetryAfter is not null || error.Length > 0) && _relayAuthError.Length == 0)
                {
                    wait = CustomRelayConfigured ? result.RetryAfter ?? backoff : backoff;
                    backoff = TimeSpan.FromSeconds(Math.Min(120, backoff.TotalSeconds * 2));
                }
                else
                {
                    backoff = TimeSpan.FromSeconds(2);
                    wait = result.HasMore ? TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan;
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                NoteRelayHealth(false, MptLogRedactor.Redact(ex.Message));
                wait = TimeSpan.FromSeconds(5);
            }
        }
    }

    /// <summary>
    /// One relay pass. The health it reports comes from the pass result: a relay that answered with a
    /// server error or asked for a retry is never recorded as reachable, and a slow pass is bounded by
    /// the caller's token rather than by the direct leg.
    /// </summary>
    private async Task<(AssistantSyncResult Result, string Error)> RunRelayPassAsync(AssistantStore store,
        AssistantIdentity identity, CancellationToken token)
    {
        OpenListClient? cloud;
        try { cloud = await AssistantRelayAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var failure = MptLogRedactor.Redact(ex.Message);
            NoteRelayHealth(false, failure);
            return (new AssistantSyncResult(0, 0, 0, 0, 0, 0, false, null), failure);
        }
        if (cloud is null) return (new AssistantSyncResult(0, 0, 0, 0, 0, 0, false, null), _relayAuthError);
        try
        {
            using var _ = cloud;
            using var shared = SharedTransport(store, identity);
            // A message for a device outside this conversation is not written into this namespace: the
            // direct channel is its real path, and the item reports honestly when that is unreachable.
            var sync = new AssistantSync(store, cloud)
            {
                ItemCancellation = ItemToken,
                Changed = NotifyAssistantTransferChanged,
                SharedTransport = shared,
                CloudPublisher = (message, payload, cancel) => PublishCloudAttachmentAsync(identity, message, payload, cancel),
                PublishFilter = item => (!CloudOnly || item.Kind == AssistantItemKind.Text || DefaultCloudAccount is not null) && item.TargetDeviceId is null && AssistantConversations.IsShared(item, identity.ConversationId)
            };
            var result = await sync.SyncAsync(identity, token);
            if (result.RetryAfter is not null)
            {
                // The relay answered with a failure it wants retried: that is not a healthy relay.
                var message = result.Message is { Length: > 0 } text ? text : "文件助手连接暂时不可用，稍后会自动重试。";
                NoteRelayHealth(false, message);
                return (result, message);
            }
            NoteRelayHealth(true, "");
            return (result, "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = MptLogRedactor.Redact(ex.Message);
            NoteRelayHealth(false, "连接文件助手失败：" + message);
            return (new AssistantSyncResult(0, 0, 0, 0, 0, 0, false, null), message);
        }
    }

    /// <summary>
    /// Records the outcome of an assistant pass on whichever relay it used. It deliberately does not
    /// touch the legacy OpenList check cache: only the explicit cloud commands verify that relay, and a
    /// configured URL is not proof either.
    /// </summary>
    private void NoteRelayHealth(bool reachable, string message)
    {
        lock (_stateLock)
        {
            _assistantRelayReachable = reachable;
            _assistantRelayCheckedAt = DateTimeOffset.UtcNow;
            _assistantRelayMessage = message;
        }
    }

    /// <summary>
    /// Own devices are confirmed only from data this pass really read back over the shared conversation:
    /// a manifest it listed, or a receipt it fetched through the conversation DAV. The local store is
    /// deliberately not consulted — an entry that arrived through a device-pairing inbox is stored in the
    /// same place, and its sender is not a member of this conversation.
    /// </summary>
    private async Task ConfirmConversationMembersAsync(AssistantSyncResult? result, CancellationToken token)
    {
        if (result?.Verified is not { Count: > 0 } proofs) return;
        foreach (var proof in proofs)
        {
            if (proof.DeviceId.Length == 0 || proof.DeviceId == Setting("deviceId")) continue;
            await ConfirmOwnDeviceAsync(proof.DeviceId, proof.Name, "", token);
        }
    }

    /// <summary>
    /// The cancellation scope of one item for the current pass. Both transports link to it, so a user
    /// cancel aborts the active WebDAV request and the active TCP stream alike, and never a sibling.
    /// </summary>
    private CancellationTokenSource ItemScope(string itemId) =>
        _itemCancellation.GetOrAdd(itemId, _ => new CancellationTokenSource());

    /// <summary>
    /// The same per-item scope, additionally linked to a caller's token: aborting the surrounding work
    /// (a stopped loop, a disposed module) aborts the item's request too, and the user's cancel of one
    /// item still aborts only that item.
    /// </summary>
    private CancellationTokenSource ItemScope(string itemId, CancellationToken parent)
    {
        var scope = ItemScope(itemId);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, parent);
        return linked;
    }

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
            && (AssistantConversations.IsPrivate(item) || AssistantConversations.IsShared(item, identity.ConversationId))
            && item.State is AssistantItemState.Queued or AssistantItemState.Sending or AssistantItemState.Stored or AssistantItemState.Failed)
            .OrderBy(item => item.CreatedAt).ToArray())
        {
            if (CloudOnly && item.Kind != AssistantItemKind.Text) continue;
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
                if (reply is null)
                {
                    await NoteUnreachableTargetAsync(item, device, token);
                    continue;
                }
                if (reply.Pending) pending++;
                if (!reply.Ok || !string.Equals(reply.State, AssistantWire.DeliveredState, StringComparison.Ordinal)) continue;
                delivered++;
                var receiptAdded = false;
                await store.MutateIfChangedAsync(current =>
                {
                    var row = current.Find(item.Id);
                    if (row is null || row.Receipts.Any(receipt => receipt.DeviceId == device.DeviceId)) return false;
                    MergeReceipt(current, row, device);
                    if (row.TargetDeviceId is not null) row.State = AssistantItemState.Delivered;
                    return receiptAdded = true;
                }, token);
                if (receiptAdded) EmitAssistantChanged("direct.receipt");
            }
        }
        // Paired devices are reached by the independent deposit scheduler, never from here: a direct
        // attempt may wait minutes for a first-contact confirmation and must not hold the public copy.
        KickDeposit();
        return new DirectOutcome(delivered, pending, false);
    }

    /// <summary>
    /// A message for a device outside the conversation has only the direct channel; when that fails the
    /// entry carries the real reason instead of a delivery claim.
    /// </summary>
    private async Task NoteUnreachableTargetAsync(AssistantItem item, DiscoveredDevice device, CancellationToken token)
    {
        if (item.TargetDeviceId is not { Length: > 0 }) return;
        if (IsConversationMember(item.TargetDeviceId)) return;
        await _assistantStore!.MutateAsync(state =>
        {
            var row = state.Find(item.Id);
            if (row is null || row.State is AssistantItemState.Delivered or AssistantItemState.Cancelled) return;
            row.Error = $"{device.Name} 不在同一个文件助手会话，且直连不可达；请让对方加入连接码，或稍后重试。";
        }, token);
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
                await DirectTokenAsync(device.DeviceId, AssistantConversations.IsPrivate(item), token),
                item.Name,
                item.Size,
                identity.DeviceId,
                item.Id,
                item.ConversationId,
                item.Kind switch
                {
                    AssistantItemKind.Text => AssistantWire.TextItem,
                    AssistantItemKind.Image => AssistantWire.ImageItem,
                    _ => AssistantWire.FileItem
                },
                item.Text,
                identity.Name,
                item.TargetDeviceId,
                Address: Setting("listenAddress"),
                Scope: AssistantConversations.IsPrivate(item) ? "device" : "shared");
            // A first contact may hold this connection while its user decides, so the budget covers it.
            // A self send only goes to own devices, which answer immediately or not at all: the connect
            // window is short so a dead candidate never holds the queue or the relay's schedule, while a
            // user-picked target may legitimately wait for its first-contact confirmation.
            var targeted = item.TargetDeviceId is { Length: > 0 };
            var budget = targeted ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
            var connect = targeted ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(3);
            // Progress goes through the store, so the shared snapshot is never written from this thread.
            return await AssistantWire.SendItemAsync(device.Address, device.Port, frame, payload,
                (done, _) => ReportAssistantTransferProgress(item.Id, done), budget, itemCancellation.Token, connect);
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
    private async Task<string> DirectTokenAsync(string deviceId, bool privateMessage, CancellationToken token)
    {
        if (!privateMessage) return _conversationKey;
        var peer = FindPeer(deviceId);
        if (peer is not null)
        {
            var stored = await SecretAsync("peer-" + deviceId, token);
            if (!string.IsNullOrEmpty(stored)) return stored;
        }
        // Old remembered contacts may lack a stored token: the receiver's existing first-contact
        // confirmation remains available, but a shared credential never substitutes for private trust.
        return "";
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
        {
            var json = JsonSerializer.SerializeToNode(item, AssistantJson.Options)!.AsObject();
            json["conversationKey"] = AssistantConversations.Key(item, Setting("deviceId"));
            items.Add(json);
        }
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
                ["ownDevices"] = OwnDevices().Length,
                ["conversationId"] = _conversationId,
                ["conversationKey"] = AssistantConversations.Shared(_conversationId)
            },
            ["items"] = items,
            ["members"] = new JsonArray(OwnDevices().Select(device => (JsonNode?)new JsonObject
            {
                ["id"] = device.DeviceId, ["name"] = device.Name, ["address"] = device.Address,
                ["canPrivateMessage"] = FindPeer(device.DeviceId) is not null,
                ["requiresPairing"] = FindPeer(device.DeviceId) is null
            }).ToArray()),
            ["pendingRequests"] = pending,
            ["relay"] = RelayJson(),
            // The device's own deposit inbox is its own namespace, next to (never inside) the conversation.
            ["inbox"] = InboxJson(),
            // Receiving is the module state: the public relay keeps receiving without a Tailnet listener.
            ["receiving"] = _receivingEnabled,
            ["receivingDetails"] = ReceivingDetailsJson(_receivingEnabled)
        };
    }

    /// <summary>
    /// Reads the durable composer draft. It is a pure local read: no relay call, no discovery, no
    /// receiver restart, and it never creates or changes a conversation entry.
    /// </summary>
    private async Task<object> AssistantPreferencesInspectAsync(CancellationToken token)
    {
        var state = await AssistantStateAsync(token);
        return PreferencesJson(state, null);
    }

    /// <summary>
    /// Saves one coherent composer snapshot. Every field present in the request replaces the stored
    /// value and an explicit null/empty value clears it, so a cleared draft can never be resurrected by
    /// a later read; a field that is absent keeps its stored value. The whole snapshot is one store
    /// transaction: a rejected value changes nothing on disk and in memory. This writes local
    /// preferences only — it never contacts the relay, restarts the receiver, emits a conversation
    /// change event or creates a message.
    /// </summary>
    private async Task<object> AssistantPreferencesUpdateAsync(JsonObject args, CancellationToken token)
    {
        foreach (var key in args.Select(pair => pair.Key))
            if (key is not ("draftText" or "attachmentPaths" or "targetDeviceId" or "conversationKey" or "scrollOffset" or "lastReadAt"))
                throw new ArgumentException($"不支持设置项：{key}。");
        var store = _assistantStore ?? throw new InvalidOperationException("会话存储尚未就绪。");
        var explicitKey = args.TryGetPropertyValue("conversationKey", out var keyNode)
            ? AssistantConversations.ValidateKey(ReadOptionalString(keyNode, "conversationKey") ?? throw new ArgumentException("会话标识不能为空。"))
            : null;
        List<string> missing = [];
        var state = await store.MutateIfChangedAsync(current =>
        {
            var activeKey = current.ActiveConversationKey ?? AssistantConversations.DraftKey(Identity(), current.Preferences);
            var key = explicitKey ?? activeKey;
            var candidate = explicitKey is null
                ? (current.Preferences ?? new AssistantPreferences()).Copy()
                : current.Drafts.TryGetValue(key, out var existing) ? existing.Copy()
                : new AssistantPreferences { TargetDeviceId = key.StartsWith("device:", StringComparison.Ordinal) ? key[7..] : null };
            if (args.TryGetPropertyValue("draftText", out var textNode))
                candidate.DraftText = AssistantPreferenceRules.Text(ReadOptionalString(textNode, "draftText"));
            if (args.TryGetPropertyValue("attachmentPaths", out var pathsNode))
            {
                var usable = new List<string>();
                foreach (var path in AssistantPreferenceRules.Attachments(ReadPathArray(pathsNode)))
                {
                    if (File.Exists(path)) usable.Add(path);
                    else missing.Add(path);
                }
                candidate.AttachmentPaths = usable;
            }
            if (args.TryGetPropertyValue("targetDeviceId", out var targetNode))
            {
                var target = AssistantPreferenceRules.Target(ReadOptionalString(targetNode, "targetDeviceId"));
                candidate.TargetDeviceId = target == Setting("deviceId") ? null : target;
            }
            if (explicitKey is not null)
            {
                var expectedTarget = key.StartsWith("device:", StringComparison.Ordinal) ? key[7..] : null;
                if (candidate.TargetDeviceId != expectedTarget)
                    throw new ArgumentException("草稿接收设备与会话不一致。");
            }
            else key = AssistantConversations.DraftKey(Identity(), candidate);
            if (args.TryGetPropertyValue("scrollOffset", out var offsetNode))
            {
                if (offsetNode?.GetValueKind() != JsonValueKind.Number
                    || !double.TryParse(offsetNode.ToJsonString(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var offset)
                    || !double.IsFinite(offset) || offset < 0)
                    throw new ArgumentException("滚动位置必须是非负有限数值。");
                candidate.ScrollOffset = offset;
            }
            if (args.TryGetPropertyValue("lastReadAt", out var readNode))
            {
                var text = ReadOptionalString(readNode, "lastReadAt");
                if (text is null) candidate.LastReadAt = null;
                else if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var readAt)) candidate.LastReadAt = readAt;
                else throw new ArgumentException("已读时间格式无效。");
            }
            var previous = current.Drafts.GetValueOrDefault(key);
            if (activeKey == key && SamePreferences(previous ?? current.Preferences, candidate)) return false;
            candidate.SavedAt = SamePreferences(previous, candidate) ? previous?.SavedAt : DateTimeOffset.UtcNow;
            // Preserve every other conversation; selecting a new one only changes the flat compatibility projection.
            current.Drafts = new Dictionary<string, AssistantPreferences>(current.Drafts, StringComparer.Ordinal) { [key] = candidate.Copy() };
            current.ActiveConversationKey = key;
            current.Preferences = candidate;
            return true;
        }, token);
        return PreferencesJson(state, missing);
    }

    /// <summary>Value equality of one snapshot; the save time is bookkeeping and never a difference.</summary>
    private static bool SamePreferences(AssistantPreferences? stored, AssistantPreferences candidate) =>
        stored is not null
            ? stored.DraftText == candidate.DraftText
                && stored.TargetDeviceId == candidate.TargetDeviceId
                && stored.ScrollOffset == candidate.ScrollOffset && stored.LastReadAt == candidate.LastReadAt
                && stored.AttachmentPaths.SequenceEqual(candidate.AttachmentPaths, AssistantPreferenceRules.PathComparer)
            : candidate.DraftText is null && candidate.TargetDeviceId is null && candidate.AttachmentPaths.Count == 0
                && candidate.ScrollOffset == 0 && candidate.LastReadAt is null;

    /// <summary>
    /// The one inspect/update snapshot shape. Attachment references are re-checked here: only files
    /// that exist right now are restored, and everything that could not be restored is listed with its
    /// file name so the page can say exactly what is missing instead of silently dropping it.
    /// </summary>
    private JsonObject PreferencesJson(AssistantState state, IReadOnlyList<string>? missingFromUpdate)
    {
        var key = state.ActiveConversationKey ?? AssistantConversations.DraftKey(Identity(), state.Preferences);
        var result = DraftJson(state.Preferences, missingFromUpdate);
        result["conversationKey"] = key;
        var drafts = new JsonObject();
        foreach (var pair in state.Drafts) drafts[pair.Key] = DraftJson(pair.Value, pair.Key == key ? missingFromUpdate : null);
        if (!drafts.ContainsKey(key)) drafts[key] = DraftJson(state.Preferences, missingFromUpdate);
        result["drafts"] = drafts;
        return result;
    }

    private JsonObject DraftJson(AssistantPreferences? preferences, IReadOnlyList<string>? missingFromUpdate)
    {
        var usable = new JsonArray();
        var missing = new List<string>();
        var missingSeen = new HashSet<string>(AssistantPreferenceRules.PathComparer);
        foreach (var path in missingFromUpdate ?? []) if (missingSeen.Add(path)) missing.Add(path);
        var usableSeen = new HashSet<string>(AssistantPreferenceRules.PathComparer);
        foreach (var path in preferences?.AttachmentPaths ?? [])
        {
            if (!usableSeen.Add(path)) continue;
            if (File.Exists(path)) usable.Add(path);
            else if (missingSeen.Add(path)) missing.Add(path);
        }
        var missingJson = new JsonArray();
        foreach (var path in missing)
            missingJson.Add(new JsonObject
            {
                ["path"] = path,
                ["name"] = Path.GetFileName(path) is { Length: > 0 } name ? name : path
            });
        var target = preferences?.TargetDeviceId;
        var (targetName, targetUsable) = DescribeTarget(target);
        return new JsonObject
        {
            ["draftText"] = preferences?.DraftText,
            ["attachmentPaths"] = usable,
            ["missingAttachments"] = missingJson,
            ["targetDeviceId"] = target,
            ["targetName"] = targetName,
            ["targetUsable"] = targetUsable,
            ["savedAt"] = preferences?.SavedAt?.ToString("O"),
            ["scrollOffset"] = preferences?.ScrollOffset ?? 0,
            ["lastReadAt"] = preferences?.LastReadAt?.ToString("O")
        };
    }

    /// <summary>
    /// Local knowledge only: a target counts as usable when it is this device, one of the conversation's
    /// own devices or a remembered peer — a relationship the user already confirmed. A device that was
    /// merely discovered is a candidate, not an authorization, so it stays unusable until the user pairs
    /// it. Nothing is probed here, and a stored target that is no longer known keeps its stable id
    /// instead of being silently cleared or sent to self.
    /// </summary>
    private (string Name, bool Usable) DescribeTarget(string? targetDeviceId)
    {
        // No target is "send to myself", whose label is the tool's own name.
        if (string.IsNullOrEmpty(targetDeviceId) || targetDeviceId == Setting("deviceId")) return (SelfTargetName, true);
        var own = OwnDevices().FirstOrDefault(device => device.DeviceId == targetDeviceId);
        if (own is not null) return (own.Name.Length > 0 ? own.Name : targetDeviceId, FindPeer(targetDeviceId) is not null);
        if (FindPeer(targetDeviceId) is { } peer)
        {
            var peerName = PeerText(peer, "name");
            return (peerName.Length > 0 ? peerName : targetDeviceId, true);
        }
        return ("", false);
    }

    /// <summary>A JSON string or null. Any other type is a client error, never a silent clear.</summary>
    private static string? ReadOptionalString(JsonNode? node, string key)
    {
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        throw new ArgumentException($"设置项 {key} 需要文本值或 null。");
    }

    /// <summary>A JSON array of strings or null; null clears the attachment list.</summary>
    private static IReadOnlyList<string> ReadPathArray(JsonNode? node)
    {
        if (node is null) return [];
        if (node is not JsonArray array) throw new ArgumentException("设置项 attachmentPaths 需要路径数组或 null。");
        var paths = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is null) continue;
            if (item is JsonValue value && value.TryGetValue<string>(out var path)) { paths.Add(path); continue; }
            throw new ArgumentException("设置项 attachmentPaths 需要路径数组或 null。");
        }
        return paths;
    }

    /// <summary>
    /// One send request may carry text and attachments together. Everything is persisted before the
    /// answer, and a failure rolls the whole request back, so the user never sees "sent" for half of
    /// it nor a retry that duplicates the half that did succeed.
    /// </summary>
    private async Task<object> AssistantSendAsync(JsonObject args, CancellationToken token)
    {
        var target = SettingsJson.ReadString(args, "targetDeviceId") ?? "";
        if (args["conversationKey"] is { } keyNode)
        {
            var key = AssistantConversations.ValidateKey(ReadOptionalString(keyNode, "conversationKey") ?? "");
            if (key == AssistantConversations.History) throw new ArgumentException("历史记录只读，请选择接收会话后转发。");
            var resolvedTarget = key.StartsWith("device:", StringComparison.Ordinal) ? key[7..] : "";
            if (resolvedTarget.Length == 0 && key != AssistantConversations.Shared(_conversationId))
                throw new ArgumentException("当前未连接这个共享会话，请先重新连接。");
            if (target.Length > 0 && target != resolvedTarget) throw new ArgumentException("接收设备与会话不一致。");
            target = resolvedTarget;
        }
        if (target.Length > 0 && target == Setting("deviceId")) target = "";
        if (target.Length > 0)
        {
            TransferFiles.DeviceId(target);
            // A target has to be a device this conversation already knows: an own device, a paired
            // device, or one this session discovered. An arbitrary id is refused.
            if (FindPeer(target) is null) throw new ArgumentException("请先与这台设备配对，再发送私聊消息。共享成员身份不会授予私聊投递权限。");
        }
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
        if (CloudOnly)
            await store.MutateAsync(current => { foreach (var queued in created) if (current.Find(queued.Id) is { Kind: not AssistantItemKind.Text } item) item.Error = FileTransfer.Core.Cloud.CloudAccountRules.PendingReason; }, token);
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
        await _relayGate.WaitAsync(token);
        try
        {
            using var cloud = await AssistantRelayAsync(token)
                ?? throw new InvalidOperationException(_relayAuthError.Length > 0 ? _relayAuthError : "文件助手连接尚未就绪，请稍后重试。");
            var identity = Identity();
            using var shared = SharedTransport(_assistantStore!, identity);
            await new AssistantSync(_assistantStore!, cloud) { ItemCancellation = ItemToken, Changed = NotifyAssistantTransferChanged,
                SharedTransport = shared }.EnsureLocalAsync(identity, itemId, token);
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
        lock (_stateLock)
        {
            _discovered.Clear();
            foreach (var result in report.Devices)
                if (result.Device.DeviceId.Length > 0 && result.Device.DeviceId != Setting("deviceId")) _discovered.Add(result.Device.DeviceId);
        }
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
            devices.Add(DeviceJson(new DiscoveredDevice(device.DeviceId, device.Name, device.Address, TransferFiles.Port, ""), FindPeer(device.DeviceId) is not null, PeerAvailable(device.DeviceId), ""));
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
            ["own"] = OwnDevices().Any(own => own.DeviceId == device.DeviceId),
            ["canPrivateMessage"] = FindPeer(device.DeviceId) is not null,
            ["requiresPairing"] = FindPeer(device.DeviceId) is null
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
        if (CustomRelayConfigured && await SecretAsync("password", token) is { Length: > 0 })
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
        // A running pass owns a snapshot of the old conversation. Finish it before switching the
        // durable store, or it can restore the old identity after the join notice has been enqueued.
        await _relayGate.WaitAsync(token);
        try { return await AssistantLinkImportCoreAsync(args, token); }
        finally { _relayGate.Release(); }
    }

    private async Task<object> AssistantLinkImportCoreAsync(JsonObject args, CancellationToken token)
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
        // The conversation changed, so the relay client and its long poll authenticate with the new identity.
        ResetPublicRelay();
        _assistant = await _assistantStore!.ConfigureAsync(Identity(), token);
        _relayRevision = -1;
        // Announcing the join is what lets the other side see this device without faking a delivery.
        var notice = await _assistantStore.EnqueueAsync(Identity(),
            AssistantDraft.ForText($"已加入文件助手：{DeviceName()}", null), token);
        EmitAssistantChanged("link.import");
        SignalAssistant();
        return new
        {
            joined = true,
            conversationId = payload.ConversationId,
            deviceId = payload.DeviceId,
            name = payload.Name,
            relayImported,
            linked = Linked,
            membershipNotice = notice.Count > 0
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

    /// <summary>True when the user configured their own relay; the public relay is only the default.</summary>
    private bool CustomRelayConfigured => Setting("webDavUrl").Length > 0 && Setting("username").Length > 0;

    private SharedLocatorTransfer? SharedTransport(AssistantStore store, AssistantIdentity identity) => CustomRelayConfigured ? null
        : new(new SharedLocatorClient(identity.ConversationId, _conversationKey, SharedRelayRoute.Public),
            new SharedLocatorClient(identity.ConversationId, _conversationKey, SharedRelayRoute.Tail),
            Path.Combine(store.Directory, "shared-copy"));

    /// <summary>
    /// The relay client this conversation uses right now: the user's own OpenList when it is configured,
    /// otherwise the public relay with the persisted conversation key. No user setting is required for
    /// the default path, and a custom relay is never overridden.
    /// </summary>
    private async Task<OpenListClient?> AssistantRelayAsync(CancellationToken token)
    {
        if (CustomRelayConfigured)
        {
            var password = await SecretAsync("password", token);
            if (password is not { Length: > 0 }) throw new InvalidOperationException("自定义存储缺少密码，请补全连接设置。");
            return new OpenListClient(Setting("webDavUrl"), Setting("username"), password);
        }
        var relay = await PublicRelayAsync(token);
        return relay?.CreateDavClient();
    }

    /// <summary>Registers (once per conversation identity) and returns the public relay client.</summary>
    private async Task<PublicRelayClient?> PublicRelayAsync(CancellationToken token)
    {
        await _publicRelayInitGate.WaitAsync(token);
        try
        {
            PublicRelayClient client;
            string identity;
            int generation;
            lock (_publicRelayLock)
            {
                if (_conversationId.Length == 0 || _conversationKey.Length != 64 || _relayAuthError.Length > 0) return null;
                identity = _conversationId + ":" + _conversationKey;
                if (_publicRelay is not null && _publicRelayIdentity == identity) return _publicRelay;
                generation = _publicRelayGeneration;
                client = new PublicRelayClient(_conversationId, _conversationKey);
            }
            try { await client.RegisterAsync(token); }
            catch (PublicRelayAuthException ex)
            {
                client.Dispose();
                lock (_publicRelayLock)
                {
                    if (generation != _publicRelayGeneration) return null;
                    _relayAuthError = ex.Message;
                }
                NoteRelayHealth(false, ex.Message);
                return null;
            }
            catch { client.Dispose(); throw; }
            lock (_publicRelayLock)
            {
                // A join can change identity while registration is in flight. Its old client must
                // never overwrite the newly joined conversation or authenticate the next poll.
                if (generation != _publicRelayGeneration)
                {
                    client.Dispose();
                    return null;
                }
                _publicRelay?.Dispose();
                _publicRelay = client;
                _publicRelayIdentity = identity;
                return client;
            }
        }
        finally { _publicRelayInitGate.Release(); }
    }

    private void ResetPublicRelay()
    {
        lock (_publicRelayLock)
        {
            _publicRelayGeneration++;
            _publicRelay?.Dispose();
            _publicRelay = null;
            _publicRelayIdentity = "";
            _relayAuthError = "";
        }
    }

    private JsonObject RelayJson()
    {
        // The public relay is the default transport, so "configured" means "a relay is available",
        // not "the user filled in settings".
        var custom = CustomRelayConfigured;
        var configured = custom || (_conversationId.Length > 0 && _conversationKey.Length == 64);
        string state;
        string message;
        lock (_stateLock)
        {
            var checkedAt = _assistantRelayCheckedAt;
            var reachable = _assistantRelayReachable;
            var detail = _assistantRelayMessage;
            state = _relayAuthError.Length > 0 ? "unavailable"
                : !configured ? "unconfigured"
                : checkedAt is null ? "unknown" : reachable ? "available" : "unavailable";
            message = _relayAuthError.Length > 0 ? _relayAuthError
                : configured && checkedAt is null ? "文件助手连接尚未开始同步。" : detail;
        }
        return new JsonObject
        {
            ["configured"] = configured,
            ["state"] = state,
            ["message"] = message,
            ["public"] = !custom,
            ["custom"] = custom,
            ["revision"] = Math.Max(0, _relayRevision),
            ["receiving"] = _receivingEnabled
        };
    }

    private JsonObject ReceivingDetailsJson(bool enabled)
    {
        var json = new JsonObject
        {
            ["enabled"] = enabled,
            ["engine"] = "public-relay",
            ["publicRelay"] = !CustomRelayConfigured,
            ["tailnetListener"] = Volatile.Read(ref _session) is not null,
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

    private void ReportAssistantTransferProgress(string itemId, long done)
    {
        if (_assistantStore?.ReportProgress(itemId, done) == true) NotifyAssistantTransferChanged(true);
    }

    private long _lastTransferProgressEvent;

    private void NotifyAssistantTransferChanged(bool progress)
    {
        if (progress)
        {
            var now = Environment.TickCount64;
            var previous = Interlocked.Read(ref _lastTransferProgressEvent);
            if (now - previous < 250 || Interlocked.CompareExchange(ref _lastTransferProgressEvent, now, previous) != previous) return;
        }
        EmitAssistantChanged(progress ? "transfer.progress" : "transfer.changed");
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
        if (received.Scope == "shared" && received.ConversationId != _conversationId)
            throw new InvalidDataException("收到的共享消息不属于当前会话。");
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
            ConversationId = received.ConversationId.Length > 0 ? received.ConversationId : null,
            Provenance = received.Scope == "shared" ? AssistantConversations.DirectShared
                : received.Scope == "device" ? AssistantConversations.DirectDevice
                : received.TargetDeviceId is null && received.ConversationId.Length > 0 ? AssistantConversations.DirectShared
                : null,
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

    /// <summary>
    /// Reads an external connection code. The payload is untrusted input: every field is checked for
    /// presence and shape before it is used, so a producer that omits one gets an "invalid connection
    /// code" answer instead of an exception from inside the parser.
    /// </summary>
    public static Payload Decode(string code)
    {
        var trimmed = (code ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > 16384)
            throw new ArgumentException("请粘贴或扫描“连接我的设备”里的连接码。");
        // A code from another flow is routed to the command that handles it.
        if (trimmed.StartsWith(Pairing.Prefix, StringComparison.Ordinal))
            throw new ArgumentException("这是设备连接码，请在“更多设置”的“添加设备”中导入。");
        if (trimmed.StartsWith("mpt://cloud/", StringComparison.Ordinal))
            throw new ArgumentException("这是网盘连接码，请在“更多设置”的“网盘中转”中导入。");
        var prefix = trimmed.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix
            : trimmed.StartsWith(LegacyPrefix, StringComparison.Ordinal) ? LegacyPrefix : "";
        if (prefix.Length == 0) throw new ArgumentException("请粘贴或扫描“连接我的设备”里的连接码。");
        var base64 = trimmed[prefix.Length..].Replace('-', '+').Replace('_', '/');
        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')), DirectTransfer.Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException) { throw new ArgumentException("连接码无效：内容无法识别。", ex); }
        if (payload is null) throw new ArgumentException("连接码无效：内容为空。");
        if (payload.Version != 1) throw new ArgumentException($"连接码无效：不支持的版本 {payload.Version}。");
        if (string.IsNullOrWhiteSpace(payload.ConversationId)) throw new ArgumentException("连接码无效：缺少会话标识。");
        if (string.IsNullOrWhiteSpace(payload.DeviceId)) throw new ArgumentException("连接码无效：缺少设备标识。");
        if (string.IsNullOrWhiteSpace(payload.Key)) throw new ArgumentException("连接码无效：缺少会话密钥。");
        TransferFiles.DeviceId(payload.ConversationId!);
        TransferFiles.DeviceId(payload.DeviceId!);
        if (payload.Key!.Length != 64 || !payload.Key.All(Uri.IsHexDigit)) throw new ArgumentException("连接码无效：会话密钥格式不正确。");
        var name = payload.Name ?? "";
        if (name.Length > 100 || name.Any(char.IsControl)) throw new ArgumentException("连接码无效：设备名称不正确。");
        var address = payload.Address ?? "";
        if (address.Length > 0 && !System.Net.IPAddress.TryParse(address, out _))
            throw new ArgumentException("连接码无效：地址格式不正确。");
        if (payload.Cloud is { Length: > 0 } cloud)
        {
            CloudConnection connection;
            try { connection = CloudConnection.Decode(cloud); }
            catch (ArgumentException ex) { throw new ArgumentException("连接码无效：网盘配置无法识别。", ex); }
            payload = payload with { Cloud = new CloudConnection(connection.Url, connection.Username, connection.Password).Encode() };
        }
        return payload with { Name = name, Address = address, Platform = payload.Platform ?? "" };
    }
}
