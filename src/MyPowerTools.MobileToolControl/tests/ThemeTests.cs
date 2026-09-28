using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// The page must be drawn by the shipped mobile theme: SDK classes and tokens only, no private palette
/// that could override a token, and every dynamic resource must follow a live light → dark → light
/// variant switch on the same page instance.
/// </summary>
public sealed class ThemeTests
{
    private static Color VariantColor(string key, ThemeVariant variant)
    {
        var application = Application.Current!;
        Assert.True(
            application.TryFindResource(key, variant, out var found),
            $"主题缺少资源 {key}（{variant}）。");
        return ((SolidColorBrush)found!).Color;
    }

    [AvaloniaFact]
    public void TheShippedThemeProvidesBothVariantsWithDifferentValues()
    {
        foreach (var key in new[]
                 {
                     MobileToolControlTheme.BackgroundBrushKey,
                     MobileToolControlTheme.CardBrushKey,
                     MobileToolControlTheme.TextBrushKey,
                     MobileToolControlTheme.SecondaryTextBrushKey,
                     MobileToolControlTheme.AccentBrushKey,
                     MobileToolControlTheme.DividerBrushKey,
                     MobileToolControlTheme.SuccessBrushKey,
                     MobileToolControlTheme.WarningBrushKey,
                     MobileToolControlTheme.AccentSoftBrushKey,
                     MobileToolControlTheme.NeutralPillBackgroundBrushKey
                 })
        {
            Assert.NotEqual(VariantColor(key, ThemeVariant.Light), VariantColor(key, ThemeVariant.Dark));
        }

        // The metrics the page relies on come from the theme too.
        Assert.True(MobileToolControlTheme.TryGetResource<Thickness>(
            MobileToolControlTheme.PagePaddingKey, out var padding));
        Assert.True(MobileToolControlTheme.TryGetResource<Thickness>(
            MobileToolControlTheme.PagePaddingNarrowKey, out var narrow));
        Assert.NotEqual(padding, narrow);
    }

    [AvaloniaFact]
    public void ThePageFollowsALightDarkLightSwitchWithoutBeingRebuilt()
    {
        using var host = PhoneHost.Create();
        host.Complete(host.Vm.RefreshDevicesAsync(announce: false));
        host.Complete(host.Vm.OpenDeviceAsync(host.Vm.Devices[0]));

        var card = host.Find<Border>(border => border.Classes.Contains(MobileToolControlTheme.CardClass));
        Assert.NotNull(card);
        var pageTitle = host.Find<TextBlock>(block => block.Classes.Contains(MobileToolControlTheme.PageTitleClass));
        Assert.NotNull(pageTitle);

        var lightCard = VariantColor(MobileToolControlTheme.CardBrushKey, ThemeVariant.Light);
        var darkCard = VariantColor(MobileToolControlTheme.CardBrushKey, ThemeVariant.Dark);
        var lightText = VariantColor(MobileToolControlTheme.TextBrushKey, ThemeVariant.Light);
        var darkText = VariantColor(MobileToolControlTheme.TextBrushKey, ThemeVariant.Dark);

        host.SetTheme(ThemeVariant.Light);
        Assert.Equal(lightCard, ((SolidColorBrush)card!.Background!).Color);
        Assert.Equal(lightText, ((SolidColorBrush)pageTitle!.Foreground!).Color);

        host.SetTheme(ThemeVariant.Dark);
        Assert.Equal(darkCard, ((SolidColorBrush)card.Background!).Color);
        Assert.Equal(darkText, ((SolidColorBrush)pageTitle.Foreground!).Color);

        host.SetTheme(ThemeVariant.Light);
        Assert.Equal(lightCard, ((SolidColorBrush)card.Background!).Color);
        Assert.Equal(lightText, ((SolidColorBrush)pageTitle.Foreground!).Color);
    }

    [AvaloniaFact]
    public void ThePageTitleUsesTheThemeTypeScaleAndThePageKeepsItsTokenPadding()
    {
        using var host = PhoneHost.Create(width: 390, height: 844);
        host.Complete(host.Vm.RefreshDevicesAsync(announce: false));

        var pageTitle = host.Find<TextBlock>(block => block.Classes.Contains(MobileToolControlTheme.PageTitleClass));
        Assert.NotNull(pageTitle);
        Assert.True(MobileToolControlTheme.TryGetResource<double>(
            MobileToolControlTheme.PageTitleFontSizeKey, out var titleSize));
        Assert.Equal(titleSize, pageTitle!.FontSize);

        var pageFrame = host.Find<Border>(border => border.Classes.Contains(MobileToolControlTheme.PageClass));
        Assert.NotNull(pageFrame);
        Assert.True(MobileToolControlTheme.TryGetResource<Thickness>(
            MobileToolControlTheme.PagePaddingKey, out var padding));
        Assert.Equal(padding, pageFrame!.Padding);
        Assert.False(pageFrame.Classes.Contains(MobileToolControlTheme.PageNarrowClass));
    }

    [AvaloniaFact]
    public void ANarrowPhoneSwitchesToTheNarrowPageClass()
    {
        using var host = PhoneHost.Create(width: 320, height: 720);
        host.Complete(host.Vm.RefreshDevicesAsync(announce: false));

        var pageFrame = host.Find<Border>(border => border.Classes.Contains(MobileToolControlTheme.PageClass));
        Assert.NotNull(pageFrame);
        Assert.True(pageFrame!.Classes.Contains(MobileToolControlTheme.PageNarrowClass));
        Assert.True(MobileToolControlTheme.TryGetResource<Thickness>(
            MobileToolControlTheme.PagePaddingNarrowKey, out var narrow));
        Assert.Equal(narrow, pageFrame.Padding);
    }

    [AvaloniaFact]
    public void ThePrimaryActionKeepsTheThemeTouchTargetAndFitsThePageWidth()
    {
        using var host = PhoneHost.Create(width: 390, height: 844);
        host.Complete(host.Vm.RefreshDevicesAsync(announce: false));

        var primary = host.FindAll<Button>(button => button.Classes.Contains(MobileToolControlTheme.PrimaryClass));
        Assert.NotEmpty(primary);
        Assert.True(MobileToolControlTheme.TryGetResource<double>(
            MobileToolControlTheme.PrimaryButtonMinHeightKey, out var minHeight));
        Assert.True(MobileToolControlTheme.TryGetResource<double>(
            MobileToolControlTheme.TouchTargetMinKey, out var touchTarget));

        // A control that is inside a closed sheet has no layout box at all; every primary action that
        // is actually laid out must meet the theme's touch target and fit the page.
        var laidOut = primary.Where(button => button.Bounds.Height > 0).ToArray();
        Assert.NotEmpty(laidOut);
        foreach (var button in laidOut)
        {
            Assert.True(
                button.Bounds.Height >= Math.Min(minHeight, touchTarget) - 0.5,
                $"主按钮「{button.Content}」高度 {button.Bounds.Height} 小于主题触控目标 {touchTarget}。");
            Assert.True(
                button.Bounds.Width > 0 && button.Bounds.Width <= 390,
                $"主按钮宽度 {button.Bounds.Width} 超出页面。");
        }
    }
}
