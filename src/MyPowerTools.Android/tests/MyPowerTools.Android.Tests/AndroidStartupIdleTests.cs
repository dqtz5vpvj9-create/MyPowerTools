using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace MyPowerTools.Android.Tests;

[Collection("Android Avalonia host")]
public sealed class AndroidStartupIdleTests
{
    [Fact]
    public async Task Showing_shell_releases_startup_animation_subscriptions()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TouchTestAppBuilder));
        await session.Dispatch(async () =>
        {
            var startup = new AndroidStartupView();
            var window = new Window { Width = 390, Height = 640, Content = startup };
            window.Show();
            window.UpdateLayout();
            await Task.Delay(100);
            Assert.True(AnimationSubscriptions() > 0, "The launch progress must exercise real Fluent animations.");
            startup.ShowShell(new Border());
            window.UpdateLayout();
            await Task.Delay(100);
            var remaining = AnimationSubscriptions();
            var progress = (ProgressBar)typeof(AndroidStartupView).GetField("_progress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(startup)!;
            var stillIndeterminate = progress.IsIndeterminate;
            // Cleanup even for the old behavior, so a live animation cannot mask the assertion
            // with an unrelated Headless session-disposal error.
            startup.ShowFailure(new Exception("Test cleanup"), () => { });
            await Task.Delay(100);
            window.Close();
            Assert.False(stillIndeterminate, "Startup must end its busy state before retaining the hidden control.");
            Assert.Equal(0, remaining);
        }, CancellationToken.None);
    }

    private static int AnimationSubscriptions()
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(Dispatcher).Assembly.GetType("Avalonia.Media.MediaContext")!;
        var context = type.GetProperty("Instance")!.GetValue(null)!;
        var clock = type.GetField("_clock", fields)!.GetValue(context)!;
        return ((System.Collections.ICollection)clock.GetType().GetField("_observers", fields)!.GetValue(clock)!).Count;
    }
}
