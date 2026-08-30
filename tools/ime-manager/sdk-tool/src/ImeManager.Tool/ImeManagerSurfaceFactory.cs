using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace ImeManager.Tool;

public sealed class ImeManagerSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var viewModel = new ImeManagerViewModel(context);
        var view = new ImeManagerView
        {
            DataContext = viewModel
        };
        Dispatcher.UIThread.Post(
            () => _ = viewModel.InitializeAsync(),
            DispatcherPriority.Background);
        return view;
    }
}
