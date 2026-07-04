using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
