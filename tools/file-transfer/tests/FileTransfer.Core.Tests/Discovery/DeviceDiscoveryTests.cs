using FileTransfer.Core;
using FileTransfer.Core.Discovery;

namespace FileTransfer.Tests.Discovery;

/// <summary>
/// Behaviour of one bounded discovery window with injected sources and an injected probe: nothing here
/// touches the machine's network, so the bounds (concurrency, window, candidate cap), the dedupe, the
/// rejection rules and the "only a real hello makes a device available" rule are all observable.
/// </summary>
public sealed class DeviceDiscoveryTests
{
    private static DiscoveryCandidate Anonymous(string address, string name = "Peer", int port = TransferFiles.Port,
        DiscoverySource source = DiscoverySource.Tailscale, int rank = 2) =>
        new(new DiscoveredDevice("", name, address, port, ""), false, source) { Rank = rank };

    private static DeviceDiscovery Discovery(DiscoveryOptions options, FakeProbe probe, params IDiscoveryCandidateSource[] sources) =>
        new(options, sources, probe);

    [Fact]
    public async Task ConcurrencyIsBoundedWhileEveryCandidateIsProbed()
    {
        var candidates = Enumerable.Range(1, 12)
            .Select(index => Anonymous($"100.64.0.{index}", $"Peer {index}"))
            .ToArray();
        var probe = FakeProbe.SlowAnswers(60);
        var options = DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(10), probeTimeout: TimeSpan.FromSeconds(5), concurrency: 3);

        var report = await Discovery(options, probe, new FakeSource("fake", candidates)).DiscoverAsync([], CancellationToken.None);

        Assert.Equal(12, probe.Calls);
        Assert.True(probe.MaxConcurrent <= 3, $"并发上限被突破：{probe.MaxConcurrent}");
        Assert.Equal(12, report.Devices.Count);
        Assert.All(report.Devices, device => Assert.True(device.Available));
        Assert.Equal(DiscoveryOutcome.Completed, report.Outcome);
    }

    [Fact]
    public async Task CallerCancellationStopsTheWindow()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var probe = FakeProbe.Hangs();
        var options = DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(30), probeTimeout: TimeSpan.FromSeconds(20));
        var discovery = Discovery(options, probe, new FakeSource("fake", Anonymous("100.64.0.7")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.DiscoverAsync([], cancel.Token));
    }

    [Fact]
    public async Task AnExpiredWindowReturnsAPartialReport()
    {
        var probe = new FakeProbe(async (candidate, token) =>
        {
            if (candidate.Device.Address.EndsWith(".1", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new IdentityProbeResult(IdentityProbeStatus.Unreachable, candidate.Device, "不应到达。");
            }
            var device = candidate.Device with { DeviceId = "device-" + candidate.Device.Address };
            return new IdentityProbeResult(IdentityProbeStatus.Available, device, "对方接收服务已应答。");
        });
        var options = DiscoveryTestSupport.Options(window: TimeSpan.FromMilliseconds(700), probeTimeout: TimeSpan.FromSeconds(10));
        var sources = new FakeSource("fake", Anonymous("100.64.0.1"), Anonymous("100.64.0.2"), Anonymous("100.64.0.3"));

        var report = await Discovery(options, probe, sources).DiscoverAsync([], CancellationToken.None);

        Assert.Equal(DiscoveryOutcome.WindowExpired, report.Outcome);
        Assert.Equal("partial", report.State);
        Assert.Equal(2, report.AvailableCount);
        Assert.True(report.Elapsed < TimeSpan.FromSeconds(5), $"窗口没有被有界化：{report.Elapsed}");
    }

    [Fact]
    public async Task CandidatesFromDifferentSourcesForOneEndpointAreDeduplicated()
    {
        var known = new[] { DiscoveryTestSupport.Device("pc-known", "Known PC", "100.64.0.7") };
        var probe = FakeProbe.Answers();
        var sources = new IDiscoveryCandidateSource[]
        {
            new FakeSource("tailscale", Anonymous("100.64.0.7")),
            new FakeSource("lan", new DiscoveryCandidate(
                new DiscoveredDevice("pc-known", "Known PC", "100.64.0.7", TransferFiles.Port, "windows"),
                false, DiscoverySource.Lan) { Rank = 1 }),
        };

        var report = await Discovery(DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5)), probe, sources)
            .DiscoverAsync(known, CancellationToken.None);

        Assert.Equal(1, probe.Calls);
        var device = Assert.Single(report.Devices);
        Assert.True(device.Paired);
        Assert.True(device.Available);
        Assert.Equal("pc-known", device.Device.DeviceId);
        Assert.Equal(DiscoverySource.Known, device.Source);
        Assert.Contains(report.Diagnostics, line => line.Contains("重复候选"));
    }

    [Fact]
    public async Task TwoAddressesOfOneDeviceCollapseIntoOneResultAfterTheHello()
    {
        var probe = new FakeProbe((candidate, _) =>
        {
            var device = candidate.Device with { DeviceId = "pc-same", Platform = "windows" };
            return Task.FromResult(new IdentityProbeResult(IdentityProbeStatus.Available, device, "对方接收服务已应答。"));
        });
        var sources = new FakeSource("fake", Anonymous("100.64.0.11"), Anonymous("100.64.0.12"));

        var report = await Discovery(DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5)), probe, sources)
            .DiscoverAsync([], CancellationToken.None);

        Assert.Equal(2, probe.Calls);
        var device = Assert.Single(report.Devices);
        Assert.Equal("pc-same", device.Device.DeviceId);
    }

    [Fact]
    public async Task PublicAddressesAreNeverProbed()
    {
        var known = new[]
        {
            DiscoveryTestSupport.Device("pc-evil", "Evil", "8.8.8.8"),
            DiscoveryTestSupport.Device("pc-ok", "Ok", "100.64.0.40"),
        };
        var probe = FakeProbe.Answers();
        var sources = new FakeSource("fake", Anonymous("203.0.113.9", "Public Stranger"));

        var report = await Discovery(DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5)), probe, sources)
            .DiscoverAsync(known, CancellationToken.None);

        Assert.Equal(1, probe.Calls);
        Assert.Equal("100.64.0.40", probe.Seen[0].Device.Address);
        Assert.Equal(2, report.Devices.Count);
        var blocked = Assert.Single(report.Devices, device => device.Device.DeviceId == "pc-evil");
        Assert.False(blocked.Available);
        Assert.Contains("Tailnet", blocked.Message);
        Assert.DoesNotContain(report.Devices, device => device.Device.DeviceId.Length == 0);
        Assert.Contains(report.Diagnostics, line => line.Contains("跳过身份查询"));
    }

    [Fact]
    public async Task ACandidateThatNeverAnswersTheHelloIsNotADevice()
    {
        var probe = FakeProbe.Rejects();
        var sources = new FakeSource("fake", Anonymous("100.64.0.21"), Anonymous("100.64.0.22"));

        var report = await Discovery(DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5)), probe, sources)
            .DiscoverAsync([], CancellationToken.None);

        Assert.Empty(report.Devices);
        Assert.Equal(2, probe.Calls);
        Assert.Contains(report.Diagnostics, line => line.Contains("没有通过 v3 身份查询"));
    }

    [Fact]
    public async Task APairedDeviceThatRejectsTheHelloStaysListedButUnavailable()
    {
        var known = new[] { DiscoveryTestSupport.Device("pc-known", "Known PC", "100.64.0.30") };
        var probe = FakeProbe.Rejects("接收密钥不正确，或协议版本不兼容。");

        var report = await Discovery(DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5)), probe,
            new FakeSource("fake")).DiscoverAsync(known, CancellationToken.None);

        var device = Assert.Single(report.Devices);
        Assert.True(device.Paired);
        Assert.False(device.Available);
        Assert.Contains("协议版本不兼容", device.Message);
    }

    [Fact]
    public async Task UnsupportedSourcesAreDiagnosedInsteadOfReportedAsSuccess()
    {
        var probe = FakeProbe.Answers();
        var sources = new FakeSource("tailscale", supported: false, "android 上没有可用的 Tailscale CLI 或 LocalAPI。");

        var report = await Discovery(DiscoveryTestSupport.Options(), probe, sources).DiscoverAsync([], CancellationToken.None);

        Assert.Empty(report.Devices);
        Assert.Equal(DiscoveryOutcome.Unsupported, report.Outcome);
        Assert.Equal("unsupported", report.State);
        Assert.Contains(report.Diagnostics, line => line.Contains("android"));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task TheCandidateCapBoundsTheProbesAndSaysSo()
    {
        var candidates = Enumerable.Range(1, 10).Select(index => Anonymous($"100.64.0.{index}", $"Peer {index}")).ToArray();
        var probe = FakeProbe.Answers();
        var options = DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5), maxCandidates: 3);

        var report = await Discovery(options, probe, new FakeSource("fake", candidates)).DiscoverAsync([], CancellationToken.None);

        Assert.Equal(3, probe.Calls);
        Assert.Equal(3, report.Devices.Count);
        Assert.Contains(report.Diagnostics, line => line.Contains("上限"));
    }

    [Fact]
    public async Task KnownDevicesComeFirstAndStayPaired()
    {
        var known = new[] { DiscoveryTestSupport.Device("pc-known", "Known PC", "100.64.0.50") };
        var sources = new FakeSource("fake", Anonymous("100.64.0.51", "Alpha"), Anonymous("100.64.0.52", "Beta"));
        var probe = FakeProbe.Answers();

        var report = await Discovery(DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5)), probe, sources)
            .DiscoverAsync(known, CancellationToken.None);

        Assert.Equal(3, report.Devices.Count);
        Assert.Equal("pc-known", report.Devices[0].Device.DeviceId);
        Assert.True(report.Devices[0].Paired);
        Assert.Equal("Alpha", report.Devices[1].Device.Name);
    }

    [Fact]
    public async Task AnAnonymousCandidateThatTurnsOutToBePairedIsMarkedPaired()
    {
        var known = new[] { DiscoveryTestSupport.Device("pc-known", "Known PC", "100.64.0.60") };
        var probe = new FakeProbe((candidate, _) =>
        {
            var device = candidate.Device with { DeviceId = "pc-known", Platform = "windows" };
            return Task.FromResult(new IdentityProbeResult(IdentityProbeStatus.Available, device, "对方接收服务已应答。"));
        });
        var sources = new FakeSource("tailscale", Anonymous("100.64.0.60"));

        var report = await Discovery(DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5)), probe, sources)
            .DiscoverAsync(known, CancellationToken.None);

        var device = Assert.Single(report.Devices);
        Assert.True(device.Paired);
        Assert.True(device.Available);
    }

    [Fact]
    public async Task TheLocalDeviceIsNeverProbed()
    {
        var options = DiscoveryTestSupport.Options(local: DiscoveryTestSupport.Device("phone-self", "Self", "100.64.0.70"));
        var probe = FakeProbe.Answers();
        var sources = new FakeSource("fake", new DiscoveryCandidate(
            new DiscoveredDevice("phone-self", "Self", "100.64.0.70", TransferFiles.Port, "android"),
            false, DiscoverySource.Lan));

        var report = await Discovery(options, probe, sources).DiscoverAsync([], CancellationToken.None);

        Assert.Equal(0, probe.Calls);
        Assert.Empty(report.Devices);
    }

    [Fact]
    public async Task ASourceThatThrowsBecomesADiagnosticAndNotAFailure()
    {
        var probe = FakeProbe.Answers();
        var broken = new FakeSource("broken", (_, _) => throw new InvalidOperationException("synthetic"));
        var healthy = new FakeSource("healthy", Anonymous("100.64.0.80"));

        var report = await Discovery(DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5)), probe, broken, healthy)
            .DiscoverAsync([], CancellationToken.None);

        Assert.Single(report.Devices);
        Assert.Contains(report.Diagnostics, line => line.Contains("broken") && line.Contains("synthetic"));
        Assert.Equal(DiscoveryOutcome.Completed, report.Outcome);
    }

    [Fact]
    public async Task ReportExposesTheSurfaceFields()
    {
        var report = await Discovery(DiscoveryTestSupport.Options(window: TimeSpan.FromSeconds(5)), FakeProbe.Answers(),
            new FakeSource("fake", Anonymous("100.64.0.90"))).DiscoverAsync([], CancellationToken.None);

        Assert.True(report.HasDevices);
        Assert.Equal("completed", report.State);
        Assert.False(string.IsNullOrWhiteSpace(report.Devices[0].Message));
        Assert.True(report.Elapsed > TimeSpan.Zero);
    }
}
