using System.Text.RegularExpressions;

namespace MyPowerTools.Abstractions;

public static class MptLogRedactor
{
    // Credential words with their common hyphenated/underscore spellings: api-key, access_key, peer-token.
    private const string CredentialWords =
        "(?:confirmation[-_]?token|refresh[-_]?token|peer[-_]?token|token|secret|password|passwd|cookie|authorization|api[-_]?key|access[-_]?key|secret[-_]?key)";
    // The key may carry a prefix (adminPassword, relayPassword, X-Auth-Token) and is followed by = or :.
    private static readonly Regex SensitivePattern = new(
        $"([A-Za-z0-9_-]*{CredentialWords}\\b\"?)(\\s*[=:]\\s*)(?:bearer\\s+)?([^\\s;,&\"'\\}}\\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Any JSON key ending in a credential word, so adminPassword / relayPassword / peerToken are covered.
    private static readonly Regex JsonSecretPattern = new(
        $"(\"[A-Za-z0-9_-]*{CredentialWords}\"\\s*:\\s*\")((?:\\\\.|[^\"\\\\])*)(\")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PairingLinkPattern = new("mpt://(?:pair|cloud)/[A-Za-z0-9_-]+", RegexOptions.Compiled);

    public static string Redact(string value)
    {
        var redacted = JsonSecretPattern.Replace(value, "$1****$3");
        return PairingLinkPattern.Replace(SensitivePattern.Replace(redacted, "$1$2****"), "mpt://connection/****");
    }
}
