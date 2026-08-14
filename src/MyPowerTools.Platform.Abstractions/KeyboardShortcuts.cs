using System.Globalization;

namespace MyPowerTools.Platform.Abstractions;

/// <summary>
/// A normalized keyboard shortcut that can be sent to the foreground application.
/// The parser intentionally accepts the same "Ctrl+Alt+Shift+Key" notation used by
/// global hotkeys, but sending does not require a modifier key.
/// </summary>
public sealed record KeyboardShortcutGesture(uint Modifiers, uint VirtualKey, string NormalizedGesture)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;

    private static readonly IReadOnlyDictionary<string, uint> NamedKeys =
        new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = 0x20,
            ["Tab"] = 0x09,
            ["Enter"] = 0x0D,
            ["Return"] = 0x0D,
            ["Escape"] = 0x1B,
            ["Esc"] = 0x1B,
            ["Backspace"] = 0x08,
            ["Delete"] = 0x2E,
            ["Del"] = 0x2E,
            ["Insert"] = 0x2D,
            ["Ins"] = 0x2D,
            ["Home"] = 0x24,
            ["End"] = 0x23,
            ["PageUp"] = 0x21,
            ["PgUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["PgDn"] = 0x22,
            ["Left"] = 0x25,
            ["Up"] = 0x26,
            ["Right"] = 0x27,
            ["Down"] = 0x28
        };

    public static bool TryParse(string gesture, out KeyboardShortcutGesture? parsed, out string error)
    {
        return TryParse(gesture, requireModifier: false, out parsed, out error);
    }

    public static bool TryParse(string gesture, bool requireModifier, out KeyboardShortcutGesture? parsed, out string error)
    {
        parsed = null;
        error = "";
        if (string.IsNullOrWhiteSpace(gesture))
        {
            error = "Keyboard shortcut is required.";
            return false;
        }

        var tokens = gesture
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .ToArray();

        if (tokens.Length == 0)
        {
            error = "Keyboard shortcut is required.";
            return false;
        }

        uint modifiers = 0;
        string? keyToken = null;
        foreach (var token in tokens)
        {
            var normalizedModifier = NormalizeModifier(token);
            if (normalizedModifier is not null)
            {
                modifiers |= normalizedModifier.Value;
                continue;
            }

            if (keyToken is not null)
            {
                error = $"Keyboard shortcut '{gesture}' contains more than one key.";
                return false;
            }

            keyToken = token;
        }

        if (requireModifier && modifiers == 0)
        {
            error = "Hotkey gesture must include at least one modifier.";
            return false;
        }

        if (keyToken is null)
        {
            error = "Keyboard shortcut must include a key.";
            return false;
        }

        if (!TryParseKey(keyToken, out var virtualKey, out var normalizedKey))
        {
            error = $"Keyboard shortcut key '{keyToken}' is unsupported.";
            return false;
        }

        parsed = new KeyboardShortcutGesture(modifiers, virtualKey, NormalizeGesture(modifiers, normalizedKey));
        return true;
    }

    private static uint? NormalizeModifier(string token)
    {
        return token.Trim() switch
        {
            var value when value.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Control", StringComparison.OrdinalIgnoreCase) => ModControl,
            var value when value.Equals("Alt", StringComparison.OrdinalIgnoreCase) => ModAlt,
            var value when value.Equals("Shift", StringComparison.OrdinalIgnoreCase) => ModShift,
            var value when value.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Meta", StringComparison.OrdinalIgnoreCase) => ModWin,
            _ => null
        };
    }

    private static bool TryParseKey(string token, out uint virtualKey, out string normalizedKey)
    {
        token = token.Trim();
        virtualKey = 0;
        normalizedKey = "";

        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                virtualKey = ch;
                normalizedKey = ch.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                normalizedKey = ch.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }

        if (token.Length is >= 2 and <= 3 &&
            token[0] is 'F' or 'f' &&
            int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + functionKey - 1);
            normalizedKey = $"F{functionKey}";
            return true;
        }

        if (NamedKeys.TryGetValue(token, out virtualKey))
        {
            normalizedKey = token.Equals("Esc", StringComparison.OrdinalIgnoreCase)
                ? "Escape"
                : token.Equals("Return", StringComparison.OrdinalIgnoreCase)
                    ? "Enter"
                    : token.Equals("Del", StringComparison.OrdinalIgnoreCase)
                        ? "Delete"
                        : token.Equals("Ins", StringComparison.OrdinalIgnoreCase)
                            ? "Insert"
                            : token.Equals("PgUp", StringComparison.OrdinalIgnoreCase)
                                ? "PageUp"
                                : token.Equals("PgDn", StringComparison.OrdinalIgnoreCase)
                                    ? "PageDown"
                                    : token;
            return true;
        }

        return false;
    }

    public static string NormalizeGesture(uint modifiers, string key)
    {
        var parts = new List<string>(5);
        if ((modifiers & ModControl) == ModControl)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & ModAlt) == ModAlt)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModShift) == ModShift)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModWin) == ModWin)
        {
            parts.Add("Win");
        }

        parts.Add(key);
        return string.Join("+", parts);
    }
}
