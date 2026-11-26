using System.Text;
using System.Text.Json.Nodes;

namespace MobileToolControl.Android;

/// <summary>
/// The parsed <c>mpt://control/&lt;base64url-json&gt;</c> connection code.
/// <see cref="ToString"/> does not print the token, so an accidental interpolation of this record in
/// a log line, event payload or error message cannot leak the credential.
/// </summary>
internal sealed record MobileToolConnectionCode(
    int Version,
    MobileToolEndpoint Endpoint,
    string GrantId,
    string DeviceName,
    string Token)
{
    /// <summary>Device name used when the code does not carry one; never blank in the UI.</summary>
    public string ResolvedDeviceName =>
        string.IsNullOrWhiteSpace(DeviceName) ? Endpoint.Display : DeviceName.Trim();

    public override string ToString() =>
        $"mpt://control version={Version} endpoint={Endpoint.Origin} grant={GrantId} device={ResolvedDeviceName} token={MobileToolControlOptions.RedactedToken}";
}

/// <summary>
/// Connection-code parsing for <c>REMOTE_CONTROL_CONTRACT.md</c>:
/// <c>mpt://control/&lt;base64url-json&gt;</c> with
/// <c>{"version":1,"endpoint":"http://100.64.0.2:49541","grantId":"...","deviceName":"工作电脑","token":"..."}</c>.
///
/// Parsing is deliberately strict and happens in the module, not in the page: the preview, the
/// confirmation and the stored record then all derive from one implementation, and the token never
/// has to travel through surface state as anything but the code string the user pasted.
/// </summary>
internal static class MobileToolConnectionCodeParser
{
    public static bool TryParse(
        string? code,
        IMobileToolEndpointPolicy policy,
        out MobileToolConnectionCode parsed,
        out string error)
    {
        parsed = null!;
        error = "";
        var value = code?.Trim() ?? "";
        if (value.Length == 0)
        {
            error = "没有可导入的连接码。";
            return false;
        }

        // A file-transfer/cloud pairing code is a different credential and must never be applied
        // here; the wording matches the desktop decoder so both ends say the same thing.
        if (value.StartsWith("mpt://pair/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("mpt://cloud/", StringComparison.OrdinalIgnoreCase))
        {
            error = "这是文件互传的连接码，不能用于电脑工具授权；请在电脑上生成工具访问连接码。";
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            error = "连接码不是有效的链接。";
            return false;
        }

        if (!string.Equals(uri.Scheme, MobileToolControlOptions.ConnectionCodeScheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Authority, MobileToolControlOptions.ConnectionCodeAuthority, StringComparison.OrdinalIgnoreCase))
        {
            error = "连接码必须以 mpt://control/ 开头。";
            return false;
        }

        var payload = uri.AbsolutePath.TrimStart('/');
        if (payload.Length == 0)
        {
            error = "连接码缺少内容。";
            return false;
        }

        string json;
        try
        {
            json = Encoding.UTF8.GetString(DecodeBase64Url(payload));
        }
        catch (FormatException)
        {
            error = "连接码内容不是有效的 base64url。";
            return false;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            error = "连接码内容不是有效的 JSON。";
            return false;
        }

        if (node is not JsonObject document)
        {
            error = "连接码内容不是 JSON 对象。";
            return false;
        }

        var version = ReadString(document, "version");
        if (!string.Equals(version, MobileToolControlOptions.ConnectionCodeVersion, StringComparison.Ordinal))
        {
            error = string.IsNullOrWhiteSpace(version)
                ? "连接码缺少版本号。"
                : $"不支持连接码版本 {version}，请升级电脑端 MPT。";
            return false;
        }

        var endpointText = ReadString(document, "endpoint");
        if (!MobileToolEndpointParser.TryParse(endpointText, policy, out var endpoint, out var endpointError))
        {
            error = endpointError;
            return false;
        }

        var grantId = ReadString(document, "grantId").Trim();
        if (grantId.Length == 0)
        {
            error = "连接码缺少授权 ID。";
            return false;
        }

        var token = ReadString(document, "token");
        if (token.Length == 0)
        {
            error = "连接码缺少授权凭据。";
            return false;
        }

        parsed = new MobileToolConnectionCode(
            int.Parse(MobileToolControlOptions.ConnectionCodeVersion, System.Globalization.CultureInfo.InvariantCulture),
            endpoint,
            grantId,
            ReadString(document, "deviceName"),
            token);
        return true;
    }

    /// <summary>base64url per RFC 4648 §5; padding and whitespace are tolerated.</summary>
    internal static byte[] DecodeBase64Url(string payload)
    {
        var normalized = payload.Replace('-', '+').Replace('_', '/');
        var builder = new StringBuilder(normalized.Length + 3);
        foreach (var character in normalized)
        {
            if (!char.IsWhiteSpace(character))
            {
                builder.Append(character);
            }
        }

        var text = builder.ToString();
        switch (text.Length % 4)
        {
            case 2:
                text += "==";
                break;
            case 3:
                text += "=";
                break;
        }

        return Convert.FromBase64String(text);
    }

    private static string ReadString(JsonObject document, string name)
    {
        if (!document.TryGetPropertyValue(name, out var node) || node is null)
        {
            return "";
        }

        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : node.ToJsonString().Trim('"');
    }
}
