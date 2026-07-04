using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ShellChromeView : UserControl
{
    public ShellChromeView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
