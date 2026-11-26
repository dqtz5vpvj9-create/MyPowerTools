using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(MyPowerTools.MobileToolControl.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// Host application for the headless tests.
///
/// It loads the real <c>MyPowerTools.AvaloniaSdk</c> mobile theme (the same
/// <c>MptMobileTheme.axaml</c> entry point the Android Shell adds) on top of Fluent, and renders
/// through Skia so text layout and the bottom sheets are measured for real. The page is therefore
/// verified against the shipped mobile classes, tokens and light/dark theme dictionaries - not a
/// private copy of the palette.
/// </summary>
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.AvaloniaSdk/"))
        {
            Source = new Uri("avares://MyPowerTools.AvaloniaSdk/Themes/MptMobileTheme.axaml")
        });
        RequestedThemeVariant = ThemeVariant.Light;
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
