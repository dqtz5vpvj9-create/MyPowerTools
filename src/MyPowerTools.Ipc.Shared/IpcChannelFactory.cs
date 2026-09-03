using System.Net.Sockets;
using System.IO.Pipes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Ipc;

/// <summary>
/// Builds authenticated gRPC channels over OS-native IPC transports
/// (Windows named pipe or Unix domain socket). Shared by every MPT host
/// (Runner HostControl, ServiceManager) so the wire/auth shape is uniform.
/// </summary>
public static class IpcChannelFactory
{
    /// <summary>
    /// Creates a channel for the default Runner HostControl endpoint with no auth token.
    /// </summary>
    public static GrpcChannel ForDefaultEndpoint()
    {
        return ForEndpoint(IpcEndpoint.RunnerDefault(PlatformId.Current()));
    }

    /// <summary>
    /// Creates an unauthenticated channel for <paramref name="endpoint"/>.
    /// Callers that need auth should use <see cref="ForEndpoint(IpcEndpoint, string?, string?)"/>.
    /// </summary>
    public static GrpcChannel ForEndpoint(IpcEndpoint endpoint)
    {
        return ForEndpoint(endpoint, headerName: null, authToken: null);
    }

    /// <summary>
    /// Creates a channel for <paramref name="endpoint"/>, optionally layering a bearer-token
    /// client interceptor identified by <paramref name="headerName"/>.
    /// </summary>
    public static GrpcChannel ForEndpoint(IpcEndpoint endpoint, string? headerName, string? authToken)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (_, cancellationToken) =>
            {
                if (endpoint.Transport == IpcTransport.NamedPipe)
                {
                    var stream = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, MptNamedPipePolicy.ClientOptions);
                    await stream.ConnectAsync(cancellationToken);
                    return stream;
                }

                if (endpoint.Transport == IpcTransport.UnixDomainSocket)
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), connectCts.Token);
                    return new NetworkStream(socket, ownsSocket: true);
                }

                throw new NotSupportedException($"Unsupported IPC transport: {endpoint.Transport}");
            }
        };

        var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler
        });

        return channel;
    }

    /// <summary>
    /// Wraps <paramref name="channel"/>'s call invoker with a bearer-token interceptor when both
    /// <paramref name="headerName"/> and <paramref name="authToken"/> are present; otherwise returns
    /// the channel's own invoker.
    /// </summary>
    public static CallInvoker AuthenticatedInvoker(GrpcChannel channel, string? headerName, string? authToken)
    {
        var baseInvoker = channel.CreateCallInvoker();
        return string.IsNullOrWhiteSpace(headerName) || string.IsNullOrWhiteSpace(authToken)
            ? baseInvoker
            : baseInvoker.Intercept(new BearerTokenAuthClientInterceptor(headerName, authToken));
    }

    /// <summary>Windows named pipe endpoint helper.</summary>
    public static IpcEndpoint ForNamedPipe(string address)
        => new IpcEndpoint(IpcTransport.NamedPipe, address);

    /// <summary>Unix domain socket endpoint helper.</summary>
    public static IpcEndpoint ForUnixSocket(string address)
        => new IpcEndpoint(IpcTransport.UnixDomainSocket, address);
}
