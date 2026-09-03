using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MyPowerTools.Abstractions;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

namespace MyPowerTools.ServiceManager.Client;

/// <summary>
/// Cross-tool administration client for the ServiceManager process. Used by the unified
/// <c>System &gt; Services</c> page. Tool Surfaces do NOT use this; they receive a scoped
/// <see cref="IServiceUnitClient"/> injected by the host, which carries only their tool's
/// identity and never the raw admin token.
/// </summary>
public sealed class ServiceManagerAdminClient : IDisposable
{
    /// <summary>Auth header for the ServiceManager process (distinct from HostControl's).</summary>
    public const string AuthHeaderName = "x-mpt-servicemanager-token";

    /// <summary>Caller-tool header injected by scoped clients so the server enforces scope.</summary>
    public const string CallerToolHeader = "x-mpt-caller-tool";

    /// <summary>Env var overriding the data root for ServiceManager token discovery.</summary>
    public const string DataRootEnvironmentVariable = "MPT_DATA_ROOT";

    /// <summary>Optional endpoint address override used by isolated installs and test instances.</summary>
    public const string EndpointEnvironmentVariable = "MPT_SERVICEMANAGER_ENDPOINT";

    private static readonly AuthTokenStore TokenStore =
        new(DataRootEnvironmentVariable, Path.Combine("state", "servicemanager.token"));

    private readonly GrpcChannel _channel;
    private readonly SM.ServiceManager.ServiceManagerClient _client;
    private readonly string? _scopeToolId;
    private readonly string? _authToken;

    private ServiceManagerAdminClient(GrpcChannel channel, string? authToken, string? scopeToolId = null)
    {
        _channel = channel;
        _authToken = authToken;
        _scopeToolId = scopeToolId;
        var invoker = IpcChannelFactory.AuthenticatedInvoker(channel, AuthHeaderName, authToken);
        if (!string.IsNullOrEmpty(scopeToolId))
        {
            invoker = invoker.Intercept(new CallerToolHeaderInterceptor(CallerToolHeader, scopeToolId));
        }

        _client = new SM.ServiceManager.ServiceManagerClient(invoker);
    }

    public static ServiceManagerAdminClient ForDefaultEndpoint()
    {
        var address = Environment.GetEnvironmentVariable(EndpointEnvironmentVariable);
        var endpoint = string.IsNullOrWhiteSpace(address)
            ? IpcEndpoint.ServiceManagerDefault(PlatformId.Current())
            : new IpcEndpoint(
                OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket,
                address);
        return ForEndpoint(endpoint);
    }

    public static ServiceManagerAdminClient ForEndpoint(IpcEndpoint endpoint)
        => ForEndpoint(endpoint, TokenStore.TryReadToken());

    public static ServiceManagerAdminClient ForEndpoint(IpcEndpoint endpoint, string? authToken)
    {
        var channel = IpcChannelFactory.ForEndpoint(endpoint);
        return new ServiceManagerAdminClient(channel, authToken);
    }

    /// <summary>
    /// Returns a new client sharing this client's channel and auth token but injecting the caller-tool
    /// header so the server enforces scope. Used to build scoped <see cref="IServiceUnitClient"/>s.
    /// The scoped client carries the same auth material (required to reach the server) but the
    /// caller-tool header constrains visibility to the owning tool's units.
    /// </summary>
    public ServiceManagerAdminClient WithScope(string toolId)
    {
        return new ServiceManagerAdminClient(_channel, _authToken, scopeToolId: toolId);
    }

    /// <summary>Token store shared with the ServiceManager host so both sides read the same material.</summary>
    public static AuthTokenStore SharedTokenStore => TokenStore;

    private static DateTime DefaultDeadline(int timeoutSeconds = 15) => DateTime.UtcNow.AddSeconds(timeoutSeconds);

    public async Task<SM.ListUnitsResponse> ListUnitsAsync(string? toolId = null, SM.UnitState stateFilter = SM.UnitState.Unspecified, CancellationToken cancellationToken = default)
    {
        return await _client.ListUnitsAsync(new SM.ListUnitsRequest
        {
            ToolId = toolId ?? "",
            State = stateFilter
        }, deadline: DefaultDeadline(), cancellationToken: cancellationToken);
    }

    public async Task<SM.UnitSnapshot> GetUnitAsync(string unitId, CancellationToken cancellationToken = default)
    {
        return await _client.GetUnitAsync(new SM.GetUnitRequest { UnitId = unitId }, deadline: DefaultDeadline(), cancellationToken: cancellationToken);
    }

    public async Task<SM.UnitSnapshot> StartAsync(string unitId, CancellationToken cancellationToken = default)
    {
        return await _client.StartAsync(new SM.UnitOpRequest { UnitId = unitId }, deadline: DefaultDeadline(), cancellationToken: cancellationToken);
    }

    public async Task<SM.UnitSnapshot> StopAsync(string unitId, CancellationToken cancellationToken = default)
    {
        return await _client.StopAsync(new SM.UnitOpRequest { UnitId = unitId }, deadline: DefaultDeadline(), cancellationToken: cancellationToken);
    }

    public async Task<SM.UnitSnapshot> RestartAsync(string unitId, CancellationToken cancellationToken = default)
    {
        return await _client.RestartAsync(new SM.UnitOpRequest { UnitId = unitId }, deadline: DefaultDeadline(), cancellationToken: cancellationToken);
    }

    public async Task<SM.ReloadResponse> ReloadAsync(CancellationToken cancellationToken = default)
    {
        return await _client.ReloadAsync(new SM.ReloadRequest(), deadline: DefaultDeadline(), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Requests a graceful ServiceManager shutdown. Units are left running for re-adoption.
    /// </summary>
    public async Task<bool> ShutdownAsync(CancellationToken cancellationToken = default)
    {
        var resp = await _client.ShutdownAsync(new SM.ShutdownRequest(), deadline: DefaultDeadline(), cancellationToken: cancellationToken);
        return resp.Ok;
    }

    public async Task<IReadOnlyList<SM.LogEntry>> TailLogsAsync(string unitId, uint tailLines = 200, CancellationToken cancellationToken = default)
    {
        using var call = _client.TailLogs(new SM.TailLogsRequest { UnitId = unitId, TailLines = tailLines }, cancellationToken: cancellationToken);
        var entries = new List<SM.LogEntry>();
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            entries.Add(call.ResponseStream.Current);
        }

        return entries;
    }

    public async IAsyncEnumerable<SM.UnitEvent> SubscribeUnitEventsAsync(ulong lastEventSeq, string? unitId = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.SubscribeUnitEvents(new SM.SubscribeUnitEventsRequest
        {
            LastEventSeq = lastEventSeq,
            UnitId = unitId ?? ""
        }, cancellationToken: cancellationToken);

        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public void Dispose() => _channel.Dispose();
}

/// <summary>
/// Client interceptor injecting the caller-tool header on every call so the ServiceManager
/// server can enforce scope (a scoped client may only see/control its own tool's units).
/// </summary>
internal sealed class CallerToolHeaderInterceptor : Interceptor
{
    private readonly string _headerName;
    private readonly string _toolId;

    public CallerToolHeaderInterceptor(string headerName, string toolId)
    {
        _headerName = headerName;
        _toolId = toolId;
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, WithHeader(context));
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        return continuation(request, WithHeader(context));
    }

    private ClientInterceptorContext<TRequest, TResponse> WithHeader<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context)
        where TRequest : class
        where TResponse : class
    {
        var headers = new Metadata();
        if (context.Options.Headers is not null)
        {
            foreach (var entry in context.Options.Headers)
            {
                headers.Add(entry);
            }
        }

        headers.Add(_headerName, _toolId);
        return new ClientInterceptorContext<TRequest, TResponse>(
            context.Method,
            context.Host,
            context.Options.WithHeaders(headers));
    }
}
