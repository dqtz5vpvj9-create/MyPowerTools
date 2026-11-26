using System.Net;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// Bounds for one discovery window. Everything is a hard upper bound: a window never runs longer
/// than <see cref="Window"/>, never opens more than <see cref="MaxConcurrency"/> sockets at once and
/// never probes more than <see cref="MaxCandidates"/> addresses.
/// </summary>
public sealed record DiscoveryOptions
{
    public static DiscoveryOptions Default { get; } = new();

    /// <summary>Total budget of one <see cref="DeviceDiscovery.DiscoverAsync"/> call.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>One budget for TCP connect plus the v3 hello frame and its answer.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Budget of the TCP connect alone, inside <see cref="ProbeTimeout"/>.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Simultaneous hello probes.</summary>
    public int MaxConcurrency { get; init; } = 8;

    /// <summary>Upper bound of distinct addresses probed in one window; the rest are reported unchecked.</summary>
    public int MaxCandidates { get; init; } = 64;

    /// <summary>
    /// File port used when a source cannot know better (Tailscale peers). The production value is
    /// <see cref="TransferFiles.Port"/>, which is the port the running <c>DirectReceiver</c> binds.
    /// </summary>
    public int Port { get; init; } = TransferFiles.Port;

    /// <summary>
    /// Rendezvous UDP port of the MPT LAN discovery protocol: solicitations and announcements are sent
    /// to the multicast group on this port, which is where every other MPT install listens.
    /// </summary>
    public int LanPort { get; init; } = LanDiscoveryProtocol.Port;

    /// <summary>
    /// Local UDP port to bind. Defaults to <see cref="LanPort"/>. It only differs in tests, which bind
    /// an exclusive port so a unicast <c>here</c> answer cannot be delivered to another channel that
    /// happens to share the rendezvous port on the same machine.
    /// </summary>
    public int? LanBindPort { get; init; }

    /// <summary>
    /// Test and multi-NIC seam: the interface used for the LAN multicast group and for sending.
    /// Null means "pick a usable LAN interface automatically"; <see cref="IPAddress.Loopback"/> is a
    /// valid choice and is what the tests use.
    /// </summary>
    public IPAddress? LanInterface { get; init; }

    /// <summary>Use the MPT LAN discovery protocol (multicast who-is / here).</summary>
    public bool LanEnabled { get; init; } = true;

    /// <summary>Use the Tailscale CLI / LocalAPI peer list.</summary>
    public bool TailscaleEnabled { get; init; } = true;

    /// <summary>
    /// How long the LAN source waits for <c>here</c> answers after sending a who-is. The LAN is a
    /// sub-second medium; this is a bounded wait, not a loop.
    /// </summary>
    public TimeSpan LanReplyWindow { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>How many who-is datagrams the LAN source sends inside <see cref="LanReplyWindow"/>.</summary>
    public int LanSolicitationAttempts { get; init; } = 2;

    /// <summary>
    /// This device's own public discovery information, used as the body of a LAN who-is. Null (or an
    /// address that is not a Tailnet address) disables only the LAN source, with a diagnostic.
    /// </summary>
    public DiscoveredDevice? LocalDevice { get; init; }

    /// <summary>
    /// When set, a running <see cref="DiscoveryBeacon"/> re-announces on this interval so peers that
    /// are already listening learn about this device without soliciting. Off by default: a resident
    /// service must not poll while idle.
    /// </summary>
    public TimeSpan? AnnounceInterval { get; init; }

    /// <summary>This device's id; candidates carrying it are the local device and are never probed.</summary>
    public string SelfDeviceId => LocalDevice?.DeviceId ?? "";

    internal void Validate()
    {
        if (Window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Window));
        if (ProbeTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ProbeTimeout));
        if (ConnectTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        if (MaxConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
        if (MaxCandidates < 1) throw new ArgumentOutOfRangeException(nameof(MaxCandidates));
        if (Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (LanPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(LanPort));
        if (LanBindPort is { } bind && bind is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(LanBindPort));
        if (LanReplyWindow < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(LanReplyWindow));
        if (LanSolicitationAttempts < 1) throw new ArgumentOutOfRangeException(nameof(LanSolicitationAttempts));
        if (AnnounceInterval is { } interval && interval < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(AnnounceInterval), "间隔至少 1 秒。");
    }
}
