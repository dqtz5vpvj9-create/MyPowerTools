using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;

namespace MobileLayout.Tests;

/// <summary>
/// Phone navigation: the avatar opens 设置, a tool row pushes its detail page, a sheet takes back
/// first and restores focus, and every phone page fits the four supported widths.
/// </summary>
public sealed class MobileNavigationTests
{
    [AvaloniaFact]
    public void Local_pairing_code_renders_a_qr_without_network_state_and_clears_on_close()
    {
        var devices = new FakeMobileDeviceService
        {
            Snapshot = new MobileDeviceSnapshot("本机", false, [], false, false, null, [], LocalAddress: null)
        };
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(390, devices);
        try
        {
            host.CompleteInitialLoad();
            MobileShellTests.PumpUntil(window, () => shell.Ready.IsCompleted && shell.PageLoad.IsCompleted, "home never loaded");
            shell.ShowSheetAsync(MobileSheetKeys.LocalPairingCode).GetAwaiter().GetResult();
            Pump(window);
            var qr = Assert.Single(shell.GetVisualDescendants().OfType<MyPowerTools.AvaloniaSdk.Controls.MptQrCode>());
            Assert.Equal(devices.PairingCode, qr.Value);
            Assert.True(qr.IsVisible);
            Assert.True(qr.Bounds.Width >= 240);
            var copy = Assert.Single(shell.GetVisualDescendants().OfType<Button>(),
                button => AutomationProperties.GetName(button) == "复制连接码");
            Assert.True(copy.IsEnabled);
            RunBack(window, shell);
            Assert.Null(qr.Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Avatar_opens_settings_and_back_returns_to_常用()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(360);
        try
        {
            host.CompleteInitialLoad();
            MobileShellTests.PumpUntil(window, () => shell.Ready.IsCompleted && shell.PageLoad.IsCompleted, "home never loaded");

            var avatar = shell.PhoneHost.GetVisualDescendants().OfType<Button>()
                .First(button => button.Classes.Contains("MptMobileAvatar"));
            Assert.Equal("打开设置", AutomationProperties.GetName(avatar));
            avatar.Command!.Execute(null);
            MobileShellTests.PumpUntil(window, () => shell.CurrentPhonePageKey == MobilePageKeys.Settings, "设置 never opened");
            Assert.Equal(1, shell.ViewModel.BackStackDepth);

            // 设置 is not a tab: no tab stays highlighted, and back returns to 常用.
            Assert.Equal("", shell.ViewModel.SelectedTabKey);
            RunBack(window, shell);
            Assert.Equal(MobilePageKeys.Home, shell.CurrentPhonePageKey);
            Assert.Equal(MobilePageKeys.Home, shell.ViewModel.SelectedTabKey);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tool_row_pushes_the_detail_page_and_back_returns_to_the_library()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(360);
        try
        {
            host.CompleteInitialLoad();
            shell.ViewModel.NavigateRoot(MobilePageKeys.Tools);
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted && shell.ViewModel.Tools.Groups.Count > 0, "工具 library never loaded");
            Pump(window);

            var row = FindRow(shell, "file-transfer");
            Assert.NotNull(row);
            row!.Command!.Execute(null);
            MobileShellTests.PumpUntil(window, () => shell.CurrentPhonePageKey == MobilePageKeys.ToolDetail, "tool detail never opened");
            Assert.True(shell.ViewModel.ToolDetail is not null,
                $"diag rows={shell.ViewModel.Tools.VisibleCount} all={shell.ViewModel.Tools.AllCount} lib={shell.ViewModel.Data.Library?.Entries.Count} err={shell.ViewModel.Tools.ErrorMessage}");
            Assert.Equal("file-transfer", shell.ViewModel.ToolDetail!.ToolId);

            RunBack(window, shell);
            Assert.Equal(MobilePageKeys.Tools, shell.CurrentPhonePageKey);
            Assert.Equal(MobilePageKeys.Tools, shell.ViewModel.SelectedTabKey);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Sheet_takes_back_first_and_restores_focus()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(360);
        try
        {
            host.CompleteInitialLoad();
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted, "home never loaded");

            var addDevice = shell.PhoneHost.GetVisualDescendants().OfType<Button>()
                .First(button => AutomationProperties.GetName(button) == "添加设备");
            addDevice.Focus();
            MobileShellTests.PumpUntil(window, () => addDevice.IsFocused, "the add-device chip never took focus");
            Assert.Same(
                addDevice,
                TopLevel.GetTopLevel(addDevice)?.FocusManager?.GetFocusedElement());
            addDevice.Command!.Execute(null);
            MobileShellTests.PumpUntil(window, () => shell.IsSheetOpen, "the pairing sheet never opened");
            Assert.Equal(MobileSheetKeys.PairDevice, shell.ViewModel.SheetKey);

            // Back closes the sheet instead of leaving the page.
            RunBack(window, shell);
            Assert.False(shell.IsSheetOpen);
            Assert.Equal(MobilePageKeys.Home, shell.CurrentPhonePageKey);
            Pump(window);
            MobileShellTests.PumpUntil(
                window,
                () => addDevice.IsFocused,
                $"focus was not restored ({shell.FocusDiagnostics}, current={TopLevel.GetTopLevel(shell)?.FocusManager?.GetFocusedElement()?.GetType().Name ?? "null"})");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Pairing_sheet_reports_the_real_device_service_failure()
    {
        var devices = new FakeMobileDeviceService
        {
            ImportFailure = new InvalidOperationException("设备服务尚未接入，无法完成该操作。")
        };
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(360, devices);
        try
        {
            host.CompleteInitialLoad();
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted, "home never loaded");
            shell.ViewModel.OpenSheet(MobileSheetKeys.PairDevice, null);
            Pump(window);

            var field = shell.GetVisualDescendants().OfType<TextBox>().First(box => box.Classes.Contains("MptMobileField"));
            field.Text = "MPT-PAIR-4F2A-9C31";
            var connect = shell.GetVisualDescendants().OfType<Button>().First(button => AutomationProperties.GetName(button) == "连接设备");
            connect.Command!.Execute(null);
            MobileShellTests.PumpUntil(
                window,
                () => shell.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "设备服务尚未接入，无法完成该操作。"),
                "the real pairing failure was never shown");
            // The sheet stays open so the code can be corrected, and nothing claims success.
            Assert.True(shell.IsSheetOpen, "a failed pairing must keep the sheet open for correction");
            Assert.Empty(devices.ImportedCodes);
            Assert.DoesNotContain("确认这次配对", shell.LastToastMessage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Device_detail_page_opens_from_the_device_tab()
    {
        var devices = new FakeMobileDeviceService
        {
            Snapshot = new MobileDeviceSnapshot(
                "测试手机",
                false,
                [new MobilePeerInfo("peer-1", "书房的电脑", "100.64.0.2", MobilePeerConnectionState.Unknown)],
                false,
                false,
                null,
                [])
        };
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(360, devices);
        try
        {
            host.CompleteInitialLoad();
            shell.ViewModel.NavigateRoot(MobilePageKeys.Devices);
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted && shell.ViewModel.Devices.Peers.Count == 1, "设备 tab never loaded");

            var peer = shell.ViewModel.Devices.Peers[0];
            Assert.Equal("已配对", peer.StateLabel);
            peer.OpenCommand.Execute(null);
            MobileShellTests.PumpUntil(window, () => shell.CurrentPhonePageKey == MobilePageKeys.DeviceDetail, "device detail never opened");
            Assert.Equal("书房的电脑", shell.ViewModel.DeviceDetail!.Name);

            RunBack(window, shell);
            Assert.Equal(MobilePageKeys.Devices, shell.CurrentPhonePageKey);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(360)]
    [InlineData(390)]
    [InlineData(768)]
    public void Every_phone_page_fits_the_viewport(int width)
    {
        var devices = new FakeMobileDeviceService
        {
            Snapshot = new MobileDeviceSnapshot(
                "测试手机",
                true,
                [new MobilePeerInfo("peer-1", "书房的电脑", "100.64.0.2", MobilePeerConnectionState.Offline, DateTimeOffset.Now)],
                true,
                true,
                "已连接",
                [new MobileTransferActivity("a1", "设计稿.pdf", "已接收", "receive", "书房的电脑", 1024, DateTimeOffset.Now)])
        };
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(width, devices);
        try
        {
            host.CompleteInitialLoad();
            MobileShellTests.PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");

            foreach (var page in MobilePageKeys.Tabs.Append(MobilePageKeys.Settings))
            {
                shell.ViewModel.NavigateRoot(MobilePageKeys.Tabs.Contains(page) ? page : MobilePageKeys.Home);
                if (page == MobilePageKeys.Settings)
                {
                    shell.ViewModel.Navigate(MobilePageKeys.Settings, null);
                }

                MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted, $"{page} never loaded");
                Pump(window);
                AssertFits(shell, width, page);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Sheet_stays_inside_a_narrow_viewport()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(320);
        try
        {
            host.CompleteInitialLoad();
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted, "home never loaded");
            shell.ViewModel.OpenSheet(MobileSheetKeys.Appearance, null);
            Pump(window);

            var sheet = shell.GetVisualDescendants().OfType<Border>().First(border => border.Classes.Contains("MptMobileSheet"));
            Assert.True(sheet.Bounds.Width <= 320 + 1, $"sheet is {sheet.Bounds.Width:0.0} wide at 320");
            Assert.True(sheet.Bounds.Height <= 800 + 1, $"sheet is {sheet.Bounds.Height:0.0} tall at 320");

            // The sheet content scrolls rather than clipping.
            var scroller = sheet.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.NotNull(scroller);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertFits(MobileShellView shell, int width, string page)
    {
        foreach (var control in shell.PhoneHost.GetVisualDescendants().OfType<Control>())
        {
            if (control.Bounds.Width <= 0 || HasScrollableAncestor(control, shell.PhoneHost) || IsAdorner(control))
            {
                continue;
            }

            var origin = control.TranslatePoint(default, shell);
            Assert.NotNull(origin);
            Assert.True(origin!.Value.X >= -1 && origin.Value.X + control.Bounds.Width <= width + 1,
                $"{page}: {control.GetType().Name} overflows the {width}px viewport (x={origin.Value.X:0.0}, width={control.Bounds.Width:0.0})");
        }
    }

    /// <summary>Scrollbars and their templates live at the edge by design; they are not page overflow.</summary>
    private static bool IsAdorner(Control control)
    {
        for (Visual? current = control; current is not null; current = current.GetVisualParent())
        {
            for (var type = current.GetType(); type is not null; type = type.BaseType)
            {
                if (type.Name.Contains("ScrollBar", StringComparison.Ordinal) ||
                    type.Name.Contains("Viewbox", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasScrollableAncestor(Control control, Control boundary)
    {
        for (Visual? current = control; current is not null && !ReferenceEquals(current, boundary); current = current.GetVisualParent())
        {
            if (current is ScrollViewer { HorizontalScrollBarVisibility: not global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled })
            {
                return true;
            }
        }

        return false;
    }

    private static Button? FindRow(MobileShellView shell, string toolId) =>
        shell.PhoneHost.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button =>
                button.Classes.Contains("MptMobileToolRow") &&
                button.DataContext is MobileToolItemViewModel item &&
                string.Equals(item.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    private static (MobileShellView Shell, Window Window) CreateShell(int width, FakeMobileDeviceService? devices = null)
    {
        var chrome = new ShellChromeViewModel(
            ShellWorkspaceController.PageLabels,
            _ => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            runtimeModeLabel: "ANDROID",
            runtimeIdentityText: "MPT Android");
        var search = new MptSearchBox();
        var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var workspace = new ShellWorkspaceController(chrome, search, pageHost, new ContentControl(), new ContentControl(), new ContentControl());
        var shell = new MobileShellView(workspace, chrome, search, MobileTestEnvironment.CreateServices(devices));
        var window = new Window { Width = width, Height = 800, Content = shell };
        window.Show();
        Pump(window);
        return (shell, window);
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void RunBack(Window window, MobileShellView shell)
    {
        var task = shell.HandleBackAsync();
        MobileShellTests.PumpUntil(window, () => task.IsCompleted, "back never settled");
        task.GetAwaiter().GetResult();
    }
}
