using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace InputMonitor.Surface.Views;

public sealed partial class InputMonitorView : UserControl
{
    public InputMonitorView()
    {
        AvaloniaXamlLoader.Load(this);
        DetachedFromVisualTree += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
