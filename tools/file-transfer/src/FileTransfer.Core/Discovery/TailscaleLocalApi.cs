using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace FileTransfer.Core.Discovery;

/// <summary>
/// Reads the status document from the tailscaled LocalAPI socket. Injectable so a test can prove the
/// fallback path without a Tailscale installation.
/// </summary>
public interface ITailscaleLocalApiTransport
{
    /// <summary>GETs <c>/localapi/v0/status</c> over the platform's socket; throws on failure.</summary>
    Task<string> GetStatusAsync(string socketPath, CancellationToken token);
}

/// <summary>
/// The real LocalAPI client: an HTTP/1.1 GET over the Unix domain socket (Linux, macOS) or over the
/// Windows named pipe <c>\\.\pipe\ProtectedPrefix\Administrators\Tailscale\tailscaled</c>. No extra
/// package is involved — <see cref="SocketsHttpHandler.ConnectCallback"/> hands HTTP the connected
/// stream. The request host is <c>local-tailscaled.sock</c>, which is the Host header the daemon
/// requires.
/// </summary>
public sealed class TailscaleLocalApiTransport : ITailscaleLocalApiTransport
{
    public async Task<string> GetStatusAsync(string socketPath, CancellationToken token)
    {
        var pipe = socketPath.StartsWith(@"\\.\pipe\", StringComparison.Ordinal);
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, cancellation) => ConnectAsync(socketPath, pipe, cancellation),
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"http://{TailscaleEnvironment.LocalApiHost}/localapi/v0/status");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"LocalAPI 返回 {(int)response.StatusCode} {response.ReasonPhrase}。");
        return await response.Content.ReadAsStringAsync(token);
    }

    private static async ValueTask<Stream> ConnectAsync(string socketPath, bool pipe, CancellationToken token)
    {
        if (pipe)
        {
            // NamedPipeClientStream wants the server and the pipe name separately.
            var name = socketPath[@"\\.\pipe\".Length..];
            var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await stream.ConnectAsync(token);
                return stream;
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Short description of the failure for a diagnostic that never leaks the payload.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        SocketException socket => $"无法连接（{socket.SocketErrorCode}）。",
        IOException io => io.Message,
        UnauthorizedAccessException => "没有访问 Tailscale 套接字的权限。",
        OperationCanceledException => "读取超时。",
        _ => new StringBuilder().Append(exception.GetType().Name).Append("：").Append(exception.Message).ToString(),
    };
}
