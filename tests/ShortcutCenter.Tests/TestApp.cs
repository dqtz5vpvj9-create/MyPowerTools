using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(ShortcutCenter.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace ShortcutCenter.Tests;

public sealed class TestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<TestApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = true
            });
    }
}
