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
        SetHostedSurface(control);
        (DataContext as ExternalSdkToolViewModel)?.ReportSurface("ready", "Dotnet surface loaded.");
    }

    public Control? ManagedSurface => _managedSurfaceHost?.Content as Control;

    public void SetHostedSurface(Control control)
    {
        if (_managedSurfaceHost is not null)
        {
            _managedSurfaceHost.Content = control;
        }
    }
}
