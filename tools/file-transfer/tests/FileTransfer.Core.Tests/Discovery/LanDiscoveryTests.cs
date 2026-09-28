using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FileTransfer.Core;
using FileTransfer.Core.Discovery;

namespace FileTransfer.Tests.Discovery;

/// <summary>
/// Real UDP sockets on the loopback interface: the MPT LAN protocol's framing, binding, solicitation,
/// unicast answer, cancellation, re-entrancy and the validation that keeps a hostile datagram out.
/// </summary>
public sealed class LanDiscoveryTests
{
    private static readonly IPAddress Loopback = IPAddress.Loopback;

    /// <summary>A raw client on an exclusive port; nothing here is MPT code.</summary>
    private sealed class RawClient : IDisposable
    {
        public RawClient()
        {
            Socket = new UdpClient(new IPEndPoint(Loopback, 0));
            Port = ((IPEndPoint)Socket.Client.LocalEndPoint!).Port;
        }

        public UdpClient Socket { get; }
        public int Port { get; }

        public async Task<int> SendAsync(byte[] payload, int port) =>
            await Socket.SendAsync(payload, new IPEndPoint(Loopback, port));

        public async Task<byte[]?> ReceiveAsync(int milliseconds)
        {
            using var timeout = new CancellationTokenSource(milliseconds);
            try
            {
                var result = await Socket.ReceiveAsync(timeout.Token);
                return result.Buffer;
            }
            catch (OperationCanceledException) { return null; }
            catch (SocketException) { return null; }
        }

        public void Dispose() => Socket.Dispose();
    }

    private static byte[] Encode(string json) => Encoding.UTF8.GetBytes(json);

    private static byte[] ValidWhoIs(string deviceId = "phone-raw", string name = "Raw Phone",
        string address = "100.64.0.9", int port = TransferFiles.Port, string nonce = "0011223344556677") =>
        LanDiscoveryProtocol.Encode(new LanDiscoveryProtocol.Packet(
            LanDiscoveryProtocol.Magic, LanDiscoveryProtocol.Version, LanDiscoveryProtocol.WhoIs,
            HelloProtocol.Version, deviceId, name, address, port, "android", nonce));

    [Fact]
    public async Task ARealSolicitationGetsAUnicastAnswerAndTheDeviceBecomesAvailable()
    {
        await using var hello = new HelloServer(
            HelloServer.Answer("pc-peer", "Peer PC", "100.64.0.5", TransferFiles.Port, "windows"));
        var rendezvous = DiscoveryTestSupport.FreeUdpPort();
        var peer = new DiscoveredDevice("pc-peer", "Peer PC", "127.0.0.1", hello.Port, "windows");
        var peerOptions = DiscoveryTestSupport.Options(local: peer, lanPort: rendezvous);
        await using var peerChannel = new LanDiscoveryChannel(peerOptions);
        await using var beacon = new DiscoveryBeacon(peerOptions, peerChannel);
        Assert.True(await beacon.StartAsync(peer, CancellationToken.None));

        // Our side binds an exclusive port, so the unicast answer can only be delivered to this socket.
        var mine = new DiscoveredDevice("phone-self", "My Phone", "127.0.0.1", TransferFiles.Port, "android");
        var myOptions = DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5), local: mine, lanPort: rendezvous)
            with { LanBindPort = DiscoveryTestSupport.FreeUdpPort() };
        await using var myChannel = new LanDiscoveryChannel(myOptions);
        var discovery = new DeviceDiscovery(myOptions, null, new HelloIdentityProbe(myOptions), myChannel);

        var report = await discovery.DiscoverAsync([], CancellationToken.None);

        var device = Assert.Single(report.Devices);
        Assert.True(device.Available);
        Assert.False(device.Paired);
        Assert.Equal(DiscoverySource.Lan, device.Source);
        Assert.Equal("pc-peer", device.Device.DeviceId);
        Assert.Equal("Peer PC", device.Device.Name);
        Assert.Equal(hello.Port, device.Device.Port);
        Assert.True(beacon.AnsweredSolicitations >= 1);
    }

    [Fact]
    public async Task AnAnnouncementCarriesOnlyPublicInformation()
    {
        var rendezvous = DiscoveryTestSupport.FreeUdpPort();
        var local = new DiscoveredDevice("pc-beacon", "Beacon PC", "100.64.0.8", TransferFiles.Port, "windows");
        var options = DiscoveryTestSupport.Options(local: local, lanPort: rendezvous);
        await using var channel = new LanDiscoveryChannel(options);
        await using var beacon = new DiscoveryBeacon(options, channel);

        using var listener = new UdpClient(AddressFamily.InterNetwork);
        listener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Client.Bind(new IPEndPoint(IPAddress.Any, rendezvous));
        listener.JoinMulticastGroup(LanDiscoveryProtocol.Group, Loopback);
        Assert.True(await beacon.StartAsync(local, CancellationToken.None));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await listener.ReceiveAsync(timeout.Token);
        var json = Encoding.UTF8.GetString(received.Buffer);
        using var document = JsonDocument.Parse(json);
        var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "mpt", "v", "kind", "protocol", "deviceId", "name", "address", "port", "platform", "nonce",
            },
            properties);
        Assert.Equal("mpt-discovery", document.RootElement.GetProperty("mpt").GetString());
        Assert.Equal("who-is", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal("100.64.0.8", document.RootElement.GetProperty("address").GetString());
        Assert.Equal(TransferFiles.Port, document.RootElement.GetProperty("port").GetInt32());
        foreach (var property in properties)
            Assert.DoesNotContain("token", property, StringComparison.OrdinalIgnoreCase);
        Assert.True(json.Length < LanDiscoveryProtocol.MaxDatagramBytes);
    }

    [Fact]
    public async Task MalformedDatagramsAreDroppedAndOnlyAValidWhoIsIsAnswered()
    {
        var rendezvous = DiscoveryTestSupport.FreeUdpPort();
        var local = new DiscoveredDevice("pc-beacon", "Beacon PC", "100.64.0.8", TransferFiles.Port, "windows");
        var options = DiscoveryTestSupport.Options(local: local, lanPort: rendezvous);
        await using var channel = new LanDiscoveryChannel(options);
        await using var beacon = new DiscoveryBeacon(options, channel);
        Assert.True(await beacon.StartAsync(local, CancellationToken.None));
        var before = channel.DroppedDatagrams;

        using var client = new RawClient();
        var malformed = new[]
        {
            Encode("{}"),
            Encode("{\"mpt\":\"mpt-discovery\",\"v\":2,\"kind\":\"who-is\"}"),
            Encode(Encoding.UTF8.GetString(ValidWhoIs()).Replace("100.64.0.9", "8.8.8.8")),
            Encode(Encoding.UTF8.GetString(ValidWhoIs()).Replace("0011223344556677", "not-a-nonce")),
            Encode(Encoding.UTF8.GetString(ValidWhoIs()).Replace("\"who-is\"", "\"scan\"")),
            Encode(new string('x', LanDiscoveryProtocol.MaxDatagramBytes + 100)),
        };
        foreach (var payload in malformed) await client.SendAsync(payload, rendezvous);
        var quiet = await client.ReceiveAsync(500);
        Assert.Null(quiet);

        await client.SendAsync(ValidWhoIs(), rendezvous);
        var answer = await client.ReceiveAsync(3000);
        Assert.NotNull(answer);
        var packet = JsonSerializer.Deserialize<LanDiscoveryProtocol.Packet>(answer!, DirectTransfer.Json);
        Assert.NotNull(packet);
        Assert.Equal("here", packet!.Kind);
        Assert.Equal("0011223344556677", packet.Nonce);
        Assert.Equal("pc-beacon", packet.DeviceId);
        Assert.Equal(1, beacon.AnsweredSolicitations);
        Assert.True(channel.DroppedDatagrams - before >= malformed.Length, $"丢弃计数 {channel.DroppedDatagrams - before}");
    }

    [Fact]
    public async Task ADisabledBeaconLeavesNoSocketAnswering()
    {
        var rendezvous = DiscoveryTestSupport.FreeUdpPort();
        var local = new DiscoveredDevice("pc-beacon", "Beacon PC", "100.64.0.8", TransferFiles.Port, "windows");
        var options = DiscoveryTestSupport.Options(local: local, lanPort: rendezvous);
        await using var channel = new LanDiscoveryChannel(options);
        await using var beacon = new DiscoveryBeacon(options, channel);
        Assert.True(await beacon.StartAsync(local, CancellationToken.None));
        await beacon.StopAsync();

        using var client = new RawClient();
        await client.SendAsync(ValidWhoIs(), rendezvous);
        Assert.Null(await client.ReceiveAsync(600));
        Assert.False(channel.IsListening);
        Assert.False(beacon.IsRunning);
    }

    [Fact]
    public async Task StartAndStopAreReentrantAndEnableDiscoverabilityAgain()
    {
        var rendezvous = DiscoveryTestSupport.FreeUdpPort();
        var local = new DiscoveredDevice("pc-beacon", "Beacon PC", "100.64.0.8", TransferFiles.Port, "windows");
        var options = DiscoveryTestSupport.Options(local: local, lanPort: rendezvous);
        await using var channel = new LanDiscoveryChannel(options);
        await using var beacon = new DiscoveryBeacon(options, channel);

        Assert.True(await beacon.StartAsync(local, CancellationToken.None));
        Assert.True(await beacon.StartAsync(local, CancellationToken.None));
        var renamed = local with { Name = "Beacon Renamed" };
        Assert.True(await beacon.StartAsync(renamed, CancellationToken.None));
        Assert.Equal("Beacon Renamed", beacon.LocalDevice!.Name);
        Assert.True(beacon.IsRunning);

        await beacon.StopAsync();
        await beacon.StopAsync();
        Assert.False(beacon.IsRunning);

        Assert.True(await beacon.StartAsync(local, CancellationToken.None));
        Assert.True(beacon.IsRunning);
        using var client = new RawClient();
        await client.SendAsync(ValidWhoIs(), rendezvous);
        Assert.NotNull(await client.ReceiveAsync(3000));
    }

    [Fact]
    public async Task ABeaconRefusesToStartWithoutAUsableTailnetAddress()
    {
        var options = DiscoveryTestSupport.Options(lanPort: DiscoveryTestSupport.FreeUdpPort());
        await using var beacon = new DiscoveryBeacon(options);

        var refused = await beacon.StartAsync(
            new DiscoveredDevice("pc-beacon", "Beacon PC", "203.0.113.5", TransferFiles.Port, "windows"), CancellationToken.None);

        Assert.False(refused);
        Assert.False(beacon.IsRunning);
        Assert.Contains("Tailnet", beacon.Fault);
    }

    [Fact]
    public async Task PassiveListenersSeeAPeerWithoutSoliciting()
    {
        var rendezvous = DiscoveryTestSupport.FreeUdpPort();
        var local = new DiscoveredDevice("pc-beacon", "Beacon PC", "100.64.0.8", TransferFiles.Port, "windows");
        var options = DiscoveryTestSupport.Options(local: local, lanPort: rendezvous);
        await using var channel = new LanDiscoveryChannel(options);
        await using var beacon = new DiscoveryBeacon(options, channel);
        var observed = new List<DiscoveredDevice>();
        beacon.PeerObserved += (device, _) => { lock (observed) observed.Add(device); };
        Assert.True(await beacon.StartAsync(local, CancellationToken.None));

        using var client = new RawClient();
        await client.SendAsync(ValidWhoIs(deviceId: "phone-raw", name: "Raw Phone"), rendezvous);
        Assert.NotNull(await client.ReceiveAsync(3000));

        DiscoveredDevice peer;
        lock (observed) peer = Assert.Single(observed);
        Assert.Equal("phone-raw", peer.DeviceId);
        Assert.Equal("Raw Phone", peer.Name);
        Assert.Equal("100.64.0.9", peer.Address);
        Assert.Equal(1, beacon.ObservedPeers);
    }

    [Fact]
    public async Task ASharedBeaconChannelIsReusedForDiscoveryAndStillSeesAnswers()
    {
        await using var hello = new HelloServer(
            HelloServer.Answer("pc-peer", "Peer PC", "100.64.0.5", TransferFiles.Port, "windows"));
        var rendezvous = DiscoveryTestSupport.FreeUdpPort();
        var peer = new DiscoveredDevice("pc-peer", "Peer PC", "127.0.0.1", hello.Port, "windows");
        var peerOptions = DiscoveryTestSupport.Options(local: peer, lanPort: rendezvous);
        await using var peerChannel = new LanDiscoveryChannel(peerOptions);
        await using var peerBeacon = new DiscoveryBeacon(peerOptions, peerChannel);
        Assert.True(await peerBeacon.StartAsync(peer, CancellationToken.None));

        // M4's wiring: one channel owned by the beacon and reused by the active discovery.
        var mine = new DiscoveredDevice("phone-self", "My Phone", "127.0.0.1", TransferFiles.Port, "android");
        var options = DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5), local: mine, lanPort: rendezvous)
            with { LanBindPort = DiscoveryTestSupport.FreeUdpPort() };
        await using var beacon = new DiscoveryBeacon(options);
        Assert.True(await beacon.StartAsync(mine, CancellationToken.None));

        // The peer's anti-amplification limiter answers one datagram per source per second, so the
        // discovery's own who-is has to be a separate second from the beacon's start announcement.
        await Task.Delay(1100);
        var discovery = new DeviceDiscovery(options, null, new HelloIdentityProbe(options), beacon.Channel);
        var report = await discovery.DiscoverAsync([], CancellationToken.None);

        var device = Assert.Single(report.Devices);
        Assert.True(device.Available);
        Assert.Equal("pc-peer", device.Device.DeviceId);
        // The passive service saw the peer answering this device's own announcement ...
        Assert.True(beacon.ObservedPeers >= 1);
        // ... and the active window reused the same socket instead of binding a second one.
        Assert.True(beacon.Channel.IsListening);
        Assert.True(peerBeacon.AnsweredSolicitations >= 1);
    }

    [Fact]
    public void ProtocolValidationRejectsHostileCandidates()
    {
        Assert.True(LanDiscoveryProtocol.TryDecode(ValidWhoIs(), Loopback, out var good, out var error), error);
        Assert.Equal("phone-raw", good.DeviceId);
        Assert.True(LanDiscoveryProtocol.TryGetDevice(good, out var device, out _));
        Assert.Equal(TransferFiles.Port, device.Port);

        // A public address in the payload is the attack this rule exists for: it would make MPT connect out.
        Assert.False(LanDiscoveryProtocol.TryDecode(
            Encode(Encoding.UTF8.GetString(ValidWhoIs()).Replace("100.64.0.9", "8.8.8.8")), Loopback, out _, out var publicError));
        Assert.Contains("Tailnet", publicError);

        // A LAN address is not a Tailnet address either: files only ever travel over the tailnet.
        Assert.False(LanDiscoveryProtocol.TryDecode(
            Encode(Encoding.UTF8.GetString(ValidWhoIs()).Replace("100.64.0.9", "192.168.1.20")), Loopback, out _, out _));
        // A datagram that claims to come from the internet is dropped before parsing.
        Assert.False(LanDiscoveryProtocol.TryDecode(ValidWhoIs(), IPAddress.Parse("203.0.113.7"), out _, out var sourceError));
        Assert.Contains("局域网", sourceError);
        Assert.False(LanDiscoveryProtocol.TryDecode([], Loopback, out _, out _));
        Assert.False(LanDiscoveryProtocol.TryDecode(
            Encode(new string('x', LanDiscoveryProtocol.MaxDatagramBytes + 1)), Loopback, out _, out var sizeError));
        Assert.Contains("长度", sizeError);
    }

    [Fact]
    public void TheReplyLimiterIsBoundedAndDeterministic()
    {
        var limiter = new LanReplyLimiter(TimeSpan.FromSeconds(1), capacity: 4);

        Assert.True(limiter.TryAccept(Loopback, 1000));
        Assert.False(limiter.TryAccept(Loopback, 1500));
        Assert.True(limiter.TryAccept(Loopback, 2001));

        for (var index = 0; index < 20; index++)
            limiter.TryAccept(IPAddress.Parse($"100.64.0.{index + 1}"), 3000 + index);

        Assert.True(limiter.Count <= 4, $"限流表没有被有界化：{limiter.Count}");
    }

    [Fact]
    public async Task AChannelCannotStartTwice()
    {
        var options = DiscoveryTestSupport.Options(lanPort: DiscoveryTestSupport.FreeUdpPort());
        await using var channel = new LanDiscoveryChannel(options);
        Assert.True(await channel.StartAsync(CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.StartAsync(CancellationToken.None));

        await channel.StopAsync();
        Assert.False(channel.IsListening);
        Assert.True(await channel.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task OurOwnAnnouncementIsNotCountedAsAPeer()
    {
        var rendezvous = DiscoveryTestSupport.FreeUdpPort();
        var local = new DiscoveredDevice("pc-beacon", "Beacon PC", "100.64.0.8", TransferFiles.Port, "windows");
        var options = DiscoveryTestSupport.Options(local: local, lanPort: rendezvous);
        await using var channel = new LanDiscoveryChannel(options);
        await using var beacon = new DiscoveryBeacon(options, channel);

        Assert.True(await beacon.StartAsync(local, CancellationToken.None));
        await Task.Delay(300);

        Assert.Equal(0, beacon.AnsweredSolicitations);
        Assert.Equal(0, beacon.ObservedPeers);
    }
}
