using Avalonia.Input;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Navigation;

namespace MyPowerTools.Tests;

public sealed class ShellBackNavigationUxTests
{
    [Fact]
    public void Alt_left_resolves_to_back_navigation()
    {
        Assert.True(ShellKeyboardShortcut.TryParseGesture("Alt+Left", out var key, out var modifiers));
        Assert.Equal(Key.Left, key);
        Assert.Equal(KeyModifiers.Alt, modifiers);
        Assert.Equal(
            ShellKeyboardAction.NavigateBack,
            ShellKeyboardShortcut.Resolve(key, modifiers).Action);
    }

    [Fact]
    public void Navigation_service_returns_to_the_previous_route()
    {
        var navigation = new ShellNavigationService();
        navigation.Navigate(ShellRoute.Tools);
        navigation.Navigate(ShellRoute.ForTool("sample-tool", "overview"));

        Assert.True(navigation.TryGoBack());
        Assert.Equal(ShellRoute.Tools, navigation.Current);
    }

    [Fact]
    public void Navigation_service_preserves_notifications_in_history()
    {
        var navigation = new ShellNavigationService();
        navigation.Navigate(ShellRoute.Notifications);
        navigation.Navigate(ShellRoute.Settings);

        Assert.True(navigation.TryGoBack());
        Assert.Equal(ShellRoute.Notifications, navigation.Current);
    }
}
