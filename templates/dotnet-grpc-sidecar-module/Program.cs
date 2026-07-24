using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using MyPowerTools.Ipc;
using MyPowerTools.Protocol.Module.V1;
using System.Diagnostics;

var pipeName = args.Length > 0 ? args[0] : "mypowertools.template.dotnet-grpc";
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc();
if (OperatingSystem.IsWindows())
{
    builder.WebHost.UseNamedPipes(MptNamedPipePolicy.Configure);
}
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
using var hostMonitorCancellation = new CancellationTokenSource();
var hostMonitor = MonitorHostProcessAsync(app.Lifetime, hostMonitorCancellation.Token);
try
{
    await app.RunAsync();
}
finally
{
    hostMonitorCancellation.Cancel();
    await hostMonitor;
}

static async Task MonitorHostProcessAsync(IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
{
    var value = Environment.GetEnvironmentVariable("MPT_HOST_PROCESS_ID");
    if (!int.TryParse(value, out var hostProcessId) || hostProcessId <= 0 || hostProcessId == Environment.ProcessId)
    {
        return;
    }

    try
    {
        using var hostProcess = Process.GetProcessById(hostProcessId);
        await hostProcess.WaitForExitAsync(cancellationToken);
    }
    catch (ArgumentException)
    {
        // The host exited before the sidecar acquired its process handle.
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return;
    }

    lifetime.StopApplication();
}

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
