using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MyPowerTools.MobileNotifications.Tests;

/// <summary>
/// One real phone page under test: the shipped surface built from
/// <see cref="MobileNotificationsSurfaceFactory"/>, a real history file through
/// <see cref="FakeNotificationsModule"/>, hosted in a headless Avalonia window at phone size.
/// </summary>
internal sealed class MobileNotificationHost : IDisposable
{
    private MobileNotificationHost(FakeNotificationsModule module, MobileNotificationsView view, Window window)
    {
        Module = module;
        View = view;
        Window = window;
    }

    public FakeNotificationsModule Module { get; }

    public MobileNotificationsView View { get; }

    public Window Window { get; }

    public static MobileNotificationHost Open(
        FakeNotificationsModule module,
        string theme = "light",
        double width = 390,
        double height = 844)
    {
        var context = module.CreateContext() with { Theme = theme };
        var view = (MobileNotificationsView)new MobileNotificationsSurfaceFactory().CreateSurface(context);
        var window = new Window
        {
            Width = width,
            Height = height,
            Content = view,
            RequestedThemeVariant = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Dark
                : ThemeVariant.Light
        };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return new MobileNotificationHost(module, view, window);
    }

    public void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Full layout + dispatcher pass; required after a sheet becomes visible.</summary>
    public void Settle()
    {
        Window.UpdateLayout();
        Pump();
        Window.UpdateLayout();
    }

    public T? Find<T>(string name) where T : Control => View.GetVisualDescendants()
        .OfType<T>()
        .FirstOrDefault(control => control.Name == name);

    public IReadOnlyList<Button> Rows() => View.GetVisualDescendants()
        .OfType<Button>()
        .Where(button => button.Classes.Contains(MobileNotificationTheme.ListRowClass))
        .ToList();

    public IReadOnlyList<Button> Chips() => View.GetVisualDescendants()
        .OfType<Button>()
        .Where(button => button.Classes.Contains(MobileNotificationTheme.FilterClass))
        .ToList();

    public Border? UnreadDot(Visual row) => row.GetVisualDescendants()
        .OfType<Border>()
        .LastOrDefault(border => border.Width == 6 && border.Height == 6);

    public string AllText() => TextOf(View);

    public static string TextOf(Visual root) => string.Join(
        "\n",
        root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(text => text.Text ?? "")
            .Where(text => text.Length > 0));

    public void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }

    /// <summary>Captures the rendered phone frame as PNG evidence under artifacts/.</summary>
    public string Capture(string name)
    {
        Settle();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = Window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The headless host did not produce a rendered frame.");
        var directory = Path.Combine(TestEnvironment.RepositoryRoot(), "artifacts", ".tmp-android-verify", "m5-screens");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{name}.png");
        frame.Save(path);
        return path;
    }

    public void Dispose() => Window.Close();
}
