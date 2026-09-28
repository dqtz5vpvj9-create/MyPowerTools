using System.Net;
using System.Net.Sockets;

namespace MobileToolControl.Android;

/// <summary>
/// A validated gateway address. Only a literal Tailnet address can produce one, so a host name,
/// a public address or a LAN address can never reach the HTTP client.
/// </summary>
internal sealed record MobileToolEndpoint(string Scheme, IPAddress Address, int Port, string Display)
{
    /// <summary>The origin used for every request; there is no path, query or user info.</summary>
    public string Origin => $"{Scheme}://{Display}";
}

/// <summary>
/// Address policy for the remote tool gateway. Production code always uses
/// <see cref="TailnetEndpointPolicy"/>; the interface exists so tests can drive the real HTTP client
/// against a loopback double. It is internal and is never read from settings, preferences or a
/// command argument, so there is no production switch that relaxes the boundary
/// (<c>REMOTE_CONTROL_CONTRACT.md</c>: 测试可注入 loopback transport，不可提供绕过生产边界的设置开关).
/// </summary>
internal interface IMobileToolEndpointPolicy
{
    /// <summary>True when this policy can validate (and the client may connect to) the address.</summary>
    bool Allows(IPAddress address);

    /// <summary>Human readable rule used in error messages, for example <c>100.64.0.0/10</c>.</summary>
    string Rule { get; }
}

/// <summary>Production policy: Tailscale CGNAT IPv4 and the Tailscale IPv6 ULA prefix only.</summary>
internal sealed class TailnetEndpointPolicy : IMobileToolEndpointPolicy
{
    public static TailnetEndpointPolicy Instance { get; } = new();

    public string Rule => $"{MobileToolControlOptions.TailnetIpv4Prefix} / {MobileToolControlOptions.TailnetIpv6Prefix}";

    public bool Allows(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsTailnetIpv4(address),
            AddressFamily.InterNetworkV6 => IsTailnetIpv6(address),
            _ => false
        };
    }

    /// <summary>100.64.0.0/10, the Tailscale IPv4 range.</summary>
    internal static bool IsTailnetIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 100 && (bytes[1] & 0xC0) == 64;
    }

    /// <summary>fd7a:115c:a1e0::/48, the Tailscale IPv6 range.</summary>
    internal static bool IsTailnetIpv6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 16 &&
               bytes[0] == 0xFD && bytes[1] == 0x7A && bytes[2] == 0x11 && bytes[3] == 0x5C &&
               bytes[4] == 0xA1 && bytes[5] == 0xE0;
    }
}

/// <summary>
/// Address parsing for the connection code and the stored device records. This is the single place
/// that decides whether an endpoint may be contacted, so the import flow and every later request
/// enforce exactly the same rule.
/// </summary>
internal static class MobileToolEndpointParser
{
    public static bool TryParse(
        string? text,
        IMobileToolEndpointPolicy policy,
        out MobileToolEndpoint endpoint,
        out string error)
    {
        endpoint = null!;
        error = "";
        var value = text?.Trim() ?? "";
        if (value.Length == 0)
        {
            error = "连接码里没有电脑地址。";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = "电脑地址不是有效的 URL。";
            return false;
        }

        if (!string.Equals(uri.Scheme, MobileToolControlOptions.AllowedEndpointScheme, StringComparison.OrdinalIgnoreCase))
        {
            error = $"只接受 {MobileToolControlOptions.AllowedEndpointScheme}:// 地址；当前是 {uri.Scheme}://。";
            return false;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath.Length > 1 && uri.AbsolutePath != "/"))
        {
            error = "电脑地址不能包含账号、路径、查询或片段。";
            return false;
        }

        // A literal IP only: Uri.HostNameType rejects host names, so no DNS name can be resolved or
        // connected to. A DNS name is what would let a Tailnet address silently point elsewhere.
        if (uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            error = "电脑地址必须是 Tailnet 的 IP 地址，不能使用主机名。";
            return false;
        }

        if (!IPAddress.TryParse(uri.Host, out var address))
        {
            error = "电脑地址不是有效的 IP 地址。";
            return false;
        }

        if (!policy.Allows(address))
        {
            error = $"只允许 {policy.Rule} 范围内的 Tailnet 地址，{address} 不在其中。";
            return false;
        }

        if (uri.Port is <= 0 or > 65535)
        {
            error = "电脑地址必须带有有效的端口。";
            return false;
        }

        // Uri.Port is -1 when the authority carries no explicit port. The connection code always
        // carries one, and a missing port would silently fall back to 80.
        var hasExplicitPort = uri.Authority.Contains(':', StringComparison.Ordinal);
        if (!hasExplicitPort)
        {
            error = "电脑地址必须带有明确的端口，例如 http://100.64.0.2:49541。";
            return false;
        }

        var display = address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]:{uri.Port}"
            : $"{address}:{uri.Port}";
        endpoint = new MobileToolEndpoint(
            MobileToolControlOptions.AllowedEndpointScheme,
            address,
            uri.Port,
            display);
        return true;
    }
}
