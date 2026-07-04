using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ModulesView : UserControl
{
    public ModulesView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
