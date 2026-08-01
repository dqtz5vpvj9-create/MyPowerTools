using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LocalLagCleaner.Tool;

public sealed partial class LocalLagCleanerView : UserControl
{
    public LocalLagCleanerView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
