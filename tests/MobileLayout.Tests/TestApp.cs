using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(MobileLayout.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]
namespace MobileLayout.Tests;
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        // The real shipped mobile theme (M1): the phone tests and the reference screenshots must run
        // with the same palette, typography and component styles the Android build uses.
        Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.AvaloniaSdk/"))
        { Source = new Uri("avares://MyPowerTools.AvaloniaSdk/Themes/MptMobileTheme.axaml") });
        Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.UI/"))
        { Source = new Uri("avares://MyPowerTools.UI/Themes/MptTheme.axaml") });
    }
}
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
