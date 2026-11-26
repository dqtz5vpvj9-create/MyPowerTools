using System.Net;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// Address policy for discovery. Discovery talks only to private direct addresses — Tailscale
/// (<c>100.64.0.0/10</c>, <c>fd7a:115c:a1e0::/48</c>), private LAN (RFC1918, IPv6 ULA, link-local),
/// and loopback — and the LAN protocol only accepts datagrams from a private local/tailnet source.
/// A public candidate is refused before any socket is opened, so a malicious announcement cannot
/// make MPT connect outwards.
/// </summary>
internal static class DiscoveryAddresses
{
    /// <summary>Private direct address (Tailscale, private LAN, or loopback); public addresses are rejected.</summary>
    public static bool IsPrivateDirect(IPAddress address) =>
        TransferFiles.IsPrivateDirectAddress(address);

    public static bool TryParse(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
        if (!IPAddress.TryParse(value.Trim(), out var parsed)) return false;
        address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        return true;
    }

    /// <summary>A candidate address is usable when it parses and is a private direct address (LAN, Tailnet, or loopback).</summary>
    public static bool IsUsableCandidate(string? value) =>
        TryParse(value, out var address) && TransferFiles.IsPrivateDirectAddress(address);

    /// <summary>
    /// Canonical form used as the dedupe key, so "100.64.0.1" and "100.64.0.01" (or two spellings of
    /// one IPv6 address) collapse into one candidate.
    /// </summary>
    public static string Normalize(string? value) => TryParse(value, out var address) ? address.ToString() : (value ?? "").Trim();

    /// <summary>
    /// True for a source address that can plausibly be on the local network or tailnet:
    /// loopback, RFC1918, link-local, CGNAT/Tailnet, IPv6 ULA or IPv6 link-local. A datagram from
    /// anything else is a spoofed or routed-in packet and is dropped.
    /// </summary>
    public static bool IsLocalNetworkSource(IPAddress address) =>
        TransferFiles.IsPrivateDirectAddress(address);

    /// <summary>Device ids and names are echoed on the wire; validate before they enter a result.</summary>
    public static bool IsValidDeviceId(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId)) return false;
        try { TransferFiles.DeviceId(deviceId); return true; }
        catch (ArgumentException) { return false; }
    }

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 100 && !name.Any(char.IsControl);

    public static bool IsValidPlatform(string? platform) =>
        !string.IsNullOrWhiteSpace(platform) && platform.Length <= 32 &&
        platform.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;
}
