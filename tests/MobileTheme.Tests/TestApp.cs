using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(MobileTheme.Tests.MobileThemeTestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace MobileTheme.Tests;

/// <summary>
/// Hosts the SDK mobile theme the same way a consumer would: one StyleInclude of
/// MptMobileTheme.axaml on top of the platform theme. Loading this application already
/// proves the theme XAML parses, every avares:// include resolves and no selector is
/// malformed; the tests then assert what the resolved styles actually produce.
/// </summary>
public sealed class MobileThemeTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.AvaloniaSdk/"))
        {
            Source = new Uri("avares://MyPowerTools.AvaloniaSdk/Themes/MptMobileTheme.axaml")
        });
    }
}

public static class MobileThemeTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<MobileThemeTestApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
