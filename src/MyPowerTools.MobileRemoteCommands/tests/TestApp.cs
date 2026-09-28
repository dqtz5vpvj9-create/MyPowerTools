using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(MyPowerTools.MobileRemoteCommands.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Host application for the headless tests.
///
/// It registers the **real** mobile theme from the SDK package
/// (<c>avares://MyPowerTools.AvaloniaSdk/Themes/MptMobileTheme.axaml</c>), exactly like the Android
/// Shell does, so the tests exercise the shipped <c>MptMobile*</c> tokens, typography, surfaces and
/// control styles instead of only this page's pre-theme fallback. The page therefore has to prove it
/// works with the theme present.
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
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
