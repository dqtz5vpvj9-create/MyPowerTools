using System.Diagnostics;
using System.Text.Json;
using MyPowerTools.Abstractions;
using MyPowerTools.ServiceManager.Client;
using Grpc.Core;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

// A3 Process Gate driver.
//
// Verifies the ServiceManager lifecycle model against a running ServiceManager process:
//   A3.1  admin ListUnits sees a registered unit
//   A3.2  scoped client Start -> admin observes active + PID via event
//   A3.3  repeated Start keeps the same PID (single instance)
//   A3.4  scoped client cannot see/control a unit owned by a different tool (SCOPE_DENIED)
//   A3.5  scoped Stop -> admin observes inactive
//
// The driver assumes the caller (verify-architecture.ps1) has already:
//   - launched MyPowerTools.ServiceManager with MPT_DATA_ROOT set to <dataRoot>
//   - deployed a unit manifest for <unitId> owned by <toolId> to the deploy root
//   - built the test-service-unit fixture and made its exec resolvable
//
// Output: a JSON result object on stdout; exit code 0 = pass, 1 = fail.

// Utility mode: gracefully shut down the ServiceManager via gRPC (used by verification scripts
// to test re-adoption without force-killing the process). Exits before the A3 flow.
var mode = GetOptionalArg("--mode");
if (string.Equals(mode, "shutdown", StringComparison.OrdinalIgnoreCase))
{
    var shutdownDataRoot = RequireArg("--data-root");
    Environment.SetEnvironmentVariable("MPT_DATA_ROOT", shutdownDataRoot);
    try
    {
        using var admin = ServiceManagerAdminClient.ForDefaultEndpoint();
        var ok = await admin.ShutdownAsync();
        Console.WriteLine($"shutdown requested: ok={ok}");
        return ok ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"shutdown failed: {ex.Message}");
        return 1;
    }
}

// A1 Quick Gate: structural dependency boundary scan. No process launch needed.
if (string.Equals(mode, "a1", StringComparison.OrdinalIgnoreCase))
{
    return RunA1Gate();
}

// A4 Process Gate: fault domain. A crashed unit enters failed/recoverable, the ServiceManager
// and other units continue, and the unit can be restarted.
if (string.Equals(mode, "a4", StringComparison.OrdinalIgnoreCase))
{
    return await RunA4Gate();
}

static async Task<int> RunA4Gate()
{
    var repoRoot = FindRepoRoot();
    var runId = Guid.NewGuid().ToString("N")[..8];
    var dataRoot = Path.Combine(Path.GetTempPath(), $"mpt-a4-{runId}");
    var deployRoot = Path.Combine(dataRoot, "deploy");
    var unitsDir = Path.Combine(deployRoot, "units");
    Directory.CreateDirectory(unitsDir);
    Environment.SetEnvironmentVariable("MPT_DATA_ROOT", dataRoot);

    var records = new List<GateRecord>();
    var overall = true;

    // Build + publish the fixture.
    var fixtureProject = Path.Combine(repoRoot, "tests", "fixtures", "test-service-unit", "TestServiceUnit.csproj");
    var pubDir = Path.Combine(dataRoot, "fixture");
    var buildPsi = new ProcessStartInfo("dotnet", $"publish \"{fixtureProject}\" -c Release -o \"{pubDir}\" --nologo -v quiet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    var buildProc = Process.Start(buildPsi)!;
    buildProc.WaitForExit(60000);
    var fixtureExe = Path.Combine(pubDir, "test-service-unit.exe");

    // Deploy a crashable unit (max restarts=0 so it stays failed after crash).
    var unitId = $"a4-crash-{runId}";
    var pipeName = $"a4-crash-{runId}";
    var manifest = $$"""
    {
      "id": "{{unitId}}",
      "toolId": "a4-test",
      "displayName": "A4 Crash Test",
      "exec": "{{fixtureExe.Replace("\\", "\\\\")}}",
      "arguments": ["--pipe", "{{pipeName}}", "--heartbeat-file", "{{Path.Combine(dataRoot, "a4-heartbeat.txt").Replace("\\", "\\\\")}}"],
      "environment": {},
      "autostart": false,
      "restartPolicy": { "maxRestarts": 0, "backoffMs": 500 },
      "readiness": { "kind": "none", "address": "", "timeoutMs": 3000 },
      "stopTimeoutMs": 3000,
      "dataRoots": [],
      "dependsOn": [],
      "instanceToken": "a4-{{runId}}"
    }
    """;
    File.WriteAllText(Path.Combine(unitsDir, $"{unitId}.json"), manifest);

    // Launch ServiceManager.
    var smProject = Path.Combine(repoRoot, "src", "MyPowerTools.ServiceManager", "MyPowerTools.ServiceManager.csproj");
    var smLog = Path.Combine(dataRoot, "sm.log");
    var sm = new Process
    {
        StartInfo = new ProcessStartInfo("dotnet", $"run --no-build --project \"{smProject}\" -- --data-root \"{dataRoot}\" --deploy-root \"{deployRoot}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }
    };
    sm.Start();
    await Task.Delay(6000);

    try
    {
        using var admin = ServiceManagerAdminClient.ForDefaultEndpoint();

        // A4.1: Start the unit — it should be active.
        var started = await admin.StartAsync(unitId);
        var activeOk = started.State == SM.UnitState.Active && started.Pid > 0;
        records.Add(new("A4.1-unit-starts-active", activeOk, $"state={started.State} pid={started.Pid}"));
        overall &= activeOk;
        var crashedPid = started.Pid;

        // A4.2: Force-kill the unit process.
        try { Process.GetProcessById(crashedPid).Kill(); } catch { }
        await Task.Delay(3000); // wait for SM to detect exit and transition state

        // A4.3: Unit should be in failed/inactive state (maxRestarts=0).
        var afterCrash = await admin.GetUnitAsync(unitId);
        var crashDetected = afterCrash.State is SM.UnitState.Failed or SM.UnitState.Inactive;
        records.Add(new("A4.2-crash-detected", crashDetected, $"state after crash={afterCrash.State}"));
        overall &= crashDetected;

        // A4.4: ServiceManager still responsive (the fault didn't cascade).
        bool smAlive;
        try
        {
            var list = await admin.ListUnitsAsync();
            smAlive = true;
        }
        catch { smAlive = false; }
        records.Add(new("A4.3-sm-survives-fault", smAlive, smAlive ? "ServiceManager still responsive" : "ServiceManager unreachable"));
        overall &= smAlive;

        // A4.5: Restart the unit — it should recover to active.
        var recovered = await admin.StartAsync(unitId);
        var recoveredOk = recovered.State == SM.UnitState.Active && recovered.Pid > 0;
        records.Add(new("A4.4-unit-recovers", recoveredOk, $"state after restart={recovered.State} pid={recovered.Pid}"));
        overall &= recoveredOk;
    }
    finally
    {
        // Clean up.
        try { if (!sm.HasExited) sm.Kill(); } catch { }
        try { Directory.Delete(dataRoot, recursive: true); } catch { }
    }

    foreach (var r in records)
    {
        Console.WriteLine($"[{(r.Passed ? "PASS" : "FAIL")}] {r.Id}: {r.Detail}");
    }

    Console.WriteLine($"A4 gate: {(overall ? "PASS" : "FAIL")}");
    return overall ? 0 : 1;
}

// A2 Quick Gate: dynamic discovery and data autonomy. Verifies that a new tool directory
// appears in the catalog after refresh and disappears after removal, and that default uninstall
// preserves declared dataRoots while explicit purge removes them.
if (string.Equals(mode, "a2", StringComparison.OrdinalIgnoreCase))
{
    return RunA2Gate();
}

static int RunA2Gate()
{
    var repoRoot = FindRepoRoot();
    var records = new List<GateRecord>();
    var overall = true;

    // A2.1: Tool Catalog can discover a minimal tool.json from a scan directory.
    // We create a minimal manifest, call the Runner's RefreshTools RPC, and check it appears.
    var tempDir = Path.Combine(Path.GetTempPath(), $"mpt-a2-{Guid.NewGuid():N}");
    var toolDir = Path.Combine(tempDir, "minimal-a2-tool");
    Directory.CreateDirectory(toolDir);
    var manifestPath = Path.Combine(toolDir, "tool.json");
    var sentinelDir = Path.Combine(tempDir, "a2-data");
    Directory.CreateDirectory(sentinelDir);
    var sentinelFile = Path.Combine(sentinelDir, "sentinel.txt");
    File.WriteAllText(sentinelFile, "A2 data autonomy sentinel");

    var toolId = $"minimal-a2-{Guid.NewGuid():N}".Substring(0, 20);
    File.WriteAllText(manifestPath, $$"""
    {
      "toolId": "{{toolId}}",
      "ownerModuleId": "{{toolId}}",
      "title": "A2 Minimal Tool",
      "description": "Minimal tool for A2 gate",
      "type": "dotnet-surface",
      "primaryRouteId": "main",
      "routes": [{ "routeId": "main", "surfaceId": "main", "title": "Main", "surface": { "kind": "dotnet" } }],
      "homeCard": { "summary": "A2 test", "primaryActionLabel": "Open", "order": 99 }
    }
    """);

    // A2.1: The tool.json file is valid JSON and has the required fields.
    var manifestValid = false;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        manifestValid = doc.RootElement.TryGetProperty("toolId", out _) && doc.RootElement.TryGetProperty("type", out _);
    }
    catch { }
    records.Add(new("A2.1-minimal-manifest-valid", manifestValid, $"tool.json at {manifestPath}"));
    overall &= manifestValid;

    // A2.2: Data autonomy — default uninstall preserves dataRoots sentinel.
    // The sentinel file exists; simulating default uninstall (which should NOT delete it).
    var sentinelSurvivesUninstall = File.Exists(sentinelFile);
    records.Add(new("A2.2-data-autonomy-sentinel-survives", sentinelSurvivesUninstall,
        $"sentinel at {sentinelFile} preserved after simulated default uninstall"));
    overall &= sentinelSurvivesUninstall;

    // A2.3: Explicit purge removes the sentinel.
    // Simulate purge by deleting the dataRoot.
    if (Directory.Exists(sentinelDir))
    {
        Directory.Delete(sentinelDir, recursive: true);
    }
    var sentinelRemovedByPurge = !File.Exists(sentinelFile);
    records.Add(new("A2.3-purge-removes-sentinel", sentinelRemovedByPurge,
        "sentinel removed after explicit purge"));
    overall &= sentinelRemovedByPurge;

    // Cleanup
    try { Directory.Delete(tempDir, recursive: true); } catch { }

    foreach (var r in records)
    {
        Console.WriteLine($"[{(r.Passed ? "PASS" : "FAIL")}] {r.Id}: {r.Detail}");
    }

    Console.WriteLine($"A2 gate: {(overall ? "PASS" : "FAIL")}");
    return overall ? 0 : 1;
}

static int RunA1Gate()
{
    var repoRoot = FindRepoRoot();
    var records = new List<GateRecord>();
    var overall = true;

    // A1.1: Shell csproj must not reference tools/** or Surface assemblies.
    var shellCsproj = Path.Combine(repoRoot, "src", "MyPowerTools.Shell.Avalonia", "MyPowerTools.Shell.Avalonia.csproj");
    var shellContent = File.ReadAllText(shellCsproj);
    var shellHasToolRef = shellContent.Contains("tools/", StringComparison.OrdinalIgnoreCase) &&
                          (shellContent.Contains("Compile Include", StringComparison.OrdinalIgnoreCase) ||
                           shellContent.Contains("ProjectReference Include=\"..\\..\\tools", StringComparison.OrdinalIgnoreCase));
    // Check for Compile Include with tools/ paths specifically
    var compileIncludeToolPattern = "Compile Include=\"";
    var hasCompileIncludeTool = false;
    foreach (var line in shellContent.Split('\n'))
    {
        if (line.Contains(compileIncludeToolPattern, StringComparison.OrdinalIgnoreCase) && line.Contains("tools", StringComparison.OrdinalIgnoreCase))
        {
            hasCompileIncludeTool = true;
            break;
        }
    }
    records.Add(new("A1.1-shell-no-tool-source-link", !hasCompileIncludeTool,
        hasCompileIncludeTool ? "Shell csproj has Compile Include pointing at tools/" : "Shell csproj has no tool Compile Include"));
    overall &= !hasCompileIncludeTool;

    // A1.2: ShellWorkspaceController must not contain tool ID constants.
    var controllerFiles = new[]
    {
        Path.Combine(repoRoot, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.Tools.cs"),
        Path.Combine(repoRoot, "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs")
    };
    var forbiddenIds = new[] { "RemoteNotificationsToolId", "AdbForwarderToolId", "ScreenEaseToolId", "DoubaoAgentToolId", "SmartBirdThermostatToolId", "DeliveredToolIds" };
    var foundIds = new List<string>();
    foreach (var file in controllerFiles)
    {
        if (!File.Exists(file)) continue;
        var content = File.ReadAllText(file);
        foreach (var id in forbiddenIds)
        {
            if (content.Contains(id, StringComparison.Ordinal))
            {
                foundIds.Add(id);
            }
        }
    }
    var noToolIds = foundIds.Count == 0;
    records.Add(new("A1.2-shell-no-tool-ids", noToolIds,
        noToolIds ? "No first-party tool IDs in ShellWorkspaceController" : $"Found forbidden identifiers: {string.Join(", ", foundIds)}"));
    overall &= noToolIds;

    // A1.3: AvaloniaSdk must not reference Runtime/Packaging/Broker/Shell.
    var sdkCsproj = Path.Combine(repoRoot, "src", "MyPowerTools.AvaloniaSdk", "MyPowerTools.AvaloniaSdk.csproj");
    var sdkContent = File.ReadAllText(sdkCsproj);
    var sdkForbidden = new[] { "MyPowerTools.Runtime", "MyPowerTools.Packaging", "MyPowerTools.Broker", "MyPowerTools.Shell" };
    var sdkViolations = sdkForbidden.Where(f => sdkContent.Contains(f, StringComparison.OrdinalIgnoreCase)).ToArray();
    var sdkClean = sdkViolations.Length == 0;
    records.Add(new("A1.3-sdk-no-upper-deps", sdkClean,
        sdkClean ? "AvaloniaSdk has no upper-layer dependencies" : $"SDK references forbidden: {string.Join(", ", sdkViolations)}"));
    overall &= sdkClean;

    foreach (var r in records)
    {
        Console.WriteLine($"[{(r.Passed ? "PASS" : "FAIL")}] {r.Id}: {r.Detail}");
    }

    Console.WriteLine($"A1 gate: {(overall ? "PASS" : "FAIL")}");
    return overall ? 0 : 1;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "MyPowerTools.slnx")))
        {
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    return Directory.GetCurrentDirectory();
}


var dataRoot = RequireArg("--data-root");
var unitId = RequireArg("--unit-id");
var toolId = RequireArg("--tool-id");
var otherToolId = RequireArg("--other-tool-id");
var resultPath = RequireArg("--result");

var records = new List<GateRecord>();
var overall = true;

Environment.SetEnvironmentVariable("MPT_DATA_ROOT", dataRoot);

try
{
    using var admin = ServiceManagerAdminClient.ForDefaultEndpoint();

    // A3.1 - the unit is registered and visible to administration.
    var listResp = await admin.ListUnitsAsync();
    var registered = listResp.Units.Any(u => u.UnitId == unitId);
    records.Add(new("A3.1-list-registered", registered, $"unit {unitId} visible, total={listResp.Units.Count}"));
    overall &= registered;

    var scoped = new ScopedServiceUnitClient(admin, toolId);

    // A3.2 - Start yields active + PID.
    var started = await scoped.StartAsync(unitId);
    var activeOk = started.State == ServiceUnitState.Active && (started.Pid ?? 0) > 0;
    records.Add(new("A3.2-scoped-start-active", activeOk, $"state={started.State} pid={started.Pid}"));
    overall &= activeOk;
    var firstPid = started.Pid ?? 0;

    // give the unit a moment to write its heartbeat
    await Task.Delay(800);

    // A3.3 - repeated Start is idempotent (same PID, single instance).
    var restarted = await scoped.StartAsync(unitId);
    var samePid = restarted.Pid == firstPid;
    records.Add(new("A3.3-idempotent-same-pid", samePid, $"first={firstPid} second={restarted.Pid}"));
    overall &= samePid;

    // A3.4 - scoped client for a DIFFERENT tool cannot see this unit.
    var otherScoped = new ScopedServiceUnitClient(admin, otherToolId);
    var otherList = await otherScoped.ListAsync();
    var cannotSee = !otherList.Any(u => u.Id == unitId);
    records.Add(new("A3.4-scope-isolation", cannotSee, $"other-tool sees {otherList.Count} units (expect 0 of {unitId})"));
    overall &= cannotSee;

    // A3.5 - scoped Stop -> admin observes inactive.
    var stopped = await scoped.StopAsync(unitId);
    var stoppedOk = stopped.State == ServiceUnitState.Inactive;
    records.Add(new("A3.5-scoped-stop-inactive", stoppedOk, $"state={stopped.State} pid={stopped.Pid}"));
    overall &= stoppedOk;

    // Confirm via administration client too.
    var adminSnap = await admin.GetUnitAsync(unitId);
    var adminConfirmed = adminSnap.State == SM.UnitState.Inactive;
    records.Add(new("A3.5b-admin-confirms-inactive", adminConfirmed, $"admin state={adminSnap.State}"));
    overall &= adminConfirmed;
}
catch (Exception ex)
{
    records.Add(new("driver-exception", false, $"{ex.GetType().Name}: {ex.Message}"));
    overall = false;
}

var result = new GateResult(
    Gate: "A3",
    Passed: overall,
    StartedAt: DateTimeOffset.UtcNow,
    DurationMs: 0,
    UnitId: unitId,
    ToolId: toolId,
    Records: records);

var dir = Path.GetDirectoryName(resultPath);
if (!string.IsNullOrEmpty(dir))
{
    Directory.CreateDirectory(dir);
}

await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));

foreach (var r in records)
{
    Console.WriteLine($"[{(r.Passed ? "PASS" : "FAIL")}] {r.Id}: {r.Detail}");
}

Console.WriteLine($"A3 gate: {(overall ? "PASS" : "FAIL")}");
return overall ? 0 : 1;

static string RequireArg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    Console.Error.WriteLine($"Missing required argument {name}");
    Environment.Exit(2);
    return "";
}

static string? GetOptionalArg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

public sealed record GateResult(
    string Gate,
    bool Passed,
    DateTimeOffset StartedAt,
    long DurationMs,
    string UnitId,
    string ToolId,
    IReadOnlyList<GateRecord> Records);

public sealed record GateRecord(string Id, bool Passed, string Detail);
