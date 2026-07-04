using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

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
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(ShellStartupOptions.FromArgs(desktop.Args));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
