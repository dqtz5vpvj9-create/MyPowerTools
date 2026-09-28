using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.VisualTree;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;

namespace MobileLayout.Tests;

/// <summary>
/// M2's integration seams: the public native-service forward the Android entry point calls, Back
/// delegation to the loaded surface, and the computer-control device ids that come from the control
/// module (a desktop grant id) instead of file pairing.
/// </summary>
public sealed class MobileControlIntegrationTests
{
    [AvaloniaFact]
    public void Native_surface_services_reach_the_shared_workspace_and_are_used_for_scans()
    {
        var shell = new MobileShellView();
        var window = new Window { Width = 390, Height = 844, Content = shell };
        try
        {
            window.Show();
            var workspace = (ShellWorkspaceController)typeof(MobileShellView)
                .GetField("_workspace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(shell)!;

            var scans = 0;
            shell.SetNativeSurfaceServices(
                _ => { scans++; return Task.FromResult<string?>("MPT-PAIR-1234"); },
                (_, _) => Task.FromResult(true));

            var field = typeof(ShellWorkspaceController)
                .GetField("_scanConnectionCodeAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            var callback = Assert.IsType<Func<CancellationToken, Task<string?>>>(field!.GetValue(workspace));
            Assert.Equal("MPT-PAIR-1234", callback(CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, scans);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Loaded_surface_consumes_back_before_the_tool_page_closes()
    {
        using var host = new TestToolHost(TestToolHost.PhoneTool("file-transfer", "文件互传"));
        var chrome = CreateChrome();
        var (shell, window) = CreateShell(chrome);
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            MobileShellTests.PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");

            var surface = new BackAwareSurface();
            var activation = shell.ActivateAsync(new ToolActivationRequest("file-transfer", "main", "file:///share/a.txt"));
            MobileShellTests.PumpUntil(window, () => shell.PageHost.Content is ExternalSdkToolView, "the tool page never opened");
            ((ExternalSdkToolView)shell.PageHost.Content!).SetManagedSurface(surface);
            // The activation finishes on the UI thread it was started from. Blocking that thread on the
            // task here deadlocks the test, so pump until it completes before reading its result.
            MobileShellTests.PumpUntil(window, () => activation.IsCompleted, "the activation never completed");
            activation.GetAwaiter().GetResult();
            Assert.True(shell.ViewModel.IsToolSurfaceOpen);

            // The surface's own sheet consumes Back first; the tool page stays open.
            surface.ConsumeBack = true;
            var handled = shell.HandleBackAsync();
            MobileShellTests.PumpUntil(window, () => handled.IsCompleted, "back never settled");
            Assert.True(handled.GetAwaiter().GetResult());
            Assert.Equal(1, surface.BackCalls);
            Assert.True(shell.ViewModel.IsToolSurfaceOpen, "the surface's sheet did not get Back first");

            // Once the surface is done with Back, the shell leaves the tool page.
            surface.ConsumeBack = false;
            var leave = shell.HandleBackAsync();
            MobileShellTests.PumpUntil(window, () => leave.IsCompleted, "back never settled");
            Assert.True(leave.GetAwaiter().GetResult());
            Assert.False(shell.ViewModel.IsToolSurfaceOpen);
            Assert.Equal(2, surface.BackCalls);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Devices_tab_lists_real_computers_and_only_claims_connected_after_a_check()
    {
        var control = new FakeMobileControlDeviceService
        {
            Snapshot = new MobileControlDeviceSnapshot(
            [
                FakeMobileControlDeviceService.Device("grant-1", "工作电脑", "reachable", "今天 09:12 应答"),
                FakeMobileControlDeviceService.Device("grant-2", "备用电脑", "imported")
            ])
        };
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var chrome = CreateChrome();
        var (shell, window) = CreateShell(chrome, control);
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            MobileShellTests.PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");

            shell.ViewModel.NavigateRoot(MobilePageKeys.Devices);
            MobileShellTests.PumpUntil(
                window,
                () => shell.PageLoad.IsCompleted && shell.ViewModel.Devices.ControlDevices.Count == 2,
                "the computer list never loaded");
            Pump(window);

            var devices = shell.ViewModel.Devices.ControlDevices;
            Assert.Equal("工作电脑", devices[0].Name);
            Assert.Equal("已连接", devices[0].StateLabel);
            Assert.True(devices[0].IsReachable);
            Assert.Equal("尚未检查", devices[1].StateLabel);
            Assert.True(devices[1].NeverChecked);

            var texts = shell.PhoneHost.GetVisualDescendants().OfType<TextBlock>()
                .Select(block => block.Text ?? "")
                .ToArray();
            Assert.Contains("电脑工具", texts);
            Assert.Contains(texts, text => text.Contains("工作电脑", StringComparison.Ordinal));
            Assert.Contains(texts, text => text.Contains("尚未检查", StringComparison.Ordinal));

            // The row opens the control surface with the module's grant id.
            devices[0].OpenCommand.Execute(null);
            MobileShellTests.PumpUntil(window, () => shell.ViewModel.IsToolSurfaceOpen, "the control surface never opened");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Devices_tab_without_a_computer_offers_the_connect_action_only()
    {
        var control = new FakeMobileControlDeviceService
        {
            Snapshot = new MobileControlDeviceSnapshot([], "需要先在电脑上授权这台手机访问电脑工具。")
        };
        using var host = new TestToolHost(TestToolHost.DefaultPhoneCatalog());
        var (shell, window) = CreateShell(CreateChrome(), control);
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            shell.ViewModel.NavigateRoot(MobilePageKeys.Devices);
            MobileShellTests.PumpUntil(window, () => shell.PageLoad.IsCompleted && control.Calls > 0, "the computer list never loaded");
            Pump(window);

            Assert.Empty(shell.ViewModel.Devices.ControlDevices);
            Assert.True(shell.ViewModel.Devices.HasControlError);
            Assert.Contains("授权", shell.ViewModel.Devices.ControlEmptyDetail);

            var connect = shell.PhoneHost.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => Avalonia.Automation.AutomationProperties.GetName(button) == "连接电脑");
            Assert.NotNull(connect);
            Assert.False(shell.ViewModel.IsToolSurfaceOpen);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Control_activation_uri_carries_the_grant_id_and_never_a_file_pairing_id()
    {
        var uri = MobileControlDeviceService.BuildActivationUri("grant-42", "input-monitor");
        Assert.StartsWith("mypowertools://device-tool?device=grant-42", uri, StringComparison.Ordinal);
        Assert.Contains("tool=input-monitor", uri, StringComparison.Ordinal);

        var connect = MobileControlDeviceService.BuildActivationUri("");
        Assert.Equal("mypowertools://device-tool?device=", connect);
    }

    [Fact]
    public void Control_device_payload_is_read_from_the_real_command_shape()
    {
        const string payload = """
        {"devices":[{"deviceId":"grant-1","deviceName":"工作电脑","endpoint":"http://100.64.0.2:49541",
        "platform":"windows","lastState":"unreachable","lastDetail":"连接超时","credentialConfigured":true},
        {"deviceName":"no id"}]}
        """;

        var devices = MobileControlDeviceService.Parse(payload);

        var device = Assert.Single(devices);
        Assert.Equal("grant-1", device.DeviceId);
        Assert.Equal("工作电脑", device.Name);
        Assert.Equal("无法连接", device.StateLabel);
        Assert.True(device.CredentialConfigured);
        Assert.Contains("连接超时", device.Subtitle);
        Assert.Empty(MobileControlDeviceService.Parse("not json"));
        Assert.Empty(MobileControlDeviceService.Parse(null));
    }

    [Fact]
    public async Task Control_device_service_reports_a_permission_requirement_instead_of_an_empty_success()
    {
        var service = new MobileControlDeviceService((_, _) => Task.FromResult(
            new ShellCommandExecutionResult(
                "permission-required",
                new MyPowerTools.Protocol.HostControl.V1.CommandExecutionResponse { State = "permission-required" },
                true)));

        var snapshot = await service.GetDevicesAsync();

        Assert.Empty(snapshot.Devices);
        Assert.Contains("授权", snapshot.Error);
    }

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 4; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
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

    private static (MobileShellView Shell, Window Window) CreateShell(
        ShellChromeViewModel chrome,
        IMobileControlDeviceService? controlDevices = null)
    {
        var search = new MptSearchBox();
        var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var workspace = new ShellWorkspaceController(chrome, search, pageHost, new ContentControl(), new ContentControl(), new ContentControl());
        var shell = new MobileShellView(workspace, chrome, search, MobileTestEnvironment.CreateServices(controlDevices: controlDevices));
        var window = new Window { Width = 390, Height = 844, Content = shell };
        return (shell, window);
    }

    /// <summary>A loaded surface that implements both activation and page-local Back handling.</summary>
    private sealed class BackAwareSurface : Control, IMptAvaloniaSurfaceActivationHandler, IMptAvaloniaSurfaceBackHandler
    {
        public bool ConsumeBack { get; set; }
        public int BackCalls { get; private set; }

        public ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public bool TryHandleBack()
        {
            BackCalls++;
            return ConsumeBack;
        }
    }
}
