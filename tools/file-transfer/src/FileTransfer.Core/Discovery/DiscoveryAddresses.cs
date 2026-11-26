using System.Net;
using System.Net.Sockets;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// Address policy for discovery. It is deliberately narrow: discovery only ever talks to Tailnet
/// addresses (plus loopback for a same-machine receiver and for tests), and the LAN protocol only
/// accepts datagrams that arrived from a local network source. A public candidate is refused before
/// any socket is opened, so a malicious announcement cannot make MPT connect outwards.
/// </summary>
internal static class DiscoveryAddresses
{
    /// <summary>Tailnet (100.64.0.0/10, fd7a:115c:a1e0::/48) or loopback; nothing else is reachable.</summary>
    public static bool IsTailnetOrLoopback(IPAddress address) =>
        TransferFiles.IsTailAddress(address) || IPAddress.IsLoopback(address);

    public static bool TryParse(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
        if (!IPAddress.TryParse(value.Trim(), out var parsed)) return false;
        address = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        return true;
    }

    /// <summary>A candidate address is usable when it parses and is a Tailnet address (or loopback).</summary>
    public static bool IsUsableCandidate(string? value) =>
        TryParse(value, out var address) && IsTailnetOrLoopback(address);

    /// <summary>
    /// Canonical form used as the dedupe key, so "100.64.0.1" and "100.64.0.01" (or two spellings of
    /// one IPv6 address) collapse into one candidate.
    /// </summary>
    public static string Normalize(string? value) => TryParse(value, out var address) ? address.ToString() : (value ?? "").Trim();

    /// <summary>
    /// True for a source address that can plausibly be on the local network (or on the tailnet):
    /// loopback, RFC1918, link-local, CGNAT/Tailnet, IPv6 ULA or IPv6 link-local. A datagram from
    /// anything else is a spoofed or routed-in packet and is dropped.
    /// </summary>
    public static bool IsLocalNetworkSource(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || b[0] == 127
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || TransferFiles.IsTailAddress(address);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address)) return true;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;                       // fc00::/7 unique local
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return true;       // fe80::/10 link local
            return TransferFiles.IsTailAddress(address);
        }
        return false;
    }

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
