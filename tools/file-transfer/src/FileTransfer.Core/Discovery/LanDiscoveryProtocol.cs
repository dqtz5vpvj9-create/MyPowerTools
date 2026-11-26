using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// MPT LAN discovery protocol v1 — a deliberately tiny UDP protocol whose only job is to tell a peer
/// on the same link which Tailnet address this device can be reached at. Files never travel here:
/// every payload below is public information (name, MPT port, Tailnet address, platform, device id)
/// and the later transfer still runs over the tailnet and still needs the pairing secret.
/// <para>
/// Wire format: one UTF-8 JSON object per datagram, at most <see cref="MaxDatagramBytes"/> bytes.
/// <c>{"mpt":"mpt-discovery","v":1,"kind":"who-is"|"here","protocol":3,"deviceId":…,"name":…,
/// "address":…,"port":…,"platform":…,"nonce":…}</c>
/// where <c>nonce</c> is 16 hex characters (8 random bytes). A <c>who-is</c> is sent to the multicast
/// group; a <c>here</c> is always a unicast answer to the datagram's source address and repeats the
/// solicitation's nonce, so an answer is only ever accepted for a solicitation this process sent.
/// </para>
/// <para>
/// Binding: UDP, IPv4, <see cref="IPAddress.Any"/>:<see cref="Port"/> with <c>SO_REUSEADDR</c>, joined
/// to <see cref="Group"/> on every usable interface with TTL 1 and multicast loopback enabled.
/// Cancellation: the receive loop is blocked on the socket and stops on its token; a request is
/// cancelled with the caller's token. Re-entrancy: start/stop is guarded by a semaphore, start twice
/// throws, stop twice is a no-op, start after stop binds a fresh socket.
/// </para>
/// </summary>
public static class LanDiscoveryProtocol
{
    public const string Magic = "mpt-discovery";
    public const int Version = 1;
    public const string WhoIs = "who-is";
    public const string Here = "here";

    /// <summary>LAN discovery UDP port. One above <see cref="TransferFiles.Port"/>, and not the file port.</summary>
    public const int Port = 47166;

    /// <summary>Administratively scoped, link-local multicast group (TTL 1, never routed).</summary>
    public const string GroupAddress = "239.255.77.80";

    public const int MaxDatagramBytes = 512;
    public const int NonceLength = 16;

    public static IPAddress Group { get; } = IPAddress.Parse(GroupAddress);

    public sealed record Packet(
        [property: JsonPropertyName("mpt")] string Mpt,
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("protocol")] int Protocol,
        [property: JsonPropertyName("deviceId")] string DeviceId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("address")] string Address,
        [property: JsonPropertyName("port")] int Port,
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("nonce")] string Nonce);

    /// <summary>One received, validated datagram turned into a device.</summary>
    public sealed record Announcement(DiscoveredDevice Device, string Nonce, bool IsAnswer, IPEndPoint Source);

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(NonceLength / 2)).ToLowerInvariant();

    public static bool IsValidNonce(string? nonce) =>
        nonce is { Length: NonceLength } && nonce.All(char.IsAsciiHexDigit);

    public static Packet CreateWhoIs(DiscoveredDevice local, string nonce) => Create(local, WhoIs, nonce);

    public static Packet CreateHere(DiscoveredDevice local, string nonce) => Create(local, Here, nonce);

    private static Packet Create(DiscoveredDevice local, string kind, string nonce) => new(
        Magic, Version, kind, HelloProtocol.Version,
        local.DeviceId, local.Name, local.Address, local.Port, local.Platform, nonce);

    public static byte[] Encode(Packet packet) => JsonSerializer.SerializeToUtf8Bytes(packet, DirectTransfer.Json);

    /// <summary>
    /// Parses and validates one datagram. Everything a hostile sender controls is checked here:
    /// the source address must be a local network source, the payload must fit, the magic/version/
    /// kind/nonce must match, and the advertised device must be a complete, Tailnet-addressed
    /// identity. Failures return false with a reason and must be dropped, never partially trusted.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> datagram, IPAddress source, out Packet packet, out string error)
    {
        packet = null!;
        error = "";
        if (!DiscoveryAddresses.IsLocalNetworkSource(source))
        {
            error = "来源不是局域网地址。";
            return false;
        }
        if (datagram.Length == 0 || datagram.Length > MaxDatagramBytes)
        {
            error = "报文长度不在允许范围内。";
            return false;
        }
        Packet? decoded;
        try
        {
            decoded = JsonSerializer.Deserialize<Packet>(datagram, DirectTransfer.Json);
        }
        catch (JsonException)
        {
            error = "报文不是有效的 JSON。";
            return false;
        }
        if (decoded is null)
        {
            error = "报文为空。";
            return false;
        }
        if (decoded.Mpt != Magic)
        {
            error = "缺少 MPT 发现标识。";
            return false;
        }
        if (decoded.V != Version)
        {
            error = $"不支持的发现协议版本 {decoded.V}。";
            return false;
        }
        if (decoded.Kind is not (WhoIs or Here))
        {
            error = "未知的报文类型。";
            return false;
        }
        if (decoded.Protocol is < 1 or > 1000)
        {
            error = "文件协议版本无效。";
            return false;
        }
        if (!IsValidNonce(decoded.Nonce))
        {
            error = "随机数无效。";
            return false;
        }
        if (!TryGetDevice(decoded, out _, out var deviceError))
        {
            error = deviceError;
            return false;
        }
        packet = decoded;
        return true;
    }

    /// <summary>Validates the device fields of a decoded packet.</summary>
    public static bool TryGetDevice(Packet packet, out DiscoveredDevice device, out string error)
    {
        device = null!;
        error = "";
        if (!DiscoveryAddresses.IsValidDeviceId(packet.DeviceId)) { error = "设备标识无效。"; return false; }
        if (!DiscoveryAddresses.IsValidName(packet.Name)) { error = "设备名无效。"; return false; }
        if (!DiscoveryAddresses.IsUsableCandidate(packet.Address)) { error = "上报的地址不是 Tailnet 地址。"; return false; }
        if (!DiscoveryAddresses.IsValidPort(packet.Port)) { error = "上报的端口无效。"; return false; }
        if (!DiscoveryAddresses.IsValidPlatform(packet.Platform)) { error = "平台信息无效。"; return false; }
        device = new DiscoveredDevice(packet.DeviceId, packet.Name, packet.Address, packet.Port, packet.Platform);
        return true;
    }
}
