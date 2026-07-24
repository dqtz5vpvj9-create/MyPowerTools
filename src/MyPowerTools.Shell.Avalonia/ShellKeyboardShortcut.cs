using Avalonia.Input;

namespace MyPowerTools.Shell.Avalonia;

public enum ShellKeyboardAction
{
    None,
    FocusCommandPalette,
    ClearCommandPalette,
    Refresh,
    Navigate
}

public sealed record ShellKeyboardShortcutResult(ShellKeyboardAction Action, string? TargetPage = null)
{
    public static ShellKeyboardShortcutResult None { get; } = new(ShellKeyboardAction.None);
}

public static class ShellKeyboardShortcut
{
    public static bool TryParseGesture(string gesture, out Key key, out KeyModifiers modifiers)
    {
        (key, modifiers) = gesture switch
        {
            "Ctrl+Shift+P" => (Key.P, KeyModifiers.Control | KeyModifiers.Shift),
            "Ctrl+R" => (Key.R, KeyModifiers.Control),
            "Ctrl+Alt+Space" => (Key.Space, KeyModifiers.Control | KeyModifiers.Alt),
            "F5" => (Key.F5, KeyModifiers.None),
            "Escape" => (Key.Escape, KeyModifiers.None),
            "Ctrl+1" => (Key.D1, KeyModifiers.Control),
            "Ctrl+2" => (Key.D2, KeyModifiers.Control),
            "Ctrl+3" => (Key.D3, KeyModifiers.Control),
            "Ctrl+4" => (Key.D4, KeyModifiers.Control),
            "Ctrl+5" => (Key.D5, KeyModifiers.Control),
            "Ctrl+6" => (Key.D6, KeyModifiers.Control),
            _ => (Key.None, KeyModifiers.None)
        };
        return key != Key.None;
    }

    public static ShellKeyboardShortcutResult Resolve(Key key, KeyModifiers modifiers)
    {
        if ((modifiers == (KeyModifiers.Control | KeyModifiers.Alt) && key == Key.Space) ||
            (modifiers == (KeyModifiers.Control | KeyModifiers.Shift) && key == Key.P))
        {
            return new ShellKeyboardShortcutResult(ShellKeyboardAction.FocusCommandPalette);
        }

        if (modifiers == KeyModifiers.None && key == Key.Escape)
        {
            return new ShellKeyboardShortcutResult(ShellKeyboardAction.ClearCommandPalette);
        }

        if ((modifiers == KeyModifiers.None && key == Key.F5) ||
            (HasOnly(modifiers, KeyModifiers.Control) && key == Key.R))
        {
            return new ShellKeyboardShortcutResult(ShellKeyboardAction.Refresh);
        }

        if (HasOnly(modifiers, KeyModifiers.Control))
        {
            var target = key switch
            {
                Key.D1 => "Home",
                Key.D2 => "Tools",
                Key.D3 => "Activity",
                Key.D4 => "Notifications",
                Key.D5 => "Settings",
                Key.D6 => "System",
                _ => null
            };

            if (target is not null)
            {
                return new ShellKeyboardShortcutResult(ShellKeyboardAction.Navigate, target);
            }
        }

        return ShellKeyboardShortcutResult.None;
    }

    private static bool HasOnly(KeyModifiers modifiers, KeyModifiers expected)
    {
        return modifiers == expected;
    }
}
