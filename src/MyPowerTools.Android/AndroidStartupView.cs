using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Button = Avalonia.Controls.Button;
using Orientation = Avalonia.Layout.Orientation;
using ProgressBar = Avalonia.Controls.ProgressBar;

namespace MyPowerTools.Android;

/// <summary>
/// The first screen the Android activity shows. It replaces a static "opening" label with the
/// live initialization stage, a bounded startup log and a retry action, so a launch that fails
/// on a phone can be diagnosed without a debugger. All work stays on the UI thread; the runtime
/// initialization that feeds it runs on the thread pool.
/// </summary>
internal sealed class AndroidStartupView : UserControl
{
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#D13438"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#8A8A8E"));
    private static readonly IBrush BorderBrush = new SolidColorBrush(Color.Parse("#55808080"));

    private readonly TextBlock _stage = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16 };
    private readonly TextBlock _elapsed = new() { FontSize = 12, Foreground = MutedBrush };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = ErrorBrush, IsVisible = false };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 4, 0, 4) };
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
    private readonly DispatcherTimer _timer;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private Action? _retry;

    internal AndroidStartupView()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateElapsed();
        _timer.Start();

        var logToggle = new Button { Content = "启动日志", HorizontalAlignment = HorizontalAlignment.Left };
        logToggle.Click += (_, _) =>
        {
            _log.IsVisible = !_log.IsVisible;
            if (_log.IsVisible)
            {
                _log.Text = AndroidStartupLog.SnapshotText();
                _log.CaretIndex = _log.Text?.Length ?? 0;
            }
        };
        var retry = new Button { Content = "重试", IsVisible = false };
        retry.Click += (_, _) =>
        {
            retry.IsVisible = false;
            _error.IsVisible = false;
            _progress.IsIndeterminate = true;
            _actions.IsVisible = false;
            _retry?.Invoke();
        };
        _retryButton = retry;
        _actions.Children.Add(retry);
        _actions.Children.Add(logToggle);

        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(24, 32, 24, 24),
            Children =
            {
                new TextBlock { Text = "MyPowerTools", FontSize = 26, FontWeight = FontWeight.SemiBold },
                _stage,
                _progress,
                _elapsed,
                _error,
                _actions,
                _log
            }
        };
        Content = new ScrollViewer { Content = panel };
        Report(AndroidHost.Current);
        AndroidHost.Progress += Report;
        DetachedFromVisualTree += (_, _) => AndroidHost.Progress -= Report;
    }

    private readonly Button _retryButton;

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
        AndroidHost.Progress -= Report;
        Content = shell;
    }

    internal void ShowFailure(Exception error, Action retry)
    {
        AndroidStartupLog.Error("startup-failed", error);
        _retry = retry;
        _timer.Stop();
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _error.Text = "启动失败：" + error.Message + Environment.NewLine +
            "诊断日志：" + (AndroidStartupLog.Path ?? "(不可用)");
        _error.IsVisible = true;
        _actions.IsVisible = true;
        _retryButton.IsVisible = true;
        _log.Text = AndroidStartupLog.SnapshotText();
        _log.IsVisible = true;
    }
}
