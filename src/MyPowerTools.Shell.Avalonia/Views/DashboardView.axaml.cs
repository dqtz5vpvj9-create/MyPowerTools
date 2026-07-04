using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class DashboardView : UserControl
{
    public DashboardView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
