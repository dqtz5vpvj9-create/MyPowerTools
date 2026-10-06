using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace AudioRelay.Tool;

public sealed class AudioRelaySurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var viewModel = new AudioRelayViewModel(context);
        var view = new AudioRelayView { DataContext = viewModel };
        Dispatcher.UIThread.Post(() => _ = viewModel.RefreshAsync(), DispatcherPriority.Background);
        return view;
    }
}
