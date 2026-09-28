using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Styling;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;

namespace MobileLayout.Tests;

/// <summary>
/// Renders every phone page at the two supported phone widths in both themes and writes them under
/// the registered verification area, so the phone presentation can be reviewed against the approved
/// prototype without a device. A page that renders nothing fails the test instead of passing
/// silently in a layout-only assertion.
/// </summary>
public sealed class MobileScreenshotTests
{
    private static readonly (string PageKey, string FileName)[] Pages =
    [
        (MobilePageKeys.Home, "home"),
        (MobilePageKeys.Tools, "tools"),
        (MobilePageKeys.Devices, "devices"),
        (MobilePageKeys.Activity, "activity"),
        (MobilePageKeys.Settings, "settings")
    ];

    [AvaloniaFact]
    public void Phone_pages_render_reference_frames_at_both_widths_and_themes()
    {
        var control = new FakeMobileControlDeviceService
        {
            Snapshot = new MobileControlDeviceSnapshot(
            [
                FakeMobileControlDeviceService.Device("grant-1", "书房的电脑", "reachable", "今天 09:12 应答"),
                FakeMobileControlDeviceService.Device("grant-2", "笔记本", "imported")
            ])
        };
        var devices = new FakeMobileDeviceService
        {
            Snapshot = new MobileDeviceSnapshot(
                "Chris 的手机",
                true,
                [new MobilePeerInfo("peer-1", "工作手机", "100.64.0.9", MobilePeerConnectionState.Online, DateTimeOffset.Now)],
                true,
                false,
                "我的网盘 · 已连接",
                [
                    new MobileTransferActivity("1", "设计稿.pdf", "已送达", "send", "书房的电脑", 2048, DateTimeOffset.Now),
                    new MobileTransferActivity("2", "素材.zip", "已存入网盘 · 等待领取", "send", "书房的电脑", 4096, DateTimeOffset.Now)
                ])
        };
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var services = MobileTestEnvironment.CreateServices(devices, control);
        var directory = Path.Combine(FindRepositoryRoot(), "artifacts", ".tmp-android-verify", "m2", "screens");
        Directory.CreateDirectory(directory);

        foreach (var width in new[] { 320, 390 })
        {
            foreach (var theme in new[] { ShellAppearanceService.LightTheme, ShellAppearanceService.DarkTheme })
            {
                var chrome = CreateChrome();
                var search = new MptSearchBox();
                var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
                var workspace = new ShellWorkspaceController(chrome, search, pageHost, new ContentControl(), new ContentControl(), new ContentControl());
                var shell = new MobileShellView(workspace, chrome, search, services);
                var window = new Window { Width = width, Height = 844, Content = shell };
                try
                {
                    window.Show();
                    host.CompleteInitialLoad();
                    MobileShellTests.PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");
                    services.Appearance.SetThemeAsync(theme).GetAwaiter().GetResult();
                    Assert.Equal(
                        theme == ShellAppearanceService.DarkTheme ? ThemeVariant.Dark : ThemeVariant.Light,
                        Application.Current!.RequestedThemeVariant);

                    MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted, "home never loaded");
                    Pump(window);

                    // One real favorite so 常用 shows the tile grid instead of only its empty hint.
                    var favorite = shell.ViewModel.Tools.Find("remote-commands");
                    Assert.NotNull(favorite);
                    if (!favorite!.IsFavorite)
                    {
                        // Idempotent: the same store is reused across the size/theme matrix, so every
                        // frame shows the same 常用 tiles instead of alternating on the second pass.
                        favorite.ToggleFavoriteCommand.Execute(null);
                    }

                    MobileShellTests.PumpUntil(window, () => favorite.IsFavorite, "the favorite was never written");

                    // Leave 常用 so the capture loop re-enters it and rebuilds the tiles from the store.
                    shell.ViewModel.NavigateRoot(MobilePageKeys.Tools);
                    Pump(window);

                    foreach (var (pageKey, fileName) in Pages)
                    {
                        if (MobilePageKeys.Tabs.Contains(pageKey, StringComparer.Ordinal))
                        {
                            shell.ViewModel.NavigateRoot(pageKey);
                        }
                        else
                        {
                            shell.ViewModel.Navigate(pageKey, null);
                        }

                        MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted, $"{pageKey} never loaded");
                        Pump(window);
                        var path = Path.Combine(directory, $"{width}-{theme}-{fileName}.png");
                        var frame = window.CaptureRenderedFrame();
                        Assert.NotNull(frame);
                        using (var stream = File.Create(path))
                        {
                            frame!.Save(stream);
                        }

                        Assert.True(new FileInfo(path).Length > 1024, $"{width}-{theme}-{fileName}.png rendered an empty frame");
                    }

                    // The bottom sheet is part of the phone experience: one frame per size/theme.
                    shell.ViewModel.OpenSheet(MobileSheetKeys.Relay, null);
                    Pump(window);
                    var sheetPath = Path.Combine(directory, $"{width}-{theme}-sheet-relay.png");
                    var sheetFrame = window.CaptureRenderedFrame();
                    Assert.NotNull(sheetFrame);
                    using (var stream = File.Create(sheetPath))
                    {
                        sheetFrame!.Save(stream);
                    }
                }
                finally
                {
                    window.Close();
                }
            }
        }
    }

    private static ShellChromeViewModel CreateChrome() => new(
        ShellWorkspaceController.PageLabels,
        _ => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        () => Task.CompletedTask,
        runtimeModeLabel: "ANDROID",
        runtimeIdentityText: "MPT Android");

    /// <summary>Walks up from the test output until the repository root marker is found.</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
