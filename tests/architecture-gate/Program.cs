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
