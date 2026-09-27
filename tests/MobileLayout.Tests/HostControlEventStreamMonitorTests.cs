using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MyPowerTools.Shell.Avalonia;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MobileLayout.Tests;

/// <summary>
/// The Shell starts its host event stream from a Background-priority dispatcher job on the UI thread.
/// The stream loop used to run inline there and captured the ambient SynchronizationContext, so every
/// event continuation -- and therefore every surface UI update -- was scheduled back through the
/// dispatcher at that priority and waited for the next input to run. The loop must execute off the
/// starting thread and must not capture its context.
/// </summary>
public sealed class HostControlEventStreamMonitorTests
{
    [Fact]
    public async Task Stream_loop_does_not_capture_the_synchronization_context_it_was_started_on()
    {
        var previous = SynchronizationContext.Current;
        var context = new CountingSynchronizationContext();
        var source = new GatedEventSource();
        var received = new List<HostProto.HostEvent>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var monitor = new HostControlEventStreamMonitor(source, TimeSpan.FromMilliseconds(10));
        monitor.EventReceived += (_, evt) =>
        {
            lock (received) received.Add(evt);
            if (received.Count == 2) delivered.TrySetResult();
        };

        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            // Start from the same ambient context as the Shell's dispatcher callback.
            monitor.Start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        source.Publish(1, "transfer.changed");
        source.Publish(2, "transfer.changed");
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([1UL, 2UL], received.Select(evt => evt.Seq).ToArray());
        Assert.Equal(0, context.PostCount);
    }

    /// <summary>Counts posts so a captured context is observable instead of silently deadlocking.</summary>
    private sealed class CountingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            d(state);
        }
    }

    private sealed class GatedEventSource : IHostControlEventSource
    {
        private readonly Channel<HostProto.HostEvent> _events = Channel.CreateUnbounded<HostProto.HostEvent>();

        public void Publish(ulong seq, string type) =>
            _events.Writer.TryWrite(new HostProto.HostEvent { Seq = seq, Type = type });

        public async IAsyncEnumerable<HostProto.HostEvent> SubscribeAsync(
            ulong lastEventSeq,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // A suspension before the first event is what exposes a captured context.
            await Task.Yield();
            await foreach (var evt in _events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }
        }
    }
}
