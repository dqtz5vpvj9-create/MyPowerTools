using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Button = Avalonia.Controls.Button;
using Orientation = Avalonia.Layout.Orientation;
using ProgressBar = Avalonia.Controls.ProgressBar;

namespace MyPowerTools.Android;

/// <summary>
/// The first screen the Android activity shows. It is the launch frame of the approved mobile
/// design — brand mark, one line of real initialization status, a bounded progress bar — instead of
/// the desktop diagnostics panel the preview used to draw: a phone launch lasts a few seconds, and
/// the technical log belongs behind an explicit action rather than on the first screen a user sees.
/// <para>
/// Real progress still drives the status line (<see cref="AndroidHost.Progress"/>), the elapsed time
/// is real, and a failure keeps the message, the log and the retry action. All work stays on the UI
/// thread; runtime initialization reports from the thread pool.
/// </para>
/// </summary>
internal sealed class AndroidStartupView : UserControl
{
    // The mobile palette from the design contract (MptSdkTheme/Mobile.axaml own the shared
    // resources; the launch frame is drawn before a page applies them, so it carries the same values
    // and follows the resolved theme).
    private static readonly Color LightBackground = Color.Parse("#F6F7F9");
    private static readonly Color DarkBackground = Color.Parse("#171B22");
    private static readonly Color LightPrimary = Color.Parse("#1C2025");
    private static readonly Color DarkPrimary = Color.Parse("#EBEDF2");
    private static readonly Color LightSecondary = Color.Parse("#69737F");
    private static readonly Color DarkSecondary = Color.Parse("#A0AABA");
    private static readonly Color LightAccent = Color.Parse("#0965EE");
    private static readonly Color DarkAccent = Color.Parse("#6BA5FF");
    private static readonly Color Failure = Color.Parse("#D13438");

    private readonly TextBlock _wordmark = new() { Text = "MPT", FontSize = 34, FontWeight = FontWeight.SemiBold, LetterSpacing = -1.2 };
    private readonly TextBlock _wordmarkDot = new() { Text = "●", FontSize = 12 };
    private readonly TextBlock _edition = new() { Text = "MY POWER TOOLS · MOBILE", FontSize = 10, LetterSpacing = 1.6 };
    private readonly TextBlock _headline = new() { Text = "正在启动", FontSize = 24, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _stage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly TextBlock _elapsed = new() { FontSize = 12 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false, FontSize = 13 };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _log = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("monospace"),
        FontSize = 11,
        MaxHeight = 260,
        IsVisible = false
    };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false };
    private readonly Button _retryButton = new() { Content = "重试", IsVisible = false };
    private readonly DispatcherTimer _timer;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private Action? _retry;

    internal AndroidStartupView()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateElapsed();
        _timer.Start();

        _retryButton.Click += (_, _) =>
        {
            _retryButton.IsVisible = false;
            _error.IsVisible = false;
            _progress.IsVisible = true;
            _progress.IsIndeterminate = true;
            _actions.IsVisible = false;
            _headline.Text = "正在启动";
            _stage.Text = "正在重新初始化…";
            _retry?.Invoke();
        };

        // The log is a diagnostics affordance, not part of the launch design, so it stays collapsed.
        var logToggle = new Button { Content = "启动日志", HorizontalAlignment = HorizontalAlignment.Left, FontSize = 12 };
        logToggle.Classes.Add("MptMobileIconButton");
        logToggle.Click += (_, _) => ToggleLog();
        _actions.Children.Add(_retryButton);
        _actions.Children.Add(logToggle);

        var mark = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        _wordmarkDot.VerticalAlignment = VerticalAlignment.Bottom;
        _wordmarkDot.Margin = new Thickness(2, 0, 0, 7);
        mark.Children.Add(_wordmark);
        mark.Children.Add(_wordmarkDot);

        var brand = new StackPanel { Spacing = 6, Children = { mark, _edition } };

        var center = new StackPanel
        {
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _headline, _stage, _progress, _elapsed, _error, _log }
        };

        // Row 2 holds the retry action and the diagnostics toggle; both stay hidden on a healthy
        // launch, which is why the design's launch frame has no controls at all.
        var footer = new Grid { RowDefinitions = new RowDefinitions("Auto"), VerticalAlignment = VerticalAlignment.Bottom };
        Grid.SetRow(_actions, 0);
        footer.Children.Add(_actions);

        var panel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Thickness(28, 40, 28, 28)
        };
        Grid.SetRow(brand, 0);
        panel.Children.Add(brand);
        Grid.SetRow(center, 1);
        panel.Children.Add(center);
        Grid.SetRow(footer, 2);
        panel.Children.Add(footer);

        Content = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };

        ApplyPalette();
        if (global::Avalonia.Application.Current is { } application)
        {
            application.ActualThemeVariantChanged += OnThemeChanged;
        }

        AttachedToVisualTree += (_, _) => ApplyPalette();
        DetachedFromVisualTree += (_, _) =>
        {
            _timer.Stop();
            _progress.IsIndeterminate = false;
            AndroidHost.Progress -= Report;
            if (global::Avalonia.Application.Current is { } current)
            {
                current.ActualThemeVariantChanged -= OnThemeChanged;
            }
        };

        Report(AndroidHost.Current);
        AndroidHost.Progress += Report;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyPalette();

    /// <summary>
    /// Keeps the launch frame on the same palette the Shell will use, so a dark-mode launch does not
    /// flash a light screen (and the reverse).
    /// </summary>
    private void ApplyPalette()
    {
        var dark = global::Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        Background = new SolidColorBrush(dark ? DarkBackground : LightBackground);
        _wordmark.Foreground = new SolidColorBrush(dark ? DarkPrimary : LightPrimary);
        _wordmarkDot.Foreground = new SolidColorBrush(dark ? DarkAccent : LightAccent);
        _edition.Foreground = new SolidColorBrush(dark ? DarkSecondary : LightSecondary);
        _headline.Foreground = new SolidColorBrush(dark ? DarkPrimary : LightPrimary);
        _stage.Foreground = new SolidColorBrush(dark ? DarkSecondary : LightSecondary);
        _elapsed.Foreground = new SolidColorBrush(dark ? DarkSecondary : LightSecondary);
        _log.Foreground = new SolidColorBrush(dark ? DarkPrimary : LightPrimary);
    }

    private void ToggleLog()
    {
        _log.IsVisible = !_log.IsVisible;
        if (_log.IsVisible)
        {
            _log.Text = AndroidStartupLog.SnapshotText();
            _log.CaretIndex = _log.Text?.Length ?? 0;
        }
    }

    private void UpdateElapsed()
    {
        var elapsed = DateTimeOffset.UtcNow - _startedAt;
        _elapsed.Text = $"已用时 {elapsed.TotalSeconds:F0} 秒";
    }

    /// <summary>Accepts progress from any thread; the runtime reports from the thread pool.</summary>
    internal void Report(StartupProgress progress)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply(progress);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Apply(progress), DispatcherPriority.Background);
        }
    }

    private void Apply(StartupProgress progress)
    {
        _stage.Text = progress.Message;
        if (_log.IsVisible)
        {
            _log.Text = AndroidStartupLog.SnapshotText();
            _log.CaretIndex = _log.Text?.Length ?? 0;
        }
    }

    /// <summary>Swaps the startup screen for the real Shell once the runtime is ready.</summary>
    internal void ShowShell(Control shell)
    {
        _timer.Stop();
        // Removing the launch content alone leaves its two Fluent progress animations subscribed
        // to the global clock. End the animation while its template is still attached.
        _progress.IsIndeterminate = false;
        AndroidHost.Progress -= Report;
        if (global::Avalonia.Application.Current is { } application)
        {
            application.ActualThemeVariantChanged -= OnThemeChanged;
        }

        Content = shell;
    }

    internal void ShowFailure(Exception error, Action retry)
    {
        AndroidStartupLog.Error("startup-failed", error);
        _retry = retry;
        _timer.Stop();
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _progress.IsVisible = false;
        _headline.Text = "启动没有完成";
        _stage.Text = "可以重试，或先查看启动日志。";
        _error.Foreground = new SolidColorBrush(Failure);
        _error.Text = error.Message + Environment.NewLine + "诊断日志：" + (AndroidStartupLog.Path ?? "(不可用)");
        _error.IsVisible = true;
        _actions.IsVisible = true;
        _retryButton.IsVisible = true;
        _log.Text = AndroidStartupLog.SnapshotText();
        _log.IsVisible = true;
    }
}
