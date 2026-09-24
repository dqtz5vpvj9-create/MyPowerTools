using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace MyPowerTools.Installer.Mac;

internal sealed class App : Application
{
    internal static InstallerArguments Arguments { get; set; } = new(false, null, null, null);

    public override void Initialize()
    {
        Name = "MyPowerTools Installer";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new InstallerWindow(Arguments, desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
