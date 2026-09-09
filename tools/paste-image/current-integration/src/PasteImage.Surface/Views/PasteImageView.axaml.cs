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
        DetachedFromVisualTree += (_, _) =>
        {
            // Detachment also happens during temporary host layout changes. The surface
            // loader owns disposal; cancelling here leaves a restored page inoperable.
            if (DataContext is PasteImageViewModel viewModel)
            {
                viewModel.ClipboardWriter = null;
            }
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
