using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using PasteImage.Surface.ViewModels;

namespace PasteImage.Surface.Views;

public sealed partial class PasteImageView : UserControl
{
    public PasteImageView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is PasteImageViewModel viewModel)
            {
                viewModel.ClipboardWriter = WriteClipboardTextAsync;
            }
        };
        DetachedFromVisualTree += (_, args) =>
        {
            // The macOS resident window temporarily detaches its content when hidden.
            // Preserve draft settings, preview and cancellation state for restoration.
            if (OperatingSystem.IsMacOS() && args.RootVisual is Window window &&
                (!window.IsVisible || window.WindowState == WindowState.Minimized)) return;
            (DataContext as IDisposable)?.Dispose();
        };
    }

    private async Task WriteClipboardTextAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard
            ?? throw new InvalidOperationException("No clipboard is available for this window.");
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.CreateText(text));
        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
    }
}
