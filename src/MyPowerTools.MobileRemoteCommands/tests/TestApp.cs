using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(MyPowerTools.MobileRemoteCommands.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Minimal host application for the headless tests. The surface paints itself from its own palette and
/// uses plain Avalonia controls, so the Fluent theme is all it needs; no Skia, no Shell, no Android host.
/// </summary>
public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}
