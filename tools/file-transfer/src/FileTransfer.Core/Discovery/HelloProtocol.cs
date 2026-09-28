namespace FileTransfer.Core.Discovery;

/// <summary>
/// The protocol v3 hello frame: the same int32 big-endian length + UTF-8 JSON framing as
/// <see cref="DirectTransfer"/>, with the file-less identity exchange from
/// docs/mobile-ux/FILE_ASSISTANT_CONTRACT.md.
/// <para>
/// Request: <c>{"version":3,"kind":"hello"}</c> — no token, no secret, nothing is written on the peer.
/// Reply:   <c>{"ok":true,"deviceId":…,"name":…,"address":…,"port":…,"platform":…}</c>.
/// </para>
/// <para>
/// M4 implements the answering side inside the existing <see cref="DirectReceiver"/>: the receive
/// loop already reads a <see cref="DirectTransfer.Offer"/>, so it only needs to branch on
/// <c>offer.Version == HelloProtocol.Version</c> (<see cref="IsHello"/>) and answer with
/// <see cref="WriteReplyAsync"/>. Discovery is not authorization: the hello carries no pairing token
/// and only ever reveals the same public fields a LAN advertisement carries. The file transfer still
/// requires the receiver's pairing secret.
/// </para>
/// </summary>
public static class HelloProtocol
{
    /// <summary>Frame version of the identity-only hello. Distinct from file v1 and probe v2.</summary>
    public const int Version = 3;

    /// <summary>The only request kind of <see cref="Version"/>.</summary>
    public const string Kind = "hello";

    public sealed record Request(int Version, string Kind);

    public sealed record Reply(
        bool Ok,
        string? DeviceId = null,
        string? Name = null,
        string? Address = null,
        int Port = 0,
        string? Platform = null,
        string? Message = null);

    public static Request CreateRequest() => new(Version, Kind);

    /// <summary>The receiver-side branch: a v3 frame is a hello, never a file offer or a v2 probe.</summary>
    public static bool IsHello(DirectTransfer.Offer offer) => offer.Version == Version;

    /// <summary>True when a frame that parsed as an offer is a well-formed hello request.</summary>
    public static bool IsValidRequest(Request? request) =>
        request is { Version: Version, Kind: Kind };

    /// <summary>Writes <c>{"version":3,"kind":"hello"}</c> using the shared transfer framing.</summary>
    public static Task WriteRequestAsync(Stream stream, CancellationToken token) =>
        DirectTransfer.WriteJsonAsync(stream, CreateRequest(), token);

    public static Task<Request> ReadRequestAsync(Stream stream, CancellationToken token) =>
        DirectTransfer.ReadJsonAsync<Request>(stream, token);

    /// <summary>Writes the identity answer using the shared transfer framing.</summary>
    public static Task WriteReplyAsync(Stream stream, Reply reply, CancellationToken token) =>
        DirectTransfer.WriteJsonAsync(stream, reply, token);

    public static Task<Reply> ReadReplyAsync(Stream stream, CancellationToken token) =>
        DirectTransfer.ReadJsonAsync<Reply>(stream, token);

    /// <summary>
    /// Returns "" for a complete v3 identity frame, otherwise the reason it cannot be trusted.
    /// A refusal (<c>ok:false</c>) is handled by the caller as <see cref="IdentityProbeStatus.Rejected"/>;
    /// this checks the positive answer's required fields.
    /// </summary>
    public static string ValidateReply(Reply? reply)
    {
        if (reply is null) return "对方应答为空。";
        if (!reply.Ok) return string.IsNullOrWhiteSpace(reply.Message) ? "对方拒绝了身份查询。" : reply.Message!;
        if (!DiscoveryAddresses.IsValidDeviceId(reply.DeviceId)) return "对方应答缺少有效的设备标识。";
        if (!DiscoveryAddresses.IsValidName(reply.Name)) return "对方应答缺少有效的设备名。";
        if (!DiscoveryAddresses.IsUsableCandidate(reply.Address)) return "对方上报的地址不是 Tailnet 地址。";
        if (!DiscoveryAddresses.IsValidPort(reply.Port)) return "对方上报的端口无效。";
        if (!DiscoveryAddresses.IsValidPlatform(reply.Platform)) return "对方应答缺少平台信息。";
        return "";
    }

    /// <summary>
    /// The device behind a validated reply. The address and port that answered are kept, not the
    /// self-reported ones, so M4 can only ever send back to an endpoint this process reached.
    /// </summary>
    public static DiscoveredDevice ToDevice(Reply reply, string address, int port) =>
        new(reply.DeviceId!, reply.Name!, address, port, reply.Platform!);
}
