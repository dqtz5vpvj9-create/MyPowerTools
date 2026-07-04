using System.Text.RegularExpressions;

namespace MyPowerTools.Runtime;

public static class MptLogRedactor
{
    private static readonly Regex SensitivePattern = new("(token|secret|password|cookie|authorization|apiKey|accessKey|refreshToken)=([^\\s;,&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Redact(string value) => SensitivePattern.Replace(value, "$1=****");
}
