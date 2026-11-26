using System.IO.Pipes;
using Microsoft.AspNetCore.Server.Kestrel.Transport.NamedPipes;

namespace MyPowerTools.Ipc;

/// <summary>Kestrel integration for the shared named-pipe security policy.</summary>
public static class MptNamedPipeTransport
{
    public static void Configure(NamedPipeTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.CurrentUserOnly = false;
        if (OperatingSystem.IsWindows())
        {
            options.PipeSecurity = MptNamedPipePolicy.CreatePipeSecurity();
            options.CreateNamedPipeServerStream = context => MptNamedPipePolicy.CreateWindowsServer(
                context.NamedPipeEndPoint.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                context.PipeOptions,
                inBufferSize: 0,
                outBufferSize: 0,
                initializeLowIntegrityLabel:
                    (context.PipeOptions & PipeOptions.FirstPipeInstance) != 0);
        }
    }
}
