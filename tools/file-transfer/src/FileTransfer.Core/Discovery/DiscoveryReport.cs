using System.Net;

namespace FileTransfer.Core.Discovery;

/// <summary>Where a candidate came from. Used for probing priority and for M4 diagnostics.</summary>
public enum DiscoverySource
{
    /// <summary>A device the user already paired or stored; probed first and never silently dropped.</summary>
    Known = 0,
    /// <summary>The MPT LAN discovery protocol (multicast who-is / here), which only advertises Tailnet addresses.</summary>
    Lan = 1,
    /// <summary>The Tailscale status API or CLI (tailnet peer list).</summary>
    Tailscale = 2,
}

/// <summary>How one bounded <see cref="DeviceDiscovery.DiscoverAsync"/> call ended.</summary>
public enum DiscoveryOutcome
{
    /// <summary>Every supported source answered and every candidate was checked inside the window.</summary>
    Completed = 0,
    /// <summary>The bounded window ran out first; the report holds the partial result of the probes that finished.</summary>
    WindowExpired = 1,
    /// <summary>No source could run on this platform (for example Android without the LAN bridge) and there was nothing to check.</summary>
    Unsupported = 2,
}

/// <summary>
/// One discovery result. <see cref="Available"/> is true only after the device itself answered the
/// protocol v3 hello frame on <see cref="Device"/>'s address and port; a stored pairing, a Tailscale
/// peer entry or a LAN advertisement never makes a device available on its own.
/// </summary>
public sealed record DiscoveryDeviceResult(
    DiscoveredDevice Device,
    bool Paired,
    bool Available,
    DiscoverySource Source,
    string Message);

/// <summary>The bounded result of one discovery window.</summary>
public sealed record DiscoveryReport(
    IReadOnlyList<DiscoveryDeviceResult> Devices,
    DiscoveryOutcome Outcome,
    IReadOnlyList<string> Diagnostics,
    TimeSpan Elapsed)
{
    public bool HasDevices => Devices.Count > 0;
    public int AvailableCount => Devices.Count(device => device.Available);

    /// <summary>M4 maps this onto the <c>discoveryState</c> field of <c>file-transfer.assistant.devices</c>.</summary>
    public string State => Outcome switch
    {
        DiscoveryOutcome.Completed => "completed",
        DiscoveryOutcome.WindowExpired => "partial",
        _ => "unsupported",
    };

    /// <summary>The first diagnostic, which is what the Surface shows when the list is empty.</summary>
    public string Message => Diagnostics.Count == 0 ? "" : Diagnostics[0];
}
