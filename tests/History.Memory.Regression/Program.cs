using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Runtime;
using Sdk = MyPowerTools.Abstractions;

if (args.Length == 1 && (args[0] == "--benchmark" || args[0] == "--benchmark-sustained"))
{
    var commands = new CommandHistory();
    var notifications = new NotificationCenter();
    var elapsed = Stopwatch.StartNew();
    var checkpoints = args[0] == "--benchmark-sustained"
        ? new[] { 0, 5_000, 10_000, 25_000, 50_000 }
        : new[] { 0, 1_250, 2_500, 5_000 };
    var index = 0;
    foreach (var checkpoint in checkpoints)
    {
        while (index < checkpoint)
        {
            var id = index.ToString("D8");
            // Distinct, realistic-sized strings: this exercises actual production
            // stores, not a list surrogate, and does not reuse one shared payload.
            commands.Add(new Sdk.CommandRequest(id, "fixture.command", new JsonObject()), null, "accepted");
            commands.Complete(new Sdk.CommandExecutionResult(id, "fixture.command", "succeeded", true,
                $"command {id} " + new string('c', 2048)));
            notifications.Publish("fixture.module", "info", $"notification {id}",
                $"body {id} " + new string('n', 2048));
            index++;
        }
        // Probe-only collections make live retention comparable. Production has no
        // forced collections. RSS is reported separately and may retain GC segments.
        var heapBeforeCollection = GC.GetTotalMemory(false);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            writesPerBuffer = checkpoint, commandCount = commands.Count, notificationCount = notifications.Count,
            managedLiveBytes = GC.GetTotalMemory(false), heapBeforeCollection,
            workingSetBytes = process.WorkingSet64, privateBytes = process.PrivateMemorySize64,
            peakWorkingSetBytes = process.PeakWorkingSet64, elapsedMs = elapsed.Elapsed.TotalMilliseconds,
#if !HISTORY_BASELINE
            commands = commands.GetStats(), notifications = notifications.GetStats(),
#endif
        }));
        Thread.Sleep(150); // Let the independent /proc sampler observe each phase.
    }
    GC.KeepAlive(commands); GC.KeepAlive(notifications);
    return;
}
#if HISTORY_BASELINE
throw new ArgumentException("The baseline binary only supports --benchmark.");
#else
if (args.Length == 1 && args[0] == "--list")
{
    Console.WriteLine("MPT_TEST_CASES_V1");
    foreach (var test in HistoryRegression.Cases) Console.WriteLine(test.Name);
    return;
}
foreach (var test in HistoryRegression.Cases)
{
    test.Run();
    Console.WriteLine("PASS: " + test.Name);
}
Console.WriteLine($"PASS: {HistoryRegression.Cases.Length} bounded-history cases.");
#endif
