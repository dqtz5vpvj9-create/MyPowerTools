using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyPowerTools.Protocol.Module.V1;

var pipeName = args.Length > 0 ? args[0] : "mypowertools.template.dotnet-grpc";
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
builder.WebHost.ConfigureKestrel(options =>
{
    if (OperatingSystem.IsWindows())
    {
        options.ListenNamedPipe(pipeName, listen => listen.Protocols = HttpProtocols.Http2);
        return;
    }

    var socketPath = Path.Combine(Path.GetTempPath(), $"{pipeName}.sock");
    if (File.Exists(socketPath))
    {
        File.Delete(socketPath);
    }

    options.ListenUnixSocket(socketPath, listen => listen.Protocols = HttpProtocols.Http2);
});

var app = builder.Build();
app.MapGrpcService<TemplateModuleService>();
app.MapGet("/", () => "MyPowerTools .NET gRPC sidecar template");
await app.RunAsync();

public sealed class TemplateModuleService : ModuleControl.ModuleControlBase
{
    public override Task<InitializeResponse> Initialize(InitializeRequest request, ServerCallContext context)
    {
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
            Summary = "Sample .NET gRPC sidecar is running.",
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
            Title = "Ping .NET gRPC Sidecar",
            Subtitle = "Executes over native IPC.",
            Kind = "action"
        });
        return Task.FromResult(response);
    }

    public override Task<CommandExecution> ExecuteCommand(ExecuteCommandRequest request, ServerCallContext context)
    {
        return Task.FromResult(new CommandExecution
        {
            InvocationId = request.InvocationId,
            CommandId = request.CommandId,
            State = CommandState.Succeeded,
            Success = true,
            Output = $"pong from dotnet grpc sidecar pid={Environment.ProcessId}"
        });
    }
}
