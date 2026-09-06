using Avalonia.Headless.XUnit;
using Avalonia.Input;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Services;

namespace ShortcutCenter.Tests;

public sealed class DispatchTests
{
    [Fact]
    public void Tool_priority_and_disjoint_tool_scopes_are_explicit()
    {
        var one = new ShortcutDefinition("one.refresh", "one.refresh", "Refresh", "One", "tool", [new("F5")], "one");
        var two = one with { Id = "two.refresh", CommandId = "two.refresh", ToolId = "two" };
        var state = TestCatalog.Create(extra: [one, two]);
        Assert.False(ShortcutCatalog.Overlaps(one, two));
        Assert.Equal("one.refresh", ShortcutCatalog.Resolve(state, "F5", "one", "", false, false)!.Definition.Id);
        Assert.Equal("two.refresh", ShortcutCatalog.Resolve(state, "F5", "two", "", false, false)!.Definition.Id);
        Assert.Equal("shell.refresh", ShortcutCatalog.Resolve(state, "F5", "", "", false, false)!.Definition.Id);
    }

    [Fact]
    public void Text_input_and_modal_overlays_do_not_trigger_context_free_actions()
    {
        var state = TestCatalog.Create();
        Assert.Null(ShortcutCatalog.Resolve(state, "Alt+Left", "", "", true, false));
        Assert.Null(ShortcutCatalog.Resolve(state, "Ctrl+K", "", "", true, true));
        Assert.NotNull(ShortcutCatalog.Resolve(state, "Ctrl+K", "", "", true, false));
        Assert.Null(ShortcutCatalog.Resolve(state, "Escape", "", "", false, false));
    }

    [Fact]
    public void User_overrides_and_contexts_resolve_deterministically()
    {
        var a = new ShortcutDefinition("a", "a", "A", "Tool", "tool", [new("Ctrl+S")], "tool", "settings");
        var b = a with { Id = "b", CommandId = "b" };
        var state = TestCatalog.Create([new("b", [new("Ctrl+S")])], [a, b]);
        Assert.Equal("b", ShortcutCatalog.Resolve(state, "Ctrl+S", "tool", "settings", false, false)!.Definition.Id);
        Assert.Null(ShortcutCatalog.Resolve(state, "Ctrl+S", "tool", "overview", false, false));
    }

    [Fact]
    public void Platform_bindings_are_not_mechanically_mixed()
    {
        var state = TestCatalog.Create(platform: "macos");
        Assert.NotNull(ShortcutCatalog.Resolve(state, "Win+K", "", "", true, false));
        Assert.Null(ShortcutCatalog.Resolve(state, "Ctrl+K", "", "", true, false));
        Assert.Equal("Win+Enter", ShortcutKeyAdapter.Format(Key.Enter, KeyModifiers.Meta));
        Assert.Null(ShortcutKeyAdapter.Format(Key.ImeProcessed, KeyModifiers.None));
        Assert.Null(ShortcutKeyAdapter.Format(Key.LeftCtrl, KeyModifiers.Control));
    }

    [AvaloniaFact]
    public async Task Buttons_and_shortcuts_share_awaitable_commands_without_double_execution()
    {
        var gate = new TaskCompletionSource(); var count = 0;
        var command = new MptAsyncRelayCommand(async () => { count++; await gate.Task; }, () => true);
        var action = MptShortcutCommand.FromCommand("run", command);
        var first = action.ExecuteAsync();
        Assert.False(action.CanExecute!());
        var second = action.ExecuteAsync();
        Assert.Equal(1, count);
        gate.SetResult(); await first; await second;
        Assert.True(action.CanExecute());
    }
}
