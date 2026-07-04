using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class LogsView : UserControl
{
    public LogsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
