using System.Text.RegularExpressions;

namespace MyPowerTools.Abstractions;

public static class MptLogRedactor
{
    private static readonly Regex SensitivePattern = new(
        "(token|secret|password|cookie|authorization|apiKey|accessKey|refreshToken)=([^\\s;,&\"'\\}\\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JsonConfirmationTokenPattern = new(
        "(\"confirmationToken\"\\s*:\\s*\")((?:\\\\.|[^\"\\\\])*)(\")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Redact(string value)
    {
        var redacted = SensitivePattern.Replace(value, "$1=****");
        return JsonConfirmationTokenPattern.Replace(redacted, "$1****$3");
    }
}
