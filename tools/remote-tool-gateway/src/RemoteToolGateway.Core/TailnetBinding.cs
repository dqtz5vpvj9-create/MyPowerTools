using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace RemoteToolGateway.Core;

/// <summary>
/// Tailnet boundary for the control listener. The gateway may only bind an address that a
/// Tailscale interface actually owns, and it only answers peers whose source address is inside
/// the Tailscale ranges. A wildcard, a public address, an ordinary LAN address and (outside an
/// explicitly injected loopback transport in tests) loopback are all refused.
/// </summary>
public static class TailnetBinding
{
    /// <summary>Tailscale assigns IPv4 addresses from the CGNAT range 100.64.0.0/10.</summary>
    public static bool IsTailnet(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 100 && (bytes[1] & 0xC0) == 64;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            // fd7a:115c:a1e0::/48 is the Tailscale IPv6 prefix.
            return bytes[0] == 0xFD && bytes[1] == 0x7A && bytes[2] == 0x11
                && bytes[3] == 0x5C && bytes[4] == 0xA1 && bytes[5] == 0xE0;
        }

        return false;
    }

    public static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(address);

    /// <summary>
    /// Peer gate. <paramref name="allowLoopbackPeers"/> exists only so the test suite can inject a
    /// loopback transport; no production setting, preference or module argument reaches it.
    /// </summary>
    public static bool IsPeerAllowed(IPAddress remoteAddress, bool allowLoopbackPeers)
    {
        if (IsTailnet(remoteAddress)) return true;
        return allowLoopbackPeers && IsLoopback(remoteAddress);
    }

    /// <summary>
    /// Accepts only a literal Tailscale address. Host names, wildcards, LAN and public addresses
    /// are refused before a socket is created.
    /// </summary>
    public static bool TryParseTailnet(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (value is "0.0.0.0" or "::" or "*" or "+" or "any") return false;
        if (!IPAddress.TryParse(value, out var parsed)) return false;
        if (!IsTailnet(parsed)) return false;
        address = parsed;
        return true;
    }

    /// <summary>Every Tailscale address on this machine, IPv4 first, without a network probe.</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses()
    {
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var unicast in network.GetIPProperties().UnicastAddresses)
                {
                    if (IsTailnet(unicast.Address)) addresses.Add(unicast.Address);
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
        {
            return [];
        }

        return addresses
            .Distinct()
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();
    }

    public static string FormatEndpoint(IPAddress address, int port)
    {
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return $"http://{host}:{port}";
    }
}
