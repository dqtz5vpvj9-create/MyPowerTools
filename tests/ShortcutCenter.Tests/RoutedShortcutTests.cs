using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;
using RemoteCommands.Surface.ViewModels;
using RemoteCommands.Surface.Views;

namespace ShortcutCenter.Tests;

public sealed class RoutedShortcutTests
{
    [AvaloniaFact]
    public async Task Unavailable_tool_action_consumes_the_key_without_running_the_application_fallback()
    {
        var source = new TestSurface { Enabled = false };
        var host = new ContentControl();
        var chrome = new ShellChromeViewModel(ShellWorkspaceController.PageLabels);
        await using var controller = Controller(host, chrome, source);
        var original = host.Content;
        Configure(controller, new(new(0, []),
            [TestSurface.Definition, new("shell.navigate.system", "shell.navigate.system", "System", "Shell", "application", [new("F5")])], [], "windows"));
        var key = Press(source, Key.F5);
        await controller.HandleKeyDownAsync(key);
        Assert.True(key.Handled);
        Assert.Equal(0, source.Executions);
        Assert.Same(original, host.Content);
        source.Enabled = true;
        await controller.HandleKeyDownAsync(Press(source, Key.F5));
        Assert.Equal(1, source.Executions);
    }

    [AvaloniaFact]
    public async Task Rebinding_and_disabling_change_actual_dispatch_while_overlays_and_ime_keep_ownership()
    {
        var source = new TestSurface();
        var host = new ContentControl();
        var chrome = new ShellChromeViewModel(ShellWorkspaceController.PageLabels);
        await using var controller = Controller(host, chrome, source);
        var snapshot = new ShortcutCatalogSnapshot(new(1, [new("test.run", [new("F8")])]), [TestSurface.Definition], [], "windows");
        Configure(controller, snapshot);
        var old = Press(source, Key.F5);
        await controller.HandleKeyDownAsync(old);
        Assert.False(old.Handled);
        await controller.HandleKeyDownAsync(Press(source, Key.F8));
        Assert.Equal(1, source.Executions);
        chrome.IsPermissionPromptOpen = true;
        var overlayKey = Press(source, Key.F8);
        await controller.HandleKeyDownAsync(overlayKey);
        Assert.False(overlayKey.Handled);
        chrome.IsPermissionPromptOpen = false;
        await controller.HandleKeyDownAsync(Press(source, Key.ImeProcessed));
        var handled = Press(source, Key.F8); handled.Handled = true;
        await controller.HandleKeyDownAsync(handled);
        Assert.Equal(1, source.Executions);
        Configure(controller, snapshot with { Configuration = new(2, [new("test.run", [new("F8")], true)]) });
        var disabled = Press(source, Key.F8);
        await controller.HandleKeyDownAsync(disabled);
        Assert.False(disabled.Handled);
        Assert.Equal(1, source.Executions);
    }

    [AvaloniaFact]
    public async Task Real_command_surface_shares_execution_and_preserves_dynamic_rerun_context_in_its_hint()
    {
        using var temp = new TemporaryDirectory();
        var context = new MptAvaloniaSurfaceContext("remote-commands", "workspace", temp.Path, "light",
            (_, _, _) => throw new NotSupportedException(), (_, _, _) => Task.CompletedTask, null!, _ => { });
        var model = new RemoteCommandsViewModel(context);
        await model.InitializeAsync();
        model.SelectedCommandIndex = model.Commands.ToList().FindIndex(command => command.Command == "replace_host_directory");
        Assert.True(model.SelectedCommandIndex >= 0);
        var view = new RemoteCommandsView { DataContext = model };
        MptShortcutHint.SetBindings(view, new Dictionary<string, string> { ["remote-commands.ui.rerun"] = "Ctrl+F9" });
        var button = view.GetLogicalDescendants().OfType<Button>()
            .Single(item => MptShortcutHint.GetCommandId(item) == "remote-commands.ui.rerun");
        Assert.Equal($"{model.LastRunSummary} (Ctrl+F9)", ToolTip.GetTip(button));
        model.Input1 = "/home/lixr/aosp_host_working_dir/out";
        var action = view.GetShortcutCommands().Single(item => item.Id == "remote-commands.ui.run");
        Assert.True(action.CanExecute!());
        await action.ExecuteAsync();
        Assert.Single(model.HistoryItems);
        Assert.Equal($"{model.LastRunSummary} (Ctrl+F9)", ToolTip.GetTip(button));
        MptShortcutHint.SetBindings(view, new Dictionary<string, string> { ["remote-commands.ui.rerun"] = "Ctrl+F10" });
        Assert.Equal($"{model.LastRunSummary} (Ctrl+F10)", ToolTip.GetTip(button));
    }

    private static ShellWorkspaceController Controller(ContentControl host, ShellChromeViewModel chrome, TestSurface surface)
    {
        var view = new ExternalSdkToolView();
        view.SetManagedSurface(surface);
        host.Content = view;
        var controller = new ShellWorkspaceController(chrome, new MptSearchBox(), host,
            new ContentControl(), new ContentControl(), new ContentControl());
        typeof(ShellWorkspaceController).GetField("_currentToolId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, "test");
        return controller;
    }

    private static void Configure(ShellWorkspaceController controller, ShortcutCatalogSnapshot snapshot)
    {
        // Seed only the transport cache; exercise the production event handler and action dispatch.
        var client = (ShortcutConfigurationClient)typeof(ShellWorkspaceController)
            .GetField("_shortcuts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller)!;
        typeof(ShortcutConfigurationClient).GetMethod("Accept", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, [snapshot]);
    }

    private static KeyEventArgs Press(Control source, Key key) =>
        new() { RoutedEvent = InputElement.KeyDownEvent, Source = source, Key = key };

    private sealed class TestSurface : UserControl, IMptShortcutCommandSource
    {
        public static ShortcutDefinition Definition { get; } = new("test.run", "test.run", "Run", "Test", "tool", [new("F5")], "test");
        public string ShortcutToolId => "test";
        public string ShortcutContext => "workspace";
        public bool Enabled { get; set; } = true;
        public int Executions { get; private set; }
        public IReadOnlyList<MptShortcutCommand> GetShortcutCommands() =>
            [new("test.run", () => { Executions++; return Task.CompletedTask; }, () => Enabled)];
    }
}
