using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class LogsView : UserControl
{
    public LogsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogsViewModel viewModel &&
            TopLevel.GetTopLevel(this) is { Clipboard: { } clipboard })
        {
            await ClipboardExtensions.SetTextAsync(clipboard, viewModel.CopyText);
        }
    }
}
