using MyPowerTools.Abstractions;
using MyPowerTools.Shell.Avalonia.ViewModels;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellRunnerEventService : IAsyncDisposable
{
    private readonly HostControlConnectionMonitor _connectionMonitor;
    private readonly HostControlEventStreamMonitor _eventStream;
    private readonly EventHandler<HostControlConnectionSnapshot> _connectionStateChangedHandler;
    private readonly EventHandler<HostProto.HostEvent> _eventReceivedHandler;
    private readonly EventHandler<Exception> _streamFaultedHandler;
    private int _disposed;

    public ShellRunnerEventService()
        : this(
            new HostControlConnectionMonitor(new HostControlRunnerConnectionProbe()),
            new HostControlEventStreamMonitor(new HostControlClientEventSource()))
    {
    }

    internal ShellRunnerEventService(
        HostControlConnectionMonitor connectionMonitor,
        HostControlEventStreamMonitor eventStream)
    {
        _connectionMonitor = connectionMonitor;
        _eventStream = eventStream;
        _connectionStateChangedHandler = OnConnectionStateChanged;
        _eventReceivedHandler = OnEventReceived;
        _streamFaultedHandler = OnStreamFaulted;
        _connectionMonitor.StateChanged += _connectionStateChangedHandler;
        _eventStream.EventReceived += _eventReceivedHandler;
        _eventStream.StreamFaulted += _streamFaultedHandler;
    }

    public event Action<string>? StatusChanged;
    public event Action<string>? RunnerStatusChanged;
    public event Action? RunnerRecovered;
    public event Action<HostProto.HostEvent>? HostEventReceived;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _connectionMonitor.Start();
        _eventStream.Start();
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var snapshot = await _connectionMonitor.CheckOnceAsync(notify: false, cancellationToken);
        ApplyConnectionSnapshot(snapshot, refreshOnRecovery: false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connectionMonitor.StateChanged -= _connectionStateChangedHandler;
        _eventStream.EventReceived -= _eventReceivedHandler;
        _eventStream.StreamFaulted -= _streamFaultedHandler;
        try
        {
            await _eventStream.DisposeAsync();
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Dispose host event stream", ex, "dispose");
        }

        try
        {
            await _connectionMonitor.DisposeAsync();
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Dispose HostControl connection monitor", ex, "dispose");
        }
    }

    private void OnConnectionStateChanged(object? sender, HostControlConnectionSnapshot snapshot)
    {
        ApplyConnectionSnapshot(snapshot, refreshOnRecovery: true);
    }

    private void OnEventReceived(object? sender, HostProto.HostEvent evt)
    {
        ApplyHostEvent(evt);
    }

    private void OnStreamFaulted(object? sender, Exception exception)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Publish(
                StatusChanged,
                $"Host event stream reconnecting: {SafeMessage(exception)}",
                "Runner stream fault subscriber");
        }
    }

    private void ApplyConnectionSnapshot(HostControlConnectionSnapshot snapshot, bool refreshOnRecovery)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Publish(
            RunnerStatusChanged,
            snapshot.Online ? $"Runner {snapshot.State}" : "Runner offline",
            "Runner status subscriber");
        if (!snapshot.Online)
        {
            Publish(StatusChanged, $"Runner offline: {SafeText(snapshot.Message)}", "Runner offline subscriber");
            return;
        }

        if (snapshot.Recovered && refreshOnRecovery)
        {
            Publish(StatusChanged, "Runner connection restored.", "Runner recovery status subscriber");
            Publish(RunnerRecovered, "Runner recovery subscriber");
        }
    }

    private void ApplyHostEvent(HostProto.HostEvent evt)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Publish(StatusChanged, $"Event {evt.Seq}: {evt.Type}", "Host event status subscriber");
        Publish(HostEventReceived, evt, "Host event subscriber");
    }

    private static void Publish<T>(Action<T>? handlers, T value, string operation)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception ex)
            {
                ShellCommandFaultLog.Write(operation, ex, "subscriber");
            }
        }
    }

    private static void Publish(Action? handlers, string operation)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                ShellCommandFaultLog.Write(operation, ex, "subscriber");
            }
        }
    }

    private static string SafeMessage(Exception exception)
    {
        if (ShellFailurePresenter.IsRpcFailure(exception))
        {
            return ShellFailurePresenter.Present(exception).StatusMessage;
        }

        return SafeText(exception.Message);
    }

    private static string SafeText(string value)
    {
        var message = MptLogRedactor.Redact(value).Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 512 ? message : message[..512];
    }
}
