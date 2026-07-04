using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class PermissionPromptView : UserControl
{
    public PermissionPromptView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
