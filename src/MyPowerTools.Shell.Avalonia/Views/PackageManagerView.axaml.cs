using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class PackageManagerView : UserControl
{
    public PackageManagerView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
