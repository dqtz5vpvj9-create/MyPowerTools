using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.UI;

namespace MyPowerTools.Shell.Avalonia;

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.Shell.Avalonia/App.cs"))
        {
            Source = new Uri("avares://MyPowerTools.UI/Themes/MptTheme.axaml")
        });
        ShellAppearanceService.ApplySavedTheme(this);
        ApplyProductPalette();
        ActualThemeVariantChanged += (_, _) => ApplyProductPalette();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Remote Notifications now runs as a Service Unit. Activation forwarding and the
            // independent detail window are owned by that surface, so the Shell simply boots
            // the main window from the startup options.
            desktop.MainWindow = new MainWindow(ShellStartupOptions.FromArgs(desktop.Args));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyProductPalette()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        MptTheme.ApplyPalette(this, dark);
    }
}
