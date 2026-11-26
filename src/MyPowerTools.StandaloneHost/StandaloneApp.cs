using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using MyPowerTools.UI;

namespace MyPowerTools.StandaloneHost;

public sealed class StandaloneApp : Application
{
    private static readonly Uri HostBaseUri = new("avares://MyPowerTools.UI/");
    private static readonly Uri SdkBaseUri = new("avares://MyPowerTools.AvaloniaSdk/");
    private readonly StandaloneSession? _session;

    public StandaloneApp()
    {
    }

    internal StandaloneApp(StandaloneSession session)
    {
        _session = session;
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add(new ResourceInclude(SdkBaseUri)
        {
            Source = new Uri("avares://MyPowerTools.AvaloniaSdk/Themes/MptSdkTheme.axaml")
        });
        Styles.Add(new StyleInclude(SdkBaseUri)
        {
            Source = new Uri("avares://MyPowerTools.AvaloniaSdk/Themes/MptMobileTheme.axaml")
        });
        Styles.Add(new StyleInclude(HostBaseUri)
        {
            Source = new Uri("avares://MyPowerTools.UI/Themes/MptTheme.axaml")
        });

        ApplyPalette();
        ActualThemeVariantChanged += (_, _) => ApplyPalette();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && _session is not null)
        {
            var window = new StandaloneWindow(_session);
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _session.DisposeSynchronously();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyPalette()
    {
        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        MptTheme.ApplyPalette(this, isDark);
    }
}
