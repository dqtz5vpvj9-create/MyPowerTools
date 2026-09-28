using System.Text.Json;
using FileTransfer.Core.Assistant;

namespace FileTransfer.Core;

/// <summary>One item in a relay inbox, as published by its manifest.</summary>
public sealed record CloudFile(int Version, string Id, string Name, long Size, string Sender, DateTimeOffset CreatedAt);

public sealed record CloudConnection(string Url, string Username, string Password)
{
    public string Encode()
    {
        Validate();
        return "mpt://cloud/" + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, DirectTransfer.Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
    public static CloudConnection Decode(string code)
    {
        const string prefix = "mpt://cloud/";
        if (!code.Trim().StartsWith(prefix, StringComparison.Ordinal) || code.Length > 16384) throw new ArgumentException("请粘贴网盘连接码。");
        var b64 = code.Trim()[prefix.Length..].Replace('-', '+').Replace('_', '/');
        CloudConnection? connection;
        try
        {
            connection = JsonSerializer.Deserialize<CloudConnection>(Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '=')), DirectTransfer.Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("网盘连接码无效：内容无法识别。", ex);
        }
        if (connection is null) throw new ArgumentException("网盘连接码无效：内容为空。");
        if (string.IsNullOrWhiteSpace(connection.Url)) throw new ArgumentException("网盘连接码无效：缺少地址。");
        if (string.IsNullOrWhiteSpace(connection.Username)) throw new ArgumentException("网盘连接码无效：缺少账号。");
        if (string.IsNullOrEmpty(connection.Password)) throw new ArgumentException("网盘连接码无效：缺少密码。");
        connection.Validate();
        return connection;
    }
    private void Validate()
    {
        OpenListClient.ValidateUrl(new Uri(Url));
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password)) throw new ArgumentException("请先连接网盘。");
    }
}
