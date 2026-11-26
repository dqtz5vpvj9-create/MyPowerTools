using System.Net;
using System.Text;
using FileTransfer.Core;
using FileTransfer.Core.Discovery;

namespace FileTransfer.Tests;

public sealed class LanDirectTransferTests : IDisposable
{
    private const string PairingToken = "lan-direct-transfer-token-0123456789";
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-lan-direct-test-" + Guid.NewGuid().ToString("N"));

    public LanDirectTransferTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("10.33.0.145")]
    [InlineData("192.168.1.50")]
    [InlineData("172.16.0.5")]
    [InlineData("172.31.255.1")]
    [InlineData("100.64.0.13")]
    [InlineData("127.0.0.1")]
    [InlineData("fd7a:115c:a1e0::1")]
    [InlineData("fc00::1")]
    [InlineData("169.254.10.20")]
    [InlineData("fe80::1")]
    [InlineData("::1")]
    public void PrivateDirectAddressesAreAcceptedAcrossAllCoreGates(string address)
    {
        var ip = IPAddress.Parse(address);

        Assert.True(TransferFiles.IsPrivateDirectAddress(ip));

        var ex = Record.Exception(() => DirectTransfer.RequirePrivateAddress(ip));
        Assert.Null(ex);

        var encoded = new Pairing("lan-peer-1", "LAN Peer", address, PairingToken).Encode();
        var decoded = Pairing.Decode(encoded);
        Assert.Equal(address, decoded.Address);

        Assert.True(DiscoveryAddresses.IsUsableCandidate(address));
        Assert.True(DiscoveryAddresses.IsLocalNetworkSource(ip));

        var packet = LanDiscoveryProtocol.CreateWhoIs(
            new DiscoveredDevice("lan-peer-1", "LAN Peer", address, TransferFiles.Port, "linux"),
            LanDiscoveryProtocol.NewNonce());
        var payload = LanDiscoveryProtocol.Encode(packet);
        Assert.True(LanDiscoveryProtocol.TryDecode(payload, IPAddress.Parse("192.168.1.10"), out var parsed, out var error), error);
        Assert.Equal(address, parsed.Address);
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("172.15.0.1")]
    [InlineData("192.169.1.1")]
    [InlineData("11.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("2606:4700::1")]
    [InlineData("::")]
    [InlineData("ff02::1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    public async Task PublicAndNonDirectAddressesAreStrictlyRejectedAcrossAllCoreGates(string address)
    {
        var ip = IPAddress.Parse(address);

        Assert.False(TransferFiles.IsLanAddress(ip));
        Assert.False(TransferFiles.IsPrivateDirectAddress(ip));

        var directError = Assert.Throws<ArgumentException>(() => DirectTransfer.RequirePrivateAddress(ip));
        Assert.Contains("公网", directError.Message);

        var encoded = new Pairing("bad-peer", "Bad Peer", address, PairingToken).Encode();
        Assert.Throws<ArgumentException>(() => Pairing.Decode(encoded));

        Assert.False(DiscoveryAddresses.IsUsableCandidate(address));
        Assert.False(DiscoveryAddresses.IsLocalNetworkSource(ip));

        var probe = new HelloIdentityProbe(new DiscoveryOptions
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            ProbeTimeout = TimeSpan.FromMilliseconds(200),
        });
        var candidate = new DiscoveryCandidate(
            new DiscoveredDevice("", "Bad Peer", address, TransferFiles.Port, "linux"),
            Paired: false,
            DiscoverySource.Lan);
        var probeResult = await probe.ProbeAsync(candidate, CancellationToken.None);
        Assert.Equal(IdentityProbeStatus.InvalidCandidate, probeResult.Status);
        Assert.False(probeResult.Available);

        var packet = LanDiscoveryProtocol.CreateWhoIs(
            new DiscoveredDevice("bad-peer", "Bad Peer", address, TransferFiles.Port, "linux"),
            LanDiscoveryProtocol.NewNonce());
        Assert.False(LanDiscoveryProtocol.TryDecode(LanDiscoveryProtocol.Encode(packet), IPAddress.Loopback, out _, out _));
    }

    [Theory]
    [InlineData("10.33.0.145", true)]
    [InlineData("172.16.0.5", true)]
    [InlineData("172.31.255.1", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("169.254.1.2", true)]
    [InlineData("fc00::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("100.64.0.13", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    public void IsLanAddressClassifiesPrivateLanRangesAccurately(string address, bool expected)
    {
        Assert.Equal(expected, TransferFiles.IsLanAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void OrderLocalAddressesPrioritizesTailscaleIpv4ThenRfc1918AndExcludesApipaAndLoopback()
    {
        var input = new[]
        {
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("169.254.42.10"),
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("192.168.1.50"),
            IPAddress.Parse("fd7a:115c:a1e0::1"),
            IPAddress.Parse("10.33.0.145"),
            IPAddress.Parse("100.64.0.13"),
            IPAddress.Parse("172.16.0.5"),
            IPAddress.Parse("192.168.1.50"),
            IPAddress.Parse("fe80::1"),
        };

        var ordered = TransferFiles.OrderLocalAddresses(input);

        Assert.Equal(
            ["100.64.0.13", "192.168.1.50", "10.33.0.145", "172.16.0.5", "fd7a:115c:a1e0::1"],
            ordered);
    }

    [Fact]
    public async Task AssistantWireAndOpenListRuntimeRejectPublicAddresses()
    {
        var frame = new AssistantWire.Frame(
            AssistantWire.Version,
            AssistantWire.ItemKind,
            Token: PairingToken,
            DeviceId: "sender-1",
            ItemId: "item-1",
            ItemKind: AssistantWire.TextItem,
            Text: "hello");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            AssistantWire.SendItemAsync("8.8.8.8", TransferFiles.Port, frame, null, null, TimeSpan.FromSeconds(2), CancellationToken.None));

        await using var runtime = new OpenListRuntime(Path.Combine(_root, "openlist"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            runtime.StartAsync("8.8.8.8", CancellationToken.None));
    }

    [Fact]
    public async Task LoopbackDirectReceiverSendProbeAndDisposeSucceedCleanly()
    {
        var inbox = Path.Combine(_root, "inbox");
        var source = Path.Combine(_root, "lan-payload.bin");
        var payload = new byte[256 * 1024 + 17];
        new Random(42).NextBytes(payload);
        await File.WriteAllBytesAsync(source, payload);

        var receiver = new DirectReceiver(
            "127.0.0.1",
            0,
            PairingToken,
            inbox,
            maximumBytes: 2 * 1024 * 1024,
            changed: (_, _, _, _) => { },
            deviceId: "lan-receiver",
            deviceName: "LAN Receiver",
            platform: "linux");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var probe = await DirectTransfer.ProbeAsync(
            "127.0.0.1",
            receiver.Port,
            PairingToken,
            expectedDeviceId: "lan-receiver",
            localDeviceId: "lan-sender",
            timeout.Token);

        Assert.True(probe.Reachable);
        Assert.True(probe.Verified);
        Assert.Equal("lan-receiver", probe.DeviceId);
        Assert.Equal("LAN Receiver", probe.Name);

        long lastProgress = 0;
        var savedName = await DirectTransfer.SendAsync(
            "127.0.0.1",
            receiver.Port,
            PairingToken,
            source,
            progress: (done, _) => lastProgress = done,
            timeout.Token,
            deviceId: "lan-sender");

        Assert.Equal("lan-payload.bin", savedName);
        Assert.Equal(payload.Length, lastProgress);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(inbox, savedName)));

        await receiver.DisposeAsync();
        await receiver.DisposeAsync();
        Assert.Null(receiver.Fault);
    }
}
