using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// Builds the phone page over a contract fake and drives it the way the device does: the test stays on
/// the headless UI thread and pumps the dispatcher instead of awaiting, because an async test body
/// resumes on a thread-pool thread and every control access there would fail its thread check.
/// </summary>
internal static class SurfaceHarness
{
    public static (MobileToolControlView View, FakeToolControlModule Module) Create(
        FakeToolControlModule? module = null,
        string theme = "light",
        Func<CancellationToken, Task<string?>>? scan = null)
    {
        module ??= new FakeToolControlModule();
        var context = new MptAvaloniaSurfaceContext(
            MobileToolControlContract.ToolId,
            "workspace",
            Path.Combine(AppContext.BaseDirectory, "data"),
            theme,
            (commandId, args, cancellationToken) => module.ExecuteAsync(commandId, args, cancellationToken),
            (_, _, _) => Task.CompletedTask,
            null!,
            _ => { })
        {
            ScanConnectionCodeAsync = scan
        };
        return (new MobileToolControlView(context), module);
    }

    /// <summary>All leaf texts in the logical tree, for content assertions.</summary>
    public static IReadOnlyList<string> Texts(Control root) => root
        .GetLogicalDescendants()
        .OfType<TextBlock>()
        .Select(block => block.Text ?? "")
        .Where(text => text.Length > 0)
        .ToArray();

    /// <summary>Finds a button by its own content text or by the first text inside it.</summary>
    public static Button FindButton(Control root, string text)
    {
        foreach (var button in root.GetLogicalDescendants().OfType<Button>())
        {
            if (button.Content is string content && content.Contains(text, StringComparison.Ordinal))
            {
                return button;
            }

            var inner = button.GetLogicalDescendants().OfType<TextBlock>()
                .Any(block => (block.Text ?? "").Contains(text, StringComparison.Ordinal));
            if (inner)
            {
                return button;
            }
        }

        throw new InvalidOperationException($"没有找到包含“{text}”的按钮。");
    }

    /// <summary>Runs queued dispatcher work, like the device would between input events.</summary>
    public static void Pump()
    {
        for (var pass = 0; pass < 4; pass++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Drives the dispatcher until the task finished. Pumping instead of blocking keeps the test on the
    /// headless UI thread and lets a continuation posted back from a thread-pool thread run.
    /// </summary>
    public static void Complete(Task task)
    {
        for (var pass = 0; pass < 400 && !task.IsCompleted; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Pump();
            if (!task.IsCompleted)
            {
                Thread.Sleep(2);
            }
        }

        Assert.True(task.IsCompleted, "操作没有在预期时间内完成。");
        task.GetAwaiter().GetResult();
        Pump();
    }

    public static T Complete<T>(Task<T> task)
    {
        Complete((Task)task);
        return task.Result;
    }

    /// <summary>Pumps until the condition holds, so async module reads are not raced.</summary>
    public static void WaitFor(Func<bool> condition, string because)
    {
        for (var pass = 0; pass < 400 && !condition(); pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Pump();
            if (!condition())
            {
                Thread.Sleep(2);
            }
        }

        Assert.True(condition(), because);
    }

    public static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }
}
