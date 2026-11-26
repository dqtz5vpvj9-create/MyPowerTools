using FileTransfer.Core;
using FileTransfer.Core.Discovery;

namespace FileTransfer.Tests.Discovery;

/// <summary>
/// The Tailscale candidate source against injected CLI/LocalAPI fakes: a machine without Tailscale,
/// without a CLI, on an unsupported platform or with a broken daemon must produce an explicit
/// diagnostic, never a quiet "discovery succeeded with nothing".
/// </summary>
public sealed class TailscalePeerSourceTests
{
    private const string StatusJson = """
        {
          "Version": "1.80.0",
          "BackendState": "Running",
          "TailscaleIPs": ["100.100.1.1"],
          "Self": {
            "ID": "nSELF", "HostName": "my-pc", "DNSName": "my-pc.tail.ts.net.", "OS": "windows",
            "TailscaleIPs": ["100.100.1.1"], "Online": true
          },
          "Peer": {
            "nodekey:aaa": {
              "ID": "nAAA", "HostName": "phone", "DNSName": "phone.tail.ts.net.", "OS": "android",
              "TailscaleIPs": ["100.64.0.21"], "Online": true, "Active": true
            },
            "nodekey:bbb": {
              "ID": "nBBB", "HostName": "laptop", "DNSName": "laptop.tail.ts.net.", "OS": "linux",
              "TailscaleIPs": ["100.64.0.22"], "Online": false
            },
            "nodekey:ccc": {
              "ID": "nCCC", "HostName": "shared-in", "DNSName": "shared.tail.ts.net.", "OS": "macos",
              "TailscaleIPs": ["100.64.0.23"], "Online": true, "ShareeNode": true
            },
            "nodekey:ddd": {
              "ID": "nDDD", "HostName": "public-only", "DNSName": "public.tail.ts.net.", "OS": "linux",
              "TailscaleIPs": ["203.0.113.7"], "Online": true
            }
          }
        }
        """;

    private sealed class FakeRunner : IProcessRunner
    {
        public Func<string, IReadOnlyList<string>, ProcessResult> Handler { get; set; } =
            (_, _) => new ProcessResult(false, -1, "", "not found");
        public List<(string Path, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
        {
            lock (Calls) Calls.Add((executable, arguments));
            return Task.FromResult(Handler(executable, arguments));
        }
    }

    private sealed class FakeLocalApi : ITailscaleLocalApiTransport
    {
        public Func<string, string>? Handler { get; set; }
        public Exception? Failure { get; set; }
        public List<string> Calls { get; } = [];

        public Task<string> GetStatusAsync(string socketPath, CancellationToken token)
        {
            lock (Calls) Calls.Add(socketPath);
            if (Failure is not null) return Task.FromException<string>(Failure);
            return Task.FromResult(Handler?.Invoke(socketPath) ?? throw new InvalidOperationException("no handler"));
        }
    }

    private static TailscaleStatusReader Reader(FakeRunner runner, FakeLocalApi? localApi = null,
        string platform = "linux", IReadOnlyList<string>? cliPaths = null) =>
        new(new TailscaleEnvironment(platform, true, cliPaths ?? ["/usr/bin/tailscale"],
            platform == "windows" ? [@"\\.\pipe\ProtectedPrefix\Administrators\Tailscale\tailscaled"] : ["/var/run/tailscale/tailscaled.sock"]),
            runner, localApi, TimeSpan.FromSeconds(1));

    [Fact]
    public async Task ThePeerListBecomesAnonymousCandidatesWithTheRealTailnetAddress()
    {
        var runner = new FakeRunner { Handler = (_, _) => new ProcessResult(true, 0, StatusJson, "") };
        var source = new TailscalePeerSource(Reader(runner));

        var result = await source.CollectAsync(
            new DiscoveryRequest([], DiscoveryTestSupport.Options()), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal(2, result.Candidates.Count);
        var phone = Assert.Single(result.Candidates, candidate => candidate.Device.Name == "phone");
        Assert.Equal("", phone.Device.DeviceId);
        Assert.Equal("100.64.0.21", phone.Device.Address);
        Assert.Equal(TransferFiles.Port, phone.Device.Port);
        Assert.Equal("android", phone.Device.Platform);
        Assert.False(phone.Paired);
        Assert.Equal(2, phone.Rank);
        var laptop = Assert.Single(result.Candidates, candidate => candidate.Device.Name == "laptop");
        Assert.Equal(3, laptop.Rank);
        // A node shared in from another user and a node without a tailnet address never become candidates.
        Assert.DoesNotContain(result.Candidates, candidate => candidate.Device.Name is "shared-in" or "public-only");
        Assert.Contains(result.Diagnostics, line => line.Contains("2 个候选节点"));
        Assert.Single(runner.Calls);
        Assert.Equal("--json", runner.Calls[0].Arguments[^1]);
        Assert.Equal("status", runner.Calls[0].Arguments[0]);
    }

    [Fact]
    public async Task AMissingCliIsReportedInsteadOfAnEmptySuccess()
    {
        var runner = new FakeRunner();
        var localApi = new FakeLocalApi { Failure = new IOException("没有那个文件或目录") };
        var source = new TailscalePeerSource(Reader(runner, localApi));

        var result = await source.CollectAsync(new DiscoveryRequest([], DiscoveryTestSupport.Options()), CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, line => line.Contains("LocalAPI"));
        Assert.Contains(result.Diagnostics, line => line.Contains("没有找到可用的 Tailscale CLI"));
    }

    [Fact]
    public async Task TheLocalApiIsUsedWhenTheCliCannotRun()
    {
        var runner = new FakeRunner();
        var localApi = new FakeLocalApi { Handler = _ => StatusJson };
        var source = new TailscalePeerSource(Reader(runner, localApi));

        var result = await source.CollectAsync(new DiscoveryRequest([], DiscoveryTestSupport.Options()), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Single(localApi.Calls);
    }

    [Fact]
    public async Task TheCliIsPreferredOverTheLocalApi()
    {
        var runner = new FakeRunner { Handler = (_, _) => new ProcessResult(true, 0, StatusJson, "") };
        var localApi = new FakeLocalApi { Handler = _ => StatusJson };
        var source = new TailscalePeerSource(Reader(runner, localApi));

        await source.CollectAsync(new DiscoveryRequest([], DiscoveryTestSupport.Options()), CancellationToken.None);

        Assert.Empty(localApi.Calls);
    }

    [Fact]
    public async Task AFailingCommandIsDiagnosedWithItsOwnMessage()
    {
        var runner = new FakeRunner { Handler = (_, _) => new ProcessResult(true, 1, "", "tailscale: not logged in") };
        var localApi = new FakeLocalApi { Handler = _ => StatusJson };
        var source = new TailscalePeerSource(Reader(runner, localApi));

        var result = await source.CollectAsync(new DiscoveryRequest([], DiscoveryTestSupport.Options()), CancellationToken.None);

        Assert.Contains(result.Diagnostics, line => line.Contains("not logged in"));
        Assert.True(result.Supported);
    }

    [Fact]
    public async Task ATimeoutIsDiagnosed()
    {
        var runner = new FakeRunner { Handler = (_, _) => new ProcessResult(true, -1, "", "命令在 1 秒内没有结束。") };
        var localApi = new FakeLocalApi { Failure = new TimeoutException() };
        var source = new TailscalePeerSource(Reader(runner, localApi));

        var result = await source.CollectAsync(new DiscoveryRequest([], DiscoveryTestSupport.Options()), CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Contains(result.Diagnostics, line => line.Contains("没有结束"));
    }

    [Fact]
    public async Task GarbageOutputIsDiagnosedAsUnparseable()
    {
        var runner = new FakeRunner { Handler = (_, _) => new ProcessResult(true, 0, "<html>not json</html>", "") };
        var localApi = new FakeLocalApi { Handler = _ => StatusJson };
        var source = new TailscalePeerSource(Reader(runner, localApi));

        var result = await source.CollectAsync(new DiscoveryRequest([], DiscoveryTestSupport.Options()), CancellationToken.None);

        Assert.Contains(result.Diagnostics, line => line.Contains("无法解析"));
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task MacOsRetriesTheCliWithAnExplicitSocket()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.Contains("--socket")
                ? new ProcessResult(true, 0, StatusJson, "")
                : new ProcessResult(true, 1, "", "failed to connect to local tailscaled"),
        };
        var environment = new TailscaleEnvironment("macos", true,
            ["/Applications/Tailscale.app/Contents/MacOS/Tailscale"], ["/var/run/tailscaled.socket"]);
        var source = new TailscalePeerSource(new TailscaleStatusReader(environment, runner, new FakeLocalApi(), TimeSpan.FromSeconds(1)));

        var result = await source.CollectAsync(new DiscoveryRequest([], DiscoveryTestSupport.Options()), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(runner.Calls, call => call.Arguments.Contains("--socket") && call.Arguments.Contains("/var/run/tailscaled.socket"));
    }

    [Fact]
    public async Task AndroidHasNoTailscaleSourceAndSaysSo()
    {
        var runner = new FakeRunner();
        var source = new TailscalePeerSource(new TailscaleStatusReader(TailscaleEnvironment.Unsupported("android"), runner, new FakeLocalApi()));

        var result = await source.CollectAsync(new DiscoveryRequest([], DiscoveryTestSupport.Options()), CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Empty(result.Candidates);
        Assert.Contains(result.Diagnostics, line => line.Contains("android"));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task DiscoveryReportsUnsupportedWhenTailscaleIsTheOnlySourceAndItCannotRun()
    {
        var runner = new FakeRunner();
        var localApi = new FakeLocalApi { Failure = new IOException("socket missing") };
        var source = new TailscalePeerSource(Reader(runner, localApi));
        var discovery = new DeviceDiscovery(DiscoveryTestSupport.Options(), [source], FakeProbe.Answers());

        var report = await discovery.DiscoverAsync([], CancellationToken.None);

        Assert.Empty(report.Devices);
        Assert.Equal(DiscoveryOutcome.Unsupported, report.Outcome);
        Assert.Equal("unsupported", report.State);
        Assert.Contains(report.Diagnostics, line => line.Contains("Tailscale"));
    }

    [Fact]
    public async Task TheRealPlatformDetectionNamesTheCurrentPlatform()
    {
        var environment = TailscaleEnvironment.Detect();
        // Only the current platform is asserted; the paths themselves come from Tailscale's own layout.
        Assert.Contains(environment.Platform, new[] { "windows", "macos", "linux", "android", "ios", "browser", "unknown" });
        if (environment.Supported) Assert.NotEmpty(environment.CliPaths);
        else Assert.Empty(environment.CliPaths);
        await Task.CompletedTask;
    }
}
