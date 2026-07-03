using MyPowerTools.HostControl;

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
    private readonly IHostControlConnectionProbe _probe;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _attemptTimeout;
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private int _consecutiveFailures;
    private bool _wasOnline;

    public HostControlConnectionMonitor(
        IHostControlConnectionProbe probe,
        TimeSpan? pollInterval = null,
        TimeSpan? attemptTimeout = null)
    {
        _probe = probe;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(2);
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

    public HostControlConnectionSnapshot LastSnapshot { get; private set; }

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
        await CheckOnceAsync(notify: true, cancellationToken);
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await CheckOnceAsync(notify: true, cancellationToken);
        }
    }

    private static string FriendlyMessage(Exception ex)
    {
        return ex is OperationCanceledException
            ? "Runner HostControl connection timed out."
            : ex.Message;
    }
}
