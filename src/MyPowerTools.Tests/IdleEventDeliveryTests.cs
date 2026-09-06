using MyPowerTools.Runtime;
using MyPowerTools.ServiceManager.Server;
using System.Text.Json.Nodes;

namespace MyPowerTools.Tests;

public sealed class IdleEventDeliveryTests
{
    [Fact]
    public async Task Host_events_wake_all_subscribers_without_losing_the_read_wait_race()
    {
        var bus = new EventBus();
        var first = bus.WaitForEventsAsync(0, CancellationToken.None);
        var second = bus.WaitForEventsAsync(0, CancellationToken.None);
        Assert.False(first.IsCompleted);
        bus.Publish("tool", "changed", new JsonObject());
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        await bus.WaitForEventsAsync(0, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        using var stop = new CancellationTokenSource();
        var waiting = bus.WaitForEventsAsync(bus.CurrentSeq, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var next = bus.WaitForEventsAsync(bus.CurrentSeq, CancellationToken.None);
        bus.Publish("tool", "next", new JsonObject());
        await next.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Unit_events_allow_filtered_subscribers_to_advance_past_other_units()
    {
        var bus = new UnitEventBus();
        bus.Publish("other", "running", new JsonObject());
        var observedSeq = bus.CurrentSeq;
        Assert.Empty(bus.Since(0, "wanted"));
        var pending = bus.WaitForEventsAsync(observedSeq, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        bus.Publish("wanted", "running", new JsonObject());
        await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(bus.Since(observedSeq, "wanted"));
    }
}
