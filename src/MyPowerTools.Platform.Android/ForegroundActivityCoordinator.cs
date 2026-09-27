namespace MyPowerTools.Platform.Android;

/// <summary>
/// Bookkeeping for the background activities that keep the Android foreground service alive.
/// Deliberately free of Android types so the lifecycle rules (parallel activities, per-module stop,
/// idempotent lease release, race-free service start/stop) can be unit tested without a device.
/// </summary>
public sealed class ForegroundActivityCoordinator
{
    /// <summary>One active lease. <see cref="ModuleId"/> is what a notification stop action targets.</summary>
    public sealed record Activity(Guid Id, string ModuleId, string Title, bool WaitingForPeers);

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Activity> _activities = [];
    private readonly Action _startOrRefreshService;
    private readonly Action _stopService;

    public ForegroundActivityCoordinator(Action startOrRefreshService, Action stopService)
    {
        _startOrRefreshService = startOrRefreshService ?? throw new ArgumentNullException(nameof(startOrRefreshService));
        _stopService = stopService ?? throw new ArgumentNullException(nameof(stopService));
    }

    /// <summary>
    /// Registers an activity and (re)starts the foreground service so the notification lists it.
    /// The service call runs inside the same critical section as the bookkeeping, so a concurrent
    /// release can never stop a service that a new activity has just started.
    /// </summary>
    public Activity Add(string moduleId, string title, bool waitingForPeers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        lock (_gate)
        {
            var activity = new Activity(Guid.NewGuid(), moduleId, string.IsNullOrWhiteSpace(title) ? moduleId : title, waitingForPeers);
            _activities.Add(activity.Id, activity);
            try
            {
                _startOrRefreshService();
            }
            catch
            {
                _activities.Remove(activity.Id);
                if (_activities.Count == 0)
                {
                    // A rejected start must not leave a notification behind without a lease.
                    _stopService();
                }

                throw;
            }

            return activity;
        }
    }

    /// <summary>Releases one lease. Returns <c>true</c> only for the first release of that lease.</summary>
    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            if (!_activities.Remove(id))
            {
                return false;
            }

            if (_activities.Count == 0)
            {
                _stopService();
            }

            return true;
        }
    }

    /// <summary>
    /// Stops only the leases owned by <paramref name="moduleId"/> and returns the module ids that
    /// actually had work. Other modules keep their leases and the service stays in the foreground.
    /// The lease objects held by the stopped module become no-ops, so the module can release them
    /// later while it unwinds its own work.
    /// </summary>
    public IReadOnlyList<string> RemoveModule(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        lock (_gate)
        {
            var stopped = _activities.Values.Where(activity => activity.ModuleId == moduleId).ToArray();
            foreach (var activity in stopped)
            {
                _activities.Remove(activity.Id);
            }

            if (stopped.Length > 0 && _activities.Count == 0)
            {
                _stopService();
            }

            return stopped.Select(activity => activity.ModuleId).Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Stops every lease; used when the system ends the foreground service.</summary>
    public IReadOnlyList<string> RemoveAll()
    {
        lock (_gate)
        {
            var stopped = _activities.Values.Select(activity => activity.ModuleId).Distinct(StringComparer.Ordinal).ToArray();
            if (_activities.Count == 0)
            {
                return stopped;
            }

            _activities.Clear();
            _stopService();
            return stopped;
        }
    }

    public IReadOnlyList<Activity> Snapshot()
    {
        lock (_gate)
        {
            return [.. _activities.Values];
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _activities.Count;
            }
        }
    }

    /// <summary>Waiting for peers is the long-lived case, so it keeps the connected-device service type.</summary>
    public bool RequiresConnectedDeviceType
    {
        get
        {
            lock (_gate)
            {
                return _activities.Values.Any(activity => activity.WaitingForPeers);
            }
        }
    }
}
