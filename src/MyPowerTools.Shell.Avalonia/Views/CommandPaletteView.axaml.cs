using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
