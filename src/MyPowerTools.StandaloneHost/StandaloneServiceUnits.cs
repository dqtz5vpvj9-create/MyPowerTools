using System.Runtime.CompilerServices;
using MyPowerTools.Abstractions;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.ServiceManager.Client;

namespace MyPowerTools.StandaloneHost;

/// <summary>
/// Safe, zero-IPC fallback <see cref="IServiceUnitClient"/> used when running standalone without
/// <c>MyPowerTools.ServiceManager</c>, returning clean empty/inactive snapshots rather than throwing
/// socket/pipe connection exceptions.
/// </summary>
public sealed class NoOpServiceUnitClient : IServiceUnitClient
{
    public NoOpServiceUnitClient(string toolId = "")
    {
        ToolId = toolId ?? string.Empty;
    }

    public static NoOpServiceUnitClient Instance { get; } = new();

    public string ToolId { get; }

    public ValueTask<IReadOnlyList<ServiceUnitSnapshot>> ListAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<ServiceUnitSnapshot>>(Array.Empty<ServiceUnitSnapshot>());

    public ValueTask<ServiceUnitSnapshot> GetSnapshotAsync(string unitId, CancellationToken cancellationToken = default)
        => throw new KeyNotFoundException($"Service unit '{unitId}' is not available in standalone mode (ServiceManager inactive).");

    public ValueTask<ServiceUnitSnapshot> StartAsync(string unitId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("当前处于单工具轻巧独立模式，后台服务管理器未启动。");

    public ValueTask<ServiceUnitSnapshot> StopAsync(string unitId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("当前处于单工具轻巧独立模式，后台服务管理器未启动。");

    public ValueTask<ServiceUnitSnapshot> RestartAsync(string unitId, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("当前处于单工具轻巧独立模式，后台服务管理器未启动。");

    public ValueTask ReloadAsync(CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async IAsyncEnumerable<ServiceUnitEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<MptToolLogEntry> TailLogsAsync(
        string unitId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public sealed class StandaloneServiceUnitClientFactory : IServiceUnitClientFactory, IDisposable
{
    private readonly ServiceManagerAdminClient? _liveClient;

    private StandaloneServiceUnitClientFactory(ServiceManagerAdminClient? liveClient)
    {
        _liveClient = liveClient;
    }

    public static async Task<StandaloneServiceUnitClientFactory> CreateAsync(
        string dataRoot,
        bool allowLiveAttach,
        CancellationToken cancellationToken)
    {
        if (!allowLiveAttach)
        {
            return new StandaloneServiceUnitClientFactory(null);
        }

        var tokenPath = Path.Combine(dataRoot, "state", "servicemanager.token");
        if (!File.Exists(tokenPath))
        {
            return new StandaloneServiceUnitClientFactory(null);
        }

        var endpoint = IpcEndpoint.ServiceManagerDefault(PlatformId.Current());
        if (endpoint.Transport == IpcTransport.UnixDomainSocket && !File.Exists(endpoint.Address))
        {
            return new StandaloneServiceUnitClientFactory(null);
        }

        ServiceManagerAdminClient? candidate = null;
        try
        {
            candidate = ServiceManagerAdminClient.ForEndpoint(endpoint);
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromMilliseconds(100));
            _ = await candidate.ListUnitsAsync(toolId: null, cancellationToken: probeCts.Token).ConfigureAwait(false);
            return new StandaloneServiceUnitClientFactory(candidate);
        }
        catch
        {
            candidate?.Dispose();
            return new StandaloneServiceUnitClientFactory(null);
        }
    }

    public IServiceUnitClient ForTool(string ownerToolId)
    {
        return _liveClient is not null
            ? new ScopedServiceUnitClient(_liveClient, ownerToolId)
            : new NoOpServiceUnitClient(ownerToolId);
    }

    public void Dispose()
    {
        _liveClient?.Dispose();
    }
}
