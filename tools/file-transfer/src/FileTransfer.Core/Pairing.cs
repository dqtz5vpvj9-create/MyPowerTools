using System.Net;
using System.Text.Json;

namespace FileTransfer.Core;

public sealed record Pairing(string DeviceId, string Name, string Address, string Token)
{
    public string Encode() => "mpt://pair/" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, DirectTransfer.Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Pairing Decode(string code)
    {
        const string prefix = "mpt://pair/";
        if (!code.Trim().StartsWith(prefix, StringComparison.Ordinal) || code.Length > 4096) throw new ArgumentException("请粘贴对方 MPT 中复制的设备连接码。");
        var b64 = code.Trim()[prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
        var value = JsonSerializer.Deserialize<Pairing>(Convert.FromBase64String(b64), DirectTransfer.Json) ?? throw new ArgumentException("设备连接码无效。");
        TransferFiles.DeviceId(value.DeviceId);
        if (!IPAddress.TryParse(value.Address, out var address) || !TransferFiles.IsTailAddress(address) || value.Token.Length < 24 ||
            value.Name.Length > 100 || value.Name.Any(char.IsControl))
            throw new ArgumentException("设备连接码无效。");
        return value;
    }
}
