using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class ClearNotificationsDialog : Window
{
    public ClearNotificationsDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public ClearNotificationsDialog(int notificationCount)
        : this()
    {
        var prompt = this.FindControl<TextBlock>("PromptText")
            ?? throw new InvalidOperationException("Clear notifications prompt was not found.");
        prompt.Text = $"Remove all {notificationCount} stored notifications? This action cannot be undone.";
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
