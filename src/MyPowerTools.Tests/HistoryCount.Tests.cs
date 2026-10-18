using CommandRequest = MyPowerTools.Abstractions.CommandRequest;
using CommandExecutionResult = MyPowerTools.Abstractions.CommandExecutionResult;
using System.Text.Json.Nodes;
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
}
