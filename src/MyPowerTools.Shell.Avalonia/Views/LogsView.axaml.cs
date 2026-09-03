using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MyPowerTools.Shell.Avalonia.ViewModels;
namespace MyPowerTools.Shell.Avalonia.Views;

public sealed partial class LogsView : UserControl
{
    public LogsView() => AvaloniaXamlLoader.Load(this);

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm || TopLevel.GetTopLevel(this) is not { Clipboard: { } cb }) return;
        try { await ClipboardExtensions.SetTextAsync(cb, vm.CopyText); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"Clipboard copy failed: {ex}"); }
    }
}
