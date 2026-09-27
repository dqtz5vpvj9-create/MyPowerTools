using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileTransfer.Core;

// MPT file protocol v1: big-endian int32 JSON length, UTF-8 JSON, then exact file bytes.
// The receiver acknowledges both admission and the completed atomic local rename.
public static class DirectTransfer
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public sealed record Offer(int Version, string Token, string Name, long Size);
    public sealed record Reply(bool Ok, string Message);

    public static async Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, json.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(json, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 2 or > 16384) throw new InvalidDataException("无效的传输握手。");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidDataException("传输握手为空。");
    }

    public static async Task<string> SendAsync(string address, int port, string pairingToken, string path,
        Action<long, long>? progress, CancellationToken token)
    {
        var ip = IPAddress.Parse(address);
        RequirePrivateAddress(ip);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var name = TransferFiles.FileName(Path.GetFileName(path));
        using var client = new TcpClient(ip.AddressFamily);
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(ip, port, connect.Token);
        }
        var stream = client.GetStream();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(TimeSpan.FromMinutes(2));
        token = idle.Token;
        await WriteJsonAsync(stream, new Offer(1, pairingToken, name, input.Length), token);
        var admitted = await ReadJsonAsync<Reply>(stream, token);
        if (!admitted.Ok) throw new IOException(admitted.Message);
        await TransferFiles.CopyAsync(input, stream, input.Length, (done, total) =>
        {
            idle.CancelAfter(TimeSpan.FromMinutes(2));
            progress?.Invoke(done, total);
        }, token);
        var finished = await ReadJsonAsync<Reply>(stream, token);
        if (!finished.Ok) throw new IOException(finished.Message);
        return finished.Message;
    }

    internal static void RequirePrivateAddress(IPAddress ip)
    {
        if (!TransferFiles.IsTailAddress(ip) && !IPAddress.IsLoopback(ip))
            throw new ArgumentException("直传请使用 Tailscale IP 地址。");
    }
}

public sealed class DirectReceiver : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _directory;
    private readonly string _token;
    private readonly long _maximumBytes;
    private readonly Action<string, long, long, string> _changed;
    private readonly Action<string, long, long, string, string> _changedDetailed;
    private readonly Func<string, CancellationToken, Task>? _publish;
    private readonly Task _loop;
    private Exception? _fault;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    /// <summary>Completes when the accept loop ends; never faults, so a watcher can release leases safely.</summary>
    public Task Completion => _loop;
    /// <summary>Non-null when the listener stopped on its own; a dead receiver must not keep a background lease.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <param name="listener">An already started listener; tests use it to simulate a listener failure.</param>
    public DirectReceiver(string address, int port, string token, string directory, long maximumBytes,
        Action<string, long, long, string> changed, Func<string, CancellationToken, Task>? publish = null,
        Action<string, long, long, string, string>? changedDetailed = null, bool sweepPartials = true,
        TcpListener? listener = null)
    {
        var ip = IPAddress.Parse(address);
        DirectTransfer.RequirePrivateAddress(ip);
        if (token.Length < 24) throw new ArgumentException("接收密钥至少需要 24 个字符。");
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        // A previous crash can leave partial files behind; a fresh receiver owns the directory.
        if (sweepPartials) TransferFiles.SweepPartials(_directory);
        _token = token;
        _maximumBytes = maximumBytes;
        _changed = changed;
        _changedDetailed = changedDetailed ?? ((name, done, total, state, _) => changed(name, done, total, state));
        _publish = publish;
        if (listener is null)
        {
            _listener = new TcpListener(ip, port);
            _listener.Start(8);
        }
        else _listener = listener;
        _loop = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_lifetime.Token); }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                catch (SocketException) when (_lifetime.IsCancellationRequested) { return; }
                catch (ObjectDisposedException) when (_lifetime.IsCancellationRequested) { return; }
                catch (Exception ex) { Volatile.Write(ref _fault, ex); return; }
                using (client)
                {
                    try
                    {
                        var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                        if (!TransferFiles.IsTailAddress(remote) && !IPAddress.IsLoopback(remote)) continue;
                        await ReceiveAsync(client, _lifetime.Token);
                    }
                    catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or ObjectDisposedException
                        or OperationCanceledException or UnauthorizedAccessException or ArgumentException or JsonException)
                    {
                        // One broken connection must never stop the listener.
                    }
                }
            }
        }
        catch (Exception ex) { Volatile.Write(ref _fault, ex); }
    }

    private async Task ReceiveAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        string? temporary = null;
        var name = "来件";
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var offer = await DirectTransfer.ReadJsonAsync<DirectTransfer.Offer>(stream, handshake.Token);
            if (offer.Version != 1 || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(offer.Token ?? ""), Encoding.UTF8.GetBytes(_token)))
                throw new InvalidDataException("接收密钥不正确，或协议版本不兼容。");
            name = TransferFiles.FileName(offer.Name);
            if (offer.Size < 0 || offer.Size > _maximumBytes) throw new InvalidDataException("文件超过接收大小限制。");
            temporary = TransferFiles.PartialPath(_directory);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await DirectTransfer.WriteJsonAsync(stream, new DirectTransfer.Reply(true, "ready"), token);
                _changed(name, 0, offer.Size, "receiving");
                // Cancellation closes the stream; a stalled sender cannot retain the receiver indefinitely.
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                idle.CancelAfter(TimeSpan.FromMinutes(2));
                await TransferFiles.CopyAsync(stream, output, offer.Size, (done, total) =>
                {
                    idle.CancelAfter(TimeSpan.FromMinutes(2));
                    _changed(name, done, total, "receiving");
                }, idle.Token);
                await output.FlushAsync(token);
            }
            var saved = TransferFiles.Commit(temporary, _directory, name);
            temporary = null;
            // Delivered means the user can actually take the file out. On Android an unpublished file
            // only exists in the app-private inbox, so publish failure is reported instead of a fake ACK.
            var publishError = await PublishAsync(saved, token);
            if (publishError.Length > 0)
            {
                var savedName = Path.GetFileName(saved);
                _changedDetailed(savedName, offer.Size, offer.Size, "failed", publishError);
                using (var reject = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    await DirectTransfer.WriteJsonAsync(stream, new DirectTransfer.Reply(false, publishError), reject.Token);
                return;
            }
            _changedDetailed(Path.GetFileName(saved), offer.Size, offer.Size, "received", "");
            // Acknowledge after publish with a bounded window of its own, so a shutdown racing the
            // commit still tells the sender the truth and it does not resend a delivered file.
            using (var acknowledge = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await DirectTransfer.WriteJsonAsync(stream, new DirectTransfer.Reply(true, Path.GetFileName(saved)), acknowledge.Token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SocketException or OperationCanceledException or ArgumentException or JsonException)
        {
            _changed(name, 0, 0, token.IsCancellationRequested ? "cancelled" : "failed");
            try
            {
                using var reply = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await DirectTransfer.WriteJsonAsync(stream, new DirectTransfer.Reply(false,
                    ex is OperationCanceledException ? "接收已停止或超时。" : ex.Message), reply.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
        }
        finally { if (temporary is not null) File.Delete(temporary); }
    }

    /// <summary>Returns "" when the file is reachable for the user, otherwise a retryable error text.</summary>
    private async Task<string> PublishAsync(string saved, CancellationToken token)
    {
        if (_publish is null) return "";
        try { await _publish(saved, token); return ""; }
        catch (OperationCanceledException) { return "文件已保存到收件文件夹，但发布已取消；请重试发送或重新开启接收以重试发布。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or SocketException)
        { return "文件已保存到收件文件夹，但发布到系统下载目录失败：" + ex.Message + " 请重试发送或重新开启接收以重试发布。"; }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        await _loop;
        _lifetime.Dispose();
    }
}
