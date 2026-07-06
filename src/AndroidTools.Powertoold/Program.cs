using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using AndroidTools.MyPowerTools;
using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyPowerTools.Protocol;
using MyPowerTools.Protocol.Module.V1;
using CommandParameterDescriptor = MyPowerTools.Abstractions.CommandParameterDescriptor;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using EventCursor = MyPowerTools.Abstractions.EventCursor;
using IMptModule = MyPowerTools.Abstractions.IMptModule;
using InitializeResult = MyPowerTools.Abstractions.InitializeResult;
using ModuleContext = MyPowerTools.Abstractions.ModuleContext;
using MptCommandDescriptor = MyPowerTools.Abstractions.MptCommandDescriptor;
using MptRuntimeError = MyPowerTools.Abstractions.MptRuntimeError;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;
using SettingsSnapshotDocument = MyPowerTools.Abstractions.SettingsSnapshotDocument;

var endpoint = AndroidToolsSidecarEndpoint.From(args);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
builder.Logging.ClearProviders();
builder.Services.AddGrpc();
builder.Services.AddSingleton<AndroidToolsModuleRegistry>();
builder.Services.AddSingleton<AndroidToolsModuleControlService>();
builder.WebHost.ConfigureKestrel(options =>
{
    if (OperatingSystem.IsWindows())
    {
        options.ListenNamedPipe(endpoint.PipeName, listen => listen.Protocols = HttpProtocols.Http2);
        return;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(endpoint.SocketPath)!);
    if (File.Exists(endpoint.SocketPath))
    {
        File.Delete(endpoint.SocketPath);
    }

    options.ListenUnixSocket(endpoint.SocketPath, listen => listen.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<AndroidToolsModuleControlService>();
app.MapGet("/", () => "AndroidTools powertoold");
await app.RunAsync();

internal sealed record AndroidToolsSidecarEndpoint(string PipeName, string SocketPath)
{
    private const string DefaultPipeName = "mypowertools.android-tools-suite.powertoold";

    public static AndroidToolsSidecarEndpoint From(IReadOnlyList<string> arguments)
    {
        var pipeName = DefaultPipeName;
        var socketPath = DefaultUnixSocketPath();

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--pipe-name" && index + 1 < arguments.Count)
            {
                pipeName = arguments[++index];
                continue;
            }

            if (argument == "--socket-path" && index + 1 < arguments.Count)
            {
                socketPath = arguments[++index];
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (OperatingSystem.IsWindows())
                {
                    pipeName = argument;
                }
                else
                {
                    socketPath = argument;
                }
            }
        }

        return new AndroidToolsSidecarEndpoint(pipeName, socketPath);
    }

    private static string DefaultUnixSocketPath()
    {
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDir))
        {
            runtimeDir = Path.GetTempPath();
        }

        return Path.Combine(runtimeDir, "mypowertools", "android-tools-suite", "powertoold.sock");
    }
}

public sealed class AndroidToolsModuleRegistry
{
    private readonly ConcurrentDictionary<string, IMptModule> _modules = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<InitializeResult> InitializeAsync(InitializeRequest request, CancellationToken cancellationToken)
    {
        var module = _modules.GetOrAdd(request.ModuleId, CreateModule);
        return await module.InitializeAsync(new ModuleContext(
            request.HostVersion,
            request.ProtocolVersion,
            request.PackageId,
            request.ModuleId,
            request.DataDir,
            request.CacheDir,
            request.LogDir,
            request.Platform,
            request.GrantedCapabilities.ToArray()), cancellationToken);
    }

    public IMptModule Get(string moduleId)
    {
        if (_modules.TryGetValue(moduleId, out var module))
        {
            return module;
        }

        throw new KeyNotFoundException($"AndroidTools module '{moduleId}' has not been initialized.");
    }

    public async ValueTask DisposeAsync(string moduleId, CancellationToken cancellationToken)
    {
        if (_modules.TryRemove(moduleId, out var module))
        {
            await module.DisposeAsync(cancellationToken);
        }
    }

    private static IMptModule CreateModule(string moduleId)
    {
        return moduleId switch
        {
            "android-tools.notifications" => new AndroidToolsNotificationsModule(),
            "android-tools.remote-commands" => new AndroidToolsRemoteCommandsModule(),
            "android-tools.process-monitor" => new AndroidToolsProcessMonitorModule(),
            _ => throw new KeyNotFoundException($"AndroidTools module '{moduleId}' is unknown.")
        };
    }
}

public sealed class AndroidToolsModuleControlService : ModuleControl.ModuleControlBase
{
    private readonly AndroidToolsModuleRegistry _registry;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _commandCancellations = new(StringComparer.OrdinalIgnoreCase);

    public AndroidToolsModuleControlService(AndroidToolsModuleRegistry registry)
    {
        _registry = registry;
    }

    public override async Task<InitializeResponse> Initialize(InitializeRequest request, ServerCallContext context)
    {
        try
        {
            var result = await _registry.InitializeAsync(request, context.CancellationToken);
            var response = new InitializeResponse
            {
                Ok = result.Ok,
                ProtocolVersion = result.ProtocolVersion
            };
            response.Capabilities.AddRange(result.Capabilities);
            if (!result.Ok && result.Error is not null)
            {
                response.Error = ToGrpcError(result.Error);
            }

            return response;
        }
        catch (Exception ex)
        {
            return new InitializeResponse
            {
                Ok = false,
                ProtocolVersion = request.ProtocolVersion,
                Error = new MptError { Code = MptErrorCodes.RuntimeUnavailable, Message = ex.Message }
            };
        }
    }

    public override async Task<ModuleStatus> GetStatus(GetStatusRequest request, ServerCallContext context)
    {
        var status = await _registry.Get(request.ModuleId).GetStatusAsync(context.CancellationToken);
        var response = new ModuleStatus
        {
            ModuleId = status.ModuleId,
            State = ToGrpcModuleState(status.State),
            Summary = status.Summary,
            UpdatedAt = status.UpdatedAt.ToString("O"),
            EventSeq = status.EventSeq
        };
        response.Checks.AddRange(status.Checks.Select(check => new HealthCheck
        {
            Id = check.Id,
            Label = check.Label,
            Ok = check.Ok,
            Message = check.Message
        }));
        return response;
    }

    public override async Task<ListCommandsResponse> ListCommands(ListCommandsRequest request, ServerCallContext context)
    {
        var commands = await _registry.Get(request.ModuleId).ListCommandsAsync(context.CancellationToken);
        var response = new ListCommandsResponse();
        response.Commands.AddRange(commands.Select(ToGrpcCommand));
        return response;
    }

    public override async Task<CommandExecution> ExecuteCommand(ExecuteCommandRequest request, ServerCallContext context)
    {
        var args = ToJsonArgs(request);
        var result = await _registry.Get(request.ModuleId).ExecuteCommandAsync(
            new CommandRequest(request.InvocationId, request.CommandId, args),
            context.CancellationToken);

        return ToGrpcCommandExecution(result);
    }

    public override async Task ExecuteCommandStream(ExecuteCommandRequest request, IServerStreamWriter<CommandExecutionEvent> responseStream, ServerCallContext context)
    {
        var args = ToJsonArgs(request);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        _commandCancellations[request.InvocationId] = linkedCancellation;
        try
        {
            await foreach (var evt in _registry.Get(request.ModuleId)
                .ExecuteCommandStreamAsync(new CommandRequest(request.InvocationId, request.CommandId, args), linkedCancellation.Token)
                .WithCancellation(linkedCancellation.Token))
            {
                var response = new CommandExecutionEvent
                {
                    InvocationId = evt.InvocationId,
                    CommandId = evt.CommandId,
                    State = evt.State,
                    Message = evt.Message,
                    Sequence = (uint)Math.Max(0, evt.Sequence),
                    Terminal = evt.Terminal
                };
                if (evt.FinalResult is not null)
                {
                    response.FinalResult = ToGrpcCommandExecution(evt.FinalResult);
                }

                await responseStream.WriteAsync(response, context.CancellationToken);
            }
        }
        finally
        {
            _commandCancellations.TryRemove(request.InvocationId, out _);
        }
    }

    public override async Task<SettingsSchema> GetSettingsSchema(GetSettingsSchemaRequest request, ServerCallContext context)
    {
        var schema = await _registry.Get(request.ModuleId).GetSettingsSchemaAsync(context.CancellationToken);
        return new SettingsSchema
        {
            ModuleId = schema.ModuleId,
            SchemaJson = schema.SchemaJson
        };
    }

    public override async Task<SettingsSnapshot> GetSettings(GetSettingsRequest request, ServerCallContext context)
    {
        var settings = await _registry.Get(request.ModuleId).GetSettingsAsync(context.CancellationToken);
        return new SettingsSnapshot
        {
            ModuleId = settings.ModuleId,
            Revision = settings.Revision,
            ValuesJson = settings.Values.ToJsonString(),
            UpdatedAt = settings.UpdatedAt.ToString("O")
        };
    }

    public override async Task<ValidationResult> ValidateSettings(ValidateSettingsRequest request, ServerCallContext context)
    {
        var result = await _registry.Get(request.ModuleId).ValidateSettingsAsync(
            new SettingsPatch(request.ModuleId, request.ExpectedRevision, ParseJsonObject(request.PatchJson)),
            context.CancellationToken);
        var response = new ValidationResult { Ok = result.Ok };
        response.Messages.AddRange(result.Messages);
        if (result.Error is not null)
        {
            response.Error = ToGrpcError(result.Error);
        }

        return response;
    }

    public override async Task<SettingsSnapshot> ApplySettings(ApplySettingsRequest request, ServerCallContext context)
    {
        var module = _registry.Get(request.ModuleId);
        var values = ParseJsonObject(request.PatchJson);
        var validation = await module.ValidateSettingsAsync(
            new SettingsPatch(request.ModuleId, request.ExpectedRevision, values),
            context.CancellationToken);
        if (!validation.Ok)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, string.Join("; ", validation.Messages)));
        }

        var settings = await module.ApplySettingsAsync(
            new SettingsSnapshotDocument(request.ModuleId, request.ExpectedRevision, values, DateTimeOffset.UtcNow),
            context.CancellationToken);
        return new SettingsSnapshot
        {
            ModuleId = settings.ModuleId,
            Revision = settings.Revision,
            ValuesJson = settings.Values.ToJsonString(),
            UpdatedAt = settings.UpdatedAt.ToString("O")
        };
    }

    public override async Task<ListSurfacesResponse> ListSurfaces(ListSurfacesRequest request, ServerCallContext context)
    {
        var surfaces = await _registry.Get(request.ModuleId).ListSurfacesAsync(context.CancellationToken);
        var response = new ListSurfacesResponse();
        response.Surfaces.AddRange(surfaces.Select(surface => new UiSurface
        {
            Id = surface.Id,
            Kind = surface.Kind,
            Title = surface.Title,
            ModelJson = surface.Model.ToJsonString()
        }));
        return response;
    }

    public override async Task SubscribeEvents(SubscribeEventsRequest request, IServerStreamWriter<ModuleEvent> responseStream, ServerCallContext context)
    {
        await foreach (var moduleEvent in _registry.Get(request.ModuleId)
            .SubscribeEventsAsync(new EventCursor(request.LastEventSeq), context.CancellationToken)
            .WithCancellation(context.CancellationToken))
        {
            await responseStream.WriteAsync(new ModuleEvent
            {
                ModuleId = moduleEvent.ModuleId,
                Seq = moduleEvent.Seq,
                Type = moduleEvent.Type,
                Time = moduleEvent.Time.ToString("O"),
                PayloadJson = moduleEvent.Payload.ToJsonString()
            }, context.CancellationToken);
        }
    }

    public override Task<CancelCommandResponse> CancelCommand(CancelCommandRequest request, ServerCallContext context)
    {
        if (_commandCancellations.TryGetValue(request.InvocationId, out var cancellation))
        {
            cancellation.Cancel();
            return Task.FromResult(new CancelCommandResponse
            {
                Accepted = true,
                State = "module-cancelling",
                Message = $"Cancellation accepted for {request.InvocationId}."
            });
        }

        return Task.FromResult(new CancelCommandResponse
        {
            Accepted = false,
            State = "module-cancel-not-found",
            Message = $"Invocation {request.InvocationId} is not running in powertoold."
        });
    }

    public override async Task<DisposeResponse> Dispose(DisposeRequest request, ServerCallContext context)
    {
        await _registry.DisposeAsync(request.ModuleId, context.CancellationToken);
        return new DisposeResponse { Ok = true };
    }

    private static JsonObject ToJsonArgs(ExecuteCommandRequest request)
    {
        if (request.TypedArgs.Fields.Count > 0)
        {
            return ParseJsonObject(request.TypedArgs.ToString());
        }

        if (!string.IsNullOrWhiteSpace(request.ArgsJson))
        {
            return ParseJsonObject(request.ArgsJson);
        }

        return ToJsonArgs(request.Args);
    }

    private static JsonObject ToJsonArgs(IDictionary<string, string> args)
    {
        var json = new JsonObject();
        foreach (var argument in args)
        {
            json[argument.Key] = DecodeArg(argument.Value);
        }

        return json;
    }

    private static JsonNode? DecodeArg(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                return JsonNode.Parse(value);
            }
            catch (JsonException)
            {
                return JsonValue.Create(value);
            }
        }

        if (bool.TryParse(value, out var boolValue))
        {
            return JsonValue.Create(boolValue);
        }

        return JsonValue.Create(value);
    }

    private static JsonObject ParseJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonNode.Parse(json)?.AsObject() ?? [];
    }

    private static MptCommand ToGrpcCommand(MptCommandDescriptor command)
    {
        var response = new MptCommand
        {
            Id = command.Id,
            Title = command.Title,
            Subtitle = command.Subtitle,
            Kind = command.Kind,
            RequiresElevation = command.RequiresElevation,
            Icon = command.Icon,
            Category = command.Category,
            DangerLevel = command.DangerLevel,
            TimeoutMs = (uint)Math.Max(0, command.TimeoutMs),
            ExecutionJson = command.Execution?.ToJsonString() ?? "",
            SupportsProgress = command.SupportsProgress,
            SupportsCancellation = command.SupportsCancellation
        };
        response.Parameters.AddRange((command.Parameters ?? []).Select(ToGrpcCommandParameter));
        response.Constraints.AddRange(command.Constraints ?? []);
        return response;
    }

    private static CommandParameter ToGrpcCommandParameter(CommandParameterDescriptor parameter)
    {
        return new CommandParameter
        {
            Id = parameter.Id,
            Label = parameter.Label,
            Type = parameter.Type,
            Required = parameter.Required,
            DefaultValue = parameter.DefaultValue ?? ""
        };
    }

    private static ModuleState ToGrpcModuleState(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "running" => ModuleState.Running,
            "degraded" => ModuleState.Degraded,
            "disabled" => ModuleState.Disabled,
            "stopped" => ModuleState.Stopped,
            "starting" => ModuleState.Starting,
            "not-configured" or "not_configured" => ModuleState.NotConfigured,
            "error" => ModuleState.Error,
            _ => ModuleState.Unknown
        };
    }

    private static CommandState ToGrpcCommandState(string state)
    {
        return state.ToLowerInvariant() switch
        {
            "accepted" => CommandState.Accepted,
            "running" => CommandState.Running,
            "succeeded" => CommandState.Succeeded,
            "failed" => CommandState.Failed,
            "cancelled" => CommandState.Cancelled,
            _ => CommandState.Unknown
        };
    }

    private static CommandExecution ToGrpcCommandExecution(CommandExecutionResult result)
    {
        var response = new CommandExecution
        {
            InvocationId = result.InvocationId,
            CommandId = result.CommandId,
            State = ToGrpcCommandState(result.State),
            Success = result.Success,
            Output = result.Output
        };
        if (result.Error is not null)
        {
            response.Error = ToGrpcError(result.Error);
        }

        return response;
    }

    private static MptError ToGrpcError(MptRuntimeError error)
    {
        return new MptError
        {
            Code = error.Code,
            Message = error.Message,
            Retryable = error.Retryable,
            DetailsJson = error.Details?.ToJsonString() ?? ""
        };
    }
}

