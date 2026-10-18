using Avalonia.Input;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Tests;

public sealed class ShellKeyboardShortcutUxTests
{
    [Fact]
    public void Ctrl_k_focuses_the_command_palette()
    {
        Assert.True(ShortcutKeyAdapter.TryParse("Ctrl+K", out var key, out var modifiers));
        Assert.Equal(Key.K, key);
        Assert.Equal(KeyModifiers.Control, modifiers);
        var snapshot = new ShortcutCatalogSnapshot(new(0, []), ShortcutCatalog.ShellCommands, [], "windows");
        Assert.Equal("shell.command-palette.open",
            ShortcutCatalog.Resolve(snapshot, ShortcutKeyAdapter.Format(key, modifiers)!, "", "", false, false)?.Definition.Id);
    }

    [Fact]
    public void Ctrl_f_remains_available_to_tool_level_search()
    {
        var snapshot = new ShortcutCatalogSnapshot(new(0, []), ShortcutCatalog.ShellCommands, [], "windows");
        Assert.Null(ShortcutCatalog.Resolve(snapshot,
            ShortcutKeyAdapter.Format(Key.F, KeyModifiers.Control)!, "", "", false, false));
    }
}
