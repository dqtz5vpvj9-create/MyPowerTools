using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ToolHostView : UserControl
{
    public ToolHostView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
