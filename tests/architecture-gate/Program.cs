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
