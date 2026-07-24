using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PasteImage.Surface.Views;

public sealed partial class PasteImageView : UserControl
{
    public PasteImageView()
    {
        AvaloniaXamlLoader.Load(this);
        DetachedFromVisualTree += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
