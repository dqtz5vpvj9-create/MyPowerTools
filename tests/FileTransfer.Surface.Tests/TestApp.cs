using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(FileTransfer.Surface.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace FileTransfer.Surface.Tests;

/// <summary>
/// The host for every test in this assembly, and the strictest one: Fluent plus the desktop product
/// theme, and deliberately **no** mobile theme. The shipped desktop app only adds
/// <c>MptMobileTheme.axaml</c> on Android, so this is what the file tool actually meets on Windows and
/// macOS. A page that forgets to carry its own scoped mobile styles fails here instead of on a user's
/// desktop.
/// </summary>
public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.UI/"))
        {
            Source = new Uri("avares://MyPowerTools.UI/Themes/MptTheme.axaml")
        });
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
