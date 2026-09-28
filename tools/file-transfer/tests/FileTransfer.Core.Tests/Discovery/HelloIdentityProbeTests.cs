using System.Buffers.Binary;
using System.Net;
using FileTransfer.Core;
using FileTransfer.Core.Discovery;

namespace FileTransfer.Tests.Discovery;

/// <summary>
/// Protocol-level proof of the v3 hello client against a real loopback TCP socket: the exact framed
/// request the contract fixes, the identity fields a device must prove, and the honest answers for an
/// old receiver, a silent peer and a public address.
/// </summary>
public sealed class HelloIdentityProbeTests
{
    private static DiscoveryCandidate Candidate(string deviceId, string address, int port,
        bool paired = false, string name = "Candidate") =>
        new(new DiscoveredDevice(deviceId, name, address, port, ""), paired, DiscoverySource.Tailscale);

    private static HelloIdentityProbe Probe(TimeSpan? probeTimeout = null) =>
        new(DiscoveryTestSupport.Options(probeTimeout: probeTimeout ?? TimeSpan.FromSeconds(2)));

    [Fact]
    public async Task V3HelloOnRealLoopbackSocketExchangesTheContractFrame()
    {
        await using var server = new HelloServer(
            HelloServer.Answer("pc-fixture", "Fixture PC", "100.64.0.5", TransferFiles.Port, "windows"));

        var result = await Probe().ProbeAsync(Candidate("", "127.0.0.1", server.Port), CancellationToken.None);

        Assert.Equal(IdentityProbeStatus.Available, result.Status);
        Assert.True(result.Available);
        Assert.Equal("pc-fixture", result.Device.DeviceId);
        Assert.Equal("Fixture PC", result.Device.Name);
        Assert.Equal("windows", result.Device.Platform);
        // The proven endpoint is kept, not the address the peer claims for itself.
        Assert.Equal("127.0.0.1", result.Device.Address);
        Assert.Equal(server.Port, result.Device.Port);

        // The request is exactly {version:3,kind:"hello"} behind the shared big-endian length prefix.
        Assert.Equal("{\"version\":3,\"kind\":\"hello\"}", server.LastJson);
        Assert.NotNull(server.LastFrame);
        Assert.Equal(server.LastFrame!.Length, 4 + BinaryPrimitives.ReadInt32BigEndian(server.LastFrame));
        Assert.Equal(3, server.LastOffer!.Version);
        Assert.Null(server.LastOffer.Token);
    }

    [Fact]
    public async Task PairedCandidateMustProveItsOwnDeviceId()
    {
        await using var server = new HelloServer(
            HelloServer.Answer("pc-someone-else", "Someone Else", "100.64.0.6", TransferFiles.Port, "linux"));

        var result = await Probe().ProbeAsync(
            Candidate("pc-fixture", "127.0.0.1", server.Port, paired: true, name: "Fixture PC"), CancellationToken.None);

        Assert.Equal(IdentityProbeStatus.IdentityMismatch, result.Status);
        Assert.False(result.Available);
        Assert.Contains("pc-someone-else", result.Message);
    }

    [Fact]
    public async Task AnOldReceiverRejectsTheV3HelloAndStaysUnavailable()
    {
        await using var server = new HelloServer(HelloServer.LegacyV2);

        var result = await Probe().ProbeAsync(
            Candidate("pc-fixture", "127.0.0.1", server.Port, paired: true), CancellationToken.None);

        Assert.Equal(IdentityProbeStatus.Rejected, result.Status);
        Assert.False(result.Available);
        Assert.Contains("协议版本不兼容", result.Message);
    }

    [Fact]
    public async Task AnAnswerWithoutTheV3FieldsIsNotAvailable()
    {
        await using var server = new HelloServer(HelloServer.Answer(new HelloProtocol.Reply(true, DeviceId: "pc-fixture")));

        var result = await Probe().ProbeAsync(Candidate("", "127.0.0.1", server.Port), CancellationToken.None);

        Assert.Equal(IdentityProbeStatus.LegacyProtocol, result.Status);
        Assert.False(result.Available);
        Assert.Contains("v3 身份帧", result.Message);
    }

    [Fact]
    public async Task PublicCandidatesAreRefusedWithoutOpeningASocket()
    {
        var started = Environment.TickCount64;
        var result = await Probe().ProbeAsync(Candidate("", "8.8.8.8", TransferFiles.Port), CancellationToken.None);

        Assert.Equal(IdentityProbeStatus.InvalidCandidate, result.Status);
        Assert.False(result.Available);
        Assert.True(Environment.TickCount64 - started < 1000, "公网候选不应触发网络连接。");
    }

    [Fact]
    public async Task InvalidPortsAndTheLocalDeviceAreRefused()
    {
        var invalidPort = await Probe().ProbeAsync(Candidate("", "100.64.0.5", 0), CancellationToken.None);
        Assert.Equal(IdentityProbeStatus.InvalidCandidate, invalidPort.Status);

        var options = DiscoveryTestSupport.Options(local: DiscoveryTestSupport.Device("phone-self", "Self", "100.64.0.9"));
        var probe = new HelloIdentityProbe(options);
        var self = await probe.ProbeAsync(Candidate("phone-self", "100.64.0.9", TransferFiles.Port), CancellationToken.None);
        Assert.Equal(IdentityProbeStatus.InvalidCandidate, self.Status);
        Assert.Contains("本机", self.Message);
    }

    [Fact]
    public async Task ASilentPeerTimesOutWithinTheProbeBudget()
    {
        await using var server = new HelloServer(HelloServer.Hold);
        var started = Environment.TickCount64;

        var result = await Probe(TimeSpan.FromMilliseconds(400))
            .ProbeAsync(Candidate("", "127.0.0.1", server.Port), CancellationToken.None);

        Assert.Equal(IdentityProbeStatus.Unreachable, result.Status);
        Assert.Contains("没有完成身份应答", result.Message);
        Assert.True(Environment.TickCount64 - started < 5000);
    }

    [Fact]
    public async Task AMalformedFrameIsRejectedInsteadOfCrashing()
    {
        await using var server = new HelloServer(async (stream, _, _) =>
            await stream.WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }));

        var result = await Probe().ProbeAsync(Candidate("", "127.0.0.1", server.Port), CancellationToken.None);

        Assert.Equal(IdentityProbeStatus.Unreachable, result.Status);
        Assert.False(result.Available);
    }

    [Fact]
    public async Task AClosedPortIsUnreachable()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await Probe().ProbeAsync(Candidate("", "127.0.0.1", port), CancellationToken.None);

        Assert.Equal(IdentityProbeStatus.Unreachable, result.Status);
        Assert.False(result.Available);
    }
}
