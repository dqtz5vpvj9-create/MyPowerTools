using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(MyPowerTools.MobileNotifications.Tests.TestAppBuilder))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace MyPowerTools.MobileNotifications.Tests;

/// <summary>
/// Host application for the headless tests.
///
/// It loads the real <c>MyPowerTools.AvaloniaSdk</c> mobile theme (the same
/// <c>MptMobileTheme.axaml</c> entry point the Shell adds) on top of Fluent, and renders through
/// Skia so text layout and the bottom sheet are measured for real. The page is therefore verified
/// against the shipped mobile classes, tokens and light/dark theme dictionaries - not a private
/// copy of the palette.
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

internal static class TestEnvironment
{
    /// <summary>
    /// The shipped store's Android lock resolves its lock directory from MPT_DATA_ROOT. Tests must
    /// never create locks in the developer's real profile, so the whole assembly is redirected to one
    /// throw-away directory before any store is constructed.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-mobile-notification-tests", "state-root");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("MPT_DATA_ROOT", root);
        Environment.SetEnvironmentVariable("MPT_REMOTE_NOTIFICATIONS_SKIP_LEGACY_IMPORT", "1");
    }

    public static string NewDataDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "mpt-mobile-notification-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyPowerTools.Android.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
