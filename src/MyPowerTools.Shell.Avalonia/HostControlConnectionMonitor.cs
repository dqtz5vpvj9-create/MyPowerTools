using MyPowerTools.HostControl;
using MyPowerTools.Shell.Avalonia.Services;
using System.Threading.Channels;

namespace MyPowerTools.Shell.Avalonia;

public interface IHostControlConnectionProbe
{
    Task<HostControlConnectionProbeResult> PingAsync(CancellationToken cancellationToken);
}

public sealed record HostControlConnectionProbeResult(string RunnerVersion, string State);

public sealed record HostControlConnectionSnapshot(
    bool Online,
    string State,
    string RunnerVersion,
    string Message,
    int ConsecutiveFailures,
    bool Recovered,
    DateTimeOffset CheckedAt);

public sealed class HostControlRunnerConnectionProbe : IHostControlConnectionProbe
{
    public async Task<HostControlConnectionProbeResult> PingAsync(CancellationToken cancellationToken)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var ping = await client.PingAsync(cancellationToken).ConfigureAwait(false);
        return new HostControlConnectionProbeResult(ping.RunnerVersion, ping.State);
    }
}

public sealed class HostControlConnectionMonitor : IAsyncDisposable
{
    private const int RestartThreshold = 3;
    private static readonly TimeSpan RestartCooldown = TimeSpan.FromSeconds(30);

    private readonly IHostControlConnectionProbe _probe;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _attemptTimeout;
    private readonly bool _eventDriven;
    private readonly Channel<byte> _checkRequests = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private int _consecutiveFailures;
    private bool _wasOnline;
    private bool _hasReportedState;
    private DateTimeOffset _lastRestartAttempt;
    private int _disposed;
    private int _checkGateUsers = 1;

    public HostControlConnectionMonitor(
        IHostControlConnectionProbe probe,
        TimeSpan? pollInterval = null,
        TimeSpan? attemptTimeout = null,
        bool? eventDriven = null)
    {
        _probe = probe;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(2);
        // Default to event-driven mode across all platforms so a healthy connection relies on
        // the persistent host event stream instead of polling gRPC every 5 seconds.
        _eventDriven = eventDriven ?? true;
        LastSnapshot = new HostControlConnectionSnapshot(
            false,
            "unknown",
            "",
            "HostControl connection has not been checked.",
            0,
            false,
            DateTimeOffset.UtcNow);
    }

    public event EventHandler<HostControlConnectionSnapshot>? StateChanged;

    /// <summary>
    /// Optional callback invoked when the Runner appears down for
    /// <see cref="RestartThreshold"/> consecutive probes. Respects a
    /// <see cref="RestartCooldown"/> between attempts.
    /// </summary>
    public Func<Task>? RestartRunner { get; set; }

    public HostControlConnectionSnapshot LastSnapshot { get; private set; }

    // The persistent host event stream detects a lost Runner without a parallel
    // heartbeat. Coalesce faults so a burst cannot queue unbounded probes.
    public void RequestCheck() => _checkRequests.Writer.TryWrite(0);

    public void Start()
    {
        lock (_stateGate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _loop is not null)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            var stopToken = _stop.Token;
            // Run off the UI SynchronizationContext so probe continuations never contend with Avalonia input.
            _loop = Task.Run(() => RunAsync(stopToken), stopToken);
        }
    }

    public Task<HostControlConnectionSnapshot> CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        return CheckOnceAsync(notify: true, cancellationToken);
    }

    public async Task<HostControlConnectionSnapshot> CheckOnceAsync(bool notify, CancellationToken cancellationToken = default)
    {
        if (!TryAcquireCheckGateRef())
        {
            return LastSnapshot;
        }

        var acquired = false;
        try
        {
            await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(_attemptTimeout);

            HostControlConnectionSnapshot snapshot;
            bool shouldNotify;
            try
            {
                var result = await _probe.PingAsync(attempt.Token).ConfigureAwait(false);
                lock (_stateGate)
                {
                    var previous = LastSnapshot;
                    var recovered = !_wasOnline && _consecutiveFailures > 0;
                    _consecutiveFailures = 0;
                    _wasOnline = true;
                    snapshot = new HostControlConnectionSnapshot(
                        true,
                        string.IsNullOrWhiteSpace(result.State) ? "running" : result.State,
                        result.RunnerVersion,
                        recovered ? "Runner HostControl connection restored." : "Runner HostControl connection healthy.",
                        0,
                        recovered,
                        DateTimeOffset.UtcNow);
                    LastSnapshot = snapshot;
                    shouldNotify = notify && HasStateChanged(previous, snapshot, _hasReportedState);
                    if (shouldNotify)
                    {
                        _hasReportedState = true;
                    }
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var shouldRestart = false;
                lock (_stateGate)
                {
                    var previous = LastSnapshot;
                    _consecutiveFailures++;
                    _wasOnline = false;
                    snapshot = new HostControlConnectionSnapshot(
                        false,
                        "offline",
                        "",
                        FriendlyMessage(ex),
                        _consecutiveFailures,
                        false,
                        DateTimeOffset.UtcNow);
                    LastSnapshot = snapshot;
                    shouldNotify = notify && HasStateChanged(previous, snapshot, _hasReportedState);
                    if (shouldNotify)
                    {
                        _hasReportedState = true;
                    }

                    if (_consecutiveFailures >= RestartThreshold && RestartRunner is not null)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (now - _lastRestartAttempt >= RestartCooldown)
                        {
                            _lastRestartAttempt = now;
                            shouldRestart = true;
                        }
                    }
                }

                if (shouldRestart)
                {
                    var restart = RestartRunner;
                    if (restart is not null)
                    {
                        _ = Task.Run(async () =>
                        {
                            try { await restart().ConfigureAwait(false); }
                            catch { /* best-effort restart */ }
                        });
                    }
                }
            }

            if (shouldNotify)
            {
                StateChanged?.Invoke(this, snapshot);
            }

            return snapshot;
        }
        finally
        {
            if (acquired)
            {
                _checkGate.Release();
            }

            ReleaseCheckGateRef();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? stop;
        Task? loop;
        lock (_stateGate)
        {
            stop = _stop;
            loop = _loop;
            _stop = null;
            _loop = null;
        }

        if (stop is not null)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            stop.Dispose();
        }

        ReleaseCheckGateRef();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_eventDriven)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = await CheckOnceAsync(notify: true, cancellationToken).ConfigureAwait(false);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Healthy connections have no timer; offline recovery retains a
                // bounded retry even if no further stream fault arrives.
                if (!snapshot.Online)
                {
                    wait.CancelAfter(_pollInterval);
                }

                try
                {
                    await _checkRequests.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                while (_checkRequests.Reader.TryRead(out _))
                {
                }
            }

            return;
        }

        await CheckOnceAsync(notify: true, cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await CheckOnceAsync(notify: true, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryAcquireCheckGateRef()
    {
        while (true)
        {
            var current = Volatile.Read(ref _checkGateUsers);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _checkGateUsers, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseCheckGateRef()
    {
        if (Interlocked.Decrement(ref _checkGateUsers) == 0)
        {
            _checkGate.Dispose();
        }
    }

    private static bool HasStateChanged(
        HostControlConnectionSnapshot previous,
        HostControlConnectionSnapshot current,
        bool hasReportedState)
    {
        if (!hasReportedState)
        {
            return true;
        }

        if (previous.Online != current.Online ||
            current.Recovered ||
            !string.Equals(previous.State, current.State, StringComparison.Ordinal) ||
            !string.Equals(previous.RunnerVersion, current.RunnerVersion, StringComparison.Ordinal))
        {
            return true;
        }

        return !current.Online &&
               !string.Equals(previous.Message, current.Message, StringComparison.Ordinal);
    }

    private static string FriendlyMessage(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return "Runner HostControl connection timed out.";
        }

        return ShellFailurePresenter.IsRpcFailure(ex)
            ? ShellFailurePresenter.Present(ex).StatusMessage
            : ex.Message;
    }
}
