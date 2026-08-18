using Avalonia.Controls;
using Avalonia.Threading;
using InputMonitor.Surface.ViewModels;
using InputMonitor.Surface.Views;
using MyPowerTools.AvaloniaSdk;

namespace InputMonitor.Surface;

public sealed class InputMonitorSurfaceFactory : IMptAvaloniaSurfaceFactory
{
    public Control CreateSurface(MptAvaloniaSurfaceContext context)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return CreateCore(context);
        }

        return Dispatcher.UIThread.Invoke(() => CreateCore(context));
    }

    private static Control CreateCore(MptAvaloniaSurfaceContext context)
    {
        var viewModel = new InputMonitorViewModel(context);
        var view = new InputMonitorView { DataContext = viewModel };
        Dispatcher.UIThread.Post(
            () => _ = viewModel.InitializeAsync(),
            DispatcherPriority.Background);
        return view;
    }
}
