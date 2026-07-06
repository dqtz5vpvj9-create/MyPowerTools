using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyPowerTools.Protocol.Module.V1;
using System.Text.Json;
using System.Text.Json.Nodes;

var pipeName = args.Length > 0 ? args[0] : "mypowertools.sample.grpc";
if (args.Skip(1).Any(arg => string.Equals(arg, "--exit-before-ready", StringComparison.OrdinalIgnoreCase)))
{
    Environment.Exit(23);
    return;
}

var startupDelayMs = args.Skip(1)
    .Where(arg => arg.StartsWith("--startup-delay-ms=", StringComparison.OrdinalIgnoreCase))
    .Select(arg => int.TryParse(arg["--startup-delay-ms=".Length..], out var value) ? value : 0)
    .FirstOrDefault(value => value > 0);
if (startupDelayMs > 0)
{
    await Task.Delay(startupDelayMs);
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.WebHost.ConfigureKestrel(options =>
{
    if (OperatingSystem.IsWindows())
    {
        options.ListenNamedPipe(pipeName, listen => listen.Protocols = HttpProtocols.Http2);
    }
    else
    {
        var path = Path.Combine(Path.GetTempPath(), $"{pipeName}.sock");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        options.ListenUnixSocket(path, listen => listen.Protocols = HttpProtocols.Http2);
    }
});

var app = builder.Build();
app.MapGrpcService<SampleModuleControlService>();
app.MapGet("/", () => "MyPowerTools sample gRPC sidecar");
await app.RunAsync();

public sealed class SampleModuleControlService : ModuleControl.ModuleControlBase
{
    public override Task<InitializeResponse> Initialize(InitializeRequest request, ServerCallContext context)
    {
        Console.WriteLine($"mpt-sidecar stdout initialized module={request.ModuleId}");
        Console.Error.WriteLine($"mpt-sidecar stderr initialized module={request.ModuleId}");
        var response = new InitializeResponse
        {
            Ok = true,
            ProtocolVersion = request.ProtocolVersion
        };
        response.Capabilities.AddRange(["status", "commands", "settings", "dashboardCard"]);
        return Task.FromResult(response);
    }

    public override Task<ModuleStatus> GetStatus(GetStatusRequest request, ServerCallContext context)
    {
        return Task.FromResult(new ModuleStatus
        {
            ModuleId = request.ModuleId,
            State = ModuleState.Running,
            Summary = "gRPC sidecar sample is running.",
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            EventSeq = 1
        });
    }

    public override Task<ListCommandsResponse> ListCommands(ListCommandsRequest request, ServerCallContext context)
    {
        var response = new ListCommandsResponse();
        response.Commands.Add(new MptCommand
        {
            Id = $"{request.ModuleId}.ping",
            Title = $"Ping {request.ModuleId}",
            Subtitle = "Sidecar module over native IPC",
            Kind = "action"
        });
        response.Commands.Add(new MptCommand
        {
            Id = $"{request.ModuleId}.crash",
            Title = $"Crash {request.ModuleId}",
            Subtitle = "Test sidecar recovery",
            Kind = "action"
        });
        response.Commands.Add(new MptCommand
        {
            Id = $"{request.ModuleId}.stream-crash",
            Title = $"Stream crash {request.ModuleId}",
            Subtitle = "Test streaming sidecar failure handling",
            Kind = "action"
        });
        response.Commands.Add(new MptCommand
        {
            Id = $"{request.ModuleId}.echo",
            Title = $"Echo {request.ModuleId}",
            Subtitle = "Echoes typed command arguments and launch context.",
            Kind = "action"
        });
        return Task.FromResult(response);
    }

    public override Task<CommandExecution> ExecuteCommand(ExecuteCommandRequest request, ServerCallContext context)
    {
        if (request.CommandId.EndsWith(".crash", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                Environment.Exit(137);
            });

            return Task.FromResult(new CommandExecution
            {
                InvocationId = request.InvocationId,
                CommandId = request.CommandId,
                State = CommandState.Succeeded,
                Success = true,
                Output = "sample sidecar crash scheduled"
            });
        }

        if (request.CommandId.EndsWith(".echo", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new CommandExecution
            {
                InvocationId = request.InvocationId,
                CommandId = request.CommandId,
                State = CommandState.Succeeded,
                Success = true,
                Output = BuildEchoPayload(request).ToJsonString(new JsonSerializerOptions { WriteIndented = false })
            });
        }

        return Task.FromResult(new CommandExecution
        {
            InvocationId = request.InvocationId,
            CommandId = request.CommandId,
            State = CommandState.Succeeded,
            Success = true,
            Output = $"pong from SampleModuleControlService pid={Environment.ProcessId} module={request.ModuleId}"
        });
    }

    private static JsonObject BuildEchoPayload(ExecuteCommandRequest request)
    {
        var legacyArgs = new JsonObject();
        foreach (var argument in request.Args)
        {
            legacyArgs[argument.Key] = argument.Value;
        }

        var typedArgs = request.TypedArgs is not null && request.TypedArgs.Fields.Count > 0
            ? JsonNode.Parse(request.TypedArgs.ToString()) as JsonObject ?? new JsonObject()
            : new JsonObject();

        return new JsonObject
        {
            ["cwd"] = Environment.CurrentDirectory,
            ["env"] = new JsonObject
            {
                ["MPT_PACKAGE_DIR"] = Environment.GetEnvironmentVariable("MPT_PACKAGE_DIR") ?? "",
                ["MPT_MODULE_ID"] = Environment.GetEnvironmentVariable("MPT_MODULE_ID") ?? "",
                ["MPT_RUNTIME_ID"] = Environment.GetEnvironmentVariable("MPT_RUNTIME_ID") ?? "",
                ["MPT_ENDPOINT_TRANSPORT"] = Environment.GetEnvironmentVariable("MPT_ENDPOINT_TRANSPORT") ?? "",
                ["MPT_ENDPOINT_ADDRESS"] = Environment.GetEnvironmentVariable("MPT_ENDPOINT_ADDRESS") ?? ""
            },
            ["argsJson"] = request.ArgsJson,
            ["typedArgs"] = typedArgs,
            ["legacyArgs"] = legacyArgs
        };
    }

    public override async Task ExecuteCommandStream(ExecuteCommandRequest request, IServerStreamWriter<CommandExecutionEvent> responseStream, ServerCallContext context)
    {
        if (request.CommandId.EndsWith(".stream-crash", StringComparison.OrdinalIgnoreCase))
        {
            await responseStream.WriteAsync(new CommandExecutionEvent
            {
                InvocationId = request.InvocationId,
                CommandId = request.CommandId,
                State = "running",
                Message = "sample stream started",
                Sequence = 1,
                Terminal = false,
                Channel = "status"
            });

            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                Environment.Exit(138);
            });
            await Task.Delay(TimeSpan.FromSeconds(30), context.CancellationToken);
            return;
        }

        var result = await ExecuteCommand(request, context);
        await responseStream.WriteAsync(new CommandExecutionEvent
        {
            InvocationId = result.InvocationId,
            CommandId = result.CommandId,
            State = result.State.ToString().Replace("CommandState", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant(),
            Message = result.Success ? result.Output : result.Error?.Message ?? "Command failed.",
            Sequence = 1,
            Terminal = true,
            FinalResult = result,
            Channel = "status"
        });
    }
}
