using System.Net;
using System.Net.Sockets;
using System.Text;

namespace RemoteToolGateway.Core;

/// <summary>
/// A bounded HTTP/1.1 listener for the control endpoints. It is deliberately small: one request
/// per connection, fixed-size header/body limits, a fixed connection budget and a hard request
/// deadline. It binds exactly one address — the Tailscale address the user enabled — and refuses
/// any peer outside the tailnet (loopback peers only when a test injected a loopback transport).
/// </summary>
public sealed class ControlHttpListener : IAsyncDisposable
{
    private const int ConnectionBudget = 8;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly bool _allowLoopbackPeers;
    private readonly Func<ControlRequest, CancellationToken, Task<ControlResponse>> _handler;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _slots = new(ConnectionBudget, ConnectionBudget);
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private Task _loop = Task.CompletedTask;
    private Exception? _fault;

    public ControlHttpListener(
        IPAddress address,
        int port,
        bool allowLoopbackPeers,
        Func<ControlRequest, CancellationToken, Task<ControlResponse>> handler,
        Action<string>? log = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _address = address ?? throw new ArgumentNullException(nameof(address));
        _port = port;
        _allowLoopbackPeers = allowLoopbackPeers;
        _handler = handler;
        _log = log;
    }

    public IPEndPoint? LocalEndpoint => _listener?.LocalEndpoint as IPEndPoint;

    public bool Running => _listener is not null;

    public Task Completion => _loop;

    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Binds and starts accepting. A bind failure propagates so the caller can report it.</summary>
    public void Start()
    {
        if (_listener is not null) return;
        var listener = new TcpListener(_address, _port);
        listener.Start(16);
        _listener = listener;
        _loop = RunAsync(listener);
    }

    private async Task RunAsync(TcpListener listener)
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(_lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (SocketException) when (_lifetime.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception ex) { Volatile.Write(ref _fault, ex); return; }

                _ = HandleClientAsync(client);
            }
        }
        catch (Exception ex) { Volatile.Write(ref _fault, ex); }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            if (!_slots.Wait(0))
            {
                // The budget protects the desktop from a burst; excess connections are told to retry.
                await TryWriteAsync(client, ControlResponse.Error(503, ControlErrorCodes.Busy, "网关正忙，请稍后重试。"));
                return;
            }

            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                deadline.CancelAfter(RequestTimeout);
                var token = deadline.Token;
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (remote is null) return;
                if (!TailnetBinding.IsPeerAllowed(remote, _allowLoopbackPeers))
                {
                    // A connection from outside the tailnet is closed with an explicit refusal.
                    await TryWriteAsync(client, ControlResponse.Error(403, ControlErrorCodes.ForbiddenPeer, "只接受 Tailscale 网络内的连接。"), token);
                    return;
                }

                client.NoDelay = true;
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, remote, token);
                if (request is null) return;
                var response = request.Error ?? await InvokeAsync(request.Request!, token);
                await TryWriteAsync(client, response, token);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
            {
                _log?.Invoke("control request failed: " + ex.GetType().Name);
            }
            finally { try { _slots.Release(); } catch (ObjectDisposedException) { } }
        }
    }

    private async Task<ControlResponse> InvokeAsync(ControlRequest request, CancellationToken token)
    {
        try { return await _handler(request, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return ControlResponse.Error(504, ControlErrorCodes.InternalError, "请求超时。");
        }
        catch (Exception)
        {
            // No exception detail crosses the wire; the desktop log keeps the local detail.
            return ControlResponse.Error(500, ControlErrorCodes.InternalError, "网关处理请求失败。");
        }
    }

    private sealed record ParsedRequest(ControlRequest? Request, ControlResponse? Error);

    private static async Task<ParsedRequest> ReadRequestAsync(NetworkStream stream, IPAddress remote, CancellationToken token)
    {
        var block = await ReadHeaderBlockAsync(stream, token);
        if (block is null) return new ParsedRequest(null, null);
        // A single TCP segment often carries the headers and the whole body: the bytes after the
        // header terminator belong to the body and must not be discarded.
        var (header, leftover) = block.Value;
        var text = Encoding.ASCII.GetString(header);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0 || lines[0].Length == 0)
        {
            return new ParsedRequest(null, ControlResponse.Error(400, ControlErrorCodes.BadRequest, "请求行缺失。"));
        }

        var parts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return new ParsedRequest(null, ControlResponse.Error(400, ControlErrorCodes.BadRequest, "请求行无效。"));
        }

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var version = parts[2];
        if (version is not ("HTTP/1.1" or "HTTP/1.0"))
        {
            return new ParsedRequest(null, ControlResponse.Error(400, ControlErrorCodes.BadRequest, "只支持 HTTP/1.1。"));
        }

        if (method is not ("GET" or "POST"))
        {
            return new ParsedRequest(null, ControlResponse.Error(405, ControlErrorCodes.MethodNotAllowed, "只支持 GET 和 POST。"));
        }

        if (target.Length > ControlWire.MaxPathLength || target.Contains('%') || target.Contains(".."))
        {
            return new ParsedRequest(null, ControlResponse.Error(400, ControlErrorCodes.BadRequest, "请求路径无效。"));
        }

        var path = target.Split('?', 2)[0];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            if (++count > ControlWire.MaxHeaderCount)
            {
                return new ParsedRequest(null, ControlResponse.Error(400, ControlErrorCodes.BadRequest, "请求头过多。"));
            }

            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var name = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            headers[name] = value;
        }

        if (headers.ContainsKey("Transfer-Encoding"))
        {
            return new ParsedRequest(null, ControlResponse.Error(400, ControlErrorCodes.BadRequest, "不支持分块传输。"));
        }

        var length = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText))
        {
            if (!int.TryParse(lengthText, out length) || length < 0)
            {
                return new ParsedRequest(null, ControlResponse.Error(400, ControlErrorCodes.BadRequest, "Content-Length 无效。"));
            }

            if (length > ControlWire.MaxRequestBytes)
            {
                return new ParsedRequest(null, ControlResponse.Error(413, ControlErrorCodes.PayloadTooLarge,
                    $"请求体超过 {ControlWire.MaxRequestBytes} 字节上限。"));
            }
        }

        if (headers.TryGetValue("Expect", out var expect) && expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n"), token);
            await stream.FlushAsync(token);
        }

        var body = "";
        if (length > 0)
        {
            var buffer = new byte[length];
            var read = Math.Min(leftover.Length, length);
            if (read > 0) Array.Copy(leftover, buffer, read);
            while (read < length)
            {
                var chunk = await stream.ReadAsync(buffer.AsMemory(read, length - read), token);
                if (chunk <= 0) break;
                read += chunk;
            }

            if (read != length)
            {
                return new ParsedRequest(null, ControlResponse.Error(400, ControlErrorCodes.BadRequest, "请求体不完整。"));
            }

            body = Encoding.UTF8.GetString(buffer);
        }

        var authorization = headers.GetValueOrDefault("Authorization", "");
        return new ParsedRequest(new ControlRequest(method, path, authorization, body, remote), null);
    }

    /// <summary>
    /// Reads until CRLFCRLF or the header cap and returns the header block plus any bytes that
    /// already belong to the body. A null result means the peer closed before sending anything.
    /// </summary>
    private static async Task<(byte[] Header, byte[] Leftover)?> ReadHeaderBlockAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        using var accumulated = new MemoryStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read <= 0) return null;
            accumulated.Write(buffer, 0, read);
            if (accumulated.Length > ControlWire.MaxHeaderBytes)
            {
                throw new InvalidDataException("Request headers exceeded the configured limit.");
            }

            var data = accumulated.GetBuffer();
            var length = (int)accumulated.Length;
            var terminator = FindHeaderTerminator(data, length);
            if (terminator >= 0)
            {
                var headerLength = terminator + 4;
                var header = new byte[headerLength];
                Array.Copy(data, header, headerLength);
                var leftover = new byte[length - headerLength];
                if (leftover.Length > 0) Array.Copy(data, headerLength, leftover, 0, leftover.Length);
                return (header, leftover);
            }
        }
    }

    private static int FindHeaderTerminator(byte[] data, int length)
    {
        for (var index = 3; index < length; index++)
        {
            if (data[index - 3] == (byte)'\r' && data[index - 2] == (byte)'\n' &&
                data[index - 1] == (byte)'\r' && data[index] == (byte)'\n')
            {
                return index - 3;
            }
        }

        return -1;
    }

    private static async Task TryWriteAsync(TcpClient client, ControlResponse response, CancellationToken token = default)
    {
        try
        {
            using var stream = client.GetStream();
            await TryWriteAsync(stream, response, token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    private static async Task TryWriteAsync(NetworkStream stream, ControlResponse response, CancellationToken token)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var head = $"HTTP/1.1 {response.StatusCode} {Reason(response.StatusCode)}\r\n" +
                   "Content-Type: application/json; charset=utf-8\r\n" +
                   "Cache-Control: no-store\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), token);
        await stream.WriteAsync(body, token);
        await stream.FlushAsync(token);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        202 => "Accepted",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "Error"
    };

    public async ValueTask DisposeAsync()
    {
        try { await _lifetime.CancelAsync(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch (Exception) { }
        _listener = null;
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _lifetime.Dispose();
        _slots.Dispose();
    }
}
