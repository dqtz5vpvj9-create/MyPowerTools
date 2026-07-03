using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia;

public interface IHostControlEventSource
{
    IAsyncEnumerable<HostProto.HostEvent> SubscribeAsync(ulong lastEventSeq, CancellationToken cancellationToken);
}

public sealed class HostControlClientEventSource : IHostControlEventSource
{
    public async IAsyncEnumerable<HostProto.HostEvent> SubscribeAsync(
        ulong lastEventSeq,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var client = MyPowerTools.HostControl.HostControlClient.ForDefaultEndpoint();
        await foreach (var evt in client.SubscribeHostEventsAsync(lastEventSeq, cancellationToken))
        {
            yield return evt;
        }
    }
}

public sealed class HostControlEventStreamMonitor : IAsyncDisposable
{
    private readonly IHostControlEventSource _source;
    private readonly TimeSpan _reconnectDelay;
    private readonly object _gate = new();
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public HostControlEventStreamMonitor(IHostControlEventSource source, TimeSpan? reconnectDelay = null)
    {
        _source = source;
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(2);
    }

    public event EventHandler<HostProto.HostEvent>? EventReceived;
    public event EventHandler<Exception>? StreamFaulted;

    public ulong LastEventSeq { get; private set; }

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
                    if (evt.Seq <= LastEventSeq)
                    {
                        continue;
                    }

                    LastEventSeq = evt.Seq;
                    EventReceived?.Invoke(this, evt);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StreamFaulted?.Invoke(this, ex);
                await Task.Delay(_reconnectDelay, cancellationToken);
            }
        }
    }
}
