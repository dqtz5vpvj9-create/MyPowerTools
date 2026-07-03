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
    public static ShellKeyboardShortcutResult Resolve(Key key, KeyModifiers modifiers)
    {
        if (HasOnly(modifiers, KeyModifiers.Control) && key is Key.K or Key.F)
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
                Key.D1 => "Dashboard",
                Key.D2 => "Modules",
                Key.D3 => "Settings",
                Key.D4 => "Logs",
                Key.D5 => "Notifications",
                Key.D6 => "Packages",
                Key.D7 => "Diagnostics",
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
