using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// The real UDP multicast transport of <see cref="LanDiscoveryProtocol"/>: one bound socket, one
/// receive loop that is blocked on the socket (no polling, no timer), and explicit start/stop.
/// <para>
/// It is shared: a running <see cref="DiscoveryBeacon"/> owns one channel and
/// <see cref="DeviceDiscovery"/> can reuse the same instance so an active discovery and the passive
/// advertiser never fight over the same UDP port. Subscribers attach to <see cref="Received"/>;
/// a subscriber that throws cannot stop the receive loop.
/// </para>
/// </summary>
public sealed class LanDiscoveryChannel : IAsyncDisposable
{
    private readonly DiscoveryOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Socket? _socket;
    private CancellationTokenSource? _lifetime;
    private Task _receive = Task.CompletedTask;
    private long _dropped;
    private long _rejected;
    private volatile bool _listening;
    private volatile string _fault = "";
    private volatile string _lastDropReason = "";

    public LanDiscoveryChannel(DiscoveryOptions? options = null) => _options = options ?? DiscoveryOptions.Default;

    /// <summary>Local bind port; <see cref="DiscoveryOptions.LanBindPort"/> when set, else the rendezvous port.</summary>
    private int BindPort => _options.LanBindPort ?? _options.LanPort;

    /// <summary>Raised on the receive loop for every validated datagram, in arrival order.</summary>
    public event Action<LanDiscoveryProtocol.Announcement>? Received;

    public bool IsListening => _listening;

    /// <summary>The interface used for sending; the group is joined on every usable interface.</summary>
    public IPAddress? Interface { get; private set; }

    /// <summary>Non-empty when the channel could not start or the receive loop stopped on its own.</summary>
    public string Fault => _fault;

    /// <summary>Validated datagrams that were dropped because they failed protocol validation.</summary>
    public long DroppedDatagrams => Interlocked.Read(ref _dropped);

    /// <summary>Datagrams dropped because their source is not a local network address.</summary>
    public long RejectedSources => Interlocked.Read(ref _rejected);

    /// <summary>Reason of the most recent drop, for diagnostics only.</summary>
    public string LastDropReason => _lastDropReason;

    /// <summary>
    /// Binds <see cref="DiscoveryOptions.LanPort"/>, joins the group and starts the receive loop.
    /// Returns false (with <see cref="Fault"/> set) when this machine has no usable interface or the
    /// socket cannot be bound, because "no LAN discovery" must be reported, not silently pretended.
    /// </summary>
    /// <exception cref="InvalidOperationException">The channel is already listening.</exception>
    public async Task<bool> StartAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (_listening) throw new InvalidOperationException("局域网发现通道已经启动；请先停止再重新启动。");
            var joins = new List<IPAddress>(UsableInterfaces());
            if (joins.Count == 0)
            {
                _fault = $"没有可用于局域网发现的网络接口（UDP {BindPort}）。";
                return false;
            }
            // The interface used for sending is always a member too; otherwise a solicitation sent out
            // of it would never be looped back or answered on this socket.
            var send = _options.LanInterface ?? joins[0];
            if (!joins.Contains(send)) joins.Insert(0, send);
            Socket socket;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.Bind(new IPEndPoint(IPAddress.Any, BindPort));
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                socket.MulticastLoopback = true;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or PlatformNotSupportedException)
            {
                _fault = $"无法绑定局域网发现端口（UDP {BindPort}）：{ex.Message}";
                return false;
            }
            var joined = 0;
            foreach (var address in joins)
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                        new MulticastOption(LanDiscoveryProtocol.Group, address));
                    joined++;
                }
                catch (SocketException) { /* one interface without multicast must not disable the rest */ }
            }
            if (joined == 0)
            {
                socket.Dispose();
                _fault = $"无法在任何网络接口上加入局域网发现组（UDP {BindPort}）。";
                return false;
            }
            try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, send.GetAddressBytes()); }
            catch (SocketException) { /* sending falls back to the default route interface */ }
            _socket = socket;
            Interface = send;
            _fault = "";
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            _listening = true;
            _receive = Task.Run(() => ReceiveLoopAsync(socket, _lifetime.Token), CancellationToken.None);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stops the receive loop and closes the socket. Idempotent; safe to call when stopped.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_listening) return;
            _listening = false;
            var socket = _socket;
            var lifetime = _lifetime;
            _socket = null;
            _lifetime = null;
            if (lifetime is not null) await lifetime.CancelAsync();
            socket?.Dispose();
            try { await _receive; } catch (Exception) { /* the loop reports faults through Fault */ }
            if (lifetime is not null) lifetime.Dispose();
            _receive = Task.CompletedTask;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Sends one datagram: to the multicast group when <paramref name="unicastTo"/> is null, otherwise
    /// to that exact endpoint (how a <c>here</c> answer reaches the solicitor without broadcasting).
    /// </summary>
    public async Task<int> SendAsync(LanDiscoveryProtocol.Packet packet, IPEndPoint? unicastTo, CancellationToken token)
    {
        var socket = _socket ?? throw new InvalidOperationException("局域网发现通道尚未启动。");
        var bytes = LanDiscoveryProtocol.Encode(packet);
        if (bytes.Length > LanDiscoveryProtocol.MaxDatagramBytes)
            throw new InvalidOperationException($"发现报文超过 {LanDiscoveryProtocol.MaxDatagramBytes} 字节。");
        var target = unicastTo ?? new IPEndPoint(LanDiscoveryProtocol.Group, _options.LanPort);
        return await socket.SendToAsync(bytes, SocketFlags.None, target, token);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Received = null;
        _gate.Dispose();
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[2048];
        var any = new IPEndPoint(IPAddress.Any, 0);
        var consecutiveErrors = 0;
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                if (token.IsCancellationRequested) return;
                // A UDP socket can report an ICMP-driven error per datagram on some platforms; only a
                // sustained run of errors means the socket is really unusable, and it must not spin.
                if (++consecutiveErrors >= 20) { _fault = "局域网发现接收失败：" + ex.Message; return; }
                continue;
            }
            consecutiveErrors = 0;
            var source = ((IPEndPoint)result.RemoteEndPoint).Address;
            // A datagram that was routed in from outside the local network (or the tailnet) is never
            // a discovery peer, no matter how well formed its payload is.
            if (!DiscoveryAddresses.IsLocalNetworkSource(source))
            {
                Interlocked.Increment(ref _rejected);
                _lastDropReason = "来源不是局域网地址。";
                continue;
            }
            if (!LanDiscoveryProtocol.TryDecode(buffer.AsSpan(0, result.ReceivedBytes), source, out var packet, out var error))
            {
                Interlocked.Increment(ref _dropped);
                _lastDropReason = error;
                continue;
            }
            var announcement = new LanDiscoveryProtocol.Announcement(
                new DiscoveredDevice(packet.DeviceId, packet.Name, packet.Address, packet.Port, packet.Platform),
                packet.Nonce,
                packet.Kind == LanDiscoveryProtocol.Here,
                (IPEndPoint)result.RemoteEndPoint);
            Raise(announcement);
        }
    }

    private void Raise(LanDiscoveryProtocol.Announcement announcement)
    {
        var handlers = Received;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<LanDiscoveryProtocol.Announcement>>())
        {
            try { handler(announcement); }
            catch (Exception) { /* one broken subscriber must not stop discovery for the others */ }
        }
    }

    /// <summary>
    /// Interfaces whose group membership is requested: every up, multicast-capable interface with an
    /// IPv4 unicast address, private addresses first. Loopback is the fallback so a single-machine
    /// setup (and the tests) still work.
    /// </summary>
    internal static IReadOnlyList<IPAddress> UsableInterfaces()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                bool multicast;
                try { multicast = nic.SupportsMulticast; } catch (NotSupportedException) { multicast = false; }
                if (!multicast) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.None)) continue;
                    // The tailnet is a point-to-point tunnel: multicast there is meaningless, and the
                    // Tailnet peers are discovered through the Tailscale status source instead.
                    if (TransferFiles.IsTailAddress(address)) continue;
                    if (!found.Contains(address)) found.Add(address);
                }
            }
        }
        catch (NetworkInformationException) { /* fall through to the loopback fallback */ }
        if (found.Count == 0) return [IPAddress.Loopback];
        return found.OrderBy(address => IsPrivate(address) ? 0 : 1).ToArray();
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    /// <summary>Diagnostic summary used by M4 when discovery found nothing.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(_listening ? $"正在监听 UDP {BindPort}" : "未监听");
        if (Interface is not null) text.Append($"，接口 {Interface}");
        if (DroppedDatagrams > 0) text.Append($"，忽略无效报文 {DroppedDatagrams} 条");
        if (RejectedSources > 0) text.Append($"，拒绝非局域网来源 {RejectedSources} 条");
        if (Fault.Length > 0) text.Append($"，{Fault}");
        return text.ToString();
    }
}
