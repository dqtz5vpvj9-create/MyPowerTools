using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ImeManager.Tool;

public sealed partial class ImeManagerView : UserControl
{
    public ImeManagerView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
