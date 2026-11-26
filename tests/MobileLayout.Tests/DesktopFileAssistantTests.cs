using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;

namespace MobileLayout.Tests;

public sealed class DesktopFileAssistantTests
{
    [AvaloniaTheory]
    [InlineData(1180, 780)]
    [InlineData(1600, 900)]
    public void Actual_file_assistant_keeps_composer_and_dialog_in_desktop_viewport(int width, int height)
    {
        var app = Application.Current!;
        var mobileTheme = app.Styles.OfType<StyleInclude>().Single(style =>
            style.Source?.AbsolutePath.Contains("MptMobileTheme.axaml") == true);
        var themeIndex = app.Styles.IndexOf(mobileTheme);
        app.Styles.Remove(mobileTheme);
        Window? window = null;
        try
        {
            var chrome = new ShellChromeView
            {
                DataContext = new ShellChromeViewModel(ShellWorkspaceController.PageLabels,
                    _ => Task.CompletedTask, () => Task.CompletedTask, () => Task.CompletedTask,
                    () => Task.CompletedTask, () => Task.CompletedTask,
                    runtimeModeLabel: "DEV", runtimeIdentityText: "Desktop integration test")
            };
            window = new Window { Width = width, Height = height, Content = chrome };
            window.Show();
            var host = chrome.FindControl<ContentControl>("ContentHost")!;
            var tool = new ExternalSdkToolView
            {
                DataContext = new ExternalSdkToolViewModel("file-transfer", "文件助手", "",
                    "dotnet-surface", "文件助手", null, false, [], null,
                    _ => Task.FromResult(""), () => Task.CompletedTask, () => Task.CompletedTask)
            };
            host.Content = tool;
            var context = new MptAvaloniaSurfaceContext("file-transfer", "main", "", "light",
                (command, _, _) => Task.FromResult(new CommandExecutionResult("layout", command,
                    "succeeded", true,
                    """{"identity":{"deviceId":"test-desktop","deviceName":"测试电脑","linked":false},"items":[],"devices":[],"settings":{},"history":[]}""")),
                (_, _, _) => Task.CompletedTask, null!, _ => { });
            var surface = new FileTransfer.Surface.TransferView(context);
            tool.SetManagedSurface(surface);
            Pump(window);

            var scrollHost = chrome.FindControl<ScrollViewer>("ContentScrollHost")!;
            Assert.Equal(ScrollBarVisibility.Disabled, scrollHost.VerticalScrollBarVisibility);
            Assert.True(surface.Bounds.Height > 400 && surface.Bounds.Height <= scrollHost.Viewport.Height + 1);
            var send = surface.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible && button.Content as string == "发送");
            var input = surface.GetVisualDescendants().OfType<TextBox>().Single(box =>
                box.IsEffectivelyVisible && box.PlaceholderText == "写点文字，或添加文件…");
            AssertInside(send, scrollHost);
            AssertInside(input, scrollHost);
            Save(window, $"desktop-file-assistant-{width}-conversation.png");

            var connect = surface.GetVisualDescendants().OfType<Button>().Single(button =>
                button.IsEffectivelyVisible && button.Content as string == "连接我的设备");
            connect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            var dialog = surface.GetVisualDescendants().OfType<Border>().Single(border =>
                border.IsEffectivelyVisible && border.Classes.Contains("MptMobileSheet"));
            Assert.True(dialog.Bounds.Width > 300 && dialog.Bounds.Width <= 521);
            AssertInside(dialog, scrollHost);
            var origin = dialog.TranslatePoint(default, scrollHost)!.Value;
            Assert.InRange(Math.Abs(origin.Y + dialog.Bounds.Height / 2 - scrollHost.Viewport.Height / 2), 0, 2);
            Save(window, $"desktop-file-assistant-{width}-dialog.png");
        }
        finally
        {
            window?.Close();
            app.Styles.Insert(themeIndex, mobileTheme);
        }
    }

    private static void AssertInside(Control control, ScrollViewer viewport)
    {
        var origin = control.TranslatePoint(default, viewport)!.Value;
        Assert.True(origin.Y >= -1 && origin.Y + control.Bounds.Height <= viewport.Viewport.Height + 1,
            $"{control.GetType().Name} extends beyond desktop viewport: {origin.Y} + {control.Bounds.Height} > {viewport.Viewport.Height}");
        Assert.True(origin.X >= -1 && origin.X + control.Bounds.Width <= viewport.Viewport.Width + 1);
    }

    private static void Pump(Window window)
    {
        for (var i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    private static void Save(Window window, string name)
    {
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "MyPowerTools.slnx"))) root = root.Parent;
        var directory = Path.Combine(root.FullName, "artifacts", ".tmp-android-verify", "root-desktop-integration");
        Directory.CreateDirectory(directory);
        frame.Save(Path.Combine(directory, name));
    }
}
