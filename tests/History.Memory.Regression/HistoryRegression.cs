#if !HISTORY_BASELINE
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MyPowerTools.Runtime;
using Sdk = MyPowerTools.Abstractions;

internal static class HistoryRegression
{
    public static readonly (string Name, Action Run)[] Cases =
    [
        ("Concrete defaults and invalid budgets", Defaults),
        ("Command oldest-entry rollover", CommandEntryRollover),
        ("Command byte rollover on completion", CommandByteRollover),
        ("Old completion may evict itself without resurrection", OldCompletion),
        ("Completion after eviction is explicit", UnretainedCompletion),
        ("Huge Unicode output and whole-grapheme truncation", HugeUnicode),
        ("Redaction precedes truncation", Redaction),
        ("Error summary fallback remains redacted", ErrorFallback),
        ("Oversized command identities are rejected unchanged", CommandIdentity),
        ("Notification read-first eviction", ReadFirst),
        ("All-unread overflow is counted without marking read", UnreadOverflow),
        ("Read-state idempotence and reversal", ReadState),
        ("Notification byte cap and unread accounting", NotificationBytes),
        ("Huge notification title and body are bounded", HugeNotification),
        ("Oversized notification module identity is rejected unchanged", NotificationIdentity),
        ("Filters and independent snapshots", Filters),
        ("Concurrent writes completion reads and read-state changes", Concurrency),
        ("Count remains allocation-free", CountAllocation),
        ("Default-budget steady-state retention plateau", Plateau),
        ("Duplicate invocation preserves first-matching completion semantics", DuplicateInvocation),
        ("Truncation counters count writes not read-state updates", TruncationCounters),
        ("Rejected large identities are not retained by the store", RejectedIdentityCollection),
        ("Original constructor and List CLR signatures remain available", BinarySignatures),
    ];

    private static Sdk.CommandRequest Request(string id, string command = "command") => new(id, command, new JsonObject());
    private static Sdk.CommandExecutionResult Result(string id, string output, string state = "succeeded") => new(id, "command", state, true, output);
    private static Sdk.MptCommandDescriptor Descriptor(string module) => new("command", module, "title", "subtitle", "execute");
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Bounds(HistoryBufferStats stats)
    {
        Check(stats.Count <= stats.MaxEntries && stats.Count >= 0, "Entry limit violated.");
        Check(stats.RetainedBytes <= stats.MaxBytes && stats.RetainedBytes >= 0, "Byte limit violated.");
        Check(stats.UnreadCount <= stats.Count && stats.UnreadCount >= 0, "Unread count invalid.");
    }
    private static void Defaults()
    {
        Check(HistoryBufferOptions.CommandDefaults == new HistoryBufferOptions(4096, 4 * 1024 * 1024, 16384), "Command defaults changed.");
        Check(HistoryBufferOptions.NotificationDefaults == new HistoryBufferOptions(2048, 2 * 1024 * 1024, 16384), "Notification defaults changed.");
        foreach (var options in new[] { new HistoryBufferOptions(0, 1024, 512), new(1, 511, 512), new(1, 1024, 511), new(1, 1024, 2048) })
        {
            try { _ = new CommandHistory(options); throw new Exception("Invalid budget accepted."); }
            catch (ArgumentOutOfRangeException) { }
        }
    }
    private static void CommandEntryRollover()
    {
        var history = new CommandHistory(new(3, 8192, 2048));
        for (var i = 0; i < 10; i++) history.Add(Request(i.ToString()), null, "accepted");
        Check(history.List().Select(r => r.InvocationId).Order().SequenceEqual(new[] { "7", "8", "9" }), "Oldest history not removed.");
        var stats = history.GetStats();
        Check(stats.Count == 3 && stats.AddedCount == 10 && stats.EvictedCount == 7 && stats.LastEvictedAt.HasValue, "Entry rollover counters wrong.");
        Check(stats.RetainedBytes == history.List().Sum(r => r.RetainedBytes), "Retained charge inconsistent.");
    }
    private static void CommandByteRollover()
    {
        var history = new CommandHistory(new(20, 2048, 2048));
        history.Add(Request("old"), null, "accepted");
        history.Add(Request("new"), null, "accepted");
        history.Complete(Result("new", new string('x', 800)));
        Check(history.Count == 1 && history.List()[0].InvocationId == "new", "Completion growth did not evict oldest.");
        Bounds(history.GetStats());
        Check(history.GetStats().TruncatedCount == 1, "Huge completion not counted as truncated.");
    }
    private static void OldCompletion()
    {
        var history = new CommandHistory(new(20, 2048, 2048));
        history.Add(Request("old"), null, "accepted");
        history.Add(Request("new"), null, "accepted");
        history.Complete(Result("old", new string('x', 800)));
        Check(history.Count == 1 && history.List()[0].InvocationId == "new", "Old completion should obey oldest-first budget.");
        history.Complete(Result("old", "late"));
        Check(history.GetStats().UnretainedCompletionCount == 1, "Missing old completion was not reported.");
    }
    private static void UnretainedCompletion()
    {
        var history = new CommandHistory(new(1, 2048, 2048));
        history.Add(Request("active"), null, "accepted");
        history.Add(Request("later"), null, "accepted");
        history.Complete(Result("active", new string('x', 1_000_000)));
        Check(history.List()[0].InvocationId == "later" && history.GetStats().UnretainedCompletionCount == 1, "Evicted active history resurrected.");
    }
    private static void HugeUnicode()
    {
        const string cluster = "👩🏽‍💻e\u0301";
        var payload = string.Concat(Enumerable.Repeat(cluster, 200_000));
        var history = new CommandHistory(new(10, 4096, 1024));
        history.Add(Request("unicode"), null, "accepted");
        history.Complete(Result("unicode", payload));
        var record = history.List().Single();
        Check(record.IsTruncated && record.RetainedBytes <= 1024 && record.OriginalBytes > 1_000_000, "Huge Unicode record cap failed.");
        var retainedPrefix = record.Summary.Replace("… [truncated]", "", StringComparison.Ordinal);
        var boundary = StringInfo.ParseCombiningCharacters(payload[..Math.Min(500, payload.Length)]);
        Check(payload.StartsWith(retainedPrefix, StringComparison.Ordinal) && boundary.Contains(retainedPrefix.Length), "Truncation split a Unicode text element.");
        Check(!retainedPrefix.Contains('\ufffd'), "Replacement characters introduced.");
    }
    private static void Redaction()
    {
        var history = new CommandHistory(new(10, 4096, 1024));
        history.Add(Request("secret"), null, "accepted");
        history.Complete(Result("secret", "token=" + new string('s', 20_000) + " {\"confirmationToken\":\"" + new string('z', 20_000) + "\"} " + new string('x', 2000)));
        var summary = history.List().Single().Summary;
        Check(summary.Contains("token=****") && summary.Contains("\"confirmationToken\":\"****\"") && !summary.Contains("ssss") && !summary.Contains("zzzz"), "Secret leaked at truncation boundary.");
        var center = new NotificationCenter(new(10, 4096, 1024));
        var notice = center.Publish("module", "info", "password=title-secret", "apiKey=body-secret " + new string('x', 2000));
        Check(!notice.Title.Contains("title-secret") && !notice.Body.Contains("body-secret"), "Notification redaction failed.");
    }
    private static void ErrorFallback()
    {
        var history = new CommandHistory();
        history.Add(Request("error"), null, "accepted");
        history.Complete(new Sdk.CommandExecutionResult("error", "command", "failed", false, "  ", new Sdk.MptRuntimeError("error", "password=secret failed")));
        Check(history.List().Single().Summary == "password=**** failed", "Error fallback changed.");
    }
    private static void CommandIdentity()
    {
        var history = new CommandHistory(new(10, 4096, 1024));
        var id = new string('i', 10_000);
        var record = history.Add(Request(id), null, "accepted");
        Check(record.InvocationId == id && !record.IsRetained && record.RetainedBytes == 0, "Identity was truncated or admitted.");
        history.Complete(Result(id, "done"));
        Check(history.Count == 0 && history.GetStats().RejectedCount == 1 && history.GetStats().UnretainedCompletionCount == 1, "Identity rejection counters wrong.");
        var longCommand = history.Add(Request("normal", id), null, "accepted");
        var longModule = history.Add(Request("normal"), Descriptor(id), "accepted");
        Check(longCommand.CommandId == id && longModule.ModuleId == id && history.Count == 0, "Command/module identity not preserved.");
    }
    private static void ReadFirst()
    {
        var center = new NotificationCenter(new(3, 8192, 2048));
        var oldestUnread = center.Publish("m", "warning", "first", "body");
        var read = center.Publish("m", "info", "second", "body");
        center.MarkRead(read.Id);
        center.Publish("m", "info", "third", "body");
        center.Publish("m", "info", "fourth", "body");
        Check(center.List().Any(n => n.Id == oldestUnread.Id) && center.List().All(n => n.Id != read.Id), "Read record was not evicted first.");
        Check(center.GetStats().EvictedCount == 1 && center.GetStats().EvictedUnreadCount == 0 && center.GetStats().UnreadCount == 3, "Read-first stats wrong.");
        Check(center.SetReadState(read.Id, true) is null, "Evicted record accepted read update.");
    }
    private static void UnreadOverflow()
    {
        var center = new NotificationCenter(new(3, 8192, 2048));
        NotificationRecord? lastEvicted = null;
        for (var i = 0; i < 10; i++) { var n = center.Publish("m", "info", i.ToString(), "body"); if (i == 6) lastEvicted = n; }
        var stats = center.GetStats();
        Check(stats.EvictedUnreadCount == 7 && stats.UnreadCount == 3 && center.List().All(n => !n.IsRead), "Unread overflow was hidden/marked read.");
        Check(stats.LastEvictedUnreadAt == lastEvicted!.Time, "Unread overflow timestamp wrong.");
    }
    private static void ReadState()
    {
        var center = new NotificationCenter(new(2, 4096, 2048));
        var first = center.Publish("m", "info", "first", "body");
        Check(!center.SetReadState(first.Id, false)!.Changed, "Initial unread changed.");
        Check(center.SetReadState(first.Id, true)!.Changed && !center.SetReadState(first.Id, true)!.Changed, "Read state is not idempotent.");
        Check(center.SetReadState(first.Id, false)!.Changed && center.GetStats().UnreadCount == 1, "Read reversal failed.");
        var second = center.Publish("m", "info", "second", "body");
        center.MarkRead(first.Id); center.MarkRead(second.Id);
        center.Publish("m", "info", "third", "body");
        Check(center.List().All(n => n.Id != first.Id) && center.GetStats().UnreadCount == 1, "Oldest read order changed.");
    }
    private static void NotificationBytes()
    {
        var center = new NotificationCenter(new(100, 2048, 1024));
        for (var i = 0; i < 50; i++) { var item = center.Publish("module", "info", "title", new string('x', 500)); if (i % 2 == 0) center.MarkRead(item.Id); }
        Bounds(center.GetStats());
        Check(center.GetStats().RetainedBytes == center.List().Sum(n => n.RetainedBytes), "Notification bytes inconsistent.");
        Check(center.GetStats().UnreadCount == center.List().Count(n => !n.IsRead), "Notification unread inconsistent.");
    }
    private static void HugeNotification()
    {
        var center = new NotificationCenter();
        var record = center.Publish("module", "warning", string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 100_000)), string.Concat(Enumerable.Repeat("🧪e\u0301", 500_000)));
        Check(record.IsTruncated && record.Title.Length <= 1024 && record.RetainedBytes <= 16384 && record.OriginalBytes > 1_000_000, "Huge notification escaped bounds.");
        Check(center.GetStats().TruncatedCount == 1 && center.GetStats().TruncatedBytes == record.OriginalBytes - record.RetainedBytes, "Truncated bytes metadata wrong.");
    }
    private static void NotificationIdentity()
    {
        var center = new NotificationCenter(new(10, 4096, 1024));
        var module = new string('m', 10_000);
        var record = center.Publish(module, "info", "title", "body");
        Check(record.ModuleId == module && !record.IsRetained && center.Count == 0 && center.GetStats().RejectedCount == 1, "Module identity rejection failed.");
        Check(center.SetReadState(record.Id, true) is null, "Rejected record entered read-state store.");
    }
    private static void Filters()
    {
        var center = new NotificationCenter();
        var one = center.Publish("one", "warning", "title", "body");
        center.Publish("two", "info", "title", "body");
        var snapshot = center.List(); center.MarkRead(one.Id);
        Check(snapshot.All(n => !n.IsRead), "Published snapshot mutated.");
        Check(center.Query(moduleId: "one", level: "WARNING", isRead: true).Single().Id == one.Id, "Notification filters changed.");
        Check(center.Query(since: one.Time, until: one.Time).Count == 0 && center.Query(until: DateTimeOffset.MinValue).Count == 0, "Time bounds not half-open.");
        Check(center.Query(limit: 0).Count == 0 && center.Query(limit: -1).Count == 0 && center.Query(limit: 1).Count == 1, "Notification limit wrong.");
        var history = new CommandHistory(); history.Add(Request("one"), Descriptor("module"), "accepted"); history.Complete(Result("one", "done"));
        Check(history.Query("module", state: "SUCCEEDED").Count == 1 && history.List("MODULE").Count == 0, "Command module/state filters wrong.");
        Check(history.Query(until: history.List()[0].StartedAt).Count == 0, "Command until not exclusive.");
    }
    private static void Concurrency()
    {
        var options = new HistoryBufferOptions(64, 48 * 1024, 1024);
        var history = new CommandHistory(options); var center = new NotificationCenter(options);
        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 1000; i++)
            {
                var id = $"{worker}:{i}"; history.Add(Request(id), null, "accepted");
                history.Complete(Result(id, new string('x', i % 600)));
                var item = center.Publish("module", "info", id, new string('n', i % 600));
                if ((i & 1) == 0) center.MarkRead(item.Id);
                if (i % 100 == 0) { Bounds(history.GetStats()); Bounds(center.GetStats()); _ = history.Query(limit: 5); _ = center.Query(limit: 5); }
            }
        });
        Bounds(history.GetStats()); Bounds(center.GetStats());
        Check(history.GetStats().AddedCount == 8000 && center.GetStats().AddedCount == 8000, "Concurrent writes lost.");
        Check(history.GetStats().RetainedBytes == history.List().Sum(r => r.RetainedBytes), "Concurrent command byte accounting wrong.");
        Check(center.GetStats().RetainedBytes == center.List().Sum(r => r.RetainedBytes) && center.GetStats().UnreadCount == center.List().Count(r => !r.IsRead), "Concurrent notification accounting wrong.");
    }
    private static void CountAllocation()
    {
        var history = new CommandHistory(); var center = new NotificationCenter();
        for (var i = 0; i < 100; i++) { history.Add(Request(i.ToString()), null, "accepted"); center.Publish("m", "info", "title", "body"); }
        var total = history.Count + center.Count;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++) total += history.Count + center.Count;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(total == 2_000_200 && bytes < 1024, $"Count allocated {bytes} bytes.");
    }
    private static void Plateau()
    {
        var history = new CommandHistory(); var center = new NotificationCenter();
        long commandBytes = 0, notificationBytes = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var id = i.ToString("D8"); history.Add(Request(id), null, "accepted"); history.Complete(Result(id, new string('x', 1000)));
            center.Publish("module", "info", id, new string('n', 1000));
            if (i == 9999) { commandBytes = history.GetStats().RetainedBytes; notificationBytes = center.GetStats().RetainedBytes; }
        }
        Check(commandBytes == history.GetStats().RetainedBytes && notificationBytes == center.GetStats().RetainedBytes, "Charged steady-state history did not plateau.");
        Check(history.GetStats().EvictedCount > 0 && center.GetStats().EvictedUnreadCount > 0, "Plateau did not exercise rollover.");
    }
    private static void DuplicateInvocation()
    {
        var history = new CommandHistory();
        history.Add(Request("same"), null, "accepted"); history.Complete(Result("same", "old"));
        history.Add(Request("same"), null, "accepted"); history.Complete(Result("same", "new"));
        Check(history.Count == 2 && history.List().Count(r => r.Summary == "new") == 1 && history.List().Count(r => r.State == "accepted") == 1, "Duplicate ID first-matching semantics changed.");
        Check(history.GetStats().RetainedBytes == history.List().Sum(r => r.RetainedBytes), "Duplicate ID charge wrong.");
    }
    private static void TruncationCounters()
    {
        var center = new NotificationCenter(new(10, 4096, 1024));
        var notice = center.Publish("m", "info", "title", new string('x', 10_000)); var before = center.GetStats();
        center.MarkRead(notice.Id); center.SetReadState(notice.Id, false);
        Check(center.GetStats().TruncatedCount == before.TruncatedCount && center.GetStats().TruncatedBytes == before.TruncatedBytes, "Read toggles recounted truncation.");
    }
    private static void RejectedIdentityCollection()
    {
        var history = new CommandHistory(new(10, 4096, 1024));
        var weak = AddRejected(history);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!weak.IsAlive && history.Count == 0, "Rejected identity is still rooted by history.");
        GC.KeepAlive(history);
    }
    private static void BinarySignatures()
    {
        Check(typeof(CommandHistory).GetConstructor(Type.EmptyTypes) is not null, "CommandHistory parameterless constructor removed.");
        Check(typeof(NotificationCenter).GetConstructor(Type.EmptyTypes) is not null, "NotificationCenter parameterless constructor removed.");
        Check(typeof(CommandHistory).GetMethod("List", new[] { typeof(string) }) is not null, "CommandHistory.List(string) removed.");
        Check(typeof(NotificationCenter).GetMethod("List", Type.EmptyTypes) is not null, "NotificationCenter.List() removed.");
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AddRejected(CommandHistory history)
    {
        var id = new string('i', 1_000_000); history.Add(Request(id), null, "accepted"); return new WeakReference(id);
    }
}
#endif
