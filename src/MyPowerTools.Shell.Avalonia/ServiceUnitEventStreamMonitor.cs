using MyPowerTools.ServiceManager.Client;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

namespace MyPowerTools.Shell.Avalonia;

/// <summary>
/// Long-lived subscriber for ServiceManager unit events. Keeps <see cref="LastEventSeq"/>
/// across transient disconnects and replays from the last seen sequence number when reconnecting.
/// Mirrors <see cref="HostControlEventStreamMonitor"/>.
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
        await foreach (var evt in client.SubscribeUnitEventsAsync(lastEventSeq, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }
}

public sealed class ServiceUnitEventStreamMonitor : IAsyncDisposable
{
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

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
            var stopToken = _stop.Token;
            // Run on the thread pool so continuations never capture the Avalonia UI SynchronizationContext.
            _loop = Task.Run(() => RunAsync(stopToken), stopToken);
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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var maxDelay = _reconnectDelay > MaxReconnectDelay ? _reconnectDelay : MaxReconnectDelay;
        var currentDelay = _reconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var evt in _source.SubscribeAsync(LastEventSeq, cancellationToken).ConfigureAwait(false))
                {
                    currentDelay = _reconnectDelay;
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

                // Treat a clean stream EOF as a disconnect so an empty/closed stream never spins at 100% CPU.
                if (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("ServiceManager unit event stream closed.");
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
                    await Task.Delay(currentDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var nextTicks = currentDelay.Ticks > maxDelay.Ticks / 2
                    ? maxDelay.Ticks
                    : currentDelay.Ticks * 2;
                currentDelay = TimeSpan.FromTicks(nextTicks);
            }
        }
    }
}
