using System.Runtime.CompilerServices;
using System.Net.Sockets;
using System.IO.Pipes;
using Google.Protobuf.WellKnownTypes;
using Grpc.Net.Client;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.HostControl;

public sealed class HostControlClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly HostProto.HostControl.HostControlClient _client;

    private HostControlClient(GrpcChannel channel)
    {
        _channel = channel;
        _client = new HostProto.HostControl.HostControlClient(channel);
    }

    public static HostControlClient ForDefaultEndpoint()
    {
        return ForEndpoint(IpcEndpoint.RunnerDefault(PlatformId.Current()));
    }

    public static HostControlClient ForEndpoint(IpcEndpoint endpoint)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, cancellationToken) =>
            {
                if (endpoint.Transport == IpcTransport.NamedPipe)
                {
                    var stream = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await stream.ConnectAsync(cancellationToken);
                    return stream;
                }

                if (endpoint.Transport == IpcTransport.UnixDomainSocket)
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }

                throw new NotSupportedException($"Unsupported IPC transport: {endpoint.Transport}");
            }
        };

        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });

        return new HostControlClient(channel);
    }

    public async Task<HostProto.PingResponse> PingAsync(CancellationToken cancellationToken = default)
    {
        return await _client.PingAsync(new HostProto.PingRequest { ClientId = Environment.MachineName }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.DashboardSnapshot> GetDashboardSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await _client.GetDashboardSnapshotAsync(new HostProto.DashboardSnapshotRequest { Locale = "" }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.ListModulesResponse> ListModulesAsync(CancellationToken cancellationToken = default)
    {
        return await _client.ListModulesAsync(new HostProto.ListModulesRequest { IncludeDisabled = true }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.ListPackagesResponse> ListPackagesAsync(CancellationToken cancellationToken = default)
    {
        return await _client.ListPackagesAsync(new HostProto.ListPackagesRequest { IncludeDisabled = true }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.PackageOperationResult> InstallPackageAsync(string sourceDirectory, CancellationToken cancellationToken = default)
    {
        return await _client.InstallPackageAsync(new HostProto.InstallPackageRequest { SourceDirectory = sourceDirectory }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.PackageOperationResult> RepairPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        return await _client.RepairPackageAsync(new HostProto.PackageOperationRequest { PackageId = packageId }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.PackageOperationResult> UninstallPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        return await _client.UninstallPackageAsync(new HostProto.PackageOperationRequest { PackageId = packageId }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.PackageOperationResult> RollbackPackageAsync(string packageId, CancellationToken cancellationToken = default)
    {
        return await _client.RollbackPackageAsync(new HostProto.PackageOperationRequest { PackageId = packageId }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.RuntimeDiagnostics> GetRuntimeDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        return await _client.GetRuntimeDiagnosticsAsync(new HostProto.RuntimeDiagnosticsRequest(), cancellationToken: cancellationToken);
    }

    public async Task<HostProto.RuntimeProcessRestartResult> RestartRuntimeProcessAsync(string transportKind, string poolKey, CancellationToken cancellationToken = default)
    {
        return await _client.RestartRuntimeProcessAsync(new HostProto.RestartRuntimeProcessRequest
        {
            TransportKind = transportKind,
            PoolKey = poolKey
        }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.RuntimeProcessPolicyResult> SetRuntimeProcessRestartPolicyAsync(string transportKind, string poolKey, bool paused, string reason = "", CancellationToken cancellationToken = default, string source = "client", DateTimeOffset? expiresAt = null)
    {
        var request = new HostProto.SetRuntimeProcessRestartPolicyRequest
        {
            TransportKind = transportKind,
            PoolKey = poolKey,
            Paused = paused,
            Reason = reason,
            Source = source
        };
        if (expiresAt is not null)
        {
            request.ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(expiresAt.Value.ToUniversalTime());
        }

        return await _client.SetRuntimeProcessRestartPolicyAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.ModuleDetail> GetModuleDetailAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        return await _client.GetModuleDetailAsync(new HostProto.GetModuleDetailRequest { ModuleId = moduleId }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.ModuleDetail> SetModuleEnabledAsync(string moduleId, bool enabled, CancellationToken cancellationToken = default)
    {
        return await _client.SetModuleEnabledAsync(new HostProto.SetModuleEnabledRequest { ModuleId = moduleId, Enabled = enabled }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.ListCommandsResponse> ListCommandsAsync(string query = "", CancellationToken cancellationToken = default)
    {
        return await _client.ListCommandsAsync(new HostProto.ListCommandsRequest { Query = query, IncludeDynamic = true }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.CommandExecutionResponse> ExecuteCommandAsync(string commandId, CancellationToken cancellationToken = default)
    {
        return await _client.ExecuteCommandAsync(new HostProto.ExecuteCommandRequest
        {
            CommandId = commandId,
            InvocationId = Guid.NewGuid().ToString("N")
        }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.ListBrokerAuditResponse> ListBrokerAuditAsync(int limit = 20, string moduleId = "", string actionId = "", CancellationToken cancellationToken = default)
    {
        return await _client.ListBrokerAuditAsync(new HostProto.ListBrokerAuditRequest
        {
            ModuleId = moduleId,
            ActionId = actionId,
            Limit = (uint)Math.Max(1, limit)
        }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.ListNotificationsResponse> ListNotificationsAsync(int limit = 50, string moduleId = "", CancellationToken cancellationToken = default)
    {
        return await _client.ListNotificationsAsync(new HostProto.ListNotificationsRequest
        {
            ModuleId = moduleId,
            Limit = (uint)Math.Max(1, limit)
        }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.SettingsSnapshot> GetSettingsAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        return await _client.GetSettingsAsync(new HostProto.GetSettingsRequest { ModuleId = moduleId }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.SettingsSchema> GetSettingsSchemaAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        return await _client.GetSettingsSchemaAsync(new HostProto.GetSettingsSchemaRequest { ModuleId = moduleId }, cancellationToken: cancellationToken);
    }

    public async Task<HostProto.SettingsSnapshot> UpdateSettingsAsync(string moduleId, ulong expectedRevision, Struct patch, CancellationToken cancellationToken = default)
    {
        return await _client.UpdateSettingsAsync(new HostProto.UpdateSettingsRequest
        {
            ModuleId = moduleId,
            ExpectedRevision = expectedRevision,
            Patch = patch
        }, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<HostProto.LogEntry>> TailLogsAsync(string moduleId, CancellationToken cancellationToken = default)
    {
        using var call = _client.TailLogs(new HostProto.TailLogsRequest { ModuleId = moduleId }, cancellationToken: cancellationToken);
        var entries = new List<HostProto.LogEntry>();
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            entries.Add(call.ResponseStream.Current);
        }

        return entries;
    }

    public async IAsyncEnumerable<HostProto.HostEvent> SubscribeHostEventsAsync(ulong lastEventSeq, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.SubscribeHostEvents(new HostProto.HostEventsRequest { LastEventSeq = lastEventSeq }, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async Task QuitRunnerAsync(CancellationToken cancellationToken = default)
    {
        await _client.QuitRunnerAsync(new HostProto.QuitRunnerRequest(), cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
