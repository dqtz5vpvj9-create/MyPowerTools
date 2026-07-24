using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Broker;
using MyPowerTools.Platform.Abstractions;
using AdbForwarder.Surface.Services;

namespace MyPowerTools.Tests;

public sealed class AdbBrokerSecurityTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Installed_resolver_accepts_the_user_level_release_broker()
    {
        var availability = new AdbForwarderElevationService().GetAvailability();

        if (availability.IsAvailable)
        {
            var launch = new InstalledAdbForwarderBrokerLaunchResolver().Resolve();
            Assert.True(Path.IsPathFullyQualified(launch.ExecutablePath));
            Assert.True(WindowsProtectedExecutable.IsTrusted(launch.ExecutablePath, out _));
            Assert.StartsWith(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MyPowerTools"),
                launch.ExecutablePath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(64, launch.Sha256.Length);
        }
        else
        {
            Assert.Contains("管理员组件尚未安装", availability.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Second_click_uses_the_in_memory_digest_and_rejects_disk_tampering()
    {
        var requestDirectory = NewRequestDirectory();
        var launcher = new RecordingLauncher();
        var resolver = new FixedResolver(new AdbForwarderBrokerLaunch(
            Path.Combine(requestDirectory, "MyPowerTools.ElevatedBroker.exe"),
            new string('a', 64)));
        var service = new AdbForwarderElevationService(
            launcher,
            requestDirectory,
            () => DateTimeOffset.UtcNow,
            resolver,
            new FixedSnapshotProvider([]));
        var mapping = Mapping();

        var staged = await service.RequestOrApproveAsync(AdbForwarderBrokerAction.Ensure, mapping, CancellationToken.None);
        var requestPath = Assert.Single(Directory.GetFiles(requestDirectory, "*.json"));
        await File.AppendAllTextAsync(requestPath, " ");
        var rejected = await service.RequestOrApproveAsync(AdbForwarderBrokerAction.Ensure, mapping, CancellationToken.None);

        Assert.Equal(AdbForwarderBrokerDisposition.ApprovalRequired, staged.Disposition);
        Assert.Equal(AdbForwarderBrokerDisposition.Failed, rejected.Disposition);
        Assert.Contains("发生变化", rejected.Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task Broker_binary_change_invalidates_the_staged_approval_before_Uac()
    {
        var requestDirectory = NewRequestDirectory();
        var launcher = new RecordingLauncher();
        var executable = Path.Combine(requestDirectory, "MyPowerTools.ElevatedBroker.exe");
        var resolver = new SequenceResolver(
            new AdbForwarderBrokerLaunch(executable, new string('a', 64)),
            new AdbForwarderBrokerLaunch(executable, new string('b', 64)));
        var service = new AdbForwarderElevationService(
            launcher,
            requestDirectory,
            () => DateTimeOffset.UtcNow,
            resolver,
            new FixedSnapshotProvider([]));

        _ = await service.RequestOrApproveAsync(AdbForwarderBrokerAction.Ensure, Mapping(), CancellationToken.None);
        var rejected = await service.RequestOrApproveAsync(AdbForwarderBrokerAction.Ensure, Mapping(), CancellationToken.None);

        Assert.Equal(AdbForwarderBrokerDisposition.Failed, rejected.Disposition);
        Assert.Contains("审批后发生变化", rejected.Message, StringComparison.Ordinal);
        Assert.Empty(launcher.Calls);
    }

    [Fact]
    public async Task Executor_consumes_the_request_once_and_replay_cannot_apply_again()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var network = new MutableNetworkBroker([]);
        var request = CreateExecutorRequest("Ensure", [Rule()], []);
        var context = Context(network, request.BrokerPath, request.Now);

        var first = await AdbPortProxyApprovalExecutor.ExecuteAsync(request.Arguments, request.Audit, context);
        var replay = await AdbPortProxyApprovalExecutor.ExecuteAsync(request.Arguments, request.Audit, context);

        Assert.Equal(0, first);
        Assert.NotEqual(0, replay);
        Assert.Equal(1, network.ApplyCount);
        Assert.False(File.Exists(request.Path));
    }

    [Fact]
    public async Task Executor_rejects_a_changed_pre_state_before_any_write()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var requested = Rule();
        var network = new MutableNetworkBroker([
            requested with { ConnectPort = requested.ConnectPort + 1 }
        ]);
        var request = CreateExecutorRequest("Ensure", [requested], []);
        var result = await AdbPortProxyApprovalExecutor.ExecuteAsync(
            request.Arguments,
            request.Audit,
            Context(network, request.BrokerPath, request.Now));

        Assert.Equal(3, result);
        Assert.Equal(0, network.ApplyCount);
        Assert.Equal(0, network.RemoveCount);
    }

    [Fact]
    public async Task Executor_rechecks_each_listener_immediately_before_remove()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var requested = Rule();
        var conflicting = requested with { ConnectPort = requested.ConnectPort + 10 };
        var network = new MutableNetworkBroker([requested])
        {
            ListOverride = call => call == 1 ? [requested] : [conflicting]
        };
        var request = CreateExecutorRequest("Remove", [requested], [requested]);
        var result = await AdbPortProxyApprovalExecutor.ExecuteAsync(
            request.Arguments,
            request.Audit,
            Context(network, request.BrokerPath, request.Now));

        Assert.Equal(3, result);
        Assert.Equal(0, network.RemoveCount);
    }

    [Fact]
    public async Task Concurrent_consumers_cannot_replay_one_token()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var network = new MutableNetworkBroker([]);
        var request = CreateExecutorRequest("Ensure", [Rule()], []);
        var context = Context(network, request.BrokerPath, request.Now);

        var results = await Task.WhenAll(
            AdbPortProxyApprovalExecutor.ExecuteAsync(request.Arguments, request.Audit, context),
            AdbPortProxyApprovalExecutor.ExecuteAsync(request.Arguments, request.Audit, context));

        Assert.Contains(0, results);
        Assert.Single(results.Where(result => result == 0));
        Assert.Equal(1, network.ApplyCount);
    }

    [Fact]
    public async Task Oversized_approval_request_is_rejected_before_json_parsing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var network = new MutableNetworkBroker([]);
        var request = CreateExecutorRequest("Ensure", [Rule()], []);
        var oversized = new string('x', (64 * 1024) + 1);
        await File.WriteAllTextAsync(request.Path, oversized, Encoding.UTF8);
        var arguments = request.Arguments.ToArray();
        var digestIndex = Array.IndexOf(arguments, "--digest") + 1;
        arguments[digestIndex] = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(oversized))).ToLowerInvariant();

        var result = await AdbPortProxyApprovalExecutor.ExecuteAsync(
            arguments,
            request.Audit,
            Context(network, request.BrokerPath, request.Now));

        Assert.Equal(2, result);
        Assert.Equal(0, network.ApplyCount);
    }

    [Fact]
    public async Task Exact_rule_appearing_between_precheck_and_apply_returns_noop_without_ownership()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var requested = Rule();
        var network = new MutableNetworkBroker([])
        {
            ListOverride = call => call == 1 ? [] : [requested]
        };
        var request = CreateExecutorRequest("Ensure", [requested], []);
        var result = await AdbPortProxyApprovalExecutor.ExecuteAsync(
            request.Arguments,
            request.Audit,
            Context(network, request.BrokerPath, request.Now));

        Assert.Equal(10, result);
        Assert.Equal(0, network.ApplyCount);
        Assert.Equal(0, network.RemoveCount);
    }

    [Fact]
    public async Task Cancellation_during_a_later_rule_uses_an_independent_rollback_token()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var first = Rule();
        var second = first with { ListenPort = first.ListenPort + 1, ConnectPort = first.ConnectPort + 1 };
        var network = new CancelSecondApplyNetworkBroker();
        var request = CreateExecutorRequest("Ensure", [first, second], []);
        var result = await AdbPortProxyApprovalExecutor.ExecuteAsync(
            request.Arguments,
            request.Audit,
            Context(network, request.BrokerPath, request.Now));

        Assert.Equal(1, result);
        Assert.Equal(2, network.ApplyCount);
        Assert.Equal(1, network.RemoveCount);
        Assert.Empty(await network.ListPortProxyRulesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task List_failure_after_a_successful_write_rolls_the_write_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var network = new FailVerificationListNetworkBroker();
        var request = CreateExecutorRequest("Ensure", [Rule()], []);
        var result = await AdbPortProxyApprovalExecutor.ExecuteAsync(
            request.Arguments,
            request.Audit,
            Context(network, request.BrokerPath, request.Now));

        Assert.Equal(1, result);
        Assert.Equal(1, network.ApplyCount);
        Assert.Equal(1, network.RemoveCount);
        Assert.Empty(await network.ListPortProxyRulesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Audit_write_failure_does_not_relabel_a_successful_network_change()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mpt-adb-audit-failure", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "audit.jsonl");
        var audit = new AuditLog(path);
        await File.WriteAllTextAsync(path, "locked");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        try
        {
            var network = new MutableNetworkBroker([]);
            var result = await new NetworkBroker(network, audit).ApplyAsync(
                "adb-forwarder",
                Rule(),
                "audit failure test",
                CancellationToken.None);

            Assert.True(result.Success, result.Message);
            Assert.Equal(1, network.ApplyCount);
            Assert.NotEmpty(audit.LastWriteError);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("remove")]
    public async Task Cli_portproxy_write_routes_are_fail_closed(string operation)
    {
        var cli = Path.Combine(Root, "src", "MyPowerTools.Cli", "bin", "Release", "net10.0", "MyPowerTools.Cli.dll");
        Assert.True(File.Exists(cli), "Release CLI must be built before the security test.");
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath is { } host && Path.GetFileName(host).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                ? host
                : "dotnet.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { cli, "broker", "portproxy", operation })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var output = await outputTask + await errorTask;

        Assert.Equal(2, process.ExitCode);
        Assert.Contains("only through the installed MyPowerTools.ElevatedBroker.exe", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Executor_rejects_expired_created_and_expires_pair()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var network = new MutableNetworkBroker([]);
        var request = CreateExecutorRequest("Ensure", [Rule()], [], now.AddMinutes(-8), now.AddMinutes(-3));
        var result = await AdbPortProxyApprovalExecutor.ExecuteAsync(
            request.Arguments,
            request.Audit,
            Context(network, request.BrokerPath, now));

        Assert.Equal(2, result);
        Assert.Equal(0, network.ApplyCount);
    }

    [Fact]
    public void Source_gates_use_user_installation_and_an_always_elevated_release_broker()
    {
        var networkSource = File.ReadAllText(Path.Combine(
            Root, "src", "MyPowerTools.Platform.Windows", "WindowsPlatformPack.cs"));
        var elevationSource = File.ReadAllText(Path.Combine(
            Root, "tools", "adb-forwarder", "current-integration", "src",
            "AdbForwarder.Surface", "Services", "AdbForwarderElevationService.cs"));
        var cliSource = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.Cli", "Program.cs"));
        var installer = File.ReadAllText(Path.Combine(Root, "scripts", "install-windows.ps1"));
        var candidateBuilder = File.ReadAllText(Path.Combine(Root, "scripts", "build-installer.ps1"));
        var toolBuilder = File.ReadAllText(Path.Combine(Root, "scripts", "build-all-tools.ps1"));
        var publisher = File.ReadAllText(Path.Combine(Root, "scripts", "publish-windows.ps1"));
        var serviceConfigurator = File.ReadAllText(Path.Combine(Root, "scripts", "configure-user-services.ps1"));
        var runtimeStarter = File.ReadAllText(Path.Combine(Root, "scripts", "start-user-runtime.ps1"));
        var innoInstaller = File.ReadAllText(Path.Combine(Root, "installer", "MyPowerTools.iss"));
        var uninstaller = File.ReadAllText(Path.Combine(Root, "scripts", "uninstall-windows.ps1"));
        var brokerProject = File.ReadAllText(Path.Combine(
            Root, "src", "MyPowerTools.ElevatedBroker", "MyPowerTools.ElevatedBroker.csproj"));
        var brokerProgram = File.ReadAllText(Path.Combine(
            Root, "src", "MyPowerTools.ElevatedBroker", "Program.cs"));
        var brokerManifest = File.ReadAllText(Path.Combine(
            Root, "src", "MyPowerTools.ElevatedBroker", "app.manifest"));
        var validationScript = File.ReadAllText(Path.Combine(
            Root, "scripts", "validate-elevated-broker.ps1"));

        Assert.Contains("Path.Combine(systemDirectory, fileName)", networkSource);
        Assert.Contains("FileName = NetshPath", networkSource);
        Assert.DoesNotContain("FileName = \"netsh.exe\"", networkSource, StringComparison.Ordinal);
        Assert.Contains("netsh portproxy list failed", networkSource);
        Assert.Contains("throw new InvalidOperationException", networkSource);
        Assert.Contains("MyPowerTools.ElevatedBroker.exe", elevationSource);
        Assert.Contains("SpecialFolder.LocalApplicationData", elevationSource);
        Assert.DoesNotContain("SpecialFolder.ProgramFiles", elevationSource, StringComparison.Ordinal);
        Assert.Contains("Verb = \"runas\"", elevationSource);
        Assert.DoesNotContain("dotnet.exe", elevationSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Portproxy writes are accepted only through the installed MyPowerTools.ElevatedBroker.exe", cliSource);
        Assert.DoesNotContain("ApplyChangeSetAsync", cliSource, StringComparison.Ordinal);
        Assert.DoesNotContain("#if false", cliSource, StringComparison.Ordinal);
        Assert.Contains("Join-Path $env:LOCALAPPDATA 'Programs\\MyPowerTools'", installer);
        Assert.Contains("$CanonicalInstallDir", installer);
        Assert.Contains("must be installed for the current user", installer);
        Assert.DoesNotContain("Run this installer from an elevated PowerShell session", installer, StringComparison.Ordinal);
        Assert.Contains("$canonicalInstallBase", candidateBuilder);
        Assert.Contains("IsolatedVerification", candidateBuilder);
        Assert.Contains("GetRelativePath($repoRoot, $collectDir)", toolBuilder);
        Assert.DoesNotContain("output       = \"artifacts/tools/", toolBuilder, StringComparison.Ordinal);
        Assert.Contains("MyPowerTools.ServiceManager\\MyPowerTools.ServiceManager.csproj", publisher);
        Assert.Contains("$publishedServiceUnitsRoot", publisher);
        Assert.Contains("configure-user-services.ps1", publisher);
        Assert.Contains("MPT_INSTALL_ROOT", serviceConfigurator);
        Assert.Contains("installed-service-units.json", serviceConfigurator);
        Assert.Contains("service-units", serviceConfigurator);
        Assert.Contains("runtime launch is blocked in Windows Session 0", serviceConfigurator);
        Assert.Contains("$currentSessionId -ne 0", serviceConfigurator);
        Assert.Contains("-RegisterOnly", installer);
        Assert.Contains("Invoke-InteractiveRuntimeBootstrap", installer);
        Assert.Contains("-LogonType Interactive", installer);
        Assert.Contains("runtime launch is blocked in Windows Session 0", runtimeStarter);
        Assert.Contains("$sessionId -eq 0", runtimeStarter);
        Assert.Contains("start-user-runtime.ps1", publisher);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\MyPowerTools", innoInstaller);
        Assert.Contains("PrivilegesRequired=lowest", innoInstaller);
        Assert.Contains("DisableDirPage=yes", innoInstaller);
        Assert.Contains("UsePreviousAppDir=no", innoInstaller);
        Assert.Contains("configure-user-services.ps1", innoInstaller);
        Assert.Contains("foreach ($process in Get-Process -ErrorAction SilentlyContinue)", uninstaller);
        Assert.Contains("Test-IsInsidePath -Parent $Root -Child $path", uninstaller);
        Assert.Contains("<PublishAot>true</PublishAot>", brokerProject);
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", brokerProject);
        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", brokerProject);
        Assert.Contains("level=\"requireAdministrator\"", brokerManifest);
        Assert.Contains("new AuditLog(auditPath, brokerRoot)", brokerProgram);
        Assert.Contains("CLR header", validationScript);
        Assert.Contains("requireAdministrator", validationScript);
        Assert.DoesNotContain("Start-Process -FilePath $brokerExe", validationScript, StringComparison.Ordinal);
    }

    private static ExecutorRequest CreateExecutorRequest(
        string action,
        IReadOnlyList<PortProxyRule> requested,
        IReadOnlyList<PortProxyRule> initial,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null)
    {
        var now = createdAt ?? DateTimeOffset.UtcNow;
        var expiry = expiresAt ?? now.AddMinutes(5);
        var brokerPath = typeof(AdbPortProxyApprovalExecutor).Assembly.Location;
        var brokerHash = HashFile(brokerPath);
        var preconditions = AdbPortProxyPreState.Capture(requested, initial);
        var token = Guid.NewGuid().ToString("N");
        var requestDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools",
            "broker-requests");
        Directory.CreateDirectory(requestDirectory);
        var path = Path.Combine(requestDirectory, $"{token}.json");
        var rulesJson = new JsonArray(requested.Select(rule => (JsonNode)new JsonObject
        {
            ["listenAddress"] = rule.ListenAddress,
            ["listenPort"] = rule.ListenPort,
            ["connectAddress"] = rule.ConnectAddress,
            ["connectPort"] = rule.ConnectPort
        }).ToArray());
        var preconditionsJson = new JsonArray(preconditions.Select(item => (JsonNode)new JsonObject
        {
            ["listenAddress"] = item.ListenAddress,
            ["listenPort"] = item.ListenPort,
            ["exists"] = item.Exists,
            ["connectAddress"] = item.ConnectAddress,
            ["connectPort"] = item.ConnectPort
        }).ToArray());
        var root = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["token"] = token,
            ["moduleId"] = "adb-forwarder",
            ["createdAt"] = now.ToString("O"),
            ["expiresAt"] = expiry.ToString("O"),
            ["action"] = action,
            ["rules"] = rulesJson,
            ["preconditions"] = preconditionsJson,
            ["preStateSha256"] = AdbPortProxyPreState.Hash(preconditions),
            ["broker"] = new JsonObject
            {
                ["path"] = brokerPath,
                ["sha256"] = brokerHash
            }
        };
        var content = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var digest = HashText(content);
        var auditPath = Path.Combine(Path.GetTempPath(), "mpt-adb-broker-audit", $"{token}.jsonl");
        return new ExecutorRequest(
            path,
            brokerPath,
            now,
            new AuditLog(auditPath),
            [
                "--request-file", path,
                "--token", token,
                "--digest", digest,
                "--broker-sha256", brokerHash
            ]);
    }

    private static AdbPortProxyApprovalExecutionContext Context(
        INetworkBroker network,
        string brokerPath,
        DateTimeOffset now) => new(network, brokerPath, true, now);

    private static PortProxyRule Rule() => new("127.0.0.1", 65421, "127.0.0.1", 65422);

    private static AdbForwarderMapping Mapping() =>
        new("secure", "Secure", true, "127.0.0.1", 65421, "127.0.0.1", 65422);

    private static string NewRequestDirectory() =>
        Path.Combine(Path.GetTempPath(), "mpt-adb-broker-service", Guid.NewGuid().ToString("N"));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashText(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MyPowerTools.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed record ExecutorRequest(
        string Path,
        string BrokerPath,
        DateTimeOffset Now,
        AuditLog Audit,
        string[] Arguments);

    private sealed class FixedResolver(AdbForwarderBrokerLaunch launch) : IAdbForwarderBrokerLaunchResolver
    {
        public AdbForwarderBrokerLaunch Resolve() => launch;
    }

    private sealed class SequenceResolver(params AdbForwarderBrokerLaunch[] launches) : IAdbForwarderBrokerLaunchResolver
    {
        private int _index;

        public AdbForwarderBrokerLaunch Resolve()
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, launches.Length - 1);
            return launches[index];
        }
    }

    private sealed class FixedSnapshotProvider(IReadOnlyList<PortProxyRule> rules)
        : IAdbForwarderPortProxySnapshotProvider
    {
        public Task<IReadOnlyList<PortProxyRule>> ListAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(rules);
        }
    }

    private sealed class RecordingLauncher(int exitCode = 0) : IAdbForwarderElevatedProcessLauncher
    {
        public List<(AdbForwarderBrokerLaunch Launch, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<int> RunAsync(
            AdbForwarderBrokerLaunch launch,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((launch, arguments));
            return Task.FromResult(exitCode);
        }
    }

    private sealed class MutableNetworkBroker(IReadOnlyList<PortProxyRule> initial) : INetworkBroker
    {
        private readonly object _gate = new();
        private List<PortProxyRule> _rules = initial.ToList();
        private int _listCount;
        private int _applyCount;
        private int _removeCount;

        public Func<int, IReadOnlyList<PortProxyRule>>? ListOverride { get; init; }
        public int ApplyCount => Volatile.Read(ref _applyCount);
        public int RemoveCount => Volatile.Read(ref _removeCount);

        public Task<IReadOnlyList<PortProxyRule>> ListPortProxyRulesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var call = Interlocked.Increment(ref _listCount);
            if (ListOverride is not null)
            {
                return Task.FromResult(ListOverride(call));
            }
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<PortProxyRule>>(_rules.ToArray());
            }
        }

        public Task<BrokerOperationResult> ApplyPortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _applyCount);
            lock (_gate)
            {
                if (_rules.Any(existing => SameListener(existing, rule)))
                {
                    return Task.FromResult(new BrokerOperationResult(false, "conflict", "listener already exists"));
                }
                _rules.Add(rule);
            }
            return Task.FromResult(new BrokerOperationResult(true, "success", "applied"));
        }

        public Task<BrokerOperationResult> RemovePortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _removeCount);
            lock (_gate)
            {
                _rules.RemoveAll(existing => SameRule(existing, rule));
            }
            return Task.FromResult(new BrokerOperationResult(true, "success", "removed"));
        }

        private static bool SameListener(PortProxyRule left, PortProxyRule right) =>
            string.Equals(left.ListenAddress, right.ListenAddress, StringComparison.OrdinalIgnoreCase) &&
            left.ListenPort == right.ListenPort;

        private static bool SameRule(PortProxyRule left, PortProxyRule right) =>
            SameListener(left, right) &&
            string.Equals(left.ConnectAddress, right.ConnectAddress, StringComparison.OrdinalIgnoreCase) &&
            left.ConnectPort == right.ConnectPort;
    }

    private sealed class CancelSecondApplyNetworkBroker : INetworkBroker
    {
        private readonly List<PortProxyRule> _rules = [];
        public int ApplyCount { get; private set; }
        public int RemoveCount { get; private set; }

        public Task<IReadOnlyList<PortProxyRule>> ListPortProxyRulesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<PortProxyRule>>(_rules.ToArray());
        }

        public Task<BrokerOperationResult> ApplyPortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCount++;
            if (ApplyCount == 2)
            {
                throw new OperationCanceledException("simulated main deadline");
            }
            _rules.Add(rule);
            return Task.FromResult(new BrokerOperationResult(true, "success", "applied"));
        }

        public Task<BrokerOperationResult> RemovePortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveCount++;
            _rules.RemoveAll(existing =>
                string.Equals(existing.ListenAddress, rule.ListenAddress, StringComparison.OrdinalIgnoreCase) &&
                existing.ListenPort == rule.ListenPort);
            return Task.FromResult(new BrokerOperationResult(true, "success", "removed"));
        }
    }

    private sealed class FailVerificationListNetworkBroker : INetworkBroker
    {
        private readonly List<PortProxyRule> _rules = [];
        private int _listCount;
        public int ApplyCount { get; private set; }
        public int RemoveCount { get; private set; }

        public Task<IReadOnlyList<PortProxyRule>> ListPortProxyRulesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _listCount) == 3)
            {
                throw new InvalidOperationException("simulated netsh list failure");
            }
            return Task.FromResult<IReadOnlyList<PortProxyRule>>(_rules.ToArray());
        }

        public Task<BrokerOperationResult> ApplyPortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCount++;
            _rules.Add(rule);
            return Task.FromResult(new BrokerOperationResult(true, "success", "applied"));
        }

        public Task<BrokerOperationResult> RemovePortProxyRuleAsync(PortProxyRule rule, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveCount++;
            _rules.RemoveAll(existing =>
                string.Equals(existing.ListenAddress, rule.ListenAddress, StringComparison.OrdinalIgnoreCase) &&
                existing.ListenPort == rule.ListenPort);
            return Task.FromResult(new BrokerOperationResult(true, "success", "removed"));
        }
    }
}
