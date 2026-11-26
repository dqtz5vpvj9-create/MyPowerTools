using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;

namespace RemoteToolGateway.Core;

/// <summary>
/// The desktop side of the <c>mpt://control/&lt;base64url-json&gt;</c> connection code. The token
/// travels inside the code once, at pairing time; it is never written to preferences, logs,
/// audits, history or exported diagnostics.
/// </summary>
public sealed record ControlConnection(
    int Version,
    string Endpoint,
    string GrantId,
    string DeviceName,
    string Token);

public static class ControlConnectionCode
{
    public const string Scheme = "mpt://control/";
    public const int CurrentVersion = 1;
    public const int MinimumTokenLength = 32;
    public const int MaximumCodeLength = 4096;

    public static string Encode(ControlConnection connection)
    {
        var json = new JsonObject
        {
            ["version"] = connection.Version,
            ["endpoint"] = connection.Endpoint,
            ["grantId"] = connection.GrantId,
            ["deviceName"] = connection.DeviceName,
            ["token"] = connection.Token
        };
        return Scheme + Base64Url.Encode(Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    /// <summary>
    /// Decodes a control code. Anything that is not a version-1 control code — a file-transfer
    /// pairing code, a cloud code, a redirecting endpoint, a wildcard or non-Tailnet host — is
    /// rejected with a reason instead of being partially applied.
    /// </summary>
    public static bool TryDecode(string? code, out ControlConnection connection, out string error)
    {
        connection = new ControlConnection(0, "", "", "", "");
        error = "";
        var text = code?.Trim() ?? "";
        if (text.Length == 0) { error = "连接码为空。"; return false; }
        if (text.Length > MaximumCodeLength) { error = "连接码过长。"; return false; }
        if (text.StartsWith("mpt://pair/", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("mpt://cloud/", StringComparison.OrdinalIgnoreCase))
        {
            error = "这是文件互传的连接码，不能用于电脑工具授权；请在电脑上生成工具访问连接码。";
            return false;
        }

        if (!text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            error = "连接码格式不正确：应以 mpt://control/ 开头。";
            return false;
        }

        JsonObject? root;
        try
        {
            var payload = Base64Url.Decode(text[Scheme.Length..]);
            root = JsonNode.Parse(Encoding.UTF8.GetString(payload)) as JsonObject;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            error = "连接码内容无法解析。";
            return false;
        }

        if (root is null) { error = "连接码内容无法解析。"; return false; }
        var version = ReadInt(root, "version");
        if (version != CurrentVersion) { error = $"不支持的连接码版本：{version}。"; return false; }
        var endpoint = ReadString(root, "endpoint");
        if (!TryParseEndpoint(endpoint, out var host, out var port, out var endpointError))
        {
            error = endpointError;
            return false;
        }

        var grantId = ReadString(root, "grantId");
        if (!IsSafeId(grantId)) { error = "连接码缺少有效的授权编号。"; return false; }
        var token = ReadString(root, "token");
        if (token.Length < MinimumTokenLength) { error = "连接码缺少有效的访问凭据。"; return false; }
        var deviceName = ControlText.Bound(ReadString(root, "deviceName"), 120);
        if (deviceName.Length == 0) deviceName = "电脑";

        connection = new ControlConnection(version, TailnetBinding.FormatEndpoint(host, port), grantId, deviceName, token);
        return true;
    }

    /// <summary>A safe preview for the phone import sheet: never contains the token.</summary>
    public static string? Describe(string? code) =>
        TryDecode(code, out var connection, out _)
            ? $"{connection.DeviceName} · {connection.Endpoint}"
            : null;

    public static bool TryParseEndpoint(string endpoint, out IPAddress host, out int port, out string error)
    {
        host = IPAddress.None;
        port = 0;
        error = "";
        if (string.IsNullOrWhiteSpace(endpoint)) { error = "连接码缺少电脑地址。"; return false; }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            error = "连接码中的电脑地址无效。";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            error = "连接码必须使用 http:// 的 Tailscale 地址；网关不跟随重定向。";
            return false;
        }

        if (uri.Port is <= 0 or > 65535 || uri.IsDefaultPort && uri.Port <= 0)
        {
            error = "连接码中的端口无效。";
            return false;
        }

        // A literal IP only: a host name could resolve anywhere, including outside the tailnet.
        if (!IPAddress.TryParse(uri.Host, out var parsed))
        {
            error = "连接码中的电脑地址必须是 Tailscale IP。";
            return false;
        }

        if (!TailnetBinding.IsTailnet(parsed))
        {
            error = "连接码中的电脑地址不是 Tailscale 地址。";
            return false;
        }

        host = parsed;
        port = uri.Port;
        return true;
    }

    private static bool IsSafeId(string value) =>
        value.Length is > 0 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string ReadString(JsonObject root, string key)
    {
        try { return root[key]?.GetValue<string>() ?? ""; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
    }

    private static int ReadInt(JsonObject root, string key)
    {
        try { return root[key]?.GetValue<int>() ?? 0; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return 0; }
    }
}

/// <summary>Unpadded base64url, the only encoding accepted inside a connection code.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            0 => padded,
            2 => padded + "==",
            3 => padded + "=",
            _ => throw new FormatException("Invalid base64url payload length.")
        };
        return Convert.FromBase64String(padded);
    }
}
