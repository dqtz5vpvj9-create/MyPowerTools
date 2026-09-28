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

    /// <summary>
    /// Raised when the hosted surface control changes. The shell chrome uses it to switch the page
    /// scroller to a bounded measure once a full-height surface is actually hosted.
    /// </summary>
    public event EventHandler? ManagedSurfaceChanged;

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
            ManagedSurfaceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
