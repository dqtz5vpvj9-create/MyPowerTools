using System.Net;
using System.Net.Sockets;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// The passive, event-driven advertisement service behind "可被发现" / "开启可发现". While it runs:
/// <list type="bullet">
/// <item>the socket's receive loop is blocked on the UDP socket — nothing polls, reads files or wakes
/// on a timer unless the user explicitly enabled a re-announce interval;</item>
/// <item>a peer's who-is is answered with a unicast <c>here</c> that carries this device's public
/// information only (name, MPT port, Tailnet address, platform, device id) and repeats the peer's
/// nonce, so a LAN host cannot be used as an amplifier and a stale answer is never accepted;</item>
/// <item>up to <see cref="DiscoveryOptions.AnnounceInterval"/> (off by default) re-announces so peers
/// that are already listening learn about this device without soliciting.</item>
/// </list>
/// M4 starts it when the user enables discoverability and calls <see cref="StopAsync"/> when the user
/// disables it or the tool is disabled; a disabled tool must leave no socket open. Start twice with the
/// same identity is a no-op, start with a changed identity rebinds, stop is idempotent, and the
/// instance stays usable after a stop. Its <see cref="Channel"/> is handed to
/// <see cref="DeviceDiscovery"/> so both share one socket instead of binding the port twice.
/// </summary>
public sealed class DiscoveryBeacon : IAsyncDisposable
{
    private const int MaxPendingReplies = 8;

    private readonly DiscoveryOptions _options;
    private readonly LanDiscoveryChannel _channel;
    private readonly LanReplyLimiter _limiter = new();
    private readonly RecentNonces _announced = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private Task? _announceLoop;
    private DiscoveredDevice? _local;
    private volatile string _fault = "";
    private long _answered;
    private long _throttled;
    private long _observed;
    private int _pending;

    /// <param name="channel">
    /// Optional shared channel. The beacon owns the channel's lifecycle: it starts it and stops it.
    /// </param>
    public DiscoveryBeacon(DiscoveryOptions? options = null, LanDiscoveryChannel? channel = null)
    {
        _options = options ?? DiscoveryOptions.Default;
        _options.Validate();
        _channel = channel ?? new LanDiscoveryChannel(_options);
    }

    public bool IsRunning { get; private set; }

    /// <summary>What this device advertises; null while stopped.</summary>
    public DiscoveredDevice? LocalDevice => _local;

    /// <summary>The shared UDP channel; pass it to <see cref="DeviceDiscovery"/> to reuse the socket.</summary>
    public LanDiscoveryChannel Channel => _channel;

    /// <summary>Non-empty when the service could not start or stopped on its own.</summary>
    public string Fault => _fault.Length > 0 ? _fault : _channel.Fault;

    /// <summary>who-is requests answered since the last start.</summary>
    public long AnsweredSolicitations => Interlocked.Read(ref _answered);

    /// <summary>who-is requests dropped by the per-source rate limit or by the pending-reply cap.</summary>
    public long ThrottledSolicitations => Interlocked.Read(ref _throttled);

    /// <summary>Peer announcements observed while running (a peer's who-is, or an answer to ours).</summary>
    public long ObservedPeers => Interlocked.Read(ref _observed);

    /// <summary>Raised for every announcement from another device, before any rate limiting.</summary>
    public event Action<DiscoveredDevice, IPEndPoint>? PeerObserved;

    /// <summary>
    /// Enables discoverability. Returns false (with <see cref="Fault"/>) when this device has no
    /// usable Tailnet address or the LAN channel cannot bind; an unstarted service is reported, never
    /// silently pretended to be discoverable.
    /// </summary>
    public async Task<bool> StartAsync(DiscoveredDevice local, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (!DiscoveryAddresses.IsValidDeviceId(local.DeviceId))
        {
            _fault = "设备标识无效，无法开启可被发现。";
            return false;
        }
        if (!DiscoveryAddresses.IsValidName(local.Name))
        {
            _fault = "设备名无效，无法开启可被发现。";
            return false;
        }
        if (!DiscoveryAddresses.IsUsableCandidate(local.Address))
        {
            _fault = "本机没有可用的 Tailnet 地址，无法开启可被发现。";
            return false;
        }
        if (!DiscoveryAddresses.IsValidPort(local.Port))
        {
            _fault = "接收端口无效，无法开启可被发现。";
            return false;
        }

        await _gate.WaitAsync(token);
        try
        {
            if (IsRunning && _local == local) return true;
            if (IsRunning) await StopCoreAsync();
            var lifetime = new CancellationTokenSource();
            var started = false;
            try
            {
                _local = local;
                _fault = "";
                Interlocked.Exchange(ref _answered, 0);
                Interlocked.Exchange(ref _throttled, 0);
                Interlocked.Exchange(ref _observed, 0);
                _announced.Clear();
                _channel.Received += OnReceived;
                started = await _channel.StartAsync(token);
                if (!started)
                {
                    _channel.Received -= OnReceived;
                    _local = null;
                    _fault = _channel.Fault.Length > 0 ? _channel.Fault : "局域网发现通道无法启动。";
                    lifetime.Dispose();
                    return false;
                }
                IsRunning = true;
                _lifetime = lifetime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or SocketException or ObjectDisposedException)
            {
                _channel.Received -= OnReceived;
                _local = null;
                _fault = "无法开启可被发现：" + ex.Message;
                lifetime.Dispose();
                return false;
            }

            // One datagram so peers that already listen learn about us; then the loop is idle again.
            await AnnounceAsync(lifetime.Token);
            if (_options.AnnounceInterval is { } interval)
                _announceLoop = Task.Run(() => AnnounceLoopAsync(interval, lifetime.Token), CancellationToken.None);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Disables discoverability and closes the socket. Idempotent.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;
        var lifetime = _lifetime;
        var loop = _announceLoop;
        _lifetime = null;
        _announceLoop = null;
        _channel.Received -= OnReceived;
        // The token source is intentionally not disposed: an in-flight best-effort reply may still hold
        // its token, and it owns no timer or handle of its own.
        if (lifetime is not null) await lifetime.CancelAsync();
        await _channel.StopAsync();
        if (loop is not null)
        {
            try { await loop; } catch (Exception) { /* the loop only ends on cancellation */ }
        }
        _local = null;
        _announced.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        PeerObserved = null;
        _gate.Dispose();
    }

    private void OnReceived(LanDiscoveryProtocol.Announcement announcement)
    {
        var local = _local;
        if (local is null) return;
        // Our own multicast is looped back to this socket; it is not a peer observation.
        if (announcement.Device.DeviceId == local.DeviceId) return;
        PeerObserved?.Invoke(announcement.Device, announcement.Source);
        // An answer to a solicitation this service sent means the peer is present right now. An answer
        // to somebody else's solicitation still reaches M4 through PeerObserved, but it is not counted
        // as this service's own result.
        if (announcement.IsAnswer)
        {
            if (_announced.Contains(announcement.Nonce)) Interlocked.Increment(ref _observed);
            return;
        }
        Interlocked.Increment(ref _observed);
        if (!_limiter.TryAccept(announcement.Source.Address))
        {
            Interlocked.Increment(ref _throttled);
            return;
        }
        if (Interlocked.Increment(ref _pending) > MaxPendingReplies)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _throttled);
            return;
        }
        _ = ReplyAsync(announcement, local);
    }

    private async Task ReplyAsync(LanDiscoveryProtocol.Announcement announcement, DiscoveredDevice local)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var packet = LanDiscoveryProtocol.CreateHere(local, announcement.Nonce);
            await _channel.SendAsync(packet, announcement.Source, timeout.Token);
            Interlocked.Increment(ref _answered);
        }
        catch (Exception) { /* best effort: a lost answer is retried by the peer's next who-is */ }
        finally { Interlocked.Decrement(ref _pending); }
    }

    private async Task AnnounceAsync(CancellationToken token)
    {
        var local = _local;
        if (local is null) return;
        var nonce = LanDiscoveryProtocol.NewNonce();
        _announced.Remember(nonce);
        try { await _channel.SendAsync(LanDiscoveryProtocol.CreateWhoIs(local, nonce), null, token); }
        catch (Exception ex) when (ex is SocketException or InvalidOperationException or ObjectDisposedException)
        {
            _fault = "局域网发现广播失败：" + ex.Message;
        }
    }

    private async Task AnnounceLoopAsync(TimeSpan interval, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(token))
        {
            if (token.IsCancellationRequested) return;
            await AnnounceAsync(token);
        }
    }

    /// <summary>Diagnostic summary for M4 when discoverability is on but nothing was found.</summary>
    public string Describe() =>
        $"{(IsRunning ? "可被发现已开启" : "可被发现已关闭")}；已应答 {AnsweredSolicitations} 次查询" +
        $"，限流 {ThrottledSolicitations} 次" + (Fault.Length > 0 ? $"；{Fault}" : "");

    /// <summary>Bounded ring of the nonces this service announced, so only real answers are counted.</summary>
    private sealed class RecentNonces
    {
        private const int Capacity = 8;
        private readonly Queue<string> _order = new();
        private readonly HashSet<string> _set = new(StringComparer.Ordinal);
        private readonly object _gate = new();

        public void Remember(string nonce)
        {
            lock (_gate)
            {
                if (!_set.Add(nonce)) return;
                _order.Enqueue(nonce);
                while (_order.Count > Capacity) _set.Remove(_order.Dequeue());
            }
        }

        public bool Contains(string nonce)
        {
            lock (_gate) return _set.Contains(nonce);
        }

        public void Clear()
        {
            lock (_gate)
            {
                _order.Clear();
                _set.Clear();
            }
        }
    }
}
