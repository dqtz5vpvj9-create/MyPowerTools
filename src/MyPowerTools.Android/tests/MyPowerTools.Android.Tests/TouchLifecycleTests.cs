using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Themes.Fluent;
using MyPowerTools.Android.Input;
using System.Reflection;

namespace MyPowerTools.Android.Tests;

[Collection("Android Avalonia host")]
public sealed class TouchLifecycleTests
{
    [Fact]
    public async Task Pause_cancels_all_captured_contacts_and_next_tap_reaches_another_control()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TouchTestAppBuilder));
        await session.Dispatch(() =>
        {
            var first = new Button { Content = "Open file", Height = 80 };
            var next = new Button { Content = "Devices", Height = 80 };
            var window = new Window
            {
                Width = 360, Height = 640,
                Content = new StackPanel { Children = { first, next } }
            };
            window.Show();
            // Avalonia's reference assembly hides the platform input APIs. Reflection stays in
            // this test adapter; production uses Android's native MotionEvent API.
            using var device = (TouchDevice)Activator.CreateInstance(typeof(TouchDevice))!;
            var sequences = new ActiveTouchSequences();
            var inputProperty = typeof(Avalonia.Platform.ITopLevelImpl).GetProperty("Input")!;
            var platform = window.PlatformImpl!;
            var input = (Action<RawInputEventArgs>)inputProperty.GetValue(platform)!;
            IInputRoot? root = null;
            // Obtain the real headless presentation root through its public input callback.
            inputProperty.SetValue(platform, (Action<RawInputEventArgs>)(args =>
            {
                if (args is RawPointerEventArgs pointer)
                    root = (IInputRoot)typeof(RawPointerEventArgs).GetProperty("Root")!.GetValue(pointer)!;
                input(args);
            }));
            window.MouseMove(new Point(10, 10));
            inputProperty.SetValue(platform, input);
            Assert.NotNull(root);
            ulong clock = 1000;
            RawTouchEventArgs Send(RawPointerEventType type, int id, Point point)
            {
                var args = (RawTouchEventArgs)Activator.CreateInstance(typeof(RawTouchEventArgs),
                    device, ++clock, root!, type, point, RawInputModifiers.None, (long)id)!;
                input(args);
                return args;
            }

            IPointer? Pointer(RawPointerEventArgs args) => (IPointer?)typeof(TouchDevice)
                .GetMethod("TryGetPointer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(device, new object[] { args });

            var firstClicks = 0;
            var nextClicks = 0;
            first.Click += (_, _) => firstClicks++;
            next.Click += (_, _) => nextClicks++;
            var firstPoint = first.TranslatePoint(new Point(20, 20), window)!.Value;
            var nextPoint = next.TranslatePoint(new Point(20, 20), window)!.Value;
            sequences.Begin(0);
            var down = Send(RawPointerEventType.TouchBegin, 0, firstPoint);
            var firstPointer = Pointer(down);
            sequences.Begin(7);
            var secondDown = Send(RawPointerEventType.TouchBegin, 7, firstPoint);
            var secondPointer = Pointer(secondDown);
            Assert.NotNull(firstPointer?.Captured);
            Assert.NotNull(secondPointer?.Captured);

            // No UP arrives: the activity loses its window to a viewer. Do exactly the cancellation
            // the Android adapter translates into native ACTION_CANCEL, once for each active ID.
            foreach (var id in sequences.TakeForCancellation())
                Send(RawPointerEventType.TouchCancel, id, firstPoint);
            Assert.Null(firstPointer!.Captured);
            Assert.Null(secondPointer!.Captured);
            Assert.Null(Pointer(down));
            Assert.Null(Pointer(secondDown));
            Assert.Empty(sequences.TakeForCancellation()); // FocusLost after Pause is harmless.
            Assert.Equal(0, firstClicks); // Cancellation must never trigger the original Open button.

            // Android reuses ID 0 on return; this must hit Devices, not the old captured Open button.
            sequences.Begin(0);
            Send(RawPointerEventType.TouchBegin, 0, nextPoint);
            Send(RawPointerEventType.TouchEnd, 0, nextPoint);
            sequences.End(0);
            Assert.Equal(1, nextClicks);
            Assert.Equal(0, firstClicks);
            Assert.Empty(sequences.TakeForCancellation());
            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public void Completed_contact_is_not_cancelled_and_later_contacts_are_not_in_a_prior_batch()
    {
        var sequences = new ActiveTouchSequences();
        sequences.Begin(0);
        sequences.Begin(7);
        sequences.End(0);
        var interrupted = sequences.TakeForCancellation();
        sequences.Begin(0);
        Assert.Equal(new[] { 7 }, interrupted);
        Assert.Equal(new[] { 0 }, sequences.TakeForCancellation());
    }
}

public sealed class TouchTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TouchTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TouchTestApp>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
