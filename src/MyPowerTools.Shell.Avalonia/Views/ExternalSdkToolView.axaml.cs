using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ExternalSdkToolView : UserControl
{
    private ContentControl? _managedSurfaceHost;

    public ExternalSdkToolView()
    {
        AvaloniaXamlLoader.Load(this);
        _managedSurfaceHost = this.FindControl<ContentControl>("ManagedSurfaceHost");
    }

    public void SetManagedSurface(Control control)
    {
        if (_managedSurfaceHost is not null)
        {
            _managedSurfaceHost.Content = control;
        }
        (DataContext as ExternalSdkToolViewModel)?.ReportSurface("ready", "Dotnet surface loaded.");
    }

    public void ReloadWebSurface()
    {
        // The embedded web view control is now owned by tool surface packages; web-surface tools
        // refresh by reloading the tool page rather than through an in-shell browser control.
        (DataContext as ExternalSdkToolViewModel)?.ReportSurface("loading", "Refreshing the tool surface.");
    }
}
