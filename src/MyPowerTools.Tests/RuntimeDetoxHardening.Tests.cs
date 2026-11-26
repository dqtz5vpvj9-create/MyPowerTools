using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MyPowerTools.ModuleHost.InProcDotNet;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Tests;

public sealed class RuntimeDetoxHardeningTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "mpt-runtime-detox-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void LogRouter_truncates_messages_over_4kb_and_rotates_with_single_backup()
    {
        var logsDir = Path.Combine(_tempRoot, "logs");
        var router = new LogRouter(logsDir, maxMessageChars: 4096, maxFileBytes: 1024);

        var oversized = new string('A', 6000);
        var truncatedRecord = router.Append("pkg.test", "mod.truncate", "info", oversized);

        Assert.Contains("... [truncated 1904 chars]", truncatedRecord.Message, StringComparison.Ordinal);
        Assert.StartsWith(new string('A', 4096), truncatedRecord.Message, StringComparison.Ordinal);

        var rotateDir = Path.Combine(_tempRoot, "logs-rotate");
        var rotatingRouter = new LogRouter(rotateDir, maxMessageChars: 4096, maxFileBytes: 1024);
        var activePath = Path.Combine(rotateDir, "mod.rotate.jsonl");
        var backupPath = activePath + ".1";

        for (var i = 0; i < 30; i++)
        {
            rotatingRouter.Append("pkg.test", "mod.rotate", "info", $"entry-{i:D2}-" + new string('x', 200));
        }

        Assert.True(File.Exists(activePath), "Active log file should exist after rotation.");
        Assert.True(File.Exists(backupPath), "Single .1 rotated backup should exist.");
        Assert.False(File.Exists(activePath + ".2"), "At most one rotated backup archive should be kept.");
        Assert.True(new FileInfo(activePath).Length < 2048, "Active log file should stay bounded near the rotation threshold.");

        var tail = rotatingRouter.Tail("mod.rotate", 10);
        Assert.NotEmpty(tail);
        Assert.Contains(tail, record => record.Message.StartsWith("entry-29-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostRuntime_stops_event_pump_after_quarantine_instead_of_looping()
    {
        var packageRoot = Path.Combine(_tempRoot, "packages");
        var moduleDir = Path.Combine(packageRoot, "quarantine-demo");
        Directory.CreateDirectory(moduleDir);

        await File.WriteAllBytesAsync(Path.Combine(moduleDir, "Stub.dll"), [0]);
        await File.WriteAllTextAsync(
            Path.Combine(moduleDir, "module.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                id = "mod.quarantine-demo",
                packageId = "pkg.quarantine-demo",
                displayName = "Quarantine Demo Module",
                version = "0.2.0",
                moduleSdk = "1.0",
                entrypoints = new[]
                {
                    new
                    {
                        kind = "inproc-dotnet",
                        priority = 100,
                        assembly = "Stub.dll",
                        type = "Stub.Type"
                    }
                },
                capabilities = new[] { "status", "commands" }
            }));

        var transport = new QuarantiningStubTransport();
        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(_tempRoot, "runtime-data")),
            [transport]);

        runtime.Load(packageRoot);
        runtime.StartModuleEventPump();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (transport.SubscribeAttempts == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        // Wait past the normal retry window to verify the pump exited rather than spinning.
        await Task.Delay(500);

        Assert.Equal(1, transport.SubscribeAttempts);
        var logs = runtime.TailLogs("mod.quarantine-demo");
        var terminalLogs = logs.Where(entry =>
            entry.Message.Contains("Module event stream stopped (terminal)", StringComparison.Ordinal)).ToArray();
        Assert.Single(terminalLogs);

        // Explicit module restart should clear quarantine and cleanly restart the event pump.
        transport.ReleaseQuarantine();
        var restart = await runtime.RestartRuntimeProcessAsync("inproc-dotnet", "module:mod.quarantine-demo", CancellationToken.None);
        Assert.True(restart.Success);

        var restartDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (transport.SubscribeAttempts < 2 && DateTimeOffset.UtcNow < restartDeadline)
        {
            await Task.Delay(25);
        }

        Assert.True(transport.SubscribeAttempts >= 2, "Event pump should restart cleanly after explicit module restart.");
    }

    [Fact]
    public void InProcShadowCopy_prunes_stale_fingerprint_directories_and_rejects_newer_shared_contract_versions()
    {
        var shadowParent = Path.Combine(_tempRoot, "cache", "inproc-shadow", "pkg.sample", "mod.sample");
        Directory.CreateDirectory(shadowParent);

        var oldest = Path.Combine(shadowParent, "aaaa1111aaaa1111");
        var older = Path.Combine(shadowParent, "bbbb2222bbbb2222");
        var previous = Path.Combine(shadowParent, "cccc3333cccc3333");
        var current = Path.Combine(shadowParent, "dddd4444dddd4444");

        var now = DateTime.UtcNow;
        CreateShadowSibling(oldest, now.AddMinutes(-30));
        CreateShadowSibling(older, now.AddMinutes(-20));
        CreateShadowSibling(previous, now.AddMinutes(-10));
        CreateShadowSibling(current, now);

        InProcDotNetModuleHost.PruneShadowCopies(shadowParent, "dddd4444dddd4444");

        Assert.True(Directory.Exists(current), "Current shadow directory must be preserved.");
        Assert.True(Directory.Exists(previous), "One most recent previous shadow directory must be preserved.");
        Assert.False(Directory.Exists(older), "Older shadow directory should be pruned.");
        Assert.False(Directory.Exists(oldest), "Oldest shadow directory should be pruned.");

        // Version compatibility guard: requesting a newer version of a host-shared contract must fail clearly.
        var hostAbstractions = typeof(Sdk.IMptModule).Assembly.GetName();
        var hostVersion = hostAbstractions.Version ?? new Version(1, 0, 0, 0);
        var newerRequest = new AssemblyName(hostAbstractions.Name!)
        {
            Version = new Version(hostVersion.Major, hostVersion.Minor + 1, 0, 0)
        };

        var ex = Assert.Throws<InvalidOperationException>(() => MptPluginLoadContext.ResolveShared(newerRequest));
        Assert.Contains("version_incompatible", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(newerRequest.Version.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains(hostVersion.ToString(), ex.Message, StringComparison.Ordinal);

        // Requesting an equal or older version must resolve to the host's loaded contract assembly.
        var compatibleRequest = new AssemblyName(hostAbstractions.Name!)
        {
            Version = hostVersion
        };
        var resolved = MptPluginLoadContext.ResolveShared(compatibleRequest);
        Assert.Same(typeof(Sdk.IMptModule).Assembly, resolved);
    }

    private static void CreateShadowSibling(string path, DateTime timestampUtc)
    {
        Directory.CreateDirectory(path);
        var marker = Path.Combine(path, ".complete");
        File.WriteAllText(marker, timestampUtc.ToString("O"));
        File.SetLastWriteTimeUtc(marker, timestampUtc);
        Directory.SetLastWriteTimeUtc(path, timestampUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class QuarantiningStubTransport : IModuleTransportRuntime, IModuleTransportDiagnosticsProvider
    {
        private int _subscribeAttempts;
        private int _quarantined = 1;

        public string Kind => "inproc-dotnet";
        public int SubscribeAttempts => Volatile.Read(ref _subscribeAttempts);

        public void ReleaseQuarantine() => Interlocked.Exchange(ref _quarantined, 0);

        public string GetProcessPoolKey(RuntimeModuleRecord module) => $"module:{module.Module.Manifest.Id}";

        public void RegisterProcessPool(string poolKey, string moduleId)
        {
        }

        public void ApplyRestartPolicy(
            string poolKey,
            string restartPolicy,
            string reason,
            DateTimeOffset updatedAt,
            DateTimeOffset? expiresAt)
        {
        }

        public IReadOnlyList<RuntimeProcessDiagnostics> GetProcessDiagnostics() => [];

        public ValueTask<RuntimeProcessRestartResult> RestartProcessAsync(string poolKey, CancellationToken cancellationToken)
        {
            var moduleId = poolKey.StartsWith("module:", StringComparison.OrdinalIgnoreCase)
                ? poolKey["module:".Length..]
                : poolKey;
            return ValueTask.FromResult(new RuntimeProcessRestartResult(
                true,
                Kind,
                poolKey,
                "unloaded",
                "Restarted stub module.",
                [moduleId]));
        }

        public ValueTask<RuntimeProcessPolicyResult> SetRestartPolicyAsync(
            string poolKey,
            bool paused,
            string reason,
            DateTimeOffset? expiresAt,
            CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(new RuntimeProcessPolicyResult(
                true,
                Kind,
                poolKey,
                paused ? "paused" : "ready",
                paused ? "paused" : "auto",
                reason,
                [],
                expiresAt));
        }

        public ValueTask<Sdk.ModuleStatusSnapshot?> GetStatusAsync(
            RuntimeModuleRecord module,
            Sdk.ModuleContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<Sdk.ModuleStatusSnapshot?>(null);

        public ValueTask<Sdk.SettingsSchemaDocument> GetSettingsSchemaAsync(
            RuntimeModuleRecord module,
            Sdk.ModuleContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new Sdk.SettingsSchemaDocument(module.Module.Manifest.Id, "{}"));

        public ValueTask<IReadOnlyList<Sdk.MptCommandDescriptor>> ListCommandsAsync(
            RuntimeModuleRecord module,
            Sdk.ModuleContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<Sdk.MptCommandDescriptor>>([]);

        public ValueTask<Sdk.CommandExecutionResult> ExecuteCommandAsync(
            RuntimeModuleRecord module,
            Sdk.ModuleContext context,
            Sdk.CommandRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new Sdk.CommandExecutionResult(request.InvocationId, request.CommandId, "completed", true, "ok"));

        public async IAsyncEnumerable<Sdk.MptModuleEvent> SubscribeEventsAsync(
            RuntimeModuleRecord module,
            Sdk.ModuleContext context,
            Sdk.EventCursor cursor,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _subscribeAttempts);
            if (Volatile.Read(ref _quarantined) != 0)
            {
                throw new InProcModuleCircuitOpenException(
                    module.Module.Manifest.Id,
                    "events.subscribe",
                    3,
                    $"InProc module '{module.Module.Manifest.Id}' is quarantined by the soft-isolation circuit breaker.");
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
