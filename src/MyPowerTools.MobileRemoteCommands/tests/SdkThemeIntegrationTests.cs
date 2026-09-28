using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// The page against the **real** SDK mobile theme (the test host registers
/// <c>MptMobileTheme.axaml</c> exactly like the Android Shell does).
///
/// These are the checks that the fallback path cannot make: that the shipped variant-aware tokens reach
/// the page, that the page does not override the theme's metrics with local values, and that a live
/// variant switch updates the marks the theme does not own without leaving the old error colour behind.
/// </summary>
public sealed class SdkThemeIntegrationTests
{
    [AvaloniaFact]
    public void The_real_sdk_theme_is_loaded_and_variant_aware()
    {
        Assert.True(RemoteCommandsMobileTheme.SdkThemeAvailable, "测试宿主没有加载 SDK 移动主题。");
        Assert.True(RemoteCommandsMobileTheme.IsSdkThemeAvailable(ThemeVariant.Light));
        Assert.True(RemoteCommandsMobileTheme.IsSdkThemeAvailable(ThemeVariant.Dark));

        var light = Assert.IsAssignableFrom<ISolidColorBrush>(
            RemoteCommandsMobileTheme.ResolveBrush(RemoteCommandsMobileTheme.CardBrushKey, ThemeVariant.Light));
        var dark = Assert.IsAssignableFrom<ISolidColorBrush>(
            RemoteCommandsMobileTheme.ResolveBrush(RemoteCommandsMobileTheme.CardBrushKey, ThemeVariant.Dark));

        Assert.NotEqual(light.Color, dark.Color);
    }

    [AvaloniaFact]
    public void Surfaces_and_page_metrics_come_from_the_sdk_theme_not_from_the_page()
    {
        using var harness = Harness();
        var view = harness.View;

        var card = Assert.IsAssignableFrom<ISolidColorBrush>(view.LastResultCardForTests.Background);
        var token = Assert.IsAssignableFrom<ISolidColorBrush>(
            RemoteCommandsMobileTheme.ResolveBrush(RemoteCommandsMobileTheme.CardBrushKey, ThemeVariant.Light));
        Assert.Equal(token.Color, card.Color);

        // The page never sets a local margin: the theme's page padding token is what applies.
        Assert.Equal(new Thickness(22, 14, 22, 24), view.PagePanelForTests.Margin);
        Assert.DoesNotContain(RemoteCommandsMobileTheme.PageNarrowClass, view.PagePanelForTests.Classes);

        harness.Resize(320, 720);
        Assert.Equal(new Thickness(18, 14, 18, 24), view.PagePanelForTests.Margin);
        Assert.Contains(RemoteCommandsMobileTheme.PageNarrowClass, view.PagePanelForTests.Classes);
    }

    [AvaloniaFact]
    public void An_attached_page_updates_on_a_light_dark_light_switch_without_leaving_the_error_colour()
    {
        using var harness = Harness();
        var view = harness.View;

        // A failing run pins the error tone (the SDK names no error colour, so this one is the page's).
        harness.Module.RunState = RemoteCommandsMobileContract.StateFailed;
        harness.Module.RunMessage = "远端命令返回退出码 2";
        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        var lightError = Assert.IsAssignableFrom<ISolidColorBrush>(
            RemoteCommandsMobilePalette.ForThemeVariant(ThemeVariant.Light).Brush("Error"));
        Assert.Equal(lightError.Color, Foreground(view.RunStateForTests));

        try
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            harness.Pump();

            var darkCard = Assert.IsAssignableFrom<ISolidColorBrush>(
                RemoteCommandsMobileTheme.ResolveBrush(RemoteCommandsMobileTheme.CardBrushKey, ThemeVariant.Dark));
            Assert.Equal(darkCard.Color, Assert.IsAssignableFrom<ISolidColorBrush>(view.LastResultCardForTests.Background).Color);

            var darkError = Assert.IsAssignableFrom<ISolidColorBrush>(
                RemoteCommandsMobilePalette.ForThemeVariant(ThemeVariant.Dark).Brush("Error"));
            Assert.NotEqual(lightError.Color, darkError.Color);
            Assert.Equal(darkError.Color, Foreground(view.RunStateForTests));

            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            harness.Pump();
            Assert.Equal(lightError.Color, Foreground(view.RunStateForTests));
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            harness.Pump();
        }
    }

    [AvaloniaFact]
    public void The_error_role_does_not_linger_when_the_run_succeeds()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Module.RunState = RemoteCommandsMobileContract.StateFailed;
        harness.Module.RunMessage = "远端命令返回退出码 2";
        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        harness.Module.RunState = RemoteCommandsMobileContract.StateSucceeded;
        harness.Module.RunMessage = "执行完成";
        harness.Module.RunExitCode = 0;
        harness.Click(view.RetryButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "重试没有结束。");

        // Both the state line and its mark now take the theme's success colour through the shared class.
        var success = Assert.IsAssignableFrom<ISolidColorBrush>(
            RemoteCommandsMobileTheme.ResolveBrush("MptMobileSuccessBrush", ThemeVariant.Light));
        Assert.Equal(success.Color, Foreground(view.RunStateForTests));
        Assert.Equal(
            success.Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(
                view.StateIconsForTests[0].GetValue(Avalonia.Controls.Shapes.Shape.StrokeProperty)).Color);
    }

    private static Color Foreground(TextBlock block) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(block.GetValue(TextBlock.ForegroundProperty)).Color;

    private static SurfaceHarness Harness()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        return SurfaceHarness.Create(module);
    }
}
