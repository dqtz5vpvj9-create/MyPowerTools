using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MyPowerTools.Shell.Avalonia;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class HostConnectionRecoveryTests
{
    [Fact]
    public async Task Healthy_runner_sleeps_until_disconnect_then_recovers_without_another_signal()
    {
        var probe = new SwitchableProbe();
        await using var monitor = new HostControlConnectionMonitor(probe,
            pollInterval: TimeSpan.FromMilliseconds(20), eventDriven: true);
        var states = Channel.CreateUnbounded<HostControlConnectionSnapshot>();
        monitor.StateChanged += (_, snapshot) => states.Writer.TryWrite(snapshot);
        monitor.Start();
        Assert.True((await states.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Online);
        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref probe.Count));

        probe.Online = false;
        monitor.RequestCheck();
        Assert.False((await states.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))).Online);
        probe.Online = true;
        HostControlConnectionSnapshot recovered;
        do
        {
            recovered = await states.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        } while (!recovered.Online);
        Assert.True(recovered.Recovered);
        var count = Volatile.Read(ref probe.Count);
        await Task.Delay(150);
        Assert.Equal(count, Volatile.Read(ref probe.Count));
    }

    [Fact]
    public async Task Closed_event_stream_notifies_connection_monitor_and_reconnects()
    {
        var source = new ClosedThenWaitingSource();
        await using var monitor = new HostControlEventStreamMonitor(source, TimeSpan.FromMilliseconds(20));
        var fault = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.StreamFaulted += (_, error) => fault.TrySetResult(error);
        monitor.Start();
        Assert.IsType<IOException>(await fault.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await source.Reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, source.Calls);
    }

    private sealed class SwitchableProbe : IHostControlConnectionProbe
    {
        public volatile bool Online = true;
        public int Count;
        public Task<HostControlConnectionProbeResult> PingAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Count);
            return Online
                ? Task.FromResult(new HostControlConnectionProbeResult("test", "running"))
                : Task.FromException<HostControlConnectionProbeResult>(new IOException("Runner exited"));
        }
    }

    private sealed class ClosedThenWaitingSource : IHostControlEventSource
    {
        public int Calls;
        public TaskCompletionSource Reconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<HostProto.HostEvent> SubscribeAsync(ulong lastEventSeq,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref Calls) == 1) yield break;
            Reconnected.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
