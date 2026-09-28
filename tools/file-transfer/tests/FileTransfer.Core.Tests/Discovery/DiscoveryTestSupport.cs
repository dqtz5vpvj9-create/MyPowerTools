using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FileTransfer.Core;
using FileTransfer.Core.Discovery;

namespace FileTransfer.Tests.Discovery;

/// <summary>
/// Shared fixtures for the discovery tests. Nothing here touches the machine's real network: peers,
/// probes and interface addresses are injected, and the only real sockets are loopback listeners the
/// test itself creates.
/// </summary>
internal static class DiscoveryTestSupport
{
    public static DiscoveredDevice Device(
        string deviceId = "pc-fixture", string name = "Fixture PC", string address = "100.64.0.5",
        int port = TransferFiles.Port, string platform = "windows") => new(deviceId, name, address, port, platform);

    public static DiscoveryOptions Options(
        TimeSpan? window = null, TimeSpan? probeTimeout = null, int concurrency = 4, int maxCandidates = 32,
        DiscoveredDevice? local = null, int lanPort = 0, TimeSpan? lanReplyWindow = null, int? lanAttempts = null) => new()
    {
        Window = window ?? TimeSpan.FromSeconds(3),
        ProbeTimeout = probeTimeout ?? TimeSpan.FromSeconds(1),
        ConnectTimeout = TimeSpan.FromMilliseconds(500),
        MaxConcurrency = concurrency,
        MaxCandidates = maxCandidates,
        LanInterface = IPAddress.Loopback,
        LanPort = lanPort == 0 ? FreeUdpPort() : lanPort,
        LanReplyWindow = lanReplyWindow ?? TimeSpan.FromMilliseconds(400),
        LanSolicitationAttempts = lanAttempts ?? 1,
        LocalDevice = local,
    };

    public static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    public static string TempDirectory()
    {
        var root = Path.Combine(
            Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
            "mpt-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

/// <summary>An injected candidate source; records how it was called and answers from a script.</summary>
internal sealed class FakeSource : IDiscoveryCandidateSource
{
    private readonly Func<DiscoveryRequest, CancellationToken, Task<CandidateSourceResult>> _collect;
    private int _calls;

    public FakeSource(string name, Func<DiscoveryRequest, CancellationToken, Task<CandidateSourceResult>> collect)
    {
        Name = name;
        _collect = collect;
    }

    public FakeSource(string name, params DiscoveryCandidate[] candidates)
        : this(name, (_, _) => Task.FromResult(new CandidateSourceResult(candidates, [], true))) { }

    public FakeSource(string name, bool supported, params string[] diagnostics)
        : this(name, (_, _) => Task.FromResult(new CandidateSourceResult([], diagnostics, supported))) { }

    public string Name { get; }
    public int Calls => Volatile.Read(ref _calls);

    public Task<CandidateSourceResult> CollectAsync(DiscoveryRequest request, CancellationToken token)
    {
        Interlocked.Increment(ref _calls);
        return _collect(request, token);
    }
}

/// <summary>An injected identity probe that also measures how many probes ran at the same time.</summary>
internal sealed class FakeProbe : IIdentityProbe
{
    private readonly Func<DiscoveryCandidate, CancellationToken, Task<IdentityProbeResult>> _handler;
    private readonly List<DiscoveryCandidate> _seen = [];
    private int _current;
    private int _max;
    private int _calls;

    public FakeProbe(Func<DiscoveryCandidate, CancellationToken, Task<IdentityProbeResult>> handler) => _handler = handler;

    public int Calls => Volatile.Read(ref _calls);
    public int MaxConcurrent => Volatile.Read(ref _max);
    public IReadOnlyList<DiscoveryCandidate> Seen { get { lock (_seen) return _seen.ToArray(); } }

    public async Task<IdentityProbeResult> ProbeAsync(DiscoveryCandidate candidate, CancellationToken token)
    {
        lock (_seen) _seen.Add(candidate);
        Interlocked.Increment(ref _calls);
        var current = Interlocked.Increment(ref _current);
        int observed;
        while (current > (observed = Volatile.Read(ref _max)))
            Interlocked.CompareExchange(ref _max, current, observed);
        try { return await _handler(candidate, token); }
        finally { Interlocked.Decrement(ref _current); }
    }

    /// <summary>Every candidate answers with its own id, so a candidate that can be identified is available.</summary>
    public static FakeProbe Answers() => new((candidate, _) =>
    {
        var device = candidate.Device with
        {
            DeviceId = candidate.Device.DeviceId.Length > 0 ? candidate.Device.DeviceId : "device-" + candidate.Device.Address,
            Platform = candidate.Device.Platform.Length > 0 ? candidate.Device.Platform : "linux",
        };
        return Task.FromResult(new IdentityProbeResult(IdentityProbeStatus.Available, device, "对方接收服务已应答。"));
    });

    /// <summary>Answers after a short delay so concurrency is observable.</summary>
    public static FakeProbe SlowAnswers(int milliseconds) => new(async (candidate, token) =>
    {
        await Task.Delay(milliseconds, token);
        var device = candidate.Device with { DeviceId = "device-" + candidate.Device.Address };
        return new IdentityProbeResult(IdentityProbeStatus.Available, device, "对方接收服务已应答。");
    });

    /// <summary>Never answers until the caller's window ends.</summary>
    public static FakeProbe Hangs() => new(async (candidate, token) =>
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return new IdentityProbeResult(IdentityProbeStatus.Unreachable, candidate.Device, "不应到达。");
    });

    public static FakeProbe Rejects(string message = "接收密钥不正确，或协议版本不兼容。") => new((candidate, _) =>
        Task.FromResult(new IdentityProbeResult(IdentityProbeStatus.Rejected, candidate.Device, message)));
}

/// <summary>
/// A real loopback MPT receiver fixture for the v3 hello: it reads the exact int32 big-endian frame
/// off a real TCP socket, keeps the raw bytes for wire assertions, and answers from a script.
/// </summary>
internal sealed class HelloServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _loop;
    private readonly List<Task> _handlers = [];
    private int _connections;

    public HelloServer(Func<Stream, DirectTransfer.Offer, HelloServer, Task>? handler = null)
    {
        Handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(8);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(RunAsync);
    }

    public int Port { get; }
    public Func<Stream, DirectTransfer.Offer, HelloServer, Task>? Handler { get; set; }
    public byte[]? LastFrame { get; private set; }
    public string? LastJson { get; private set; }
    public DirectTransfer.Offer? LastOffer { get; private set; }
    public int Connections => Volatile.Read(ref _connections);

    /// <summary>Cancelled when the fixture is disposed, so a handler that holds a connection can end.</summary>
    public CancellationToken LifetimeToken => _lifetime.Token;

    /// <summary>Closes the connection without answering.</summary>
    public static Task Silent(Stream stream, DirectTransfer.Offer offer, HelloServer server) => Task.CompletedTask;

    /// <summary>Keeps the connection open without answering, so the client's own budget has to expire.</summary>
    public static async Task Hold(Stream stream, DirectTransfer.Offer offer, HelloServer server)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, server.LifetimeToken); }
        catch (OperationCanceledException) { }
    }

    /// <summary>A current production receiver's answer to a v3 hello: it only knows file v1 and probe v2.</summary>
    public static Task LegacyV2(Stream stream, DirectTransfer.Offer offer, HelloServer server) =>
        offer.Version == 2
            ? DirectTransfer.WriteJsonAsync(stream, new DirectTransfer.Reply(false, "probe"), CancellationToken.None)
            : DirectTransfer.WriteJsonAsync(stream, new DirectTransfer.Reply(false, "接收密钥不正确，或协议版本不兼容。"), CancellationToken.None);

    public static Func<Stream, DirectTransfer.Offer, HelloServer, Task> Answer(HelloProtocol.Reply reply) =>
        (stream, _, _) => HelloProtocol.WriteReplyAsync(stream, reply, CancellationToken.None);

    public static Func<Stream, DirectTransfer.Offer, HelloServer, Task> Answer(
        string deviceId, string name, string address, int port, string platform, string? message = null) =>
        Answer(new HelloProtocol.Reply(true, deviceId, name, address, port, platform, message));

    private async Task RunAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_lifetime.Token); }
            catch (Exception) { return; }
            lock (_handlers) _handlers.Add(Task.Run(() => HandleAsync(client)));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        Interlocked.Increment(ref _connections);
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var header = new byte[4];
                await stream.ReadExactlyAsync(header, CancellationToken.None);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                var payload = new byte[length];
                await stream.ReadExactlyAsync(payload, CancellationToken.None);
                LastFrame = [.. header, .. payload];
                LastJson = System.Text.Encoding.UTF8.GetString(payload);
                LastOffer = JsonSerializer.Deserialize<DirectTransfer.Offer>(payload, DirectTransfer.Json);
                if (Handler is not null && LastOffer is not null) await Handler(stream, LastOffer, this);
            }
            catch (Exception) { /* the fixture only records what happened */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        _listener.Stop();
        try { await _loop; } catch (Exception) { }
        Task[] handlers;
        lock (_handlers) handlers = _handlers.ToArray();
        try { await Task.WhenAny(Task.WhenAll(handlers), Task.Delay(TimeSpan.FromSeconds(2))); } catch (Exception) { }
        _lifetime.Dispose();
    }
}
