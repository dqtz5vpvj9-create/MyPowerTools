using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AudioRelay.Tool;

public sealed partial class AudioRelayView : UserControl
{
    public AudioRelayView() => AvaloniaXamlLoader.Load(this);
}
