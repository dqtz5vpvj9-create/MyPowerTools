using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileTransfer.Surface;

/// <summary>
/// Display-only decode of a connection code that arrived from outside MPT (paste or <c>mpt://</c>
/// activation). An external app can hand this surface any string, so the page shows the object of
/// the pending import before the user confirms it: device name/number/address for a pairing code,
/// WebDAV address and account for a cloud code. Secrets are never rendered — the pairing token and
/// the cloud password stay hidden. Import still runs through the module command, which validates
/// the code again and remains the only component that stores it.
/// </summary>
internal static class ConnectionCodePreview
{
    private const string PairPrefix = "mpt://pair/";
    private const string CloudPrefix = "mpt://cloud/";
    private const int MaximumTextLength = 120;

    public static string? Describe(string? code)
    {
        var value = (code ?? "").Trim();
        if (value.Length == 0) return null;
        if (value.StartsWith(PairPrefix, StringComparison.Ordinal)) return DescribePair(value);
        if (value.StartsWith(CloudPrefix, StringComparison.Ordinal)) return DescribeCloud(value);
        return "无法识别的连接码：请确认已复制完整。";
    }

    private static string DescribePair(string code)
    {
        if (code.Length > 4096) return "设备连接码过长，无法识别。";
        if (Decode(code[PairPrefix.Length..]) is not JsonObject payload) return "设备连接码无法解析。";
        var name = Text(payload, "name");
        var deviceId = Text(payload, "deviceId");
        var address = Text(payload, "address");
        if (name is null || deviceId is null || address is null) return "设备连接码缺少名称、设备号或地址。";
        return $"将添加设备：{name} · 设备号 {deviceId} · 地址 {address}";
    }

    private static string DescribeCloud(string code)
    {
        if (code.Length > 16384) return "网盘连接码过长，无法识别。";
        if (Decode(code[CloudPrefix.Length..]) is not JsonObject payload) return "网盘连接码无法解析。";
        var url = Text(payload, "url");
        var username = Text(payload, "username");
        if (url is null || username is null) return "网盘连接码缺少地址或账号。";
        return $"将导入网盘：{url} · 账号 {username} · 密码已隐藏";
    }

    private static JsonNode? Decode(string base64)
    {
        try
        {
            var value = base64.Replace('-', '+').Replace('_', '/');
            value = value.PadRight((value.Length + 3) / 4 * 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Single-line, control-character-free field text; anything longer is truncated.</summary>
    private static string? Text(JsonObject payload, string key)
    {
        if (payload[key] is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
        var clean = new string(text.Where(character => !char.IsControl(character)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > MaximumTextLength ? clean[..MaximumTextLength] + "…" : clean;
    }
}
