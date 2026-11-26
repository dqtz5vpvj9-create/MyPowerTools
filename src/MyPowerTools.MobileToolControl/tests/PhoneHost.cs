using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.MobileToolControl.Tests;

/// <summary>
/// Window-based host for the page, used by the theme and screenshot checks. Unlike the logic harness it
/// shows a real (headless) window, so the page is laid out and rendered by Skia through the shipped
/// mobile theme, and a frame can be captured for review.
/// </summary>
internal sealed class PhoneHost : IDisposable
{
    private PhoneHost(FakeToolControlModule module, MobileToolControlView view, Window window)
    {
        Module = module;
        View = view;
        Window = window;
    }

    public FakeToolControlModule Module { get; }

    public MobileToolControlView View { get; }

    public Window Window { get; }

    public MobileToolControlViewModel Vm => View.ViewModel;

    public static PhoneHost Create(
        FakeToolControlModule? module = null,
        double width = 390,
        double height = 844,
        Func<CancellationToken, Task<string?>>? scan = null)
    {
        module ??= new FakeToolControlModule();
        var (view, _) = SurfaceHarness.Create(module, scan: scan);
        var window = new Window { Width = width, Height = height, Content = view };
        var host = new PhoneHost(module, view, window);
        window.Show();
        host.Pump();
        return host;
    }

    public void Pump()
    {
        for (var pass = 0; pass < 4; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }
    }

    public void WaitFor(Func<bool> condition, string because)
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

    public void Complete(Task task)
    {
        for (var pass = 0; pass < 800 && !task.IsCompleted; pass++)
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

    public void Resize(double width, double height)
    {
        Window.Width = width;
        Window.Height = height;
        Pump();
    }

    public void SetTheme(ThemeVariant variant)
    {
        Application.Current!.RequestedThemeVariant = variant;
        Pump();
        Window.UpdateLayout();
        Pump();
    }

    public void Click(Button button)
    {
        Assert.True(button.IsEnabled, "按钮当前不可点击。");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    /// <summary>Control classes an element in the visual tree carries, for theme assertions.</summary>
    public T? Find<T>(Func<T, bool> predicate) where T : Control =>
        View.GetLogicalDescendants().OfType<T>().FirstOrDefault(predicate);

    public IReadOnlyList<T> FindAll<T>(Func<T, bool> predicate) where T : Control =>
        View.GetLogicalDescendants().OfType<T>().Where(predicate).ToArray();

    /// <summary>Renders the current frame and writes it to the temporary verification area.</summary>
    public string Save(string fileName)
    {
        var frame = Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.True(frame!.PixelSize.Width > 0 && frame.PixelSize.Height > 0, $"{fileName} 是空帧。");

        var directory = Path.Combine(RepoPaths.Root(), "artifacts", ".tmp-android-verify", "g2-theme-shots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        frame.Save(path);
        return path;
    }

    public void Dispose() => Window.Close();
}

internal static class RepoPaths
{
    public static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
