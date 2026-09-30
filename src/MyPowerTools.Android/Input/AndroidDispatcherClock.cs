using System.Reflection;
using Avalonia.Threading;

namespace MyPowerTools.Android.Input;

/// <summary>
/// Avalonia 12.0.5 creates the dispatcher before installing Android's implementation, retaining
/// the first stopwatch. Android's earlier stopwatch then treats future timer deadlines as overdue
/// and repeatedly posts immediate callbacks. Use the platform clock for both deadline calculation
/// and delivery. This compatibility shim is isolated here because Avalonia exposes no clock setter.
/// </summary>
internal static class AndroidDispatcherClock
{
    internal static void Align()
    {
        var dispatcher = Dispatcher.UIThread;
        dispatcher.VerifyAccess();
        const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
        var implementation = typeof(Dispatcher).GetField("_impl", fields)!.GetValue(dispatcher)!;
        var clock = implementation.GetType().GetProperty("Now")!.GetMethod!
            .CreateDelegate<Func<long>>(implementation);
        typeof(Dispatcher).GetField("_timeProvider", fields)!.SetValue(dispatcher, clock);
    }
}
