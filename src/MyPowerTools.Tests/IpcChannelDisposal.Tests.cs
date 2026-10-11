using System.Net.Sockets;
using MyPowerTools.HostControl;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Tests;

public class IpcChannelDisposalTests
{
    [Fact]
    public async Task DisposingChannelClosesItsOwnedIpcConnection()
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath();
        var path = Path.Combine(directory, "mpt-ipc-" + Guid.NewGuid().ToString("N") + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var client = HostControlClient.ForEndpoint(new IpcEndpoint(IpcTransport.UnixDomainSocket, path), "test-token");
            using var callToken = new CancellationTokenSource();
            var call = client.PingAsync(callToken.Token);
            using var connection = await listener.AcceptAsync(deadline.Token);
            // Drain the preface/frames until EOF; cancellation alone must not be
            // confused with closing the connection owned by the disposed channel.
            var closed = Task.Run(async () =>
            {
                var buffer = new byte[4096];
                while (await connection.ReceiveAsync(buffer, SocketFlags.None, deadline.Token) != 0) { }
            });
            callToken.Cancel();
            try { await call; } catch (Grpc.Core.RpcException) { }
            client.Dispose();
            await closed;
        }
        finally { File.Delete(path); }
    }
}
