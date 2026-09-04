using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Grpc.Core;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.ServiceManager.Client;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

internal static class ProcessGateRunner
{
    public static async Task<int> RunA3Async()
    {
        var context = await GateContext.CreateAsync("a3");
        var records = new List<GateRecord>();
        var trackedPids = new HashSet<int>();
        RunningServiceManager? firstManager = null;
        RunningServiceManager? secondManager = null;

        try
        {
            var heartbeat = Path.Combine(context.DataRoot, "lifecycle-heartbeat.txt");
            var unitId = $"a3-service-{context.RunId}";
            var toolId = $"a3-tool-{context.RunId}";
            var otherToolId = $"a3-other-{context.RunId}";
            var readinessPipe = $"mpt-a3-readiness-{context.RunId}";
            context.WriteUnitManifest(unitId, toolId, heartbeat, maxRestarts: 1, readinessPipe: readinessPipe);
            var orphanPids = context.StartFixtureProcesses(heartbeat, readinessPipe, count: 2);
            foreach (var orphanPid in orphanPids) trackedPids.Add(orphanPid);

            var batchUnitId = $"a3-batch-{context.RunId}";
            var batchToolId = $"a3-batch-tool-{context.RunId}";
            var batchHeartbeat = Path.Combine(context.DataRoot, "batch-heartbeat.txt");
            if (OperatingSystem.IsWindows())
            {
                context.WriteBatchUnitManifest(batchUnitId, batchToolId, batchHeartbeat);
            }

            var unreadyUnitId = $"a3-unready-{context.RunId}";
            var advertisedPipe = $"mpt-a3-unready-{context.RunId}";
            var workerPipe = $"mpt-a3-different-{context.RunId}";
            context.WriteUnitManifest(
                unreadyUnitId,
                $"a3-unready-tool-{context.RunId}",
                Path.Combine(context.DataRoot, "unready-heartbeat.txt"),
                maxRestarts: 0,
                readinessPipe: advertisedPipe,
                workerPipe: workerPipe,
                autostart: true,
                readinessTimeoutMs: 5000);

            var managerStart = Stopwatch.StartNew();
            firstManager = context.StartServiceManager("first");
            using var firstAdmin = await context.WaitForClientAsync(firstManager);
            managerStart.Stop();
            Add(records, "A3.0-control-plane-precedes-worker-readiness",
                managerStart.Elapsed < TimeSpan.FromSeconds(4),
                $"controlPlaneMs={managerStart.Elapsed.TotalMilliseconds:0}; blockedWorkerTimeoutMs=5000");
            Add(records, "A3.1-real-manager-catalog", (await firstAdmin.ListUnitsAsync()).Units.Any(unit => unit.UnitId == unitId), unitId);

            var scoped = new ScopedServiceUnitClient(firstAdmin, toolId);
            var started = await scoped.StartAsync(unitId);
            var firstPid = started.Pid ?? 0;
            if (firstPid > 0) trackedPids.Add(firstPid);
            var orphanCleanup = orphanPids.Contains(firstPid) &&
                                await WaitForProcessCountAsync(orphanPids, expectedAlive: 1, TimeSpan.FromSeconds(5));
            Add(records, "A3.1b-orphan-discovery-deduplicates", orphanCleanup,
                $"started={firstPid}; original={string.Join(',', orphanPids)}; alive={orphanPids.Count(ProcessIsAlive)}");
            Add(records, "A3.2-scoped-start-active", started.State == ServiceUnitState.Active && firstPid > 0, $"state={started.State}; pid={firstPid}");
            var readinessRoundTrip = started.Readiness is not null &&
                                     string.Equals(started.Readiness.Kind, "pipe", StringComparison.Ordinal) &&
                                     string.Equals(started.Readiness.Address, readinessPipe, StringComparison.Ordinal) &&
                                     started.Readiness.Timeout == TimeSpan.FromMilliseconds(1000);
            Add(records, "A3.2b-readiness-contract-roundtrip", readinessRoundTrip,
                started.Readiness is null
                    ? "readiness missing"
                    : $"kind={started.Readiness.Kind}; address={started.Readiness.Address}; timeout={started.Readiness.Timeout.TotalMilliseconds}ms");

            var unready = await WaitForSnapshotAsync(
                firstAdmin,
                unreadyUnitId,
                snapshot => snapshot.State == SM.UnitState.Degraded,
                TimeSpan.FromSeconds(7)) ?? throw new InvalidOperationException("Unresponsive startup unit never entered Degraded state.");
            if (unready.Pid > 0) trackedPids.Add(unready.Pid);
            Add(records, "A3.2c-unresponsive-readiness-is-degraded",
                unready.State == SM.UnitState.Degraded &&
                unready.Readiness is { Ok: false } &&
                unready.LastError.Contains("readiness timed out", StringComparison.OrdinalIgnoreCase),
                $"state={unready.State}; ready={unready.Readiness?.Ok}; error={unready.LastError}");
            await firstAdmin.StopAsync(unreadyUnitId);
            context.DeleteUnitManifest(unreadyUnitId);
            await firstAdmin.ReloadAsync();

            Add(records, "A3.3-heartbeat-ready", await WaitForFileGrowthAsync(heartbeat, 0, TimeSpan.FromSeconds(5)), heartbeat);

            var repeated = await scoped.StartAsync(unitId);
            Add(records, "A3.4-start-idempotent", repeated.Pid == firstPid && ProcessIsAlive(firstPid), $"first={firstPid}; repeated={repeated.Pid}");

            var ownEvents = await CollectEventsAsync(scoped, 0, TimeSpan.FromSeconds(2), evt =>
                evt.UnitId == unitId && string.Equals(evt.Payload["state"]?.GetValue<string>(), "active", StringComparison.OrdinalIgnoreCase));
            var activeEvent = ownEvents.LastOrDefault(evt => string.Equals(evt.Payload["state"]?.GetValue<string>(), "active", StringComparison.OrdinalIgnoreCase));
            Add(records, "A3.5-scoped-event-payload", activeEvent is not null && activeEvent.UnitId == unitId,
                activeEvent is null ? "active event missing" : $"seq={activeEvent.Seq}; payload={activeEvent.Payload.ToJsonString()}");

            var otherScoped = new ScopedServiceUnitClient(firstAdmin, otherToolId);
            var otherList = await otherScoped.ListAsync();
            var directDenied = await IsPermissionDeniedAsync(() => otherScoped.GetSnapshotAsync(unitId).AsTask());
            var leakedEvents = await CollectEventsAsync(otherScoped, 0, TimeSpan.FromMilliseconds(700));
            Add(records, "A3.6-server-enforced-scope", otherList.All(unit => unit.Id != unitId) && directDenied && leakedEvents.Count == 0,
                $"visible={otherList.Count}; directDenied={directDenied}; leakedEvents={leakedEvents.Count}");

            var beforeClientDispose = FileLength(heartbeat);
            firstAdmin.Dispose();
            Add(records, "A3.7-client-lifecycle-independent", await WaitForFileGrowthAsync(heartbeat, beforeClientDispose, TimeSpan.FromSeconds(3)) && ProcessIsAlive(firstPid),
                $"pid={firstPid}; before={beforeClientDispose}; after={FileLength(heartbeat)}");

            var batchPid = 0;
            if (OperatingSystem.IsWindows())
            {
                using var batchClient = await context.WaitForClientAsync(firstManager);
                var batchStarted = await batchClient.StartAsync(batchUnitId);
                batchPid = batchStarted.Pid;
                if (batchPid > 0) trackedPids.Add(batchPid);
                Add(records, "A3.7b-batch-wrapper-active",
                    batchStarted.State == SM.UnitState.Active && batchPid > 0 &&
                    await WaitForFileGrowthAsync(batchHeartbeat, 0, TimeSpan.FromSeconds(5)),
                    $"state={batchStarted.State}; pid={batchPid}");
            }

            using (var shutdownClient = await context.WaitForClientAsync(firstManager))
            {
                Add(records, "A3.8-graceful-manager-shutdown", await shutdownClient.ShutdownAsync(), "Shutdown RPC accepted");
            }
            await firstManager.WaitForExitAsync(TimeSpan.FromSeconds(10));
            var afterManagerExit = FileLength(heartbeat);
            var survivedManager = ProcessIsAlive(firstPid) && await WaitForFileGrowthAsync(heartbeat, afterManagerExit, TimeSpan.FromSeconds(3));
            Add(records, "A3.9-unit-outlives-manager", survivedManager, $"managerExited={firstManager.HasExited}; unitPid={firstPid}");

            secondManager = context.StartServiceManager("second");
            using var secondAdmin = await context.WaitForClientAsync(secondManager);
            var readopted = await WaitForSnapshotAsync(secondAdmin, unitId, snap => snap.State == SM.UnitState.Active && snap.Pid == firstPid, TimeSpan.FromSeconds(15));
            Add(records, "A3.10-restart-readopts-same-pid", readopted is not null, readopted is null ? "re-adoption missing" : $"pid={readopted.Pid}; state={readopted.State}");

            if (OperatingSystem.IsWindows())
            {
                var batchReadopted = await WaitForSnapshotAsync(
                    secondAdmin,
                    batchUnitId,
                    snap => snap.State == SM.UnitState.Active && snap.Pid == batchPid,
                    TimeSpan.FromSeconds(6));
                Add(records, "A3.10b-batch-restart-readopts-same-pid", batchReadopted is not null,
                    batchReadopted is null ? $"expected pid={batchPid}" : $"pid={batchReadopted.Pid}; state={batchReadopted.State}");

                await secondAdmin.StopAsync(batchUnitId);
                var batchStoppedAt = FileLength(batchHeartbeat);
                await Task.Delay(700);
                Add(records, "A3.10c-batch-stop-ends-process-tree",
                    !ProcessIsAlive(batchPid) && FileLength(batchHeartbeat) == batchStoppedAt,
                    $"pid={batchPid}; alive={ProcessIsAlive(batchPid)}; before={batchStoppedAt}; after={FileLength(batchHeartbeat)}");
                context.DeleteUnitManifest(batchUnitId);
                await secondAdmin.ReloadAsync();
            }

            var secondScoped = new ScopedServiceUnitClient(secondAdmin, toolId);
            var restarted = await secondScoped.RestartAsync(unitId);
            var secondPid = restarted.Pid ?? 0;
            if (secondPid > 0) trackedPids.Add(secondPid);
            Add(records, "A3.11-explicit-restart-new-pid", restarted.State == ServiceUnitState.Active && secondPid > 0 && secondPid != firstPid,
                $"old={firstPid}; new={secondPid}");

            await Task.Delay(500);
            var logs = await secondAdmin.TailLogsAsync(unitId, 20);
            Add(records, "A3.12-admin-log-path", logs.Any(entry => entry.Message.Contains("heartbeat", StringComparison.OrdinalIgnoreCase)), $"entries={logs.Count}");

            var stopped = await secondScoped.StopAsync(unitId);
            var stoppedAt = FileLength(heartbeat);
            await Task.Delay(700);
            var stoppedCleanly = stopped.State == ServiceUnitState.Inactive && !ProcessIsAlive(secondPid) && FileLength(heartbeat) == stoppedAt;
            Add(records, "A3.13-explicit-stop-owned-lifecycle", stoppedCleanly, $"state={stopped.State}; pidAlive={ProcessIsAlive(secondPid)}");

            var beforeUpgrade = await secondScoped.StartAsync(unitId);
            var beforeUpgradePid = beforeUpgrade.Pid ?? 0;
            if (beforeUpgradePid > 0) trackedPids.Add(beforeUpgradePid);
            var upgradedHeartbeat = Path.Combine(context.DataRoot, "lifecycle-heartbeat-upgraded.txt");
            context.WriteUnitManifest(unitId, toolId, upgradedHeartbeat, maxRestarts: 1, readinessPipe);
            await secondAdmin.ReloadAsync();
            var upgraded = await WaitForSnapshotAsync(
                secondAdmin,
                unitId,
                snapshot => snapshot.State == SM.UnitState.Active && snapshot.Pid > 0 && snapshot.Pid != beforeUpgradePid,
                TimeSpan.FromSeconds(15));
            var upgradedPid = upgraded?.Pid ?? 0;
            if (upgradedPid > 0) trackedPids.Add(upgradedPid);
            var upgradedManifestApplied = upgraded is not null &&
                                          !ProcessIsAlive(beforeUpgradePid) &&
                                          await WaitForFileGrowthAsync(upgradedHeartbeat, 0, TimeSpan.FromSeconds(10));
            Add(records, "A3.14-reload-applies-upgraded-manifest", upgradedManifestApplied,
                $"old={beforeUpgradePid}; new={upgradedPid}; oldAlive={ProcessIsAlive(beforeUpgradePid)}; heartbeat={FileLength(upgradedHeartbeat)}");

            context.DeleteUnitManifest(unitId);
            await secondAdmin.ReloadAsync();
            var afterRemoval = await secondAdmin.ListUnitsAsync();
            var removedCleanly = afterRemoval.Units.All(unit => unit.UnitId != unitId) && !ProcessIsAlive(upgradedPid);
            Add(records, "A3.15-reload-removes-unit-and-process", removedCleanly,
                $"visible={afterRemoval.Units.Any(unit => unit.UnitId == unitId)}; pid={upgradedPid}; alive={ProcessIsAlive(upgradedPid)}");

            await secondAdmin.ShutdownAsync();
            await secondManager.WaitForExitAsync(TimeSpan.FromSeconds(10));
            return await context.CompleteAsync("A3", records, [firstManager, secondManager]);
        }
        catch (Exception ex)
        {
            Add(records, "A3.exception", false, $"{ex.GetType().Name}: {ex.Message}");
            return await context.CompleteAsync("A3", records, [firstManager, secondManager], ex);
        }
        finally
        {
            await StopManagersAsync(firstManager, secondManager);
            KillTrackedProcesses(trackedPids);
            context.Dispose();
        }
    }

    public static async Task<int> RunA4Async()
    {
        var context = await GateContext.CreateAsync("a4");
        var records = new List<GateRecord>();
        var trackedPids = new HashSet<int>();
        RunningServiceManager? manager = null;

        try
        {
            var unitA = $"a4-failing-{context.RunId}";
            var unitB = $"a4-healthy-{context.RunId}";
            var toolA = $"a4-tool-a-{context.RunId}";
            var toolB = $"a4-tool-b-{context.RunId}";
            var heartbeatA = Path.Combine(context.DataRoot, "unit-a-heartbeat.txt");
            var heartbeatB = Path.Combine(context.DataRoot, "unit-b-heartbeat.txt");
            context.WriteUnitManifest(unitA, toolA, heartbeatA, maxRestarts: 0);
            context.WriteUnitManifest(unitB, toolB, heartbeatB, maxRestarts: 1);

            manager = context.StartServiceManager("fault-domain");
            using var admin = await context.WaitForClientAsync(manager);
            var startA = await admin.StartAsync(unitA);
            var startB = await admin.StartAsync(unitB);
            if (startA.Pid > 0) trackedPids.Add(startA.Pid);
            if (startB.Pid > 0) trackedPids.Add(startB.Pid);
            var bothReady = startA.State == SM.UnitState.Active && startB.State == SM.UnitState.Active &&
                            await WaitForFileGrowthAsync(heartbeatA, 0, TimeSpan.FromSeconds(5)) &&
                            await WaitForFileGrowthAsync(heartbeatB, 0, TimeSpan.FromSeconds(5));
            Add(records, "A4.1-two-independent-units-active", bothReady, $"A={startA.Pid}; B={startB.Pid}");

            var bBeforeFault = FileLength(heartbeatB);
            Process.GetProcessById(startA.Pid).Kill(entireProcessTree: false);
            var failedA = await WaitForSnapshotAsync(admin, unitA, snap => snap.State == SM.UnitState.Failed, TimeSpan.FromSeconds(6));
            Add(records, "A4.2-unit-a-crash-contained", failedA is not null, failedA is null ? "failed state missing" : $"state={failedA.State}; error={failedA.LastError}");

            var healthyB = await admin.GetUnitAsync(unitB);
            var bAdvanced = await WaitForFileGrowthAsync(heartbeatB, bBeforeFault, TimeSpan.FromSeconds(3));
            Add(records, "A4.3-unit-b-keeps-serving", healthyB.State == SM.UnitState.Active && healthyB.Pid == startB.Pid && bAdvanced,
                $"state={healthyB.State}; pid={healthyB.Pid}; heartbeatAdvanced={bAdvanced}");

            var list = await admin.ListUnitsAsync();
            var logsB = await admin.TailLogsAsync(unitB, 20);
            Add(records, "A4.4-control-plane-survives", !manager.HasExited && list.Units.Count == 2 && logsB.Any(entry => entry.Message.Contains("heartbeat", StringComparison.OrdinalIgnoreCase)),
                $"managerAlive={!manager.HasExited}; units={list.Units.Count}; logs={logsB.Count}");

            var scopedB = new ScopedServiceUnitClient(admin, toolB);
            var bEvents = await CollectEventsAsync(scopedB, 0, TimeSpan.FromMilliseconds(900));
            var directDenied = await IsPermissionDeniedAsync(() => scopedB.GetSnapshotAsync(unitA).AsTask());
            Add(records, "A4.5-fault-events-remain-scoped", bEvents.Count > 0 && bEvents.All(evt => evt.UnitId == unitB) && directDenied,
                $"events={bEvents.Count}; foreignEvent={bEvents.Any(evt => evt.UnitId == unitA)}; directDenied={directDenied}");

            var recoveredA = await admin.StartAsync(unitA);
            if (recoveredA.Pid > 0) trackedPids.Add(recoveredA.Pid);
            Add(records, "A4.6-failed-unit-recoverable", recoveredA.State == SM.UnitState.Active && recoveredA.Pid > 0 && recoveredA.Pid != startA.Pid,
                $"old={startA.Pid}; recovered={recoveredA.Pid}; state={recoveredA.State}");

            await admin.StopAsync(unitA);
            await admin.StopAsync(unitB);
            await admin.ShutdownAsync();
            await manager.WaitForExitAsync(TimeSpan.FromSeconds(10));
            return await context.CompleteAsync("A4", records, [manager]);
        }
        catch (Exception ex)
        {
            Add(records, "A4.exception", false, $"{ex.GetType().Name}: {ex.Message}");
            return await context.CompleteAsync("A4", records, [manager], ex);
        }
        finally
        {
            await StopManagersAsync(manager);
            KillTrackedProcesses(trackedPids);
            context.Dispose();
        }
    }

    private static void Add(ICollection<GateRecord> records, string id, bool passed, string detail)
    {
        records.Add(new GateRecord(id, passed, detail));
        Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {id}: {detail}");
    }

    private static async Task<List<ServiceUnitEvent>> CollectEventsAsync(
        IServiceUnitClient client,
        ulong cursor,
        TimeSpan duration,
        Func<ServiceUnitEvent, bool>? stopWhen = null)
    {
        using var cancellation = new CancellationTokenSource(duration);
        var events = new List<ServiceUnitEvent>();
        try
        {
            await foreach (var evt in client.SubscribeEventsAsync(new EventCursor(cursor), cancellation.Token))
            {
                events.Add(evt);
                if (stopWhen?.Invoke(evt) == true)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
        }

        return events;
    }

    private static async Task<bool> IsPermissionDeniedAsync(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            return true;
        }
    }

    private static async Task<SM.UnitSnapshot?> WaitForSnapshotAsync(
        ServiceManagerAdminClient client,
        string unitId,
        Func<SM.UnitSnapshot, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var snapshot = await client.GetUnitAsync(unitId);
                if (predicate(snapshot)) return snapshot;
            }
            catch (RpcException)
            {
            }

            await Task.Delay(100);
        }

        return null;
    }

    private static async Task<bool> WaitForFileGrowthAsync(string path, long baseline, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (FileLength(path) > baseline) return true;
            await Task.Delay(100);
        }

        return false;
    }

    private static async Task<bool> WaitForProcessCountAsync(
        IReadOnlyList<int> processIds,
        int expectedAlive,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (processIds.Count(ProcessIsAlive) == expectedAlive)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return processIds.Count(ProcessIsAlive) == expectedAlive;
    }

    private static long FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static bool ProcessIsAlive(int pid)
    {
        if (pid <= 0) return false;
        try { return !Process.GetProcessById(pid).HasExited; }
        catch { return false; }
    }

    private static void KillTrackedProcesses(IEnumerable<int> pids)
    {
        foreach (var pid in pids.Distinct())
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    private static async Task StopManagersAsync(params RunningServiceManager?[] managers)
    {
        foreach (var manager in managers.Where(item => item is not null).Cast<RunningServiceManager>())
        {
            await manager.StopAsync();
        }
    }

    private sealed class GateContext : IDisposable
    {
        private GateContext(string gate, string repoRoot, string runId, string dataRoot, string deployRoot, string fixtureExec, IReadOnlyList<string> fixturePrefixArgs)
        {
            Gate = gate;
            RepoRoot = repoRoot;
            RunId = runId;
            DataRoot = dataRoot;
            DeployRoot = deployRoot;
            FixtureExec = fixtureExec;
            FixturePrefixArgs = fixturePrefixArgs;
            Endpoint = new IpcEndpoint(
                OperatingSystem.IsWindows() ? IpcTransport.NamedPipe : IpcTransport.UnixDomainSocket,
                OperatingSystem.IsWindows() ? $"mypowertools.arch.{gate}.{runId}" : Path.Combine(dataRoot, $"{gate}.sock"));
            InstanceName = $"MyPowerTools.Architecture.{gate}.{runId}";
            Token = ServiceManagerAdminClient.SharedTokenStore.GetOrCreateToken(dataRoot);
            EvidenceDirectory = Path.Combine(repoRoot, "artifacts", "architecture-smoke", gate);
            Directory.CreateDirectory(EvidenceDirectory);
        }

        public string Gate { get; }
        public string RepoRoot { get; }
        public string RunId { get; }
        public string DataRoot { get; }
        public string DeployRoot { get; }
        public string FixtureExec { get; }
        public IReadOnlyList<string> FixturePrefixArgs { get; }
        public IpcEndpoint Endpoint { get; }
        public string InstanceName { get; }
        public string Token { get; }
        public string EvidenceDirectory { get; }

        public static async Task<GateContext> CreateAsync(string gate)
        {
            var repoRoot = FindRepoRoot();
            var runId = Guid.NewGuid().ToString("N")[..8];
            var dataRoot = Path.Combine(Path.GetTempPath(), $"mpt-{gate}-{runId}");
            var deployRoot = Path.Combine(dataRoot, "deploy");
            var unitsRoot = Path.Combine(deployRoot, "units");
            var fixtureOutput = Path.Combine(dataRoot, "fixture");
            Directory.CreateDirectory(unitsRoot);

            var managerProject = Path.Combine(repoRoot, "src", "MyPowerTools.ServiceManager", "MyPowerTools.ServiceManager.csproj");
            var fixtureProject = Path.Combine(repoRoot, "tests", "fixtures", "test-service-unit", "TestServiceUnit.csproj");
            await RunDotnetAsync(["build", managerProject, "-c", "Release", "--nologo", "-v", "quiet"]);
            await RunDotnetAsync(["publish", fixtureProject, "-c", "Release", "-o", fixtureOutput, "--nologo", "-v", "quiet"]);

            var nativeFixture = Path.Combine(fixtureOutput, OperatingSystem.IsWindows() ? "test-service-unit.exe" : "test-service-unit");
            if (File.Exists(nativeFixture))
            {
                return new GateContext(gate, repoRoot, runId, dataRoot, deployRoot, nativeFixture, []);
            }

            var fixtureDll = Path.Combine(fixtureOutput, "test-service-unit.dll");
            if (!File.Exists(fixtureDll)) throw new FileNotFoundException("Published service fixture is missing.", fixtureDll);
            return new GateContext(gate, repoRoot, runId, dataRoot, deployRoot, "dotnet", [fixtureDll]);
        }

        public void WriteUnitManifest(
            string unitId,
            string toolId,
            string heartbeatFile,
            int maxRestarts,
            string? readinessPipe = null,
            string? workerPipe = null,
            bool autostart = false,
            int readinessTimeoutMs = 1000)
        {
            var arguments = FixturePrefixArgs.Concat(["--heartbeat-file", heartbeatFile, "--interval-ms", "150"]).ToList();
            var effectiveWorkerPipe = workerPipe ?? readinessPipe;
            if (!string.IsNullOrWhiteSpace(effectiveWorkerPipe))
            {
                arguments.AddRange(["--pipe", effectiveWorkerPipe]);
            }

            var readiness = new
            {
                kind = string.IsNullOrWhiteSpace(readinessPipe) ? "none" : "pipe",
                address = readinessPipe ?? string.Empty,
                timeoutMs = readinessTimeoutMs
            };
            var manifest = new
            {
                id = unitId,
                toolId,
                displayName = $"Architecture fixture {unitId}",
                exec = FixtureExec,
                arguments = arguments.ToArray(),
                workingDirectory = Path.GetDirectoryName(FixtureExec) ?? DataRoot,
                environment = new Dictionary<string, string>(),
                autostart,
                restartPolicy = new { maxRestarts, backoffMs = 200 },
                readiness,
                stopTimeoutMs = 600,
                dataRoots = Array.Empty<string>(),
                dependsOn = Array.Empty<string>(),
                instanceToken = $"{Gate}-{unitId}-{RunId}"
            };
            var path = Path.Combine(DeployRoot, "units", $"{unitId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        public IReadOnlyList<int> StartFixtureProcesses(string heartbeatFile, string? readinessPipe, int count)
        {
            var arguments = BuildFixtureArguments(heartbeatFile, readinessPipe);
            var processIds = new List<int>(count);
            for (var index = 0; index < count; index++)
            {
                var startInfo = new ProcessStartInfo(FixtureExec)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(FixtureExec) ?? DataRoot
                };
                foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start orphan fixture.");
                processIds.Add(process.Id);
                process.Dispose();
            }

            return processIds;
        }

        public void WriteBatchUnitManifest(string unitId, string toolId, string heartbeatFile)
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var wrapper = Path.Combine(DataRoot, $"{unitId}.cmd");
            var fixtureCommand = string.Join(
                ' ',
                new[] { FixtureExec }.Concat(FixturePrefixArgs).Select(QuoteBatchArgument));
            File.WriteAllText(wrapper, $"@echo off\r\n{fixtureCommand} %*\r\n");

            var arguments = new[] { "--heartbeat-file", heartbeatFile, "--interval-ms", "150" };
            var manifest = new
            {
                id = unitId,
                toolId,
                displayName = $"Architecture batch fixture {unitId}",
                exec = wrapper,
                arguments,
                workingDirectory = DataRoot,
                environment = new Dictionary<string, string>(),
                autostart = false,
                restartPolicy = new { maxRestarts = 1, backoffMs = 200 },
                readiness = new { kind = "none", address = string.Empty, timeoutMs = 1000 },
                stopTimeoutMs = 600,
                dataRoots = Array.Empty<string>(),
                dependsOn = Array.Empty<string>(),
                instanceToken = $"{Gate}-{unitId}-{RunId}"
            };
            var path = Path.Combine(DeployRoot, "units", $"{unitId}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        }

        private IReadOnlyList<string> BuildFixtureArguments(string heartbeatFile, string? readinessPipe)
        {
            var arguments = FixturePrefixArgs.Concat(["--heartbeat-file", heartbeatFile, "--interval-ms", "150"]).ToList();
            if (!string.IsNullOrWhiteSpace(readinessPipe))
            {
                arguments.AddRange(["--pipe", readinessPipe]);
            }

            return arguments;
        }

        private static string QuoteBatchArgument(string value)
            => $"\"{value.Replace("\"", "\"\"")}\"";

        public void DeleteUnitManifest(string unitId)
        {
            var path = Path.Combine(DeployRoot, "units", $"{unitId}.json");
            if (File.Exists(path)) File.Delete(path);
        }

        public RunningServiceManager StartServiceManager(string label)
        {
            var managerDll = Path.Combine(RepoRoot, "artifacts", "build", "bin", "MyPowerTools.ServiceManager", "release", "MyPowerTools.ServiceManager.dll");
            if (!File.Exists(managerDll)) throw new FileNotFoundException("ServiceManager build output is missing.", managerDll);
            var arguments = new[]
            {
                managerDll,
                "--data-root", DataRoot,
                "--deploy-root", DeployRoot,
                "--endpoint-address", Endpoint.Address,
                "--instance-name", InstanceName
            };
            return RunningServiceManager.Start("dotnet", arguments, label);
        }

        public async Task<ServiceManagerAdminClient> WaitForClientAsync(RunningServiceManager manager)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
            Exception? lastError = null;
            while (DateTime.UtcNow < deadline)
            {
                if (manager.HasExited)
                {
                    throw new InvalidOperationException($"ServiceManager exited during startup: {manager.ErrorText}");
                }

                var client = ServiceManagerAdminClient.ForEndpoint(Endpoint, Token);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
                    await client.ListUnitsAsync(cancellationToken: timeout.Token);
                    return client;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    client.Dispose();
                    await Task.Delay(120);
                }
            }

            throw new TimeoutException($"ServiceManager did not become ready: {lastError?.Message}; stderr={manager.ErrorText}");
        }

        public async Task<int> CompleteAsync(string gate, IReadOnlyList<GateRecord> records, IReadOnlyList<RunningServiceManager?> managers, Exception? exception = null)
        {
            var passed = records.Count > 0 && records.All(record => record.Passed);
            var payload = new
            {
                gate,
                passed,
                runId = RunId,
                completedAt = DateTimeOffset.UtcNow,
                endpoint = Endpoint.Address,
                records,
                exception = exception?.ToString(),
                managers = managers.Where(item => item is not null).Select(item => new
                {
                    item!.Label,
                    item.ProcessId,
                    item.HasExited,
                    stdout = item.OutputText,
                    stderr = item.ErrorText
                })
            };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            var runPath = Path.Combine(EvidenceDirectory, $"result-{RunId}.json");
            var latestPath = Path.Combine(EvidenceDirectory, "result.json");
            await File.WriteAllTextAsync(runPath, json);
            await File.WriteAllTextAsync(latestPath, json);
            Console.WriteLine($"{gate} gate: {(passed ? "PASS" : "FAIL")}; evidence={runPath}");
            return passed ? 0 : 1;
        }

        public void Dispose()
        {
            try { Directory.Delete(DataRoot, recursive: true); } catch { }
        }

        private static async Task RunDotnetAsync(IReadOnlyList<string> args)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch dotnet.");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"dotnet {string.Join(' ', args)} failed ({process.ExitCode}): {await stderr}; {await stdout}");
            }
            await stdout;
            await stderr;
        }

        private static string FindRepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx"))) return directory.FullName;
                directory = directory.Parent;
            }
            throw new DirectoryNotFoundException("MyPowerTools repository root was not found.");
        }
    }

    internal sealed class RunningServiceManager
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _output = new();
        private readonly ConcurrentQueue<string> _error = new();

        private RunningServiceManager(Process process, string label)
        {
            _process = process;
            Label = label;
            process.OutputDataReceived += (_, args) => { if (args.Data is not null) _output.Enqueue(args.Data); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is not null) _error.Enqueue(args.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        public string Label { get; }
        public int ProcessId => _process.Id;
        public bool HasExited => _process.HasExited;
        public string OutputText => string.Join(Environment.NewLine, _output);
        public string ErrorText => string.Join(Environment.NewLine, _error);

        public static RunningServiceManager Start(string executable, IReadOnlyList<string> arguments, string label)
        {
            var psi = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch ServiceManager.");
            return new RunningServiceManager(process, label);
        }

        public async Task WaitForExitAsync(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_process.HasExited) return;
                await Task.Delay(100);
            }

            throw new TimeoutException($"ServiceManager process {_process.Id} did not exit within {timeout}.");
        }

        public async Task StopAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: false);
                    await WaitForExitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch
            {
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
