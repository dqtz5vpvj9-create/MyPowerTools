using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using PasteImage.Surface.ViewModels;
using PasteImage.Surface.Views;

namespace PasteImage.Surface;

public sealed class PasteImageSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var viewModel = new PasteImageViewModel(context);
        var view = new PasteImageView { DataContext = viewModel };
        Dispatcher.UIThread.Post(
            () => _ = viewModel.InitializeAsync(),
            DispatcherPriority.Background);
        return view;
    }
}
