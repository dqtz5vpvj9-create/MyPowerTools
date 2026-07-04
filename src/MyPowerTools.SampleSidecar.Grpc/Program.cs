using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyPowerTools.Protocol.Module.V1;

var pipeName = args.Length > 0 ? args[0] : "mypowertools.sample.grpc";
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

        return Task.FromResult(new CommandExecution
        {
            InvocationId = request.InvocationId,
            CommandId = request.CommandId,
            State = CommandState.Succeeded,
            Success = true,
            Output = $"pong from SampleModuleControlService pid={Environment.ProcessId} module={request.ModuleId}"
        });
    }
}
