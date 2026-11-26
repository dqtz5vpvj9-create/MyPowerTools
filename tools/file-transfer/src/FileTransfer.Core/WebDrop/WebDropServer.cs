using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace FileTransfer.Core.WebDrop;

/// <summary>
/// Lightweight zero-install HTTP/1.1 file download server built directly on <see cref="TcpListener"/>.
/// Avoids <see cref="HttpListener"/> so non-elevated Windows hosts never hit URLACL errors.
/// </summary>
public sealed class WebDropServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int StreamChunkBytes = 64 * 1024;
    private static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly List<DropEntry> _drops = [];
    private readonly ConcurrentDictionary<long, Task> _activeConnections = new();
    private readonly int _requestedPort;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleStopDelay;
    private readonly Func<IReadOnlyList<IPAddress>> _addressProvider;

    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;
    private ITimer? _lifecycleTimer;
    private DateTimeOffset? _idleSinceUtc;
    private long _listenerGeneration;
    private long _connectionId;
    private int _boundPort;
    private bool _disposed;

    public WebDropServer(
        int port = 0,
        TimeProvider? timeProvider = null,
        TimeSpan? idleStopDelay = null,
        Func<IReadOnlyList<IPAddress>>? addressProvider = null)
    {
        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 0 and 65535.");
        }

        _requestedPort = port;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleStopDelay = idleStopDelay ?? WebDropDefaults.DefaultIdleStopDelay;
        _addressProvider = addressProvider ?? DiscoverCandidateAddresses;

        if (_idleStopDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleStopDelay), idleStopDelay, "Idle stop delay must be non-negative.");
        }
    }

    public bool IsListening
    {
        get
        {
            lock (_gate)
            {
                return _listener is not null && !_disposed;
            }
        }
    }

    public int Port
    {
        get
        {
            lock (_gate)
            {
                return _boundPort;
            }
        }
    }

    public WebDropInfo CreateDrop(string filePath, TimeSpan? ttl = null, int maxDownloads = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("File does not exist.", fullPath);
        }

        var effectiveTtl = ttl ?? WebDropDefaults.DefaultTtl;
        if (effectiveTtl < WebDropDefaults.MinTtl || effectiveTtl > WebDropDefaults.MaxTtl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ttl),
                effectiveTtl,
                $"TTL must be between {WebDropDefaults.MinTtl.TotalSeconds:0} and {WebDropDefaults.MaxTtl.TotalSeconds:0} seconds.");
        }

        if (maxDownloads < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDownloads), maxDownloads, "maxDownloads must be 0 (unlimited) or positive.");
        }

        var fileInfo = new FileInfo(fullPath);
        var now = _timeProvider.GetUtcNow();
        var id = "drop-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var entry = new DropEntry(
            id,
            token,
            fullPath,
            fileInfo.Name,
            fileInfo.Length,
            now,
            now + effectiveTtl,
            maxDownloads);

        int port;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _idleSinceUtc = null;
            EnsureListeningLocked();
            _drops.Add(entry);
            port = _boundPort;
            UpdateLifecycleTimerLocked(now);
        }

        return BuildInfo(entry, now, port);
    }

    public IReadOnlyList<WebDropInfo> ListDrops(bool includeInactive = false)
    {
        var now = _timeProvider.GetUtcNow();
        List<DropEntry> snapshot;
        int port;
        ListenerStopHandle? stopHandle = null;

        lock (_gate)
        {
            snapshot = _drops.ToList();
            port = _boundPort;
            stopHandle = UpdateLifecycleTimerLocked(now);
        }

        StopListenerInBackground(stopHandle);

        var result = new List<WebDropInfo>(snapshot.Count);
        foreach (var entry in snapshot)
        {
            var info = BuildInfo(entry, now, port);
            if (includeInactive || info.Active)
            {
                result.Add(info);
            }
        }

        return result;
    }

    public WebDropInfo? GetDrop(string dropId)
    {
        if (string.IsNullOrWhiteSpace(dropId))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            var entry = _drops.FirstOrDefault(item => string.Equals(item.Id, dropId, StringComparison.Ordinal));
            return entry is null ? null : BuildInfo(entry, now, _boundPort);
        }
    }

    public bool RevokeDrop(string dropId)
    {
        if (string.IsNullOrWhiteSpace(dropId))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        ListenerStopHandle? stopHandle = null;
        bool revoked;

        lock (_gate)
        {
            var entry = _drops.FirstOrDefault(item => string.Equals(item.Id, dropId, StringComparison.Ordinal));
            if (entry is null || entry.Revoked)
            {
                revoked = false;
            }
            else
            {
                entry.Revoked = true;
                revoked = true;
                stopHandle = UpdateLifecycleTimerLocked(now);
            }
        }

        StopListenerInBackground(stopHandle);
        return revoked;
    }

    public static IReadOnlyList<IPAddress> DiscoverCandidateAddresses()
    {
        var tailscale = new List<IPAddress>();
        var privateLan = new List<IPAddress>();
        var seen = new HashSet<IPAddress>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = nic.GetIPProperties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || !seen.Add(address))
                    {
                        continue;
                    }

                    if (IsTailscaleIpv4(address))
                    {
                        tailscale.Add(address);
                    }
                    else if (IsPrivateLanIpv4(address))
                    {
                        privateLan.Add(address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Fall through to loopback fallback.
        }

        var ordered = new List<IPAddress>(tailscale.Count + privateLan.Count + 1);
        ordered.AddRange(tailscale);
        ordered.AddRange(privateLan);
        if (seen.Add(IPAddress.Loopback))
        {
            ordered.Add(IPAddress.Loopback);
        }

        return ordered;
    }

    internal static bool IsTailscaleIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    internal static bool IsPrivateLanIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    public async ValueTask DisposeAsync()
    {
        ListenerStopHandle? stopHandle;
        ITimer? lifecycleTimer;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lifecycleTimer = _lifecycleTimer;
            _lifecycleTimer = null;
            _idleSinceUtc = null;
            stopHandle = DetachListenerLocked();
        }

        lifecycleTimer?.Dispose();
        if (stopHandle is not null)
        {
            await StopListenerResourcesAsync(stopHandle).ConfigureAwait(false);
        }

        var inFlight = _activeConnections.Values.ToArray();
        if (inFlight.Length > 0)
        {
            try
            {
                await Task.WhenAll(inFlight).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort drain during shutdown.
            }
        }
    }

    private void EnsureListeningLocked()
    {
        if (_listener is not null)
        {
            return;
        }

        var listener = new TcpListener(IPAddress.Any, _requestedPort);
        listener.Start();

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var cts = new CancellationTokenSource();
        var generation = ++_listenerGeneration;

        _listener = listener;
        _boundPort = port;
        _acceptCts = cts;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, generation, cts.Token));
    }

    private ListenerStopHandle? DetachListenerLocked()
    {
        if (_listener is null && _acceptCts is null && _acceptLoop is null)
        {
            return null;
        }

        var handle = new ListenerStopHandle(_listener, _acceptCts, _acceptLoop);
        _listener = null;
        _acceptCts = null;
        _acceptLoop = null;
        _boundPort = 0;
        _listenerGeneration++;
        return handle;
    }

    private ListenerStopHandle? UpdateLifecycleTimerLocked(DateTimeOffset now)
    {
        if (_disposed || _listener is null)
        {
            DisposeLifecycleTimerLocked();
            _idleSinceUtc = null;
            return null;
        }

        DateTimeOffset? earliestExpiry = null;
        foreach (var entry in _drops)
        {
            if (IsActiveLocked(entry, now))
            {
                if (earliestExpiry is null || entry.ExpiresAt < earliestExpiry.Value)
                {
                    earliestExpiry = entry.ExpiresAt;
                }
            }
        }

        if (earliestExpiry is { } nextExpiry)
        {
            _idleSinceUtc = null;
            var delay = nextExpiry - now;
            if (delay < TimeSpan.FromMilliseconds(1))
            {
                delay = TimeSpan.FromMilliseconds(1);
            }

            ArmLifecycleTimerLocked(delay);
            return null;
        }

        _idleSinceUtc ??= now;
        var remainingGrace = (_idleSinceUtc.Value + _idleStopDelay) - now;
        if (remainingGrace <= TimeSpan.Zero)
        {
            DisposeLifecycleTimerLocked();
            _idleSinceUtc = null;
            return DetachListenerLocked();
        }

        ArmLifecycleTimerLocked(remainingGrace);
        return null;
    }

    private void ArmLifecycleTimerLocked(TimeSpan dueTime)
    {
        if (_lifecycleTimer is null)
        {
            _lifecycleTimer = _timeProvider.CreateTimer(OnLifecycleTimerFired, state: null, dueTime, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _lifecycleTimer.Change(dueTime, Timeout.InfiniteTimeSpan);
        }
    }

    private void DisposeLifecycleTimerLocked()
    {
        var timer = _lifecycleTimer;
        _lifecycleTimer = null;
        timer?.Dispose();
    }

    private void OnLifecycleTimerFired(object? state)
    {
        ListenerStopHandle? stopHandle;
        lock (_gate)
        {
            if (_disposed || _listener is null)
            {
                return;
            }

            stopHandle = UpdateLifecycleTimerLocked(_timeProvider.GetUtcNow());
        }

        StopListenerInBackground(stopHandle);
    }

    private static bool IsActiveLocked(DropEntry entry, DateTimeOffset now)
    {
        if (entry.Revoked)
        {
            return false;
        }

        if (now >= entry.ExpiresAt)
        {
            return false;
        }

        if (entry.MaxDownloads > 0 && entry.DownloadCount >= entry.MaxDownloads)
        {
            return false;
        }

        return true;
    }

    private static void StopListenerInBackground(ListenerStopHandle? handle)
    {
        if (handle is not null)
        {
            _ = Task.Run(() => StopListenerResourcesAsync(handle));
        }
    }

    private static async Task StopListenerResourcesAsync(ListenerStopHandle handle)
    {
        if (handle.AcceptCts is not null)
        {
            try
            {
                await handle.AcceptCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        try
        {
            handle.Listener?.Stop();
        }
        catch (Exception)
        {
        }

        if (handle.AcceptLoop is not null)
        {
            try
            {
                await handle.AcceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        handle.AcceptCts?.Dispose();
    }

    private async Task AcceptLoopAsync(TcpListener listener, long generation, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                var connId = Interlocked.Increment(ref _connectionId);
                var task = HandleConnectionTrackedAsync(connId, client, token);
                _activeConnections[connId] = task;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            ListenerStopHandle? stopHandle = null;
            lock (_gate)
            {
                if (_listenerGeneration == generation)
                {
                    stopHandle = UpdateLifecycleTimerLocked(_timeProvider.GetUtcNow());
                }
            }

            StopListenerInBackground(stopHandle);
        }
    }

    private async Task HandleConnectionTrackedAsync(long connectionId, TcpClient client, CancellationToken serverToken)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                await using var stream = client.GetStream();
                await HandleHttpRequestAsync(stream, serverToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Ignore abrupt client disconnects or malformed sockets.
        }
        finally
        {
            _activeConnections.TryRemove(connectionId, out _);
        }
    }

    private async Task HandleHttpRequestAsync(NetworkStream stream, CancellationToken serverToken)
    {
        var request = await ReadHttpRequestAsync(stream, serverToken).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        if (request.Malformed)
        {
            await WriteTextResponseAsync(stream, 400, "Bad Request", "text/plain; charset=utf-8", "Bad Request", isHead: false, extraHeaders: null, serverToken).ConfigureAwait(false);
            return;
        }

        var isHead = string.Equals(request.Method, "HEAD", StringComparison.Ordinal);
        var isGet = string.Equals(request.Method, "GET", StringComparison.Ordinal);
        if (!isGet && !isHead)
        {
            await WriteTextResponseAsync(
                stream,
                405,
                "Method Not Allowed",
                "text/plain; charset=utf-8",
                "Method Not Allowed",
                isHead: false,
                extraHeaders: "Allow: GET, HEAD\r\n",
                serverToken).ConfigureAwait(false);
            return;
        }

        var path = StripQuery(request.Target);
        if (!TryParseDropRoute(path, out var token, out var isDownload))
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not Found", isHead, extraHeaders: null, serverToken).ConfigureAwait(false);
            return;
        }

        var entry = FindByToken(token);
        if (entry is null)
        {
            await WriteTextResponseAsync(stream, 404, "Not Found", "text/plain; charset=utf-8", "Not Found", isHead, extraHeaders: null, serverToken).ConfigureAwait(false);
            return;
        }

        if (!isDownload)
        {
            await HandleLandingPageAsync(stream, entry, isHead, serverToken).ConfigureAwait(false);
            return;
        }

        await HandleDownloadAsync(stream, entry, request.Headers, isHead, serverToken).ConfigureAwait(false);
    }

    private async Task HandleLandingPageAsync(NetworkStream stream, DropEntry entry, bool isHead, CancellationToken token)
    {
        var now = _timeProvider.GetUtcNow();
        DropSnapshot snapshot;
        ListenerStopHandle? stopHandle;

        lock (_gate)
        {
            snapshot = SnapshotLocked(entry, now);
            stopHandle = UpdateLifecycleTimerLocked(now);
        }

        if (!snapshot.Active || !File.Exists(snapshot.FilePath))
        {
            var goneHtml = RenderGoneHtml(snapshot);
            await WriteTextResponseAsync(stream, 410, "Gone", "text/html; charset=utf-8", goneHtml, isHead, extraHeaders: null, token).ConfigureAwait(false);
            StopListenerInBackground(stopHandle);
            return;
        }

        var html = RenderLandingHtml(snapshot, now);
        await WriteTextResponseAsync(stream, 200, "OK", "text/html; charset=utf-8", html, isHead, extraHeaders: null, token).ConfigureAwait(false);
        StopListenerInBackground(stopHandle);
    }

    private async Task HandleDownloadAsync(
        NetworkStream stream,
        DropEntry entry,
        Dictionary<string, string> headers,
        bool isHead,
        CancellationToken token)
    {
        var now = _timeProvider.GetUtcNow();
        DropSnapshot initialSnapshot;
        ListenerStopHandle? stopHandle;

        lock (_gate)
        {
            initialSnapshot = SnapshotLocked(entry, now);
            stopHandle = UpdateLifecycleTimerLocked(now);
        }

        if (!initialSnapshot.Active || !File.Exists(initialSnapshot.FilePath))
        {
            await WriteTextResponseAsync(stream, 410, "Gone", "text/plain; charset=utf-8", "This download link has expired or been revoked.", isHead, extraHeaders: null, token).ConfigureAwait(false);
            StopListenerInBackground(stopHandle);
            return;
        }

        FileStream? fileStream = null;
        try
        {
            fileStream = new FileStream(
                initialSnapshot.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: StreamChunkBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var fileLength = fileStream.Length;
            long rangeStart = 0;
            long rangeEnd = fileLength > 0 ? fileLength - 1 : -1;
            var isPartial = false;

            if (headers.TryGetValue("Range", out var rangeHeader) && !string.IsNullOrWhiteSpace(rangeHeader))
            {
                var rangeResult = TryParseSingleByteRange(rangeHeader, fileLength, out var parsedStart, out var parsedEnd);
                if (rangeResult == RangeParseStatus.Unsatisfiable)
                {
                    await WriteTextResponseAsync(
                        stream,
                        416,
                        "Range Not Satisfiable",
                        "text/plain; charset=utf-8",
                        "Requested range not satisfiable.",
                        isHead,
                        extraHeaders: $"Content-Range: bytes */{fileLength}\r\nAccept-Ranges: bytes\r\n",
                        token).ConfigureAwait(false);
                    return;
                }

                if (rangeResult == RangeParseStatus.Valid)
                {
                    rangeStart = parsedStart;
                    rangeEnd = parsedEnd;
                    isPartial = true;
                }
            }

            if (!isHead)
            {
                lock (_gate)
                {
                    var currentNow = _timeProvider.GetUtcNow();
                    if (!IsActiveLocked(entry, currentNow))
                    {
                        stopHandle = UpdateLifecycleTimerLocked(currentNow) ?? stopHandle;
                        fileStream.Dispose();
                        fileStream = null;
                    }
                    else
                    {
                        entry.DownloadCount++;
                        stopHandle = UpdateLifecycleTimerLocked(currentNow) ?? stopHandle;
                    }
                }

                if (fileStream is null)
                {
                    await WriteTextResponseAsync(stream, 410, "Gone", "text/plain; charset=utf-8", "This download link has expired or reached its download limit.", isHead: false, extraHeaders: null, token).ConfigureAwait(false);
                    return;
                }
            }

            var contentLength = fileLength == 0 ? 0 : (rangeEnd - rangeStart + 1);
            var statusCode = isPartial ? 206 : 200;
            var reasonPhrase = isPartial ? "Partial Content" : "OK";
            var contentDisposition = BuildContentDisposition(initialSnapshot.FileName);

            var headerBuilder = new StringBuilder(512);
            headerBuilder.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reasonPhrase).Append("\r\n");
            headerBuilder.Append("Content-Type: application/octet-stream\r\n");
            headerBuilder.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            headerBuilder.Append("Accept-Ranges: bytes\r\n");
            if (isPartial)
            {
                headerBuilder.Append("Content-Range: bytes ")
                    .Append(rangeStart.ToString(CultureInfo.InvariantCulture))
                    .Append('-')
                    .Append(rangeEnd.ToString(CultureInfo.InvariantCulture))
                    .Append('/')
                    .Append(fileLength.ToString(CultureInfo.InvariantCulture))
                    .Append("\r\n");
            }

            headerBuilder.Append("Content-Disposition: ").Append(contentDisposition).Append("\r\n");
            headerBuilder.Append("Cache-Control: no-store\r\n");
            headerBuilder.Append("X-Content-Type-Options: nosniff\r\n");
            headerBuilder.Append("Referrer-Policy: no-referrer\r\n");
            headerBuilder.Append("Connection: close\r\n\r\n");

            var headerBytes = Encoding.ASCII.GetBytes(headerBuilder.ToString());
            await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);

            if (!isHead && contentLength > 0)
            {
                if (rangeStart > 0)
                {
                    fileStream.Seek(rangeStart, SeekOrigin.Begin);
                }

                var buffer = ArrayPool<byte>.Shared.Rent(StreamChunkBytes);
                try
                {
                    var remaining = contentLength;
                    while (remaining > 0)
                    {
                        var toRead = (int)Math.Min(buffer.Length, remaining);
                        var read = await fileStream.ReadAsync(buffer.AsMemory(0, toRead), token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        await stream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        remaining -= read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            await WriteTextResponseAsync(stream, 410, "Gone", "text/plain; charset=utf-8", "File is no longer available.", isHead, extraHeaders: null, token).ConfigureAwait(false);
        }
        finally
        {
            if (fileStream is not null)
            {
                await fileStream.DisposeAsync().ConfigureAwait(false);
            }

            StopListenerInBackground(stopHandle);
        }
    }

    private DropEntry? FindByToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var candidateBytes = Encoding.UTF8.GetBytes(token);
        lock (_gate)
        {
            DropEntry? matched = null;
            foreach (var entry in _drops)
            {
                if (entry.TokenBytes.Length == candidateBytes.Length
                    && CryptographicOperations.FixedTimeEquals(entry.TokenBytes, candidateBytes))
                {
                    matched = entry;
                }
            }

            return matched;
        }
    }

    private WebDropInfo BuildInfo(DropEntry entry, DateTimeOffset now, int port)
    {
        var snapshot = SnapshotLocked(entry, now);
        var addresses = _addressProvider();
        if (addresses.Count == 0)
        {
            addresses = [IPAddress.Loopback];
        }

        var urls = new List<string>(addresses.Count);
        foreach (var address in addresses)
        {
            urls.Add($"http://{address}:{port}/d/{snapshot.Token}");
        }

        var primaryUrl = urls[0];
        var downloadUrl = primaryUrl + "/download";

        return new WebDropInfo(
            snapshot.Id,
            snapshot.Token,
            snapshot.FilePath,
            snapshot.FileName,
            snapshot.SizeBytes,
            snapshot.CreatedAt,
            snapshot.ExpiresAt,
            snapshot.MaxDownloads,
            snapshot.DownloadCount,
            snapshot.Revoked,
            snapshot.Active,
            port,
            primaryUrl,
            downloadUrl,
            urls);
    }

    private static DropSnapshot SnapshotLocked(DropEntry entry, DateTimeOffset now)
    {
        return new DropSnapshot(
            entry.Id,
            entry.Token,
            entry.FilePath,
            entry.FileName,
            entry.SizeBytes,
            entry.CreatedAt,
            entry.ExpiresAt,
            entry.MaxDownloads,
            entry.DownloadCount,
            entry.Revoked,
            IsActiveLocked(entry, now));
    }

    internal static string BuildContentDisposition(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        var safeExt = SanitizeAsciiExtension(extension);
        var fallback = string.IsNullOrEmpty(safeExt) ? "download" : "download" + safeExt;
        var encoded = Uri.EscapeDataString(fileName);
        return $"attachment; filename=\"{fallback}\"; filename*=UTF-8''{encoded}";
    }

    private static string SanitizeAsciiExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension) || extension[0] != '.')
        {
            return "";
        }

        var sb = new StringBuilder(extension.Length);
        sb.Append('.');
        for (var i = 1; i < extension.Length && sb.Length < 16; i++)
        {
            var ch = extension[i];
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(ch);
            }
        }

        return sb.Length > 1 ? sb.ToString() : "";
    }

    private static string RenderLandingHtml(DropSnapshot drop, DateTimeOffset now)
    {
        var safeName = WebUtility.HtmlEncode(drop.FileName);
        var sizeText = FormatFileSize(drop.SizeBytes);
        var expiresText = drop.ExpiresAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var remaining = drop.ExpiresAt - now;
        var remainingText = FormatRemaining(remaining);
        var countText = drop.MaxDownloads > 0
            ? $"{drop.DownloadCount} / {drop.MaxDownloads}"
            : $"{drop.DownloadCount} (不限次)";
        var downloadHref = $"/d/{drop.Token}/download";

        return $$"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{safeName}} - 文件下载</title>
              <style>
                :root { color-scheme: light dark; }
                body {
                  margin: 0;
                  min-height: 100vh;
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
                  background: #f5f7fb;
                  color: #182230;
                  padding: 20px;
                  box-sizing: border-box;
                }
                @media (prefers-color-scheme: dark) {
                  body { background: #10151c; color: #e6edf3; }
                  .card { background: #161d27 !important; border-color: #273244 !important; }
                  .meta { color: #9aa7b8 !important; }
                }
                .card {
                  width: 100%;
                  max-width: 520px;
                  background: #ffffff;
                  border: 1px solid #e2e8f0;
                  border-radius: 16px;
                  padding: 28px;
                  box-shadow: 0 12px 32px rgba(15, 23, 42, 0.08);
                }
                h1 {
                  margin: 0 0 16px;
                  font-size: 20px;
                  line-height: 1.4;
                  word-break: break-all;
                }
                .meta {
                  margin: 0 0 24px;
                  font-size: 14px;
                  line-height: 1.8;
                  color: #475467;
                }
                .btn {
                  display: block;
                  width: 100%;
                  box-sizing: border-box;
                  text-align: center;
                  padding: 14px 20px;
                  border-radius: 10px;
                  background: #2563eb;
                  color: #ffffff;
                  text-decoration: none;
                  font-size: 16px;
                  font-weight: 600;
                }
                .btn:hover { background: #1d4ed8; }
              </style>
            </head>
            <body>
              <main class="card">
                <h1>{{safeName}}</h1>
                <div class="meta">
                  <div>文件大小：{{WebUtility.HtmlEncode(sizeText)}}</div>
                  <div>已下载次数：{{WebUtility.HtmlEncode(countText)}}</div>
                  <div>有效期至：{{WebUtility.HtmlEncode(expiresText)}}（剩余 {{WebUtility.HtmlEncode(remainingText)}}）</div>
                </div>
                <a class="btn" href="{{downloadHref}}" download>下载文件 / Download</a>
              </main>
            </body>
            </html>
            """;
    }

    private static string RenderGoneHtml(DropSnapshot drop)
    {
        var reason = drop.Revoked
            ? "该下载链接已被发送方撤销。"
            : drop.MaxDownloads > 0 && drop.DownloadCount >= drop.MaxDownloads
                ? "该下载链接已达到最大下载次数。"
                : "该下载链接已过期或文件不可用。";

        return $$"""
            <!DOCTYPE html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>链接已失效</title>
            </head>
            <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; padding: 40px; text-align: center;">
              <h1>410 Gone</h1>
              <p>{{WebUtility.HtmlEncode(reason)}}</p>
            </body>
            </html>
            """;
    }

    internal static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes} B"
            : string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {units[unitIndex]}");
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "0 秒";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours} 小时 {remaining.Minutes} 分钟";
        }

        if (remaining.TotalMinutes >= 1)
        {
            return $"{(int)remaining.TotalMinutes} 分钟 {remaining.Seconds} 秒";
        }

        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} 秒";
    }

    private static async Task<ParsedHttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken serverToken)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        readCts.CancelAfter(HeaderReadTimeout);

        var buffer = new byte[MaxHeaderBytes];
        var totalRead = 0;
        var headerEndIndex = -1;

        while (totalRead < buffer.Length)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (read == 0)
            {
                return null;
            }

            totalRead += read;
            headerEndIndex = FindHeaderTerminator(buffer.AsSpan(0, totalRead));
            if (headerEndIndex >= 0)
            {
                break;
            }
        }

        if (headerEndIndex < 0)
        {
            return new ParsedHttpRequest("", "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Malformed: true);
        }

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEndIndex);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return new ParsedHttpRequest("", "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Malformed: true);
        }

        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return new ParsedHttpRequest("", "", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Malformed: true);
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                return new ParsedHttpRequest("", "", headers, Malformed: true);
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = value;
        }

        return new ParsedHttpRequest(parts[0], parts[1], headers, Malformed: false);
    }

    private static int FindHeaderTerminator(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i <= data.Length - 4; i++)
        {
            if (data[i] == (byte)'\r'
                && data[i + 1] == (byte)'\n'
                && data[i + 2] == (byte)'\r'
                && data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static string StripQuery(string target)
    {
        var queryIndex = target.IndexOf('?');
        return queryIndex >= 0 ? target[..queryIndex] : target;
    }

    private static bool TryParseDropRoute(string path, out string token, out bool isDownload)
    {
        token = "";
        isDownload = false;

        if (!path.StartsWith("/d/", StringComparison.Ordinal))
        {
            return false;
        }

        var remainder = path[3..];
        if (remainder.Length == 0)
        {
            return false;
        }

        var slashIndex = remainder.IndexOf('/');
        if (slashIndex < 0)
        {
            token = remainder;
            isDownload = false;
            return IsValidTokenFormat(token);
        }

        var candidateToken = remainder[..slashIndex];
        var suffix = remainder[(slashIndex + 1)..];
        if (!string.Equals(suffix, "download", StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsValidTokenFormat(candidateToken))
        {
            return false;
        }

        token = candidateToken;
        isDownload = true;
        return true;
    }

    private static bool IsValidTokenFormat(string token)
    {
        if (token.Length is < 8 or > 128)
        {
            return false;
        }

        foreach (var ch in token)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static RangeParseStatus TryParseSingleByteRange(
        string headerValue,
        long fileLength,
        out long start,
        out long end)
    {
        start = 0;
        end = 0;

        if (!headerValue.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return RangeParseStatus.Ignore;
        }

        var spec = headerValue[6..].Trim();
        if (spec.Length == 0 || spec.Contains(','))
        {
            // Ignore multi-range specs and serve full content per RFC 9110.
            return RangeParseStatus.Ignore;
        }

        var dashIndex = spec.IndexOf('-');
        if (dashIndex < 0)
        {
            return RangeParseStatus.Ignore;
        }

        if (fileLength <= 0)
        {
            return RangeParseStatus.Unsatisfiable;
        }

        var startText = spec[..dashIndex].Trim();
        var endText = spec[(dashIndex + 1)..].Trim();

        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out var suffixLength)
                || suffixLength <= 0)
            {
                return RangeParseStatus.Unsatisfiable;
            }

            start = Math.Max(0, fileLength - suffixLength);
            end = fileLength - 1;
            return RangeParseStatus.Valid;
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out start))
        {
            return RangeParseStatus.Ignore;
        }

        if (start >= fileLength)
        {
            return RangeParseStatus.Unsatisfiable;
        }

        if (endText.Length == 0)
        {
            end = fileLength - 1;
            return RangeParseStatus.Valid;
        }

        if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end))
        {
            return RangeParseStatus.Ignore;
        }

        if (end < start)
        {
            return RangeParseStatus.Unsatisfiable;
        }

        if (end >= fileLength)
        {
            end = fileLength - 1;
        }

        return RangeParseStatus.Valid;
    }

    private static async Task WriteTextResponseAsync(
        NetworkStream stream,
        int statusCode,
        string reasonPhrase,
        string contentType,
        string body,
        bool isHead,
        string? extraHeaders,
        CancellationToken token)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder(256);
        sb.Append("HTTP/1.1 ").Append(statusCode).Append(' ').Append(reasonPhrase).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(bodyBytes.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("X-Content-Type-Options: nosniff\r\n");
        sb.Append("Referrer-Policy: no-referrer\r\n");
        if (!string.IsNullOrEmpty(extraHeaders))
        {
            sb.Append(extraHeaders);
        }

        sb.Append("Connection: close\r\n\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, token).ConfigureAwait(false);
        if (!isHead && bodyBytes.Length > 0)
        {
            await stream.WriteAsync(bodyBytes, token).ConfigureAwait(false);
        }

        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private enum RangeParseStatus
    {
        Ignore,
        Valid,
        Unsatisfiable
    }

    private sealed record ListenerStopHandle(
        TcpListener? Listener,
        CancellationTokenSource? AcceptCts,
        Task? AcceptLoop);

    private sealed record ParsedHttpRequest(
        string Method,
        string Target,
        Dictionary<string, string> Headers,
        bool Malformed);

    private sealed record DropSnapshot(
        string Id,
        string Token,
        string FilePath,
        string FileName,
        long SizeBytes,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        int MaxDownloads,
        int DownloadCount,
        bool Revoked,
        bool Active);

    private sealed class DropEntry(
        string id,
        string token,
        string filePath,
        string fileName,
        long sizeBytes,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        int maxDownloads)
    {
        public string Id { get; } = id;
        public string Token { get; } = token;
        public byte[] TokenBytes { get; } = Encoding.UTF8.GetBytes(token);
        public string FilePath { get; } = filePath;
        public string FileName { get; } = fileName;
        public long SizeBytes { get; } = sizeBytes;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int MaxDownloads { get; } = maxDownloads;
        public int DownloadCount { get; set; }
        public bool Revoked { get; set; }
    }
}
