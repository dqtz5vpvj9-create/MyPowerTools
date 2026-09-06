using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Tests;

public sealed class PersonalUxFavoritesTests
{
    [Fact]
    public async Task Favorites_and_recents_survive_restart_and_deduplicate_case_insensitively()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tool-preferences-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "preferences.json");
            var store = new ShellToolPreferencesStore(path);
            await store.SetFavoriteAsync("remote-commands", true);
            await store.SetFavoriteAsync("REMOTE-COMMANDS", true);
            await store.RecordOpenedAsync("screenease");
            await store.RecordOpenedAsync("remote-commands");
            await store.RecordOpenedAsync("ScreenEase");
            var reloaded = new ShellToolPreferencesStore(path);
            Assert.Equal(new[] { "remote-commands" }, reloaded.Current.FavoriteToolIds);
            Assert.Equal(new[] { "ScreenEase", "remote-commands" }, reloaded.Current.RecentToolIds);
            await reloaded.SetFavoriteAsync("REMOTE-COMMANDS", false);
            Assert.Empty(new ShellToolPreferencesStore(path).Current.FavoriteToolIds);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Damaged_preferences_do_not_break_startup_or_get_silently_destroyed()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tool-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "preferences.json");
            await File.WriteAllTextAsync(path, "{ unfinished");
            var store = new ShellToolPreferencesStore(path);
            Assert.Empty(store.Current.FavoriteToolIds);
            await store.SetFavoriteAsync("adb-forwarder", true);
            Assert.Equal("{ unfinished", File.ReadAllText(Assert.Single(Directory.GetFiles(root, "*.unreadable-*"))));
            Assert.Single(new ShellToolPreferencesStore(path).Current.FavoriteToolIds);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void New_users_still_see_the_catalog_and_dashboard_counts_are_not_limited_to_history()
    {
        var first = Card("first");
        var second = Card("second");
        var emptyHistory = new HomeViewModel([], [], [], 2, allTools: [first, second]);
        Assert.True(emptyHistory.IsReady);
        Assert.Equal(2, emptyHistory.ReadyToolCount);
        Assert.Equal("Tools", emptyHistory.DashboardToolsTitle);
        var personalized = new HomeViewModel([second], [first], [], 2, allTools: [first, second]);
        Assert.Equal("second", personalized.DashboardTools[0].ToolId);
        Assert.Equal("first", personalized.DashboardTools[1].ToolId);
        Assert.Equal(2, personalized.ReadyToolCount);
    }

    private static ToolCardViewModel Card(string id) => new(id, id, "", "", "", "Ready", "", ToolAvailability.Available, false);
}
