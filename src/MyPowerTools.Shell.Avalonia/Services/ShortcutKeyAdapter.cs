using Avalonia.Input;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Shell.Avalonia.Services;

public static class ShortcutKeyAdapter
{
    public static string? Format(Key key, KeyModifiers modifiers)
    {
        if (key is Key.None or Key.ImeProcessed or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return null;
        var name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1])) name = name[1..];
        var flags = ((modifiers & KeyModifiers.Control) != 0 ? KeyboardShortcutGesture.ModControl : 0u)
            | ((modifiers & KeyModifiers.Alt) != 0 ? KeyboardShortcutGesture.ModAlt : 0u)
            | ((modifiers & KeyModifiers.Shift) != 0 ? KeyboardShortcutGesture.ModShift : 0u)
            | ((modifiers & KeyModifiers.Meta) != 0 ? KeyboardShortcutGesture.ModWin : 0u);
        return KeyboardShortcutGesture.TryParse(KeyboardShortcutGesture.NormalizeGesture(flags, name), out var parsed, out _)
            ? parsed!.NormalizedGesture : null;
    }

    public static bool TryParse(string gesture, out Key key, out KeyModifiers modifiers)
    {
        key = Key.None; modifiers = KeyModifiers.None;
        if (!KeyboardShortcutGesture.TryParse(gesture, out var parsed, out _)) return false;
        var name = parsed!.NormalizedGesture.Split('+')[^1];
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) name = "D" + name;
        if (!Enum.TryParse(name, true, out key)) return false;
        if ((parsed.Modifiers & KeyboardShortcutGesture.ModControl) != 0) modifiers |= KeyModifiers.Control;
        if ((parsed.Modifiers & KeyboardShortcutGesture.ModAlt) != 0) modifiers |= KeyModifiers.Alt;
        if ((parsed.Modifiers & KeyboardShortcutGesture.ModShift) != 0) modifiers |= KeyModifiers.Shift;
        if ((parsed.Modifiers & KeyboardShortcutGesture.ModWin) != 0) modifiers |= KeyModifiers.Meta;
        return true;
    }
}
