using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using System.Globalization;
using System.Text.Json.Nodes;
using Sdk = MyPowerTools.Abstractions;
using MyPowerTools.Abstractions;
using MyPowerTools.Runtime;

namespace MyPowerTools.Tests;

public sealed class HistoryCountTests
{
    [Fact]
    public void Command_count_preserves_completed_and_active_history()
    {
        var history = new CommandHistory();
        Assert.Equal(0, history.Count);
        history.Add(new CommandRequest("first", "example", new JsonObject()), null, "accepted");
        history.Add(new CommandRequest("second", "example", new JsonObject()), null, "accepted");
        history.Complete(new CommandExecutionResult("first", "example", "succeeded", true, "complete output"));
        Assert.Equal(2, history.Count);
        Assert.Equal(history.List().Count, history.Count);
        Assert.Contains(history.List(), item => item.Summary == "complete output");
        Assert.Contains(history.List(), item => item.InvocationId == "second" && item.State == "accepted");
    }

    [Fact]
    public void Notification_count_includes_read_and_unread_without_discarding_bodies()
    {
        var center = new NotificationCenter();
        Assert.Equal(0, center.Count);
        var first = center.Publish("test", "info", "first", "full first body");
        center.Publish("test", "info", "second", "full second body");
        center.MarkRead(first.Id);
        Assert.Equal(2, center.Count);
        Assert.Equal(center.List().Count, center.Count);
        Assert.Contains(center.List(), item => item.IsRead && item.Body == "full first body");
        Assert.Contains(center.List(), item => !item.IsRead && item.Body == "full second body");
    }

    [Fact]
    [Trait("HistoryBehavior", "H18")]
    public void Count_does_not_allocate_sorted_history_snapshots()
    {
        // This fixture tests Count allocation independently of default rollover.
        var options = new HistoryBufferOptions(10_000, 16 * 1024 * 1024, 16 * 1024);
        var history = new CommandHistory(options);
        var center = new NotificationCenter(options);
        for (var index = 0; index < 10_000; index++)
        {
            history.Add(new CommandRequest(index.ToString(), "example", new JsonObject()), null, "accepted");
            center.Publish("test", "info", "title", "body");
        }
        // Warm locks/accessors before measuring this thread; no forced product GC.
        Assert.Equal(10_000, history.Count);
        Assert.Equal(10_000, center.Count);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = 0;
        for (var index = 0; index < 100; index++) total += history.Count + center.Count;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(2_000_000, total);
        Assert.True(allocated < 1024, $"Count allocated {allocated} bytes.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H01")]
    public void History_defaults_and_invalid_budgets_are_enforced()
    {
        Assert.True(HistoryBufferOptions.CommandDefaults == new HistoryBufferOptions(4096, 4 * 1024 * 1024, 16384), "Command defaults changed.");
        Assert.True(HistoryBufferOptions.NotificationDefaults == new HistoryBufferOptions(2048, 2 * 1024 * 1024, 16384), "Notification defaults changed.");
        foreach (var options in new[] { new HistoryBufferOptions(0, 1024, 512), new(1, 511, 512), new(1, 1024, 511), new(1, 1024, 2048) })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CommandHistory(options));
        }
    }

    [Fact]
    [Trait("HistoryBehavior", "H02")]
    public void Command_entry_rollover_evicts_oldest_and_preserves_accounting()
    {
        var history = new CommandHistory(new(3, 8192, 2048));
        for (var i = 0; i < 10; i++) history.Add(Request(i.ToString()), null, "accepted");
        Assert.True(history.List().Select(r => r.InvocationId).Order().SequenceEqual(new[] { "7", "8", "9" }), "Oldest history not removed.");
        var stats = history.GetStats();
        Assert.True(stats.Count == 3 && stats.AddedCount == 10 && stats.EvictedCount == 7 && stats.LastEvictedAt.HasValue, "Entry rollover counters wrong.");
        Assert.True(stats.RetainedBytes == history.List().Sum(r => r.RetainedBytes), "Retained charge inconsistent.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H03")]
    public void Command_completion_growth_obeys_byte_budget()
    {
        var history = new CommandHistory(new(20, 2048, 2048));
        history.Add(Request("old"), null, "accepted");
        history.Add(Request("new"), null, "accepted");
        history.Complete(Result("new", new string('x', 800)));
        Assert.True(history.Count == 1 && history.List()[0].InvocationId == "new", "Completion growth did not evict oldest.");
        AssertBounds(history.GetStats());
        Assert.True(history.GetStats().TruncatedCount == 1, "Huge completion not counted as truncated.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H04")]
    public void Old_command_completion_can_evict_itself_without_resurrection()
    {
        var history = new CommandHistory(new(20, 2048, 2048));
        history.Add(Request("old"), null, "accepted");
        history.Add(Request("new"), null, "accepted");
        history.Complete(Result("old", new string('x', 800)));
        Assert.True(history.Count == 1 && history.List()[0].InvocationId == "new", "Old completion should obey oldest-first budget.");
        history.Complete(Result("old", "late"));
        Assert.True(history.GetStats().UnretainedCompletionCount == 1, "Missing old completion was not reported.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H05")]
    public void Completion_after_command_eviction_is_counted_without_retention()
    {
        var history = new CommandHistory(new(1, 2048, 2048));
        history.Add(Request("active"), null, "accepted");
        history.Add(Request("later"), null, "accepted");
        history.Complete(Result("active", new string('x', 1_000_000)));
        Assert.True(history.List()[0].InvocationId == "later" && history.GetStats().UnretainedCompletionCount == 1, "Evicted active history resurrected.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H06")]
    public void Large_unicode_output_truncates_at_whole_grapheme_boundaries()
    {
        const string cluster = "👩🏽‍💻e\u0301";
        var payload = string.Concat(Enumerable.Repeat(cluster, 200_000));
        var history = new CommandHistory(new(10, 4096, 1024));
        history.Add(Request("unicode"), null, "accepted");
        history.Complete(Result("unicode", payload));
        var record = history.List().Single();
        Assert.True(record.IsTruncated && record.RetainedBytes <= 1024 && record.OriginalBytes > 1_000_000, "Huge Unicode record cap failed.");
        var retainedPrefix = record.Summary.Replace("… [truncated]", "", StringComparison.Ordinal);
        var boundary = StringInfo.ParseCombiningCharacters(payload[..Math.Min(500, payload.Length)]);
        Assert.True(payload.StartsWith(retainedPrefix, StringComparison.Ordinal) && boundary.Contains(retainedPrefix.Length), "Truncation split a Unicode text element.");
        Assert.True(!retainedPrefix.Contains('\ufffd'), "Replacement characters introduced.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H07")]
    public void History_redacts_command_and_notification_secrets_before_truncation()
    {
        var history = new CommandHistory(new(10, 4096, 1024));
        history.Add(Request("secret"), null, "accepted");
        history.Complete(Result("secret", "token=" + new string('s', 20_000) + " {\"confirmationToken\":\"" + new string('z', 20_000) + "\"} " + new string('x', 2000)));
        var summary = history.List().Single().Summary;
        Assert.True(summary.Contains("token=****") && summary.Contains("\"confirmationToken\":\"****\"") && !summary.Contains("ssss") && !summary.Contains("zzzz"), "Secret leaked at truncation boundary.");
        var center = new NotificationCenter(new(10, 4096, 1024));
        var notice = center.Publish("module", "info", "password=title-secret", "apiKey=body-secret " + new string('x', 2000));
        Assert.True(!notice.Title.Contains("title-secret") && !notice.Body.Contains("body-secret"), "Notification redaction failed.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H08")]
    public void Command_error_summary_fallback_remains_redacted()
    {
        var history = new CommandHistory();
        history.Add(Request("error"), null, "accepted");
        history.Complete(new Sdk.CommandExecutionResult("error", "command", "failed", false, "  ", new Sdk.MptRuntimeError("error", "password=secret failed")));
        Assert.True(history.List().Single().Summary == "password=**** failed", "Error fallback changed.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H09")]
    public void Oversized_command_and_module_identities_are_rejected_unchanged()
    {
        var history = new CommandHistory(new(10, 4096, 1024));
        var id = new string('i', 10_000);
        var record = history.Add(Request(id), null, "accepted");
        Assert.True(record.InvocationId == id && !record.IsRetained && record.RetainedBytes == 0, "Identity was truncated or admitted.");
        history.Complete(Result(id, "done"));
        Assert.True(history.Count == 0 && history.GetStats().RejectedCount == 1 && history.GetStats().UnretainedCompletionCount == 1, "Identity rejection counters wrong.");
        var longCommand = history.Add(Request("normal", id), null, "accepted");
        var longModule = history.Add(Request("normal"), Descriptor(id), "accepted");
        Assert.True(longCommand.CommandId == id && longModule.ModuleId == id && history.Count == 0, "Command/module identity not preserved.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H10")]
    public void Notification_rollover_evicts_read_entries_before_unread_entries()
    {
        var center = new NotificationCenter(new(3, 8192, 2048));
        var oldestUnread = center.Publish("m", "warning", "first", "body");
        var read = center.Publish("m", "info", "second", "body");
        center.MarkRead(read.Id);
        center.Publish("m", "info", "third", "body");
        center.Publish("m", "info", "fourth", "body");
        Assert.True(center.List().Any(n => n.Id == oldestUnread.Id) && center.List().All(n => n.Id != read.Id), "Read record was not evicted first.");
        Assert.True(center.GetStats().EvictedCount == 1 && center.GetStats().EvictedUnreadCount == 0 && center.GetStats().UnreadCount == 3, "Read-first stats wrong.");
        Assert.True(center.SetReadState(read.Id, true) is null, "Evicted record accepted read update.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H11")]
    public void Unread_notification_overflow_is_counted_without_marking_read()
    {
        var center = new NotificationCenter(new(3, 8192, 2048));
        NotificationRecord? lastEvicted = null;
        for (var i = 0; i < 10; i++) { var n = center.Publish("m", "info", i.ToString(), "body"); if (i == 6) lastEvicted = n; }
        var stats = center.GetStats();
        Assert.True(stats.EvictedUnreadCount == 7 && stats.UnreadCount == 3 && center.List().All(n => !n.IsRead), "Unread overflow was hidden/marked read.");
        Assert.True(stats.LastEvictedUnreadAt == lastEvicted!.Time, "Unread overflow timestamp wrong.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H12")]
    public void Notification_read_reversal_preserves_unread_count_and_oldest_read_eviction()
    {
        var center = new NotificationCenter(new(2, 4096, 2048));
        var first = center.Publish("m", "info", "first", "body");
        // Initial/already-read idempotence is owned by Notification_center_updates_read_state_idempotently.
        center.MarkRead(first.Id);
        Assert.True(center.SetReadState(first.Id, false)!.Changed && center.GetStats().UnreadCount == 1, "Read reversal failed.");
        var second = center.Publish("m", "info", "second", "body");
        center.MarkRead(first.Id); center.MarkRead(second.Id);
        center.Publish("m", "info", "third", "body");
        Assert.True(center.List().All(n => n.Id != first.Id) && center.GetStats().UnreadCount == 1, "Oldest read order changed.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H13")]
    public void Notification_byte_budget_preserves_unread_accounting()
    {
        var center = new NotificationCenter(new(100, 2048, 1024));
        for (var i = 0; i < 50; i++) { var item = center.Publish("module", "info", "title", new string('x', 500)); if (i % 2 == 0) center.MarkRead(item.Id); }
        AssertBounds(center.GetStats());
        Assert.True(center.GetStats().RetainedBytes == center.List().Sum(n => n.RetainedBytes), "Notification bytes inconsistent.");
        Assert.True(center.GetStats().UnreadCount == center.List().Count(n => !n.IsRead), "Notification unread inconsistent.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H14")]
    public void Large_notification_titles_and_bodies_are_bounded()
    {
        var center = new NotificationCenter();
        var record = center.Publish("module", "warning", string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", 100_000)), string.Concat(Enumerable.Repeat("🧪e\u0301", 500_000)));
        Assert.True(record.IsTruncated && record.Title.Length <= 1024 && record.RetainedBytes <= 16384 && record.OriginalBytes > 1_000_000, "Huge notification escaped bounds.");
        Assert.True(center.GetStats().TruncatedCount == 1 && center.GetStats().TruncatedBytes == record.OriginalBytes - record.RetainedBytes, "Truncated bytes metadata wrong.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H15")]
    public void Oversized_notification_module_identity_is_rejected_unchanged()
    {
        var center = new NotificationCenter(new(10, 4096, 1024));
        var module = new string('m', 10_000);
        var record = center.Publish(module, "info", "title", "body");
        Assert.True(record.ModuleId == module && !record.IsRetained && center.Count == 0 && center.GetStats().RejectedCount == 1, "Module identity rejection failed.");
        Assert.True(center.SetReadState(record.Id, true) is null, "Rejected record entered read-state store.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H16")]
    public void History_filters_snapshots_and_half_open_time_bounds_are_preserved()
    {
        var center = new NotificationCenter();
        var one = center.Publish("one", "warning", "title", "body");
        center.Publish("two", "info", "title", "body");
        var snapshot = center.List(); center.MarkRead(one.Id);
        Assert.True(snapshot.All(n => !n.IsRead), "Published snapshot mutated.");
        Assert.True(center.Query(moduleId: "one", level: "WARNING", isRead: true).Single().Id == one.Id, "Notification filters changed.");
        Assert.True(center.Query(since: one.Time, until: one.Time).Count == 0 && center.Query(until: DateTimeOffset.MinValue).Count == 0, "Time bounds not half-open.");
        Assert.True(center.Query(limit: 0).Count == 0 && center.Query(limit: -1).Count == 0 && center.Query(limit: 1).Count == 1, "Notification limit wrong.");
        var history = new CommandHistory(); history.Add(Request("one"), Descriptor("module"), "accepted"); history.Complete(Result("one", "done"));
        Assert.True(history.Query("module", state: "SUCCEEDED").Count == 1 && history.List("MODULE").Count == 0, "Command module/state filters wrong.");
        Assert.True(history.Query(until: history.List()[0].StartedAt).Count == 0, "Command until not exclusive.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H17")]
    public void Concurrent_history_writes_and_read_updates_preserve_accounting()
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
                if (i % 100 == 0) { AssertBounds(history.GetStats()); AssertBounds(center.GetStats()); _ = history.Query(limit: 5); _ = center.Query(limit: 5); }
            }
        });
        AssertBounds(history.GetStats()); AssertBounds(center.GetStats());
        Assert.True(history.GetStats().AddedCount == 8000 && center.GetStats().AddedCount == 8000, "Concurrent writes lost.");
        Assert.True(history.GetStats().RetainedBytes == history.List().Sum(r => r.RetainedBytes), "Concurrent command byte accounting wrong.");
        Assert.True(center.GetStats().RetainedBytes == center.List().Sum(r => r.RetainedBytes) && center.GetStats().UnreadCount == center.List().Count(r => !r.IsRead), "Concurrent notification accounting wrong.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H19")]
    public void Default_history_budgets_reach_a_steady_state_retention_plateau()
    {
        var history = new CommandHistory(); var center = new NotificationCenter();
        long commandBytes = 0, notificationBytes = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var id = i.ToString("D8"); history.Add(Request(id), null, "accepted"); history.Complete(Result(id, new string('x', 1000)));
            center.Publish("module", "info", id, new string('n', 1000));
            if (i == 9999) { commandBytes = history.GetStats().RetainedBytes; notificationBytes = center.GetStats().RetainedBytes; }
        }
        Assert.True(commandBytes == history.GetStats().RetainedBytes && notificationBytes == center.GetStats().RetainedBytes, "Charged steady-state history did not plateau.");
        Assert.True(history.GetStats().EvictedCount > 0 && center.GetStats().EvictedUnreadCount > 0, "Plateau did not exercise rollover.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H20")]
    public void Duplicate_invocation_completion_updates_the_first_matching_record()
    {
        var history = new CommandHistory();
        history.Add(Request("same"), null, "accepted"); history.Complete(Result("same", "old"));
        history.Add(Request("same"), null, "accepted"); history.Complete(Result("same", "new"));
        Assert.True(history.Count == 2 && history.List().Count(r => r.Summary == "new") == 1 && history.List().Count(r => r.State == "accepted") == 1, "Duplicate ID first-matching semantics changed.");
        Assert.True(history.GetStats().RetainedBytes == history.List().Sum(r => r.RetainedBytes), "Duplicate ID charge wrong.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H21")]
    public void Notification_read_state_updates_do_not_recount_truncation()
    {
        var center = new NotificationCenter(new(10, 4096, 1024));
        var notice = center.Publish("m", "info", "title", new string('x', 10_000)); var before = center.GetStats();
        center.MarkRead(notice.Id); center.SetReadState(notice.Id, false);
        Assert.True(center.GetStats().TruncatedCount == before.TruncatedCount && center.GetStats().TruncatedBytes == before.TruncatedBytes, "Read toggles recounted truncation.");
    }

    [Fact]
    [Trait("HistoryBehavior", "H22")]
    public void Rejected_large_command_identities_are_not_rooted_by_history()
    {
        var history = new CommandHistory(new(10, 4096, 1024));
        var weak = AddRejected(history);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(!weak.IsAlive && history.Count == 0, "Rejected identity is still rooted by history.");
        GC.KeepAlive(history);
    }

    [Fact]
    [Trait("HistoryBehavior", "H23")]
    public void Original_history_constructors_and_list_clr_signatures_remain_available()
    {
        Assert.True(typeof(CommandHistory).GetConstructor(Type.EmptyTypes) is not null, "CommandHistory parameterless constructor removed.");
        Assert.True(typeof(NotificationCenter).GetConstructor(Type.EmptyTypes) is not null, "NotificationCenter parameterless constructor removed.");
        Assert.True(typeof(CommandHistory).GetMethod("List", new[] { typeof(string) }) is not null, "CommandHistory.List(string) removed.");
        Assert.True(typeof(NotificationCenter).GetMethod("List", Type.EmptyTypes) is not null, "NotificationCenter.List() removed.");
    }

    private static Sdk.CommandRequest Request(string id, string command = "command") => new(id, command, new JsonObject());
    private static Sdk.CommandExecutionResult Result(string id, string output, string state = "succeeded") => new(id, "command", state, true, output);
    private static Sdk.MptCommandDescriptor Descriptor(string module) => new("command", module, "title", "subtitle", "execute");
    private static void AssertBounds(HistoryBufferStats stats)
    {
        Assert.True(stats.Count <= stats.MaxEntries && stats.Count >= 0, "Entry limit violated.");
        Assert.True(stats.RetainedBytes <= stats.MaxBytes && stats.RetainedBytes >= 0, "Byte limit violated.");
        Assert.True(stats.UnreadCount <= stats.Count && stats.UnreadCount >= 0, "Unread count invalid.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference AddRejected(CommandHistory history)
    {
        var id = new string('i', 1_000_000); history.Add(Request(id), null, "accepted"); return new WeakReference(id);
    }
}
