using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class SettingsCenterView : UserControl
{
    public SettingsCenterView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
