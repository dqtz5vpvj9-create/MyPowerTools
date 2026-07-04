using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ModuleDetailView : UserControl
{
    public ModuleDetailView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
