using System.Net;
using System.Text.Json;

namespace FileTransfer.Core;

/// <summary>
/// One device's stable identity and its file-transfer credential. The address is only an optional
/// direct candidate: a device with no network interface today still has an identity and can be paired,
/// and the stable device id (never the address) is what later imports and probes are keyed on.
/// </summary>
public sealed record Pairing(string DeviceId, string Name, string Address, string Token,
    PublicInboxPairing? Inbox = null)
{
    /// <summary>The only supported code scheme. A shortened numeric code is deliberately not implemented.</summary>
    public const string Prefix = "mpt://pair/";

    public string Encode() => Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, DirectTransfer.Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Pairing Decode(string code)
    {
        if (code.Trim().StartsWith("mpt://assistant/", StringComparison.Ordinal))
            throw new ArgumentException("这是文件助手连接码，请用 file-transfer.assistant.link.preview 预览、file-transfer.assistant.link.import 导入。");
        if (!code.Trim().StartsWith(Prefix, StringComparison.Ordinal) || code.Length > 4096) throw new ArgumentException("请粘贴对方 MPT 中复制的设备连接码。");
        var b64 = code.Trim()[Prefix.Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
        Pairing? value;
        try { value = JsonSerializer.Deserialize<Pairing>(Convert.FromBase64String(b64), DirectTransfer.Json); }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // A body that is not even readable is an invalid code, not a runtime exception from the parser.
            throw new ArgumentException("设备连接码无效：内容无法识别。", ex);
        }
        if (value is null) throw new ArgumentException("设备连接码无效：内容为空。");
        // Structure before shape: a producer that omits a field must not reach a Length or parse call.
        if (string.IsNullOrWhiteSpace(value.DeviceId)) throw new ArgumentException("设备连接码无效：缺少设备标识。");
        if (string.IsNullOrWhiteSpace(value.Token)) throw new ArgumentException("设备连接码无效：缺少配对密钥。");
        TransferFiles.DeviceId(value.DeviceId!);
        if (!ValidToken(value.Token!)) throw new ArgumentException("设备连接码无效：配对密钥不正确。");
        var name = value.Name ?? "";
        if (name.Length > 100 || name.Any(char.IsControl)) throw new ArgumentException("设备连接码无效：设备名称不正确。");
        // The address is optional and, when present, still has to be a private direct candidate: a
        // public address is never accepted for the direct channel.
        var address = (value.Address ?? "").Trim();
        if (address.Length > 0 && (!IPAddress.TryParse(address, out var parsed) || !TransferFiles.IsTailAddress(parsed)))
            throw new ArgumentException("设备连接码无效：直连地址必须是 Tailscale 地址。");
        // The optional deposit inbox grants file delivery only; it is validated like any other input
        // and an old code without one stays valid.
        if (value.Inbox is { } inbox)
        {
            if (!PublicInboxIds.IsInboxId(inbox.InboxId) || !PublicInboxIds.IsKey(inbox.DepositKey))
                throw new ArgumentException("设备连接码无效：收件箱信息不正确。");
        }
        return value with { Name = name, Address = address, Token = value.Token! };
    }

    /// <summary>
    /// The token is the only credential a peer needs, so a code whose secret could never have been
    /// generated (too short, whitespace, control characters) is rejected before it is stored and
    /// silently fails a later transfer.
    /// </summary>
    private static bool ValidToken(string token) =>
        token.Length is >= 24 and <= 512 && token.All(c => c is > ' ' and < (char)127);
}
