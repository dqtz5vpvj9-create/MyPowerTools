using System.Text.Json;

namespace MyPowerTools.AvaloniaSdk;

/// <summary>Accepts only the expected provider callback; URLs and tokens never become error text.</summary>
public static class CloudAuthorizationCallback
{
    public static string? BaiduRefreshToken(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "api.oplist.org" || !uri.IsDefaultPort || uri.AbsolutePath != "/" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length < 2) return null;
        try
        {
            using var data = JsonDocument.Parse(Convert.FromBase64String(Uri.UnescapeDataString(uri.Fragment[1..])));
            var payload = data.RootElement;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("driver_txt", out var driver) &&
                driver.ValueKind == JsonValueKind.String && driver.GetString() == "baiduyun_go" &&
                payload.TryGetProperty("refresh_token", out var token) && token.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(token.GetString())) return token.GetString();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or UriFormatException) { }
        return null;
    }
}

/// <summary>An in-memory login result; never render, persist or log its credential.</summary>
public sealed record MptCloudAuthorizationResult(string ProviderId, string CredentialKind, string Credential)
{
    public override string ToString() => $"Cloud authorization: {ProviderId}, credential hidden";
}
