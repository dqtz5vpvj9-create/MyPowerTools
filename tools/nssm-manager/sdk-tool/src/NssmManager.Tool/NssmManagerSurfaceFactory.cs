using Avalonia.Controls;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace NssmManager.Tool;

public sealed class NssmManagerSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        var model = new NssmManagerViewModel(context);
        var view = new NssmManagerView { DataContext = model };
        Dispatcher.UIThread.Post(() => _ = model.RefreshAsync(), DispatcherPriority.Background);
        return view;
    }
}
