using Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Hosting;
using MyPowerTools.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Protocol.ServiceManager.V1;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// gRPC service implementation bridging the ServiceManager wire contract to the in-process engine.
/// Auth (bearer token) is enforced by the shared <c>BearerTokenAuthServerInterceptor</c> registered
/// in DI; scope (caller_tool_id vs unit owner) is enforced here per-RPC.
/// </summary>
public sealed class ServiceManagerGrpcService : SM.ServiceManager.ServiceManagerBase
{
    private readonly ServiceManagerEngine _engine;
    private readonly IHostApplicationLifetime? _lifetime;

    public ServiceManagerGrpcService(ServiceManagerEngine engine, IHostApplicationLifetime? lifetime = null)
    {
        _engine = engine;
        _lifetime = lifetime;
    }

    public override Task<ListUnitsResponse> ListUnits(ListUnitsRequest request, ServerCallContext context)
    {
        ServiceUnitState? filter = request.State == SM.UnitState.Unspecified ? null : MapToEngine(request.State);
        var units = _engine.List(CallerToolId(context), filter).Select(ToProto).ToList();
        return Task.FromResult(new ListUnitsResponse { Units = { units } });
    }

    public override Task<SM.UnitSnapshot> GetUnit(GetUnitRequest request, ServerCallContext context)
    {
        try
        {
            var snapshot = _engine.GetSnapshot(request.UnitId, CallerToolId(context));
            return Task.FromResult(ToProto(snapshot));
        }
        catch (KeyNotFoundException)
        {
            throw NotFound(request.UnitId);
        }
        catch (ServiceUnitScopeDeniedException ex)
        {
            throw ScopeDenied(ex);
        }
    }

    public override async Task<SM.UnitSnapshot> Start(UnitOpRequest request, ServerCallContext context)
    {
        try
        {
            var snapshot = await _engine.StartAsync(request.UnitId, CallerToolId(context), context.CancellationToken);
            return ToProto(snapshot);
        }
        catch (KeyNotFoundException)
        {
            throw NotFound(request.UnitId);
        }
        catch (ServiceUnitScopeDeniedException ex)
        {
            throw ScopeDenied(ex);
        }
    }

    public override async Task<SM.UnitSnapshot> Stop(UnitOpRequest request, ServerCallContext context)
    {
        try
        {
            var snapshot = await _engine.StopAsync(request.UnitId, CallerToolId(context), context.CancellationToken);
            return ToProto(snapshot);
        }
        catch (KeyNotFoundException)
        {
            throw NotFound(request.UnitId);
        }
        catch (ServiceUnitScopeDeniedException ex)
        {
            throw ScopeDenied(ex);
        }
    }

    public override async Task<SM.UnitSnapshot> Restart(UnitOpRequest request, ServerCallContext context)
    {
        try
        {
            var snapshot = await _engine.RestartAsync(request.UnitId, CallerToolId(context), context.CancellationToken);
            return ToProto(snapshot);
        }
        catch (KeyNotFoundException)
        {
            throw NotFound(request.UnitId);
        }
        catch (ServiceUnitScopeDeniedException ex)
        {
            throw ScopeDenied(ex);
        }
    }

    public override Task<ReloadResponse> Reload(ReloadRequest request, ServerCallContext context)
    {
        var count = _engine.Reload();
        return Task.FromResult(new ReloadResponse { UnitCount = (uint)count });
    }

    public override async Task TailLogs(TailLogsRequest request, IServerStreamWriter<SM.LogEntry> responseStream, ServerCallContext context)
    {
        try
        {
            var tail = (int)Math.Max(0, request.TailLines);
            if (tail == 0)
            {
                tail = 200;
            }

            var entries = _engine.TailLogs(request.UnitId, tail, CallerToolId(context));
            foreach (var entry in entries)
            {
                await responseStream.WriteAsync(ToProto(entry));
            }
        }
        catch (KeyNotFoundException)
        {
            throw NotFound(request.UnitId);
        }
        catch (ServiceUnitScopeDeniedException ex)
        {
            throw ScopeDenied(ex);
        }
    }

    public override async Task SubscribeUnitEvents(SubscribeUnitEventsRequest request, IServerStreamWriter<SM.UnitEvent> responseStream, ServerCallContext context)
    {
        var lastSeq = request.LastEventSeq;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            foreach (var evt in _engine.Events.Since(lastSeq, string.IsNullOrEmpty(request.UnitId) ? null : request.UnitId))
            {
                lastSeq = evt.Seq;
                await responseStream.WriteAsync(ToProto(evt));
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), context.CancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    public override Task<SM.ShutdownResponse> Shutdown(SM.ShutdownRequest request, ServerCallContext context)
    {
        // Graceful shutdown: trigger host stop on a background thread so this RPC can return first.
        // The host's shutdown runs `finally { engine.DisposeAsync() }` which detaches from units
        // WITHOUT stopping them, leaving them alive for re-adoption by the next ServiceManager.
        if (_lifetime is null)
        {
            return Task.FromResult(new SM.ShutdownResponse { Ok = false });
        }

        _ = Task.Run(() =>
        {
            try
            {
                _lifetime.StopApplication();
            }
            catch
            {
                // best-effort
            }
        });

        return Task.FromResult(new SM.ShutdownResponse { Ok = true });
    }

    // The caller's tool identity travels in request metadata so the client interceptor
    // (scoped) attaches it automatically and the admin client leaves it absent.
    private static string? CallerToolId(ServerCallContext context)
    {
        return context.RequestHeaders.GetValue("x-mpt-caller-tool");
    }

    private static RpcException NotFound(string unitId)
        => new(new Status(StatusCode.NotFound, $"Service unit '{unitId}' not found."));

    private static RpcException ScopeDenied(ServiceUnitScopeDeniedException ex)
        => new(new Status(StatusCode.PermissionDenied, $"MPT_{nameof(MptErrorCodes.ScopeDenied)}: {ex.Message}"));

    private static SM.UnitSnapshot ToProto(Abstractions.ServiceUnitSnapshot s)
    {
        var proto = new SM.UnitSnapshot
        {
            UnitId = s.Id,
            ToolId = s.ToolId,
            DisplayName = s.DisplayName,
            State = MapFromEngine(s.State),
            Pid = s.Pid ?? 0,
            Version = s.Version ?? "",
            Autostart = s.Autostart,
            RestartPolicy = $"{s.RestartPolicy.MaxRestarts}/{(int)s.RestartPolicy.Backoff.TotalMilliseconds}ms",
            RestartCount = s.RestartCount,
            LastError = s.LastError ?? "",
            ExitCode = s.ExitCode ?? 0,
            EventSeq = s.EventSeq
        };

        if (s.StartedAt is not null)
        {
            proto.StartedAt = Timestamp.FromDateTimeOffset(s.StartedAt.Value);
        }

        if (s.Uptime is not null)
        {
            proto.Uptime = Duration.FromTimeSpan(s.Uptime.Value);
        }

        if (s.Readiness is not null)
        {
            proto.Readiness = new SM.UnitReadiness { Ok = s.State == Abstractions.ServiceUnitState.Active, Message = s.Readiness.Kind };
        }

        return proto;
    }

    private static SM.UnitEvent ToProto(Abstractions.ServiceUnitEvent e)
    {
        var proto = new SM.UnitEvent
        {
            Seq = e.Seq,
            UnitId = e.UnitId,
            Type = e.Type
        };

        if (e.Time != default)
        {
            proto.Time = Timestamp.FromDateTimeOffset(e.Time);
        }

        return proto;
    }

    private static SM.LogEntry ToProto(Abstractions.MptToolLogEntry entry)
    {
        var proto = new SM.LogEntry
        {
            Level = entry.Level,
            Category = entry.Category,
            Message = entry.Message,
            Stream = "stdout"
        };

        if (entry.Time != default)
        {
            proto.Time = Timestamp.FromDateTimeOffset(entry.Time);
        }

        return proto;
    }

    private static SM.UnitState MapFromEngine(Abstractions.ServiceUnitState state) => state switch
    {
        Abstractions.ServiceUnitState.Inactive => SM.UnitState.Inactive,
        Abstractions.ServiceUnitState.Activating => SM.UnitState.Activating,
        Abstractions.ServiceUnitState.Active => SM.UnitState.Active,
        Abstractions.ServiceUnitState.Degraded => SM.UnitState.Degraded,
        Abstractions.ServiceUnitState.Failed => SM.UnitState.Failed,
        Abstractions.ServiceUnitState.Deactivating => SM.UnitState.Deactivating,
        _ => SM.UnitState.Unspecified
    };

    private static Abstractions.ServiceUnitState? MapToEngine(SM.UnitState state) => state switch
    {
        SM.UnitState.Inactive => Abstractions.ServiceUnitState.Inactive,
        SM.UnitState.Activating => Abstractions.ServiceUnitState.Activating,
        SM.UnitState.Active => Abstractions.ServiceUnitState.Active,
        SM.UnitState.Degraded => Abstractions.ServiceUnitState.Degraded,
        SM.UnitState.Failed => Abstractions.ServiceUnitState.Failed,
        SM.UnitState.Deactivating => Abstractions.ServiceUnitState.Deactivating,
        SM.UnitState.Unspecified => null,
        _ => null
    };
}
