using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class SystemHubView : UserControl
{
    public SystemHubView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
