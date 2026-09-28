namespace FileTransfer.Core.Discovery;

/// <summary>
/// A candidate produced by one source, before any network identity check. <see cref="Rank"/> orders
/// the probing queue (lower first) so a bounded window spends its budget on the most likely devices.
/// </summary>
public sealed record DiscoveryCandidate(DiscoveredDevice Device, bool Paired, DiscoverySource Source)
{
    /// <summary>Lower is probed earlier. 0 known/paired, 1 LAN, 2 online Tailscale peer, 3 offline peer.</summary>
    public int Rank { get; init; }

    /// <summary>Optional source detail kept for diagnostics (for example "Tailscale 节点在线").</summary>
    public string Hint { get; init; } = "";
}

/// <summary>Everything a source needs for one collection round.</summary>
public sealed record DiscoveryRequest(IReadOnlyList<DiscoveredDevice> Known, DiscoveryOptions Options)
{
    /// <summary>Known device ids, used to promote an anonymous candidate that turns out to be paired.</summary>
    public bool IsKnownDeviceId(string deviceId) =>
        deviceId.Length > 0 && Known.Any(device => device.DeviceId.Length > 0 && device.DeviceId == deviceId);
}

/// <summary>
/// What one source produced. <see cref="Supported"/> is false when the source cannot run at all on
/// this platform or in this environment (no CLI, no multicast interface, missing local address);
/// that is always accompanied by a diagnostic and never reported as a successful empty discovery.
/// </summary>
public sealed record CandidateSourceResult(
    IReadOnlyList<DiscoveryCandidate> Candidates,
    IReadOnlyList<string> Diagnostics,
    bool Supported)
{
    public static CandidateSourceResult Unsupported(string diagnostic) => new([], [diagnostic], false);
}

/// <summary>
/// An injectable peer source. M4 keeps the defaults; tests replace them so no test depends on the
/// machine's real network.
/// </summary>
public interface IDiscoveryCandidateSource
{
    string Name { get; }

    /// <summary>
    /// Collects candidates inside the caller's window. Implementations must respect
    /// <paramref name="token"/> and must not block past it.
    /// </summary>
    Task<CandidateSourceResult> CollectAsync(DiscoveryRequest request, CancellationToken token);
}
