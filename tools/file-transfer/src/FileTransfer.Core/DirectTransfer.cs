using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileTransfer.Core;

// MPT file protocol v1: big-endian int32 JSON length, UTF-8 JSON, then exact file bytes.
// The receiver acknowledges both admission and the completed atomic local rename.
// Protocol v2 adds a file-less reachability probe: it uses the same frame and the same pairing
// secret but never writes anything, so a peer's state can only become "online" after the paired
// receiver itself answered.
public static class DirectTransfer
{
    /// <summary>Frame version of a real file offer.</summary>
    public const int FileVersion = 1;
    /// <summary>Frame version of a reachability probe; a v1-only peer rejects it and stays unverified.</summary>
    public const int ProbeVersion = 2;
    /// <summary>
    /// Largest accepted JSON frame. A maximum length text (8192 characters) serializes far above the
    /// old 16 KiB bound once non-ASCII characters are escaped, so the bound follows the text limit
    /// instead of cutting legitimate messages off.
    /// </summary>
    public const int MaxFrameBytes = 256 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    /// <summary>The sender's own device id is optional so a v0.1.0 peer still parses a v1 offer.</summary>
    public sealed record Offer(int Version, string Token, string Name, long Size, string? DeviceId = null);
    public sealed record Reply(bool Ok, string Message);
    /// <summary>A v0.1.0 receiver answers a probe with the v1 Reply shape, which leaves the peer fields empty.</summary>
    public sealed record ProbeReply(bool Ok, string? DeviceId, string? Name, string? Message);
    /// <summary>
    /// Outcome of a probe. <paramref name="Reachable"/> means an MPT endpoint answered at all;
    /// <paramref name="Verified"/> means it proved it is the expected paired device. A reachable but
    /// unverified endpoint (a stranger, or a peer whose build does not support v2 probes) is not
    /// offline and must be presented as unverified instead.
    /// </summary>
    public sealed record PeerProbeResult(bool Reachable, bool Verified, string DeviceId, string Name, string Message);

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
        if (length is < 2 or > MaxFrameBytes) throw new InvalidDataException("无效的传输握手。");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidDataException("传输握手为空。");
    }

    public static async Task<string> SendAsync(string address, int port, string pairingToken, string path,
        Action<long, long>? progress, CancellationToken token, string deviceId = "")
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
        await WriteJsonAsync(stream, new Offer(FileVersion, pairingToken, name, input.Length, deviceId.Length == 0 ? null : deviceId), token);
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

    /// <summary>
    /// Asks the device at <paramref name="address"/> whether its MPT receiver is up. Nothing is
    /// written on the remote side, so this is a pure read-only check. A connection failure or a
    /// timeout reports <c>Reachable=false</c>; an endpoint that answered but did not prove it is the
    /// expected paired device reports <c>Reachable=true, Verified=false</c>, which a caller must not
    /// present as offline. Caller cancellation is the single exception that propagates.
    /// </summary>
    /// <param name="timeout">One budget for the whole exchange: TCP connect, probe frame and answer.</param>
    public static async Task<PeerProbeResult> ProbeAsync(string address, int port, string pairingToken,
        string expectedDeviceId, string localDeviceId, CancellationToken token, TimeSpan? timeout = null)
    {
        // A malformed address or a public address is a caller error, not a probe result.
        var ip = IPAddress.Parse(address);
        RequirePrivateAddress(ip);
        var budget = timeout ?? TimeSpan.FromSeconds(8);
        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
            window.CancelAfter(budget);
            using var client = new TcpClient(ip.AddressFamily);
            await client.ConnectAsync(ip, port, window.Token);
            var stream = client.GetStream();
            await WriteJsonAsync(stream, new Offer(ProbeVersion, pairingToken, "", 0, localDeviceId), window.Token);
            var reply = await ReadJsonAsync<ProbeReply>(stream, window.Token);
            var verified = reply.Ok && string.Equals(reply.DeviceId, expectedDeviceId, StringComparison.Ordinal);
            var message = verified
                ? "对方接收服务已应答。"
                : reply.Ok
                    ? "端点有应答，但不是记录的设备；请重新导入对方连接码。"
                    : string.IsNullOrWhiteSpace(reply.Message) ? "对方拒绝了检查，连接密钥可能已失效。" : reply.Message!;
            return new PeerProbeResult(true, verified, reply.DeviceId ?? "", reply.Name ?? "", message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new PeerProbeResult(false, false, "", "", $"对方在 {budget.TotalSeconds:0} 秒内没有完成连接与应答。");
        }
        catch (SocketException)
        {
            return new PeerProbeResult(false, false, "", "", "无法连接对方地址；对方可能未开启接收，或不在同一网络。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or FormatException)
        {
            return new PeerProbeResult(false, false, "", "", "对方没有返回可识别的 MPT 应答：" + ex.Message);
        }
    }

    /// <summary>
    /// Adapts the earlier five-argument change callback to the current six-argument shape, which adds
    /// the sending device's id. Callers that do not need the sender keep a one-line migration path.
    /// </summary>
    public static Action<string, long, long, string, string, string> Detailed(
        Action<string, long, long, string, string> callback) =>
        (name, done, total, state, message, _) => callback(name, done, total, state, message);

    internal static void RequirePrivateAddress(IPAddress ip)
    {
        if (!TransferFiles.IsTailAddress(ip) && !IPAddress.IsLoopback(ip))
            throw new ArgumentException("直传请使用 Tailscale IP 地址。");
    }
}

public sealed class DirectReceiver : IAsyncDisposable
{
    /// <summary>Connections handled at once; one of them may be waiting for the user's first-contact answer.</summary>
    public const int MaxConcurrentConnections = 4;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _directory;
    private readonly string _token;
    private readonly long _maximumBytes;
    private readonly Action<string, long, long, string> _changed;
    private readonly Action<string, long, long, string, string, string> _changedDetailed;
    private readonly Func<string, CancellationToken, Task>? _publish;
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly string _platform;
    private readonly ReceiveAuthorization? _authorization;
    private readonly Func<string, string?, string?, string?, CancellationToken, Task<bool>>? _isTrusted;
    private readonly Func<string, CancellationToken, Task<bool>>? _isDuplicate;
    private readonly Func<ReceivedItem, CancellationToken, Task>? _onItem;
    private readonly SemaphoreSlim _transfers = new(1, 1);
    private readonly Task _loop;
    private int _active;
    private Exception? _fault;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    /// <summary>Completes when the accept loop ends; never faults, so a watcher can release leases safely.</summary>
    public Task Completion => _loop;
    /// <summary>Non-null when the listener stopped on its own; a dead receiver must not keep a background lease.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <param name="changedDetailed">Receives terminal state changes together with the sending device's id when the offer carried one.</param>
    /// <param name="deviceId">This receiver's id; it is echoed to a verified probe so the caller can confirm the peer identity.</param>
    /// <param name="authorization">First-contact queue. Without it, an unknown device is refused instead of queued.</param>
    /// <param name="isTrusted">Decides whether a presented token is an accepted credential for that device.</param>
    /// <param name="isDuplicate">Reports an item id that was already saved, so a retry does not duplicate content.</param>
    /// <param name="onItem">Persists the accepted item and its metadata before the receiver acknowledges it.</param>
    /// <param name="listener">An already started listener; tests use it to simulate a listener failure.</param>
    public DirectReceiver(string address, int port, string token, string directory, long maximumBytes,
        Action<string, long, long, string> changed, Func<string, CancellationToken, Task>? publish = null,
        Action<string, long, long, string, string, string>? changedDetailed = null, bool sweepPartials = true,
        TcpListener? listener = null, string deviceId = "", string deviceName = "", string platform = "",
        ReceiveAuthorization? authorization = null, Func<string, string?, string?, string?, CancellationToken, Task<bool>>? isTrusted = null,
        Func<string, CancellationToken, Task<bool>>? isDuplicate = null, Func<ReceivedItem, CancellationToken, Task>? onItem = null)
    {
        var ip = IPAddress.Parse(address);
        DirectTransfer.RequirePrivateAddress(ip);
        if (token.Length < 24) throw new ArgumentException("接收密钥至少需要 24 个字符。");
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        // A previous crash can leave partial files behind; a fresh receiver owns the directory.
        if (sweepPartials) TransferFiles.SweepPartials(_directory);
        _token = token;
        _deviceId = deviceId;
        _deviceName = deviceName;
        _platform = platform;
        _authorization = authorization;
        _isTrusted = isTrusted;
        _isDuplicate = isDuplicate;
        _onItem = onItem;
        _maximumBytes = maximumBytes;
        _changed = changed;
        _changedDetailed = changedDetailed ?? new Action<string, long, long, string, string, string>(
            (name, done, total, state, _, _) => changed(name, done, total, state));
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
                // A first contact can hold its connection while the user decides, so connections are
                // handled concurrently under a small cap instead of one at a time.
                var remote = ((IPEndPoint)client.Client.RemoteEndPoint!).Address;
                if (!TransferFiles.IsTailAddress(remote) && !IPAddress.IsLoopback(remote)) { client.Dispose(); continue; }
                while (Volatile.Read(ref _active) >= MaxConcurrentConnections)
                {
                    try { await Task.Delay(25, _lifetime.Token); }
                    catch (OperationCanceledException) { client.Dispose(); return; }
                }
                Interlocked.Increment(ref _active);
                _ = HandleAsync(client);
            }
        }
        catch (Exception ex) { Volatile.Write(ref _fault, ex); }
    }

    /// <summary>Runs one connection to completion; a failure is contained to that connection.</summary>
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try { await ReceiveAsync(client, _lifetime.Token); }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or ObjectDisposedException
                or OperationCanceledException or UnauthorizedAccessException or ArgumentException or JsonException)
            {
                // One broken connection must never stop the listener.
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private async Task ReceiveAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        string? temporary = null;
        var name = "来件";
        var sender = "";
        var holdsTransfer = false;
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var offer = await DirectTransfer.ReadJsonAsync<AssistantWire.Frame>(stream, handshake.Token);
            // The identity hello is answered before any credential is examined: it is the one frame an
            // unpaired device may send, and its answer carries public identity only.
            if (offer.Version == Discovery.HelloProtocol.Version && offer.Kind == Discovery.HelloProtocol.Kind)
            {
                await AnswerHelloAsync(stream, token);
                return;
            }
            if (offer.Version == AssistantWire.Version && offer.Kind == AssistantWire.ItemKind)
            {
                await ReceiveItemAsync(stream, offer, client, token);
                return;
            }
            var keyMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(offer.Token ?? ""), Encoding.UTF8.GetBytes(_token));
            if (offer.Version == DirectTransfer.ProbeVersion)
            {
                // A probe never reaches the inbox: it is answered and closed without touching a file.
                await AnswerProbeAsync(stream, keyMatches, token);
                return;
            }
            if (offer.Version != DirectTransfer.FileVersion || !keyMatches)
                throw new InvalidDataException("接收密钥不正确，或协议版本不兼容。");
            sender = offer.DeviceId ?? "";
            name = TransferFiles.FileName(offer.Name is { Length: > 0 } legacyName ? legacyName : "来件");
            if (offer.Size < 0 || offer.Size > _maximumBytes) throw new InvalidDataException("文件超过接收大小限制。");
            // Only one payload is written at a time, so a partial file can never outlive its transfer
            // and collide with the next one. A first contact waits for its answer outside this lock.
            await _transfers.WaitAsync(token);
            holdsTransfer = true;
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
                _changedDetailed(savedName, offer.Size, offer.Size, "failed", publishError, sender);
                using (var reject = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    await DirectTransfer.WriteJsonAsync(stream, new DirectTransfer.Reply(false, publishError), reject.Token);
                return;
            }
            _changedDetailed(Path.GetFileName(saved), offer.Size, offer.Size, "received", "", sender);
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
        finally
        {
            if (temporary is not null) File.Delete(temporary);
            if (holdsTransfer) _transfers.Release();
        }
    }

    /// <summary>
    /// Answers the credential-free identity hello. The answer is the public identity the discovery
    /// contract asks for and nothing else: no token, no conversation material, no file access.
    /// </summary>
    private async Task AnswerHelloAsync(Stream stream, CancellationToken token)
    {
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        var reply = new Discovery.HelloProtocol.Reply(true, _deviceId, _deviceName, endpoint.Address.ToString(), endpoint.Port, _platform);
        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
            window.CancelAfter(TimeSpan.FromSeconds(5));
            await DirectTransfer.WriteJsonAsync(stream, reply, window.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
    }

    /// <summary>
    /// Receives one assistant item. A known credential or an approval the user already gave for this
    /// exact endpoint and item admits it; an unknown device holds the connection while its user decides,
    /// so an accepted first contact transfers immediately instead of waiting for a later retry. The
    /// item is acknowledged only after the module persisted it, and a repeated item id is answered as
    /// delivered without writing a second copy.
    /// </summary>
    private async Task ReceiveItemAsync(Stream stream, AssistantWire.Frame frame, TcpClient client, CancellationToken token)
    {
        string? temporary = null;
        var deviceId = frame.DeviceId ?? "";
        var name = "来件";
        var holdsTransfer = false;
        var endpoint = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        try
        {
            if (deviceId.Length == 0 || frame.ItemId is not { Length: > 0 } itemId) throw new InvalidDataException("来件缺少设备或条目标识。");
            TransferFiles.DeviceId(deviceId);
            var itemKind = frame.ItemKind is { Length: > 0 } kind ? kind : AssistantWire.FileItem;
            if (itemKind is not (AssistantWire.TextItem or AssistantWire.ImageItem or AssistantWire.FileItem))
                throw new InvalidDataException("来件类型不受支持。");
            // A frame addressed to another device is never this device's success.
            if (frame.TargetDeviceId is { Length: > 0 } addressed && addressed != _deviceId)
                throw new InvalidDataException("这条内容是发给另一台设备的。");
            if (frame.Size < 0 || frame.Size > _maximumBytes) throw new InvalidDataException("文件超过接收大小限制。");
            var text = frame.Text ?? "";
            if (itemKind == AssistantWire.TextItem)
            {
                if (text.Length is 0 or > 8192 || text.Any(char.IsControl)) throw new InvalidDataException("文本内容无效。");
            }
            else
            {
                if (text.Length > 0) throw new InvalidDataException("附件不能携带文本内容。");
                name = TransferFiles.FileName(frame.Name is { Length: > 0 } offered ? offered : "来件");
            }
            // The address a device advertises is the one it listens on; an observed endpoint is only a
            // fallback, because an outgoing connection can be sourced from another interface.
            var advertised = ValidAddress(frame.Address) ? frame.Address! : endpoint;
            var trusted = Crypto(_token, frame.Token)
                || (_isTrusted is not null && await _isTrusted(frame.Token ?? "", deviceId, advertised, frame.SenderName, token));
            if (!trusted && !(_authorization?.IsApproved(deviceId, endpoint, itemId) ?? false))
            {
                if (_authorization is null) throw new InvalidDataException("这台设备尚未加入你的会话。");
                if (_authorization.Decision(deviceId, endpoint, itemId) == false)
                    throw new InvalidDataException("对方已拒绝过这条内容的接收请求。");
                var request = _authorization.Request(deviceId, endpoint, DisplayName(frame.SenderName, deviceId),
                    [DisplayName(frame.Name ?? text, "内容")], advertised, frame.Token, [itemId]);
                // The prompt is raised once per endpoint/device/item set; the connection stays open.
                _changed("接收请求", 0, 0, "pending");
                if (!await _authorization.WaitAsync(request.RequestId, token))
                    throw new InvalidDataException("对方没有在限时内确认接收。");
            }
            // Only an authorized caller reaches this point, and only content that is really available
            // locally may be confirmed as delivered again.
            if (_isDuplicate is not null && await _isDuplicate(itemId, token))
            {
                await DirectTransfer.WriteJsonAsync(stream, new AssistantWire.ItemReply(true, "已保存（重复请求已忽略）。",
                    false, null, AssistantWire.DeliveredState, frame.Name), token);
                return;
            }
            string? saved = null;
            if (itemKind != AssistantWire.TextItem)
            {
                name = TransferFiles.FileName(frame.Name is { Length: > 0 } offered ? offered : "来件");
                await _transfers.WaitAsync(token);
                holdsTransfer = true;
                temporary = TransferFiles.PartialPath(_directory);
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                {
                    await DirectTransfer.WriteJsonAsync(stream, new AssistantWire.ItemReply(true, "ready"), token);
                    if (frame.Size > 0)
                    {
                        _changed(name, 0, frame.Size, "receiving");
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                        idle.CancelAfter(TimeSpan.FromMinutes(2));
                        await TransferFiles.CopyAsync(stream, output, frame.Size, (done, total) =>
                        {
                            idle.CancelAfter(TimeSpan.FromMinutes(2));
                            _changed(name, done, total, "receiving");
                        }, idle.Token);
                    }
                    await output.FlushAsync(token);
                }
                saved = TransferFiles.Commit(temporary, _directory, name);
                temporary = null;
            }
            // The module adopts the item into its own durable timeline before this acknowledgement:
            // a stored path that the platform publisher deleted is not an openable message.
            if (_onItem is not null)
                await _onItem(new ReceivedItem(itemId, frame.ConversationId ?? "", deviceId, DisplayName(frame.SenderName, deviceId),
                    itemKind, text, frame.Name, saved, frame.Size, DateTimeOffset.UtcNow, frame.TargetDeviceId), token);
            var displayName = saved is null ? DisplayName(frame.Name, "内容") : Path.GetFileName(saved);
            _changedDetailed(displayName, frame.Size, frame.Size, "received", "", deviceId);
            await DirectTransfer.WriteJsonAsync(stream, new AssistantWire.ItemReply(true, displayName,
                false, null, AssistantWire.DeliveredState, displayName), token);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or SocketException
            or OperationCanceledException or ArgumentException or JsonException)
        {
            _changed(name, 0, 0, token.IsCancellationRequested ? "cancelled" : "failed");
            try
            {
                using var reply = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await DirectTransfer.WriteJsonAsync(stream, new AssistantWire.ItemReply(false,
                    ex is OperationCanceledException ? "接收已停止或超时。" : ex.Message), reply.Token);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
        }
        finally
        {
            if (temporary is not null) File.Delete(temporary);
            if (holdsTransfer) _transfers.Release();
        }
    }

    private static bool ValidAddress(string? address) =>
        address is { Length: > 0 } text && IPAddress.TryParse(text, out var ip)
        && (TransferFiles.IsTailAddress(ip) || IPAddress.IsLoopback(ip));

    private static bool Crypto(string expected, string? presented) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented ?? ""), Encoding.UTF8.GetBytes(expected));

    private static string DisplayName(string? value, string fallback) =>
        value is { Length: > 0 } text && text.Length <= 100 && !text.Any(char.IsControl) ? text : fallback;

    /// <summary>
    /// Answers a reachability probe. The device id and name are only revealed after the pairing
    /// secret matched, so an unpaired caller cannot use the probe to fingerprint this device.
    /// </summary>
    private async Task AnswerProbeAsync(Stream stream, bool keyMatches, CancellationToken token)
    {
        var reply = keyMatches
            ? new DirectTransfer.ProbeReply(true, _deviceId, _deviceName, "接收服务已应答。")
            : new DirectTransfer.ProbeReply(false, "", "", "接收密钥不正确。");
        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
            window.CancelAfter(TimeSpan.FromSeconds(5));
            await DirectTransfer.WriteJsonAsync(stream, reply, window.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
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
        // Connections are handled concurrently, so a shutdown also waits for the ones already accepted:
        // their cancellation path deletes the partial file before this returns.
        for (var i = 0; i < 200 && Volatile.Read(ref _active) > 0; i++) await Task.Delay(25);
        _lifetime.Dispose();
    }
}
