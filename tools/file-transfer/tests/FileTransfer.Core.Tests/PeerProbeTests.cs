using System.Net;
using System.Net.Sockets;
using FileTransfer.Core;

namespace FileTransfer.Tests;

/// <summary>
/// Protocol-level proof for the mobile peer state contract: a device may only be reported online
/// after the paired receiver itself answered, a probe never writes anything on either side, and a
/// v0.1.0 peer that rejects the probe is reported as unverified instead of online.
/// </summary>
public sealed class PeerProbeTests : IDisposable
{
    private const string Key = "probe-pairing-key-0123456789abcdef";
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-probe-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<DirectReceiver> _receivers = [];

    public PeerProbeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var receiver in _receivers) receiver.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private DirectReceiver Receiver(string deviceId = "pc-fixture", string name = "Fixture PC", Action<string, long, long, string>? changed = null)
    {
        var receiver = new DirectReceiver("127.0.0.1", 0, Key, Path.Combine(_root, "inbox"), 1024 * 1024,
            changed ?? ((_, _, _, _) => { }), deviceId: deviceId, deviceName: name);
        _receivers.Add(receiver);
        return receiver;
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task OnlyTheExpectedPairedDeviceMakesAProbeVerified()
    {
        var receiver = Receiver();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var verified = await DirectTransfer.ProbeAsync("127.0.0.1", receiver.Port, Key, "pc-fixture", "phone-test", timeout.Token);
        Assert.True(verified.Reachable);
        Assert.True(verified.Verified);
        Assert.Equal("pc-fixture", verified.DeviceId);
        Assert.Equal("Fixture PC", verified.Name);

        // Someone else's MPT receiver answers, but it is not the device this pairing points at.
        var stranger = await DirectTransfer.ProbeAsync("127.0.0.1", receiver.Port, Key, "pc-other", "phone-test", timeout.Token);
        Assert.True(stranger.Reachable);
        Assert.False(stranger.Verified);
        Assert.NotEqual("", stranger.Message);
    }

    [Fact]
    public async Task AProbeWithTheWrongSecretIsAnsweredButNeverVerified()
    {
        var receiver = Receiver();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var wrong = await DirectTransfer.ProbeAsync("127.0.0.1", receiver.Port, "wrong-secret-0123456789abcdef", "pc-fixture", "phone-test", timeout.Token);
        Assert.True(wrong.Reachable);
        Assert.False(wrong.Verified);
        // The identity is not disclosed to a caller that could not present the pairing secret.
        Assert.Empty(wrong.DeviceId);
        Assert.Empty(wrong.Name);
        Assert.Contains("密钥", wrong.Message);
    }

    [Fact]
    public async Task AnAddressWithoutAnAnsweringReceiverIsNeverOnline()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var refused = await DirectTransfer.ProbeAsync("127.0.0.1", ClosedPort(), Key, "pc-fixture", "phone-test", timeout.Token);
        Assert.False(refused.Reachable);
        Assert.False(refused.Verified);
        Assert.NotEmpty(refused.Message);
    }

    [Fact]
    public async Task AProbeNeverWritesAFileOrReportsATransfer()
    {
        var events = 0;
        var receiver = Receiver(changed: (_, _, _, _) => Interlocked.Increment(ref events));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (var i = 0; i < 3; i++)
        {
            var probe = await DirectTransfer.ProbeAsync("127.0.0.1", receiver.Port, Key, "pc-fixture", "phone-test", timeout.Token);
            Assert.True(probe.Verified);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "inbox")));
        Assert.Equal(0, Volatile.Read(ref events));
        Assert.Null(receiver.Fault);
    }

    [Fact]
    public async Task ALegacyPeerThatRejectsTheProbeIsReachableButUnverified()
    {
        // Simulates a v0.1.0 receiver: it reads the frame, refuses v2 and answers with the v1 shape.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = client.GetStream();
            var offer = await DirectTransfer.ReadJsonAsync<DirectTransfer.Offer>(stream, timeout.Token);
            Assert.Equal(DirectTransfer.ProbeVersion, offer.Version);
            Assert.Equal("phone-test", offer.DeviceId);
            await DirectTransfer.WriteJsonAsync(stream, new DirectTransfer.Reply(false, "接收密钥不正确，或协议版本不兼容。"), timeout.Token);
        }, timeout.Token);
        try
        {
            var result = await DirectTransfer.ProbeAsync("127.0.0.1", port, Key, "pc-fixture", "phone-test", timeout.Token);
            Assert.True(result.Reachable);
            Assert.False(result.Verified);
            Assert.Contains("不兼容", result.Message);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    [Fact]
    public async Task AnUnrelatedEndpointIsNotVerified()
    {
        // A plain TCP service that answers nothing recognizable must never be reported as a peer.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            await client.GetStream().WriteAsync(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }, timeout.Token);
        }, timeout.Token);
        try
        {
            var result = await DirectTransfer.ProbeAsync("127.0.0.1", port, Key, "pc-fixture", "phone-test", timeout.Token);
            Assert.False(result.Verified);
            Assert.NotEqual("pc-fixture", result.DeviceId);
        }
        finally
        {
            listener.Stop();
            await server;
        }
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndPublicAddressesAreRefused()
    {
        var receiver = Receiver();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DirectTransfer.ProbeAsync("127.0.0.1", receiver.Port, Key, "pc-fixture", "phone-test", cancelled.Token));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            DirectTransfer.ProbeAsync("8.8.8.8", 47165, Key, "pc-fixture", "phone-test", CancellationToken.None));
    }
}
