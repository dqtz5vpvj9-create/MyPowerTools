namespace MyPowerTools.Platform.Abstractions;

/// <summary>
/// A time-boxed lease on the Wi-Fi multicast lock that the platform needs before it can receive
/// LAN discovery datagrams.
/// <para>
/// Discovery is a page-scoped activity, not a background service, so the lock is only ever held
/// while something is actually listening: the device-discovery window is open, or the user enabled
/// receiving and it is running. Disposing the lease releases it, and a lease that is never disposed
/// is released when the page or the app goes away. There is no "hold it forever" mode here, because
/// a permanently held multicast lock is a permanent wake-up source, and an app must not turn one on
/// as a side effect of being installed.
/// </para>
/// </summary>
public interface IMobileWifiMulticast
{
    /// <summary>
    /// Takes one lease. Dispose it to release. Overlapping leases are reference counted, so two
    /// openers cannot release each other's lock.
    /// </summary>
    IDisposable Acquire(string reason);
}

/// <summary>
/// Where the Android host publishes its <see cref="IMobileWifiMulticast"/> implementation.
/// <para>
/// The Android host assigns <see cref="Current"/> at startup. A module that runs in the same app
/// process (LAN discovery) reads it here instead of referencing the Android application assembly,
/// which keeps the module portable and the wiring a deliberate, single place. It is
/// <see langword="null"/> on every other platform, so callers must treat "no lock available" as a
/// supported state and report discovery as unavailable rather than silently failing to hear
/// anything.
/// </para>
/// </summary>
public static class MobileWifiMulticast
{
    private static IMobileWifiMulticast? _current;

    public static IMobileWifiMulticast? Current
    {
        get => _current;
        set => _current = value;
    }

    /// <summary>
    /// Takes a lease when the platform has one, otherwise returns a no-op lease. Callers still use
    /// <c>using</c>, so the call site does not need a platform check.
    /// </summary>
    public static IDisposable AcquireIfAvailable(string reason) =>
        _current?.Acquire(reason) ?? NoLease.Instance;

    private sealed class NoLease : IDisposable
    {
        internal static readonly NoLease Instance = new();

        public void Dispose()
        {
        }
    }
}
