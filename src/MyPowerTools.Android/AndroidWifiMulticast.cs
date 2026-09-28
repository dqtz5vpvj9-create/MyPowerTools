using Android.Net.Wifi;
using MyPowerTools.Platform.Abstractions;
using A = global::Android;

namespace MyPowerTools.Android;

/// <summary>
/// The Android <see cref="IMobileWifiMulticast"/>: a reference-counted lease over
/// <c>WifiManager.MulticastLock</c>.
/// <para>
/// Receiving a LAN discovery datagram on a phone needs the multicast lock, and taking one keeps the
/// Wi-Fi radio awake. That is exactly why the lock lives here, with a hard rule: it is acquired when
/// a caller asks for a lease and released the moment the last lease is disposed. The page that opens
/// the discovery window owns a lease; a user-disabled receiver owns none; process death releases the
/// platform lock. Nothing in this type schedules, retries or re-acquires on its own.
/// </para>
/// <para>
/// The implementation is published as <see cref="MobileWifiMulticast.Current"/> by the host at
/// startup, so the discovery module (which runs in this process) can take a lease without
/// referencing the Android application assembly.
/// </para>
/// </summary>
public sealed class AndroidWifiMulticast : IMobileWifiMulticast
{
    private readonly WifiManager? _wifi;
    private readonly object _gate = new();
    private WifiManager.MulticastLock? _lock;
    private int _leases;

    internal AndroidWifiMulticast(WifiManager? wifi) => _wifi = wifi;

    /// <summary>
    /// Builds the implementation from the current application context, or <see langword="null"/> when
    /// the platform reports no Wi-Fi service. A device without Wi-Fi simply has no LAN discovery; that
    /// is a normal state, not an error to log on every start.
    /// </summary>
    internal static AndroidWifiMulticast? TryCreate()
    {
        try
        {
            var wifi = A.App.Application.Context.GetSystemService(A.Content.Context.WifiService) as WifiManager;
            return wifi is null ? null : new AndroidWifiMulticast(wifi);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True while a lease is held and the platform lock is actually held.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _leases > 0 && _lock?.IsHeld == true;
            }
        }
    }

    public IDisposable Acquire(string reason)
    {
        lock (_gate)
        {
            _leases++;
            if (_leases == 1)
            {
                // The first lease creates the platform lock with its reason; Android offers no way to
                // rename it afterwards, and the extra leases only keep it alive.
                AcquirePlatformLock(reason);
            }

            return new Lease(this);
        }
    }

    private void AcquirePlatformLock(string reason)
    {
        _ = reason;
        try
        {
            _lock = _wifi?.CreateMulticastLock("mpt-lan-discovery");
            // No reference counting by the platform: the app owns the count, and a lock that is
            // already held must not be acquired twice.
            _lock?.SetReferenceCounted(false);
            _lock?.Acquire();
        }
        catch (Exception ex)
        {
            // Discovery degrades to "no devices found"; it must never take the app down. The lease
            // still exists so the caller's using-block stays balanced.
            _lock = null;
            AndroidStartupLog.Error("wifi-multicast", ex);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (_leases == 0) return;
            _leases--;
            if (_leases > 0) return;
            try
            {
                if (_lock?.IsHeld == true) _lock.Release();
            }
            catch (Exception ex)
            {
                AndroidStartupLog.Error("wifi-multicast-release", ex);
            }
            finally
            {
                try { _lock?.Dispose(); } catch (Exception) { }
                _lock = null;
            }
        }
    }

    private sealed class Lease(AndroidWifiMulticast owner) : IDisposable
    {
        private AndroidWifiMulticast? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
