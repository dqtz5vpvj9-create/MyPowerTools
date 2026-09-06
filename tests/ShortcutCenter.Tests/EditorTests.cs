using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace ShortcutCenter.Tests;

public sealed class EditorTests
{
    [AvaloniaFact]
    public async Task Edit_rebind_disable_reset_and_undo_use_one_store_and_update_hints()
    {
        using var temp = new TemporaryDirectory(); var store = new HotkeyStore(temp.Path);
        var client = Client(store);
        using var model = new ShortcutCenterViewModel(client); await model.RefreshAsync();
        model.Selected = model.Rows.Single(row => row.Id == "shell.refresh");
        model.BindingText = "F8\nCtrl+Shift+R";
        await model.SaveAsync();
        Assert.Equal("F8 / Ctrl+Shift+R", client.Hint("shell.refresh"));
        Assert.DoesNotContain(ShortcutCatalog.Effective(client.Snapshot), item => item.Definition.Id == "shell.refresh" && item.Gesture == "F5");
        var mac = client.Snapshot with { Platform = "macos" };
        Assert.Contains(ShortcutCatalog.Effective(mac), item => item.Definition.Id == "shell.refresh" && item.Gesture == "F5");
        await model.ToggleDisabledAsync(); Assert.Equal("", client.Hint("shell.refresh"));
        await model.UndoAsync(); Assert.Equal("F8 / Ctrl+Shift+R", client.Hint("shell.refresh"));
        await model.ResetAsync(); Assert.Contains("F5", client.Hint("shell.refresh"));
        Assert.Empty(store.ReadShortcuts().Overrides);
    }

    [AvaloniaFact]
    public async Task Typed_drafts_survive_refresh_and_switching_actions()
    {
        using var temp = new TemporaryDirectory(); var client = Client(new HotkeyStore(temp.Path));
        using var model = new ShortcutCenterViewModel(client); await model.RefreshAsync();
        model.Selected = model.Rows.Single(row => row.Id == "shell.refresh"); model.BindingText = "F9";
        model.Selected = model.Rows.Single(row => row.Id == "shell.shortcuts.open");
        model.Selected = model.Rows.Single(row => row.Id == "shell.refresh");
        Assert.Equal("F9", model.BindingText);
        await model.RefreshAsync(); Assert.Equal("F9", model.BindingText);
    }

    [AvaloniaFact]
    public async Task Imports_are_atomic_and_preserve_unavailable_tools_and_other_platforms()
    {
        using var temp = new TemporaryDirectory(); var store = new HotkeyStore(temp.Path); var client = Client(store);
        using var model = new ShortcutCenterViewModel(client); await model.RefreshAsync();
        await model.ImportAsync("""{"schemaVersion":1,"overrides":[{"id":"absent.action","bindings":[{"gesture":"Cmd+F9","platform":"macos"}]}]}""");
        Assert.Single(store.ReadShortcuts().Overrides);
        Assert.Contains(model.Rows, row => row.Id == "absent.action" && row.Status.StartsWith("Unavailable"));
        var before = model.Export();
        await model.ImportAsync("""{"schemaVersion":1,"overrides":[{"id":"valid","bindings":[{"gesture":"F9"}]},{"id":"invalid","bindings":[{"gesture":"Ctrl+None"}]}]}""");
        Assert.Equal(before, model.Export());
        Assert.Contains("Save not confirmed", model.Status);
    }

    [AvaloniaFact]
    public async Task Key_recording_consumes_input_without_executing_the_action()
    {
        using var temp = new TemporaryDirectory(); using var model = new ShortcutCenterViewModel(Client(new HotkeyStore(temp.Path)));
        await model.RefreshAsync(); model.Selected = model.Rows.Single(row => row.Id == "shell.refresh");
        var view = new ShortcutCenterView { DataContext = model };
        var window = new Window { Content = view, Width = 1024, Height = 860 }; window.Show();
        try
        {
            model.BindingText = ""; model.IsRecording = true;
            var editor = view.FindControl<TextBox>("BindingEditor")!;
            var args = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.K, KeyModifiers = KeyModifiers.Control, Source = editor };
            editor.RaiseEvent(args);
            Assert.True(args.Handled); Assert.Equal("Ctrl+K", model.BindingText); Assert.False(model.IsRecording);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Tool_button_hints_follow_inherited_host_bindings()
    {
        var button = new Button { Content = "Run" };
        MptShortcutHint.SetCommandId(button, "tool.run");
        var host = new StackPanel { Children = { button } };
        MptShortcutHint.SetBindings(host, new Dictionary<string, string> { ["tool.run"] = "Ctrl+Enter" });
        Assert.Equal("Run (Ctrl+Enter)", ToolTip.GetTip(button));
        MptShortcutHint.SetBindings(host, new Dictionary<string, string> { ["tool.run"] = "Ctrl+K" });
        Assert.Equal("Run (Ctrl+K)", ToolTip.GetTip(button));
        MptShortcutHint.SetBindings(host, new Dictionary<string, string>());
        Assert.Equal("Run", ToolTip.GetTip(button));
    }

    [AvaloniaFact]
    public void All_seven_compiled_surfaces_expose_the_optional_action_contract()
    {
        IMptShortcutCommandSource[] sources =
        [new AdbForwarder.Surface.Views.AdbForwarderView(), new InputMonitor.Surface.Views.InputMonitorView(),
         new ScreenEase.Surface.Views.ScreenEaseView(), new SmartBird.Surface.Views.SmartBirdThermostatView(),
         new DoubaoAgent.Surface.Views.DoubaoAgentView(), new RemoteNotifications.Surface.Views.RemoteNotificationsView(),
         new RemoteCommands.Surface.Views.RemoteCommandsView()];
        Assert.Equal(7, sources.Select(source => source.ShortcutToolId).Distinct().Count());
        foreach (var source in sources) Assert.Empty(source.GetShortcutCommands());
    }

    private static ShortcutConfigurationClient Client(HotkeyStore store)
    {
        ShortcutCatalogSnapshot Read() => TestCatalog.Create() with { Configuration = store.ReadShortcuts() };
        return new(_ => Task.FromResult(Read()), (revision, edits, _) =>
        {
            store.UpdateShortcuts(revision, edits, Read().Commands);
            return Task.FromResult(Read());
        });
    }
}
