using Android.Content;
using Android.Net;
using Android.OS;
using A = global::Android;

namespace MyPowerTools.Android;

/// <summary>Event-driven recovery, including Doze exit while the Activity stays in the background.</summary>
internal static class AndroidTransferRecovery
{
    private static NetworkCallback? _network;
    private static IdleReceiver? _idle;
    internal static RecoveryEvents Events { get; } = new();

    // Use a host capability expressed through BCL interfaces: the plugin's collectible
    // load context has its own FileTransfer.Core, so a static event there is not shared.
    internal sealed class RecoveryEvents : IObservable<bool>
    {
        private event Action? Available;
        public IDisposable Subscribe(IObserver<bool> observer)
        {
            Action callback = () => observer.OnNext(true);
            Available += callback;
            return new Subscription(() => Available -= callback);
        }
        internal void Notify() => Available?.Invoke();
        private sealed class Subscription(Action release) : IDisposable
        {
            private Action? _release = release;
            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    internal static void Install()
    {
        if (_network is not null) return;
        var context = A.App.Application.Context;
        var connectivity = (ConnectivityManager)context.GetSystemService(Context.ConnectivityService)!;
        _network = new NetworkCallback();
        connectivity.RegisterDefaultNetworkCallback(_network);
        _idle = new IdleReceiver();
        var filter = new IntentFilter(PowerManager.ActionDeviceIdleModeChanged);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            context.RegisterReceiver(_idle, filter, ReceiverFlags.NotExported);
        else context.RegisterReceiver(_idle, filter);
    }

    private sealed class NetworkCallback : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => Events.Notify();
    }

    private sealed class IdleReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == PowerManager.ActionDeviceIdleModeChanged &&
                context?.GetSystemService(Context.PowerService) is PowerManager power && !power.IsDeviceIdleMode)
                Events.Notify();
        }
    }
}
