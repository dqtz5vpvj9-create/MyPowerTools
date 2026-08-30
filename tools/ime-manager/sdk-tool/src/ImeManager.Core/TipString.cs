using System.Globalization;
using System.Text.RegularExpressions;

namespace ImeManager.MyPowerTools;

public readonly record struct ParsedTipString(
    string Canonical,
    ushort LanguageId,
    InputMethodKind Kind,
    Guid ProcessorClsid,
    Guid ProfileGuid,
    uint KeyboardLayoutId)
{
    private static readonly Regex Pattern = new(
        @"^(?:0x)?(?<lang>[0-9a-f]{4}):(?:(?:0x)?(?<klid>[0-9a-f]{8})|(?<clsid>\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\})(?<profile>\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}))$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const int MaximumLength = 260;
    public const int MaximumEnabledCount = 64;

    public static bool TryParse(string? raw, out ParsedTipString parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaximumLength)
        {
            return false;
        }

        var match = Pattern.Match(raw.Trim());
        if (!match.Success)
        {
            return false;
        }

        var languageId = ushort.Parse(match.Groups["lang"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (match.Groups["klid"].Success)
        {
            var keyboardLayoutId = uint.Parse(
                match.Groups["klid"].Value,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            parsed = new ParsedTipString(
                CanonicalKeyboard(languageId, keyboardLayoutId),
                languageId,
                InputMethodKind.KeyboardLayout,
                Guid.Empty,
                Guid.Empty,
                keyboardLayoutId);
            return true;
        }

        var clsid = Guid.Parse(match.Groups["clsid"].Value);
        var profile = Guid.Parse(match.Groups["profile"].Value);
        parsed = new ParsedTipString(
            CanonicalTextService(languageId, clsid, profile),
            languageId,
            InputMethodKind.TextService,
            clsid,
            profile,
            0);
        return true;
    }

    public static string CanonicalKeyboard(ushort languageId, uint keyboardLayoutId) =>
        $"{languageId:X4}:{keyboardLayoutId:X8}";

    public static string CanonicalTextService(ushort languageId, Guid processorClsid, Guid profileGuid) =>
        $"{languageId:X4}:{processorClsid.ToString("B").ToUpperInvariant()}{profileGuid.ToString("B").ToUpperInvariant()}";

    public static string RequireCanonical(string raw)
    {
        if (!TryParse(raw, out var parsed))
        {
            throw new ArgumentException($"不是有效的输入法标识：{raw}", nameof(raw));
        }

        return parsed.Canonical;
    }

    public string ToAssemblyItemValue()
    {
        if (Kind == InputMethodKind.KeyboardLayout)
        {
            var layout = KeyboardLayoutGuid(KeyboardLayoutId);
            return $"{Guid.Empty.ToString("B").ToUpperInvariant()}{layout.ToString("B").ToUpperInvariant()}";
        }

        return $"{ProcessorClsid.ToString("B").ToUpperInvariant()}{ProfileGuid.ToString("B").ToUpperInvariant()}";
    }

    public static bool TryParseAssemblyItem(ushort languageId, string? raw, out ParsedTipString parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 76)
        {
            return false;
        }

        if (!Guid.TryParseExact(raw[..38], "B", out var first) ||
            !Guid.TryParseExact(raw[38..], "B", out var second))
        {
            return false;
        }

        if (first == Guid.Empty)
        {
            var layoutId = ReadKeyboardLayoutId(second);
            parsed = new ParsedTipString(
                CanonicalKeyboard(languageId, layoutId),
                languageId,
                InputMethodKind.KeyboardLayout,
                Guid.Empty,
                Guid.Empty,
                layoutId);
            return true;
        }

        parsed = new ParsedTipString(
            CanonicalTextService(languageId, first, second),
            languageId,
            InputMethodKind.TextService,
            first,
            second,
            0);
        return true;
    }

    public static Guid KeyboardLayoutGuid(uint keyboardLayoutId) =>
        new(unchecked((int)keyboardLayoutId), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    public static uint ReadKeyboardLayoutId(Guid guid)
    {
        var bytes = guid.ToByteArray();
        return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
    }
}
