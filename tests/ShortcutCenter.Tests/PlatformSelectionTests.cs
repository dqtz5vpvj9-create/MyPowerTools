using Avalonia.Headless.XUnit;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace ShortcutCenter.Tests;

public sealed class PlatformSelectionTests
{
    [AvaloniaFact]
    public async Task First_load_uses_the_confirmed_platform_and_later_refresh_keeps_the_user_choice()
    {
        using var temp = new TemporaryDirectory();
        var store = new HotkeyStore(temp.Path);
        ShortcutCatalogSnapshot Read() => TestCatalog.Create(platform: "macos") with { Configuration = store.ReadShortcuts() };
        var client = new ShortcutConfigurationClient(_ => Task.FromResult(Read()), (revision, edits, _) =>
        {
            store.UpdateShortcuts(revision, edits, Read().Commands);
            return Task.FromResult(Read());
        });
        using var model = new ShortcutCenterViewModel(client);
        await model.RefreshAsync();
        Assert.Equal("macos", model.EditorPlatform);
        model.Selected = model.Rows.Single(row => row.Id == "shell.refresh");
        model.EditorPlatform = "windows";
        model.BindingText = "F9";
        await model.RefreshAsync();
        Assert.Equal("windows", model.EditorPlatform);
        Assert.Equal("F9", model.BindingText);
        await model.SaveAsync();
        Assert.StartsWith("Saved.", model.Status);
        Assert.Contains("F5", client.Hint("shell.refresh"));
        var windows = client.Snapshot with { Platform = "windows" };
        Assert.Equal("F9", ShortcutCatalog.Effective(windows).Single(item => item.Definition.Id == "shell.refresh").Gesture);
    }
}
