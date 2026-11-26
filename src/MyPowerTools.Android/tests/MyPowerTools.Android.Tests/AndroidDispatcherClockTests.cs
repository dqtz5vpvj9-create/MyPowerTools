using System.Reflection;
using Avalonia.Headless;
using Avalonia.Threading;
using MyPowerTools.Android.Input;

namespace MyPowerTools.Android.Tests;

[Collection("Android Avalonia host")]
public sealed class AndroidDispatcherClockTests
{
    [Fact]
    public async Task Platform_clock_replaces_earlier_epoch_and_delayed_work_still_runs()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TouchTestAppBuilder));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await session.Dispatch(async () =>
        {
            var dispatcher = Dispatcher.UIThread;
            const BindingFlags fields = BindingFlags.NonPublic | BindingFlags.Instance;
            var implementation = typeof(Dispatcher).GetField("_impl", fields)!.GetValue(dispatcher)!;
            var platformClock = implementation.GetType().GetProperty("Now")!.GetMethod!.CreateDelegate<Func<long>>(implementation);
            var clockField = typeof(Dispatcher).GetField("_timeProvider", fields)!;
            // Reproduce the different epoch installed before Android's dispatcher implementation.
            clockField.SetValue(dispatcher, (Func<long>)(() => platformClock() - 10000));
            AndroidDispatcherClock.Align();
            var aligned = (Func<long>)clockField.GetValue(dispatcher)!;
            var before = platformClock();
            var now = aligned();
            Assert.InRange(now, before, platformClock());
            DispatcherTimer.RunOnce(() => completed.SetResult(), TimeSpan.FromMilliseconds(100));
            Assert.False(completed.Task.IsCompleted);
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }, CancellationToken.None);
    }
}
