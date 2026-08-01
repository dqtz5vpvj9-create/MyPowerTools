using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace LocalLagCleaner.Tool;

public sealed class LocalLagCleanerSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var viewModel = new LocalLagCleanerViewModel(context);
        var view = new LocalLagCleanerView
        {
            DataContext = viewModel
        };
        Dispatcher.UIThread.Post(
            () => _ = viewModel.InitializeAsync(),
            DispatcherPriority.Background);
        return view;
    }
}
