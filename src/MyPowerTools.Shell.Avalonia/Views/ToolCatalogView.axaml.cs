using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ToolCatalogView : UserControl
{
    public ToolCatalogView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
