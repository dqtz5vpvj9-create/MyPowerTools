using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellRunnerEventService : IAsyncDisposable
{
    private readonly HostControlConnectionMonitor _connectionMonitor;
    private readonly HostControlEventStreamMonitor _eventStream;

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
        _connectionMonitor.StateChanged += (_, snapshot) => ApplyConnectionSnapshot(snapshot, refreshOnRecovery: true);
        _eventStream.EventReceived += (_, evt) => ApplyHostEvent(evt);
        _eventStream.StreamFaulted += (_, ex) => StatusChanged?.Invoke($"Host event stream reconnecting: {ex.Message}");
    }

    public event Action<string>? StatusChanged;
    public event Action<string>? RunnerStatusChanged;
    public event Action? RunnerRecovered;
    public event Action<HostProto.HostEvent>? HostEventReceived;

    public void Start()
    {
        _connectionMonitor.Start();
        _eventStream.Start();
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _connectionMonitor.CheckOnceAsync(notify: false, cancellationToken);
        ApplyConnectionSnapshot(snapshot, refreshOnRecovery: false);
    }

    public async ValueTask DisposeAsync()
    {
        await _eventStream.DisposeAsync();
        await _connectionMonitor.DisposeAsync();
    }

    private void ApplyConnectionSnapshot(HostControlConnectionSnapshot snapshot, bool refreshOnRecovery)
    {
        RunnerStatusChanged?.Invoke(snapshot.Online ? $"Runner {snapshot.State}" : "Runner offline");
        if (!snapshot.Online)
        {
            StatusChanged?.Invoke($"Runner offline: {snapshot.Message}");
            return;
        }

        if (snapshot.Recovered && refreshOnRecovery)
        {
            StatusChanged?.Invoke("Runner connection restored.");
            RunnerRecovered?.Invoke();
        }
    }

    private void ApplyHostEvent(HostProto.HostEvent evt)
    {
        StatusChanged?.Invoke($"Event {evt.Seq}: {evt.Type}");
        HostEventReceived?.Invoke(evt);
    }
}
