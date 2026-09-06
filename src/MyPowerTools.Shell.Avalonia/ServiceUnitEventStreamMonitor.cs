using MyPowerTools.ServiceManager.Client;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

namespace MyPowerTools.Shell.Avalonia;

/// <summary>
/// Subscribes to the ServiceManager's <c>SubscribeUnitEvents</c> stream so the unified Services
/// page and tool Surfaces update reactively when a unit changes state — no polling, no manual
/// refresh. Mirrors <see cref="HostControlEventStreamMonitor"/>: a self-reconnecting loop that
/// dedupes by <c>seq</c> and advances the cursor across reconnects.
/// </summary>
public interface IServiceUnitEventSource
{
    IAsyncEnumerable<SM.UnitEvent> SubscribeAsync(ulong lastEventSeq, CancellationToken cancellationToken);
}

public sealed class ServiceManagerUnitEventSource : IServiceUnitEventSource
{
    public async IAsyncEnumerable<SM.UnitEvent> SubscribeAsync(
        ulong lastEventSeq,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = ServiceManagerAdminClient.ForDefaultEndpoint();
        await foreach (var evt in client.SubscribeUnitEventsAsync(lastEventSeq, cancellationToken: cancellationToken))
        {
            yield return evt;
        }
    }
}

public sealed class ServiceUnitEventStreamMonitor : IAsyncDisposable
{
    private readonly IServiceUnitEventSource _source;
    private readonly TimeSpan _reconnectDelay;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public ServiceUnitEventStreamMonitor(IServiceUnitEventSource source, TimeSpan? reconnectDelay = null)
    {
        _source = source;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Raised on the background loop thread for every new unit event.</summary>
    public event EventHandler<SM.UnitEvent>? UnitEventReceived;

    /// <summary>Raised when the stream faults (e.g. ServiceManager down); the loop reconnects after a delay.</summary>
    public event EventHandler<Exception>? StreamFaulted;

    /// <summary>Raised when the stream reconnects after a fault.</summary>
    public event EventHandler? StreamRecovered;

    public ulong LastEventSeq { get; private set; }

    public bool IsFaulted { get; private set; }

    public void Start(ulong lastEventSeq = 0)
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            LastEventSeq = lastEventSeq;
            _stop = new CancellationTokenSource();
            _loop = RunAsync(_stop.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? stop;
        Task? loop;
        lock (_gate)
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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var evt in _source.SubscribeAsync(LastEventSeq, cancellationToken))
                {
                    if (IsFaulted)
                    {
                        IsFaulted = false;
                        StreamRecovered?.Invoke(this, EventArgs.Empty);
                    }

                    if (evt.Seq <= LastEventSeq)
                    {
                        continue;
                    }

                    LastEventSeq = evt.Seq;
                    UnitEventReceived?.Invoke(this, evt);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                IsFaulted = true;
                StreamFaulted?.Invoke(this, ex);
                try
                {
                    await Task.Delay(_reconnectDelay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
