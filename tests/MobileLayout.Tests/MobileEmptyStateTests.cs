using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Grpc.Core;
using MyPowerTools.HostControl;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;

namespace MobileLayout.Tests;

/// <summary>
/// Empty and unavailable states must be real: no example device, no example transfer, no invented
/// success. Every message has to come from the service that actually answered.
/// </summary>
public sealed class MobileEmptyStateTests
{
    [AvaloniaFact]
    public void Devices_page_without_a_device_service_shows_an_empty_state_and_no_example_device()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(new UnavailableMobileDeviceService());
        try
        {
            host.CompleteInitialLoad();
            shell.ViewModel.NavigateRoot(MobilePageKeys.Devices);
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted, "设备 never loaded");
            Pump(window);

            var viewModel = shell.ViewModel.Devices;
            Assert.True(viewModel.IsEmpty);
            Assert.False(viewModel.HasPeers);
            Assert.Equal("还没有连接设备", viewModel.EmptyTitle);
            Assert.Equal(UnavailableMobileDeviceService.NotConnectedNotice, viewModel.Notice);
            Assert.False(viewModel.RelayConfigured);
            Assert.Equal("连接一个网盘", viewModel.RelaySummary);

            var texts = shell.PhoneHost.GetVisualDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? "")
                .ToArray();
            Assert.Contains(texts, text => text.Contains("还没有连接设备", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, text => text.Contains("MacBook", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(texts, text => text.Contains("工作电脑", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, text => string.Equals(text.Trim(), "在线", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, text => text.Contains("上次在线", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public async Task Unavailable_device_service_reports_no_data_and_fails_writes_honestly()
    {
        var service = new UnavailableMobileDeviceService();

        var snapshot = await service.GetSnapshotAsync();
        Assert.Empty(snapshot.Peers);
        Assert.Empty(snapshot.Activities);
        Assert.False(snapshot.Receiving);
        Assert.False(snapshot.RelayConfigured);
        Assert.Equal(UnavailableMobileDeviceService.NotConnectedNotice, snapshot.Notice);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportPairingAsync("MPT-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemovePeerAsync("peer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetPairingCodeAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckPeerAsync("peer"));
    }

    [AvaloniaFact]
    public void Unchecked_peer_is_reported_as_not_checked_instead_of_online()
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
        var (shell, window) = CreateShell(devices);
        try
        {
            host.CompleteInitialLoad();
            shell.ViewModel.NavigateRoot(MobilePageKeys.Devices);
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted && shell.ViewModel.Devices.Peers.Count == 1, "设备 never loaded");

            var peer = shell.ViewModel.Devices.Peers[0];
            Assert.False(peer.IsOnline);
            Assert.True(peer.IsUnknown);
            Assert.Equal("已配对 · 尚未检查", peer.StateLabel);
            Assert.Contains("不会显示在线", peer.StateDetail);

            // The only state change is a real, user-triggered check.
            Assert.Equal(0, devices.CheckCalls);
            peer.CheckCommand.Execute(null);
            MobileShellTests.PumpUntil(window, () => devices.CheckCalls == 1 && peer.IsOnline, "the reachability check never ran");
            Assert.NotNull(peer.Peer.CheckedAt);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Activity_page_without_records_shows_an_empty_state()
    {
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(new FakeMobileDeviceService());
        try
        {
            host.CompleteInitialLoad();
            shell.ViewModel.NavigateRoot(MobilePageKeys.Activity);
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted, "动态 never loaded");
            Pump(window);

            Assert.True(shell.ViewModel.Activity.IsEmpty);
            var texts = shell.PhoneHost.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToArray();
            Assert.Contains(texts, text => text.Contains("还没有动态", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, text => text.Contains("已发送到", StringComparison.Ordinal));
            Assert.DoesNotContain(texts, text => text.Contains("等待领取", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Activity_page_renders_only_real_transfer_records()
    {
        var devices = new FakeMobileDeviceService
        {
            Snapshot = new MobileDeviceSnapshot(
                "测试手机",
                false,
                [],
                true,
                true,
                "我的网盘 · 已连接",
                [
                    new MobileTransferActivity("1", "会议记录.pdf", "已送达", "send", "书房的电脑", 2048, DateTimeOffset.Now),
                    new MobileTransferActivity("2", "素材.zip", "已存入网盘", "send", "书房的电脑", 4096, DateTimeOffset.Now, "等待领取")
                ])
        };
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(devices);
        try
        {
            host.CompleteInitialLoad();
            shell.ViewModel.NavigateRoot(MobilePageKeys.Activity);
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted && shell.ViewModel.Activity.Items.Count >= 2, "动态 never loaded");

            var items = shell.ViewModel.Activity.Items;
            Assert.Contains(items, item => item.Title == "会议记录.pdf");
            Assert.Contains(items, item => item.Title == "素材.zip" && item.Detail.Contains("等待领取", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tool_library_reports_the_real_catalog_failure_instead_of_an_empty_success()
    {
        // No EmbeddedInvoker: every HostControl call fails, exactly like a Runner that is not up.
        var shell = CreateUnhostedShell(out var window);
        try
        {
            shell.ViewModel.NavigateRoot(MobilePageKeys.Tools);
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted, "工具 never settled");
            Pump(window);

            var tools = shell.ViewModel.Tools;
            Assert.True(tools.HasError, "a failed catalog load must be reported");
            Assert.NotEmpty(tools.ErrorMessage);
            Assert.False(tools.IsEmpty, "a failure must not read as an empty catalog");
            var texts = shell.PhoneHost.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToArray();
            Assert.Contains(texts, text => text == tools.ErrorMessage);
        }
        finally
        {
            window.Close();
        }
    }

    private static MobileShellView CreateUnhostedShell(out Window window)
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
        var shell = new MobileShellView(workspace, chrome, search, MobileTestEnvironment.CreateServices());
        window = new Window { Width = 360, Height = 800, Content = shell };
        window.Show();
        for (var pass = 0; pass < 3; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return shell;
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static (MobileShellView Shell, Window Window) CreateShell(
        MyPowerTools.Shell.Avalonia.Services.Mobile.IMobileDeviceService devices)
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
        var window = new Window { Width = 360, Height = 800, Content = shell };
        window.Show();
        for (var pass = 0; pass < 3; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return (shell, window);
    }
}
