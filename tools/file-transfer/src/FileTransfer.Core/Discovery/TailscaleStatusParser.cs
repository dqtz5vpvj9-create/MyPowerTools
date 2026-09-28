using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileTransfer.Core.Discovery;

/// <summary>One peer from <c>tailscale status --json</c> / <c>GET /localapi/v0/status</c>.</summary>
public sealed record TailscalePeer(
    string StableId,
    string HostName,
    string DnsName,
    string Os,
    IReadOnlyList<string> Addresses,
    bool Online,
    bool Active);

/// <summary>The parsed tailnet state. <see cref="Peers"/> never contains this device or a shared-in node.</summary>
public sealed record TailscaleStatus(string BackendState, IReadOnlyList<TailscalePeer> Peers)
{
    public bool Running => string.Equals(BackendState, "Running", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses the Tailscale status document. Field names follow the daemon's ipnstate.Status JSON
/// (<c>BackendState</c>, <c>Self</c>, <c>Peer</c>, <c>HostName</c>, <c>DNSName</c>, <c>OS</c>,
/// <c>TailscaleIPs</c>, <c>Online</c>, <c>Active</c>, <c>ShareeNode</c>) and are matched
/// case-insensitively, so both the CLI's indented output and the LocalAPI body parse the same way.
/// </summary>
public static class TailscaleStatusParser
{
    public static TailscaleStatus Parse(string json)
    {
        var document = JsonSerializer.Deserialize<TailscaleDocument>(json, DirectTransfer.Json)
            ?? throw new InvalidDataException("Tailscale 状态为空。");
        var peers = new List<TailscalePeer>();
        foreach (var (key, peer) in document.Peer ?? [])
        {
            if (peer is null) continue;
            // Nodes shared in from another user are hidden by tailscale status itself; MPT follows
            // that rule so a stranger's machine never shows up as one of "my devices".
            if (peer.ShareeNode) continue;
            var addresses = (peer.TailscaleIPs ?? []).Where(DiscoveryAddresses.IsUsableCandidate).ToArray();
            if (addresses.Length == 0) continue;
            peers.Add(new TailscalePeer(
                peer.ID ?? key,
                HostName(peer),
                peer.DNSName ?? "",
                peer.OS ?? "",
                addresses,
                peer.Online,
                peer.Active));
        }
        return new TailscaleStatus(document.BackendState ?? "", peers);
    }

    /// <summary>HostName is the machine name; DNSName ("host.tailnet.ts.net.") is the fallback label.</summary>
    private static string HostName(TailscalePeerDto peer)
    {
        if (!string.IsNullOrWhiteSpace(peer.HostName)) return peer.HostName.Trim();
        var dns = peer.DNSName ?? "";
        var label = dns.TrimEnd('.').Split('.')[0];
        return label.Length > 0 ? label : "Tailscale 设备";
    }

    private sealed record TailscaleDocument(string? BackendState, TailscalePeerDto? Self, Dictionary<string, TailscalePeerDto?>? Peer);

    private sealed record TailscalePeerDto(
        [property: JsonPropertyName("ID")] string? ID,
        string? HostName,
        string? DNSName,
        [property: JsonPropertyName("OS")] string? OS,
        List<string>? TailscaleIPs,
        bool Online,
        bool Active,
        bool ShareeNode);
}
