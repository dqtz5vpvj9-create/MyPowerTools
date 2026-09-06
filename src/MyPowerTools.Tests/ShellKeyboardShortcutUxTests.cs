using Avalonia.Input;
using MyPowerTools.Shell.Avalonia;

namespace MyPowerTools.Tests;

public sealed class ShellKeyboardShortcutUxTests
{
    [Fact]
    public void Ctrl_k_focuses_the_command_palette()
    {
        Assert.True(ShellKeyboardShortcut.TryParseGesture("Ctrl+K", out var key, out var modifiers));
        Assert.Equal(Key.K, key);
        Assert.Equal(KeyModifiers.Control, modifiers);
        Assert.Equal(
            ShellKeyboardAction.FocusCommandPalette,
            ShellKeyboardShortcut.Resolve(key, modifiers).Action);
    }

    [Fact]
    public void Ctrl_f_remains_available_to_tool_level_search()
    {
        Assert.Equal(
            ShellKeyboardAction.None,
            ShellKeyboardShortcut.Resolve(Key.F, KeyModifiers.Control).Action);
    }
}
