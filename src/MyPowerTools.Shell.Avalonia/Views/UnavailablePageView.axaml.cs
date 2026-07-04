using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class UnavailablePageView : UserControl
{
    public UnavailablePageView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
