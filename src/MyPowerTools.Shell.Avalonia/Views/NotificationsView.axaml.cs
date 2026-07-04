using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class NotificationsView : UserControl
{
    public NotificationsView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
