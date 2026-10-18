using System.Diagnostics;
using System.Text.Json;
using MyPowerTools.Runtime;

var limits = new[] { -1, 0, 1, 2, 3, 5, 10, 200 };
if (args.Length == 1 && args[0] == "--list")
{
    Console.WriteLine("MPT_TEST_CASES_V1");
    foreach (var limit in limits) Console.WriteLine($"Tail equivalence limit={limit}");
    Console.WriteLine("Missing file returns empty");
    Console.WriteLine("Malformed history outside tail is ignored");
    Console.WriteLine("Malformed selected history still throws");
    foreach (var test in TailReadRegression.Cases) Console.WriteLine(test);
    return;
}

if (args.Length == 3 && args[0] == "--benchmark-io")
{
    TailReadRegression.Benchmark(args[1], args[2]);
    return;
}

if (args.Length == 2 && args[0] == "--benchmark")
{
    var router = new LogRouter(args[1]);
    var timer = Stopwatch.StartNew();
    var rows = router.Tail("fixture", 200);
    if (rows.Count != 200 || rows[0].EventSeq != 99800 || rows[^1].EventSeq != 99999)
        throw new Exception("Benchmark tail content mismatch.");
    Console.WriteLine(JsonSerializer.Serialize(new { rows = rows.Count, elapsedMs = timer.Elapsed.TotalMilliseconds,
        peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64, gcHeapBytes = GC.GetTotalMemory(false) }));
    Thread.Sleep(300); // Let external /proc sampler collect the post-work snapshot.
    return;
}
var root = Path.Combine(Path.GetTempPath(), "mpt-log-tail-" + Guid.NewGuid().ToString("N"));
try
{
    var router = new LogRouter(root);
    var records = Enumerable.Range(0, 8).Select(i => new LogRecord(DateTimeOffset.UnixEpoch, "pkg", "fixture", "", (ulong)i, "", "info", "行 " + i)).ToArray();
    var lines = records.Select(r => JsonSerializer.Serialize(r)).ToList();
    lines.Insert(6, " "); lines.Add("null");
    File.WriteAllLines(Path.Combine(root, "fixture.jsonl"), lines);
    foreach (var count in limits)
    {
        var expected = lines.TakeLast(count).Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => JsonSerializer.Deserialize<LogRecord>(s)).Where(r => r is not null).ToArray();
        if (!expected.SequenceEqual(router.Tail("fixture", count))) throw new Exception($"Tail mismatch at {count}.");
    }
    if (router.Tail("missing").Count != 0) throw new Exception("Missing file result changed.");
    // Malformed history outside the selected tail must not be deserialized.
    File.WriteAllLines(Path.Combine(root, "fixture.jsonl"), new[] { "bad JSON", lines[0] });
    if (router.Tail("fixture", 1).Single() != records[0]) throw new Exception("Old invalid record leaked into tail.");
    bool threw = false;
    try { router.Tail("fixture", 2); } catch (JsonException) { threw = true; }
    if (!threw) throw new Exception("Malformed selected records must retain existing error behavior.");
    var comparisons = TailReadRegression.Run(root);
    Console.WriteLine($"PASS: {11 + TailReadRegression.Cases.Length} log-tail cases; {comparisons} additional equivalence/race comparisons.");
}
finally { Directory.Delete(root, true); }
