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
        var ping = await client.PingAsync(cancellationToken);
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
    private DateTimeOffset _lastRestartAttempt;

    public HostControlConnectionMonitor(
        IHostControlConnectionProbe probe,
        TimeSpan? pollInterval = null,
        TimeSpan? attemptTimeout = null,
        bool? eventDriven = null)
    {
        _probe = probe;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(2);
        _eventDriven = eventDriven ?? OperatingSystem.IsMacOS();
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
            if (_loop is not null)
            {
                return;
            }

            _stop = new CancellationTokenSource();
            _loop = RunAsync(_stop.Token);
        }
    }

    public Task<HostControlConnectionSnapshot> CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        return CheckOnceAsync(notify: true, cancellationToken);
    }

    public async Task<HostControlConnectionSnapshot> CheckOnceAsync(bool notify, CancellationToken cancellationToken = default)
    {
        await _checkGate.WaitAsync(cancellationToken);
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(_attemptTimeout);

            HostControlConnectionSnapshot snapshot;
            try
            {
                var result = await _probe.PingAsync(attempt.Token);
                lock (_stateGate)
                {
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
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var shouldRestart = false;
                lock (_stateGate)
                {
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
                            try { await restart(); }
                            catch { /* best-effort restart */ }
                        });
                    }
                }
            }

            if (notify)
            {
                StateChanged?.Invoke(this, snapshot);
            }

            return snapshot;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? stop;
        Task? loop;
        lock (_stateGate)
        {
            stop = _stop;
            loop = _loop;
            _stop = null;
            _loop = null;
        }

        if (stop is null)
        {
            return;
        }

        await stop.CancelAsync();
        if (loop is not null)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_eventDriven)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = await CheckOnceAsync(notify: true, cancellationToken);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                // Healthy connections have no timer; offline recovery retains a
                // bounded retry even if no further stream fault arrives.
                if (!snapshot.Online) wait.CancelAfter(_pollInterval);
                try { await _checkRequests.Reader.WaitToReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                while (_checkRequests.Reader.TryRead(out _)) { }
            }
            return;
        }

        await CheckOnceAsync(notify: true, cancellationToken);
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await CheckOnceAsync(notify: true, cancellationToken);
        }
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
