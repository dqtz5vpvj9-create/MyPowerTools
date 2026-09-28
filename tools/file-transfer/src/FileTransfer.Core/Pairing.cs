using System.Net;
using System.Text.Json;

namespace FileTransfer.Core;

public sealed record Pairing(string DeviceId, string Name, string Address, string Token)
{
    /// <summary>The only supported code scheme. A shortened numeric code is deliberately not implemented.</summary>
    public const string Prefix = "mpt://pair/";

    public string Encode() => Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, DirectTransfer.Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Pairing Decode(string code)
    {
        if (!code.Trim().StartsWith(Prefix, StringComparison.Ordinal) || code.Length > 4096) throw new ArgumentException("请粘贴对方 MPT 中复制的设备连接码。");
        var b64 = code.Trim()[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
        var value = JsonSerializer.Deserialize<Pairing>(Convert.FromBase64String(b64), DirectTransfer.Json) ?? throw new ArgumentException("设备连接码无效。");
        TransferFiles.DeviceId(value.DeviceId);
        if (!IPAddress.TryParse(value.Address, out var address) || !TransferFiles.IsTailAddress(address) || !ValidToken(value.Token) ||
            value.Name.Length > 100 || value.Name.Any(char.IsControl))
            throw new ArgumentException("设备连接码无效。");
        return value;
    }

    /// <summary>
    /// The token is the only credential a peer needs, so a code whose secret could never have been
    /// generated (too short, whitespace, control characters) is rejected before it is stored and
    /// silently fails a later transfer.
    /// </summary>
    private static bool ValidToken(string token) =>
        token.Length is >= 24 and <= 512 && token.All(c => c is > ' ' and < (char)127);
}
