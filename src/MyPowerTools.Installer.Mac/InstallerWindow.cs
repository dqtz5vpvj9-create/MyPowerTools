using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPowerTools.Packaging.Ota;

namespace MyPowerTools.Installer.Mac;

/// <summary>
/// The whole installer UI: one status line, one progress bar, one primary button.
/// Every engine call runs off the UI thread; every control update goes through
/// <see cref="Dispatcher.UIThread"/>.
/// </summary>
internal sealed class InstallerWindow : Window
{
    private enum PrimaryAction
    {
        None,
        Install,
        Open,
        RetryCheck,
        RetryApply
    }

    private readonly InstallerArguments _arguments;
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly TextBlock _status;
    private readonly TextBlock _detail;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _quit;
    private MacNativeInstaller? _installer;
    private MacInstallOptions? _options;
    private PrimaryAction _action;
    private bool _busy;
    private MacInstallProgress? _pendingProgress;
    private int _progressPostScheduled;

    public InstallerWindow(InstallerArguments arguments, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _arguments = arguments;
        _lifetime = lifetime;

        Title = "MyPowerTools 安装器";
        Width = 460;
        Height = 320;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var title = new TextBlock
        {
            Text = "MyPowerTools",
            FontSize = 26,
            FontWeight = FontWeight.SemiBold
        };
        var subtitle = new TextBlock
        {
            Text = "安装或更新 MyPowerTools，全程无需使用终端。",
            FontSize = 13,
            Opacity = 0.7,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        _status = new TextBlock
        {
            Text = "正在检查最新版本…",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 40,
            Margin = new Thickness(0, 28, 0, 0)
        };
        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            MinHeight = 6,
            IsIndeterminate = true,
            Margin = new Thickness(0, 10, 0, 0)
        };
        _detail = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(0, 6, 0, 0),
            MinHeight = 16
        };

        _primary = new Button
        {
            Content = "请稍候",
            MinWidth = 150,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = false
        };
        _primary.Classes.Add("accent");
        _primary.Click += (_, _) => OnPrimaryClicked();

        _quit = new Button
        {
            Content = "退出",
            FontSize = 12,
            Padding = new Thickness(12, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        _quit.Click += (_, _) => Close();

        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_quit, Dock.Left);
        DockPanel.SetDock(_primary, Dock.Right);
        buttons.Children.Add(_quit);
        buttons.Children.Add(_primary);

        var body = new StackPanel();
        body.Children.Add(title);
        body.Children.Add(subtitle);
        body.Children.Add(_status);
        body.Children.Add(_progress);
        body.Children.Add(_detail);

        var root = new DockPanel { Margin = new Thickness(32, 28, 32, 24) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;

        Closing += (_, e) =>
        {
            // Closing mid-install could leave the swap half done; the engine rolls back on
            // failure, but it cannot if the process disappears under it.
            if (_busy)
            {
                e.Cancel = true;
                _detail.Text = "正在安装，请等待完成后再退出。";
            }
        };
        _lifetime.ShutdownRequested += (_, e) =>
        {
            if (_busy)
                e.Cancel = true;
        };

        Opened += (_, _) => Start();
    }

    private void Start()
    {
        if (!OperatingSystem.IsMacOS())
        {
            SetState("此安装器仅适用于 macOS，请在 Mac 上打开。", PrimaryAction.None, null);
            _progress.IsVisible = false;
            return;
        }

        try
        {
            var options = MacInstallOptions.CreateDefault() with { Relaunch = true };
            if (_arguments.Channel is { } channel)
                options = options with { Channel = channel };
            var appPath = _arguments.AppBundlePath ??
                InstallerArguments.FindEnclosingProductBundle(AppContext.BaseDirectory);
            if (appPath is not null)
                options = options with { AppBundlePath = appPath };
            if (_arguments.FeedUrl is { } feed)
                options = options with { FeedUrl = feed };
            _options = options;
            _installer = new MacNativeInstaller(options, OnProgress);
        }
        catch (Exception exception)
        {
            SetState("无法启动安装器：" + exception.Message, PrimaryAction.None, null);
            _progress.IsVisible = false;
            return;
        }

        _ = CheckAsync(autoApply: _arguments.AutoUpdate);
    }

    private async Task CheckAsync(bool autoApply)
    {
        var installer = _installer!;
        SetBusy("正在检查最新版本…");
        JsonObject result;
        try
        {
            result = await Task.Run(() => installer.CheckAsync());
        }
        catch (Exception exception)
        {
            OnUi(() => SetState(
                "检查更新失败：" + exception.Message + "\n请确认网络连接后重试。",
                PrimaryAction.RetryCheck,
                "重试"));
            return;
        }

        var installed = GetBool(result, "installed");
        var available = GetBool(result, "available");
        var current = GetString(result, "currentVersion");
        var latest = GetString(result, "latestVersion");
        var reason = GetString(result, "reason");

        OnUi(() =>
        {
            if (!installed)
            {
                SetState(
                    latest is null ? "这台 Mac 还没有安装 MyPowerTools。" : $"这台 Mac 还没有安装 MyPowerTools。最新版本 {latest}。",
                    PrimaryAction.Install,
                    "安装 MyPowerTools");
            }
            else if (available)
            {
                var from = current is null ? string.Empty : $"当前版本 {current}，";
                SetState($"{from}有新版本 {latest} 可以更新。", PrimaryAction.Install, $"更新到 {latest}");
                if (autoApply)
                    _ = ApplyAsync();
            }
            else if (string.Equals(reason, "downgrade-blocked", StringComparison.Ordinal))
            {
                SetState($"已安装 {current}，比线上版本 {latest} 更新，无需更新。", PrimaryAction.Open, "打开 MyPowerTools");
            }
            else
            {
                SetState($"已是最新版本 {current ?? latest}", PrimaryAction.Open, "打开 MyPowerTools");
            }
        });
    }

    private async Task ApplyAsync()
    {
        var installer = _installer!;
        SetBusy("正在准备…");
        JsonObject result;
        try
        {
            result = await Task.Run(() => installer.ApplyAsync());
        }
        catch (Exception exception)
        {
            OnUi(() => SetState("安装失败：" + exception.Message, PrimaryAction.RetryApply, "重试"));
            return;
        }

        if (!GetBool(result, "success"))
        {
            var error = GetString(result, "error") ?? "未知错误。";
            OnUi(() => SetState("安装失败：" + error + "\n原来的版本没有受到影响。", PrimaryAction.RetryApply, "重试"));
            return;
        }

        if (GetBool(result, "upToDate"))
        {
            var version = GetString(result, "toVersion") ?? GetString(result, "fromVersion");
            OnUi(() => SetState($"已是最新版本 {version}", PrimaryAction.Open, "打开 MyPowerTools"));
            return;
        }

        OnUi(() =>
        {
            _busy = false;
            _progress.IsIndeterminate = false;
            _progress.Value = 100;
            _progress.IsVisible = true;
            _status.Text = "完成，正在打开 MyPowerTools…";
            _detail.Text = GetString(result, "toVersion") is { } to ? $"已安装版本 {to}" : string.Empty;
            _primary.IsEnabled = false;
            _primary.Content = "完成";
            _quit.IsEnabled = false;
        });
        await Task.Delay(TimeSpan.FromSeconds(2));
        OnUi(() => _lifetime.Shutdown());
    }

    private void OnPrimaryClicked()
    {
        switch (_action)
        {
            case PrimaryAction.Install:
            case PrimaryAction.RetryApply:
                _ = ApplyAsync();
                break;
            case PrimaryAction.RetryCheck:
                _ = CheckAsync(autoApply: _arguments.AutoUpdate);
                break;
            case PrimaryAction.Open:
                OpenProduct();
                break;
        }
    }

    private void OpenProduct()
    {
        var appPath = _options?.AppBundlePath;
        if (string.IsNullOrWhiteSpace(appPath) || !Directory.Exists(appPath))
        {
            SetState("找不到已安装的 MyPowerTools，请重新检查。", PrimaryAction.RetryCheck, "重试");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            startInfo.ArgumentList.Add(appPath);
            using var _ = Process.Start(startInfo);
            _lifetime.Shutdown();
        }
        catch (Exception exception)
        {
            SetState("无法打开 MyPowerTools：" + exception.Message, PrimaryAction.Open, "打开 MyPowerTools");
        }
    }

    private void OnProgress(MacInstallProgress progress)
    {
        // Download progress can arrive many times per frame; keep only the newest report and
        // schedule at most one UI update at a time.
        Volatile.Write(ref _pendingProgress, progress);
        if (Interlocked.Exchange(ref _progressPostScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _progressPostScheduled, 0);
                var latest = Volatile.Read(ref _pendingProgress);
                if (latest is not null && _busy)
                    ShowProgress(latest);
            });
        }
    }

    private void ShowProgress(MacInstallProgress progress)
    {
        if (!string.IsNullOrWhiteSpace(progress.Message))
            _status.Text = progress.Message;

        int? percent = progress.Percent;
        if (percent is null && progress.ReceivedBytes is { } received && progress.TotalBytes is > 0 and var total)
            percent = (int)Math.Clamp(received * 100 / total, 0, 100);

        var downloading = string.Equals(progress.Stage, "download", StringComparison.Ordinal);
        if (downloading && percent is { } value)
        {
            _progress.IsIndeterminate = false;
            _progress.Value = Math.Clamp(value, 0, 100);
        }
        else
        {
            _progress.IsIndeterminate = true;
        }

        _detail.Text = downloading && progress.ReceivedBytes is { } bytes
            ? progress.TotalBytes is > 0 and var size
                ? $"{FormatMegabytes(bytes)} / {FormatMegabytes(size)}"
                : FormatMegabytes(bytes)
            : string.Empty;
    }

    private void SetBusy(string status)
    {
        OnUi(() =>
        {
            _busy = true;
            _action = PrimaryAction.None;
            _status.Text = status;
            _detail.Text = string.Empty;
            _progress.IsVisible = true;
            _progress.IsIndeterminate = true;
            _primary.Content = "请稍候";
            _primary.IsEnabled = false;
            _quit.IsEnabled = false;
        });
    }

    private void SetState(string status, PrimaryAction action, string? primaryText)
    {
        _busy = false;
        _action = action;
        _status.Text = status;
        _detail.Text = string.Empty;
        // A static bar at rest: no animation runs while the window just waits for a click.
        _progress.IsIndeterminate = false;
        _progress.Value = 0;
        _progress.IsVisible = false;
        _primary.IsVisible = action != PrimaryAction.None;
        _primary.IsEnabled = action != PrimaryAction.None;
        _primary.Content = primaryText ?? string.Empty;
        _quit.IsEnabled = true;
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private static string FormatMegabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static bool GetBool(JsonObject json, string name) =>
        json.TryGetPropertyValue(name, out var node) && node is JsonValue value &&
        value.TryGetValue<bool>(out var flag) && flag;

    private static string? GetString(JsonObject json, string name) =>
        json.TryGetPropertyValue(name, out var node) && node is JsonValue value &&
        value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;
}
