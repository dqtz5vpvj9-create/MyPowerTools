using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;
using MyPowerTools.UI.Controls;

namespace MobileLayout.Tests;

/// <summary>
/// Covers the behaviours the Android build depends on: the four phone tabs, the workspace hosts the
/// phone shell must render, the activation race that used to drop a shared file, and the back walk
/// from a tool surface to the phone pages.
///
/// The workspace is never disposed here on purpose: <see cref="ShellWorkspaceController.DisposeAsync"/>
/// waits on Runner event streams that do not exist in a UI-only test, which would hang the run.
/// </summary>
public sealed class MobileShellTests
{
    private static ShellChromeViewModel CreateChrome(Action<string> onNavigate) =>
        new(ShellWorkspaceController.PageLabels,
            navigate: page => { onNavigate(page); return Task.CompletedTask; },
            refresh: () => Task.CompletedTask,
            openCommandPalette: () => Task.CompletedTask,
            closeCommandPalette: () => Task.CompletedTask,
            dismissPermissionPrompt: () => Task.CompletedTask,
            runtimeModeLabel: "ANDROID",
            runtimeIdentityText: "MPT Android");

    private static (MobileShellView Shell, ShellWorkspaceController Workspace, ContentControl Host, Window Window) CreateShell(
        ShellChromeViewModel chrome,
        int width,
        FakeMobileDeviceService? devices = null)
    {
        var search = new MptSearchBox();
        var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var workspace = new ShellWorkspaceController(chrome, search, pageHost, new ContentControl(), new ContentControl(), new ContentControl());
        var shell = new MobileShellView(workspace, chrome, search, MobileTestEnvironment.CreateServices(devices));
        var window = new Window { Width = width, Height = 800, Content = shell };
        window.Show();
        Pump(window);
        return (shell, workspace, pageHost, window);
    }

    private static string CurrentPage(ShellWorkspaceController workspace) =>
        (string)typeof(ShellWorkspaceController)
            .GetField("_currentPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workspace)!;

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(360)]
    [InlineData(390)]
    [InlineData(768)]
    public void Phone_shell_exposes_four_tabs_and_keeps_touch_targets_usable(int width)
    {
        var navigations = new List<string>();
        var chrome = CreateChrome(navigations.Add);
        var (shell, _, _, window) = CreateShell(chrome, width);
        try
        {
            var bar = shell.NavigationButtons;
            Assert.Equal(4, bar.Count);
            Assert.Equal(["常用", "工具", "设备", "动态"],
                bar.Select(button => AutomationProperties.GetName(button)).ToArray());
            Assert.Equal(4, shell.ViewModel.Tabs.Count);

            foreach (var button in bar)
            {
                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)), "a phone tab has no automation name");
                Assert.True(button.Bounds.Width >= 44, $"{button.Bounds.Width:0.0} wide tap target at {width}");
                Assert.True(button.Bounds.Height >= 48, $"{button.Bounds.Height:0.0} tall tap target at {width}");
                var origin = button.TranslatePoint(default, window);
                Assert.NotNull(origin);
                Assert.True(origin!.Value.X >= -1 && origin.Value.X + button.Bounds.Width <= width + 1,
                    $"{AutomationProperties.GetName(button)} left the viewport at {width}");
            }

            // Every tab really switches the phone page (the tab command is the production command).
            var expected = new[] { MobilePageKeys.Home, MobilePageKeys.Tools, MobilePageKeys.Devices, MobilePageKeys.Activity };
            for (var index = 0; index < bar.Count; index++)
            {
                bar[index].Command!.Execute(null);
                Pump(window);
                Assert.Equal(expected[index], shell.CurrentPhonePageKey);
            }

            window.Width = width == 320 ? 768 : 320;
            Pump(window);
            Assert.Equal(4, shell.NavigationButtons.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The default constructor is the one Android uses. The workspace must write into the page host,
    /// command panel and permission panel that this shell actually renders — the previous wiring gave
    /// the workspace freshly allocated controls that were never in the visual tree, so every page
    /// stayed blank while navigation "worked".
    /// </summary>
    [AvaloniaFact]
    public void Default_shell_renders_the_workspace_page_host_and_its_overlays()
    {
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            window.Show();
            Pump(window);

            var chrome = (ShellChromeViewModel)typeof(MobileShellView)
                .GetField("_chrome", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(shell)!;
            var workspace = (ShellWorkspaceController)typeof(MobileShellView)
                .GetField("_workspace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(shell)!;

            // Exactly one content host is on screen: the phone pages own it until a tool surface loads.
            Assert.True(shell.PhoneHost.IsEffectivelyVisible, "the phone page host must be visible on a fresh shell");
            Assert.False(shell.PageHost.IsEffectivelyVisible, "the workspace host must stay off screen until a tool surface loads");

            // The host the workspace writes tool surfaces into is rendered by this shell. The workspace
            // may set IsVisible on that control itself, so the shell owns a wrapper for the decision.
            var pageHost = shell.PageHost;
            Assert.Contains(pageHost, shell.GetVisualDescendants().OfType<ContentControl>());
            shell.ViewModel.IsToolSurfaceOpen = true;
            Pump(window);
            Assert.True(shell.PageHost.IsEffectivelyVisible, "the workspace host must show while a tool surface is open");
            Assert.False(shell.PhoneHost.IsEffectivelyVisible);
            var pageContent = new TextBlock { Text = "page" };
            pageHost.Content = pageContent;
            Pump(window);
            Assert.Contains(pageContent, shell.GetVisualDescendants());
            shell.ViewModel.IsToolSurfaceOpen = false;
            Pump(window);
            Assert.False(shell.PageHost.IsEffectivelyVisible);

            // The command panel the workspace fills is rendered by this shell.
            var commandPanel = workspace.MobileCommandPanel;
            Assert.Contains(commandPanel, shell.GetVisualDescendants().OfType<ContentControl>());

            // The permission panel the workspace fills is rendered by this shell.
            var permissionPanel = workspace.MobilePermissionPanel;
            Assert.Contains(permissionPanel, shell.GetLogicalDescendants().OfType<ContentControl>());
            var prompt = new TextBlock { Text = "system action" };
            permissionPanel.Content = prompt;

            // Opening the permission prompt through the chrome makes that panel visible and rendered.
            var permissionOverlay = permissionPanel.GetLogicalAncestors().OfType<Border>().First();
            Assert.False(permissionOverlay.IsVisible);
            chrome.IsPermissionPromptOpen = true;
            Pump(window);
            Assert.True(permissionOverlay.IsVisible);
            Assert.Contains(prompt, shell.GetVisualDescendants());
            chrome.IsPermissionPromptOpen = false;
            Pump(window);
            Assert.False(permissionOverlay.IsVisible);

            // The command palette routes through the same shell.
            chrome.IsCommandPaletteOpen = true;
            Pump(window);
            Assert.Contains(commandPanel, shell.GetVisualDescendants().OfType<ContentControl>().Where(control => control.IsEffectivelyVisible));
            chrome.IsCommandPaletteOpen = false;
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The default constructor creates the chrome with no delegates if it is not careful, which makes
    /// every tab tap, refresh and overlay a silent no-op. The chrome must keep driving the real
    /// workspace; the phone shell does not install its own callbacks instead.
    /// </summary>
    [AvaloniaFact]
    public void Default_shell_chrome_commands_reach_the_real_workspace()
    {
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            window.Show();
            Pump(window);

            var workspace = (ShellWorkspaceController)typeof(MobileShellView)
                .GetField("_workspace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(shell)!;
            var chrome = (ShellChromeViewModel)typeof(MobileShellView)
                .GetField("_chrome", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(shell)!;

            var settings = chrome.NavigationItems.First(item => item.Label == "Settings");
            settings.NavigateCommand.Execute(null);
            PumpUntil(window, () => CurrentPage(workspace) == "Settings", "the chrome Settings command never reached the workspace");

            chrome.OpenCommandPaletteCommand.Execute(null);
            PumpUntil(window, () => chrome.IsCommandPaletteOpen, "open command palette did not reach the workspace");
            chrome.CloseCommandPaletteCommand.Execute(null);
            PumpUntil(window, () => !chrome.IsCommandPaletteOpen, "close command palette did not reach the workspace");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Regression for the Android multi-share failure. The shell starts its initial open when it is
    /// attached, and the platform used to deliver the share activation before that finished: the late
    /// open navigated to Home, began a new workspace generation and discarded the activation's page
    /// load, so the user landed back on the tool list with an empty selection. The activation now waits
    /// for <see cref="MobileShellView.Ready"/>.
    /// </summary>
    [AvaloniaFact]
    public void Activation_that_races_the_initial_open_still_lands_on_the_tool_page()
    {
        using var host = new TestToolHost(TestToolHost.PhoneTool("file-transfer", "文件互传")).HoldInitialListTools();
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            var workspace = WorkspaceOf(shell);

            // Start the activation in the same turn as the attach, before the initial open can finish.
            window.Show();
            Assert.False(shell.Ready.IsCompleted, "the initial open completed before the race could be exercised");
            var activation = shell.ActivateAsync(
                new ToolActivationRequest("file-transfer", "main", "file:///data/cache/shares/a%20file.txt"));
            Assert.False(workspace.IsToolPageOpen, "the tool page opened before the initial catalog load finished");
            host.CompleteInitialLoad();

            PumpUntil(window, () => shell.Ready.IsCompleted && shell.PageHost.Content is ExternalSdkToolView,
                "activation did not reach the tool page");

            Pump(window);
            Pump(window);
            Assert.True(workspace.IsToolPageOpen, "the late initial open navigated away from the activated tool");
            Assert.Equal("file-transfer", CurrentToolId(workspace));
            Assert.IsType<ExternalSdkToolView>(shell.PageHost.Content);
            Assert.True(shell.ViewModel.IsToolSurfaceOpen, "the phone shell did not switch to the shared file surface");
            Assert.True(activation.IsCompleted, "activation did not finish");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A sequential share (one activation per file) must not rebuild the tool page between files: the
    /// surface that received the first file is the one that receives the last.
    /// </summary>
    [AvaloniaFact]
    public void Sequential_share_activations_keep_one_tool_page_instance()
    {
        using var host = new TestToolHost(TestToolHost.PhoneTool("file-transfer", "文件互传"));
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");

            // The tool is already open the way a library tap leaves it, then the files arrive one by one.
            shell.ViewModel.NavigateRoot(MobilePageKeys.Tools);
            PumpUntil(window, () => FindToolRow(shell, "file-transfer") is not null, "the tool row was never rendered");
            // The row pushes the phone detail page; the detail page's primary action opens the surface.
            FindToolRow(shell, "file-transfer")!.Command!.Execute(null);
            PumpUntil(window, () => shell.CurrentPhonePageKey == MobilePageKeys.ToolDetail, "the tool detail never opened");
            shell.ViewModel.ToolDetail!.OpenCommand.Execute(null);
            PumpUntil(window, () => shell.PageHost.Content is ExternalSdkToolView, "the tool surface did not open");
            var opened = Assert.IsType<ExternalSdkToolView>(shell.PageHost.Content);
            var handler = new RecordingActivationHandler();
            opened.SetManagedSurface(handler);
            Pump(window);

            for (var index = 0; index < 3; index++)
            {
                var delivered = index + 1;
                RunActivation(window, shell,
                    new ToolActivationRequest("file-transfer", "main", $"file:///data/cache/shares/{index}.txt"),
                    () => handler.Requests.Count == delivered, $"share {index} never reached the open surface");
            }

            Assert.Same(opened, shell.PageHost.Content);
            Assert.Same(handler, opened.ManagedSurface);
            Assert.Equal(3, handler.Requests.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Cold start regression: the runtime emits its registry.loaded / module.enabled burst after the
    /// shell is up, and that refresh used to reload the "Tools" page key while a share activation was
    /// still loading its surface, which replaced the loading tool page with the gallery.
    /// </summary>
    [AvaloniaFact]
    public void Background_registry_refresh_does_not_evict_the_open_tool_page()
    {
        using var host = new TestToolHost(TestToolHost.PhoneTool("file-transfer", "文件互传"));
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");
            var workspace = WorkspaceOf(shell);

            RunActivation(window, shell,
                new ToolActivationRequest("file-transfer", "main", "file:///data/cache/shares/cold.txt"),
                () => shell.PageHost.Content is ExternalSdkToolView, "the activation did not open the tool page");
            var opened = Assert.IsType<ExternalSdkToolView>(shell.PageHost.Content);
            var handler = new RecordingActivationHandler();
            opened.SetManagedSurface(handler);
            Pump(window);

            ApplyHostEvent(window, workspace, "registry.loaded");

            Assert.Same(opened, shell.PageHost.Content);
            Assert.Same(handler, opened.ManagedSurface);
            Assert.Equal("file-transfer", CurrentToolId(workspace));

            var afterRefresh = new ToolActivationRequest("file-transfer", "main", "file:///data/cache/shares/after.txt");
            RunActivation(window, shell, afterRefresh,
                () => handler.Requests.Count == 1, "a share after the refresh never reached the surface");
            Assert.Equal([afterRefresh], handler.Requests);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Back must walk tool surface -> phone page -> 常用 tab before it lets Android background the app.
    /// The old check used the navigation highlight, which reported Home while a shared file was open.
    /// </summary>
    [AvaloniaFact]
    public void Back_leaves_the_tool_surface_then_the_phone_pages_before_it_backgrounds_the_app()
    {
        using var host = new TestToolHost(TestToolHost.PhoneTool("file-transfer", "文件互传"));
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");
            var workspace = WorkspaceOf(shell);

            RunActivation(window, shell,
                new ToolActivationRequest("file-transfer", "main", "file:///data/cache/shares/a.txt"),
                () => shell.PageHost.Content is ExternalSdkToolView, "tool page did not open");
            Assert.True(shell.ViewModel.IsToolSurfaceOpen);

            // Back leaves the tool surface and returns to the phone page that opened it.
            Assert.True(RunBack(window, shell, () => !shell.ViewModel.IsToolSurfaceOpen, "back did not leave the tool surface"),
                "back left the app while a tool surface was open");
            Assert.False(workspace.IsToolPageActive);

            // Back from a pushed page returns to the previous phone page.
            shell.ViewModel.NavigateRoot(MobilePageKeys.Tools);
            Pump(window);
            Assert.True(RunBack(window, shell, () => shell.CurrentPhonePageKey == MobilePageKeys.Home, "back did not reach 常用"),
                "back did not consume the 工具 tab");

            // Back from 常用 lets Android background the app.
            Assert.False(RunBack(window, shell, () => true, "back did not settle from 常用"),
                "back must let Android background the app from 常用");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The guard must not disable the refresh for the workspace catalog itself: with no tool surface
    /// open, a registry refresh still reloads the current workspace page.
    /// </summary>
    [AvaloniaFact]
    public void Background_registry_refresh_still_reloads_the_workspace_catalog()
    {
        using var host = new TestToolHost(TestToolHost.PhoneTool("file-transfer", "文件互传"));
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");
            var workspace = WorkspaceOf(shell);

            shell.ViewModel.NavigateRoot(MobilePageKeys.Tools);
            PumpUntil(window, () => FindToolRow(shell, "file-transfer") is not null, "the tool row was never rendered");
            shell.ViewModel.Tools.Find("file-transfer")!.OpenCommand.Execute(null);
            PumpUntil(window, () => shell.PageHost.Content is ExternalSdkToolView, "the tool surface did not open");
            Assert.True(RunBack(window, shell, () => !shell.ViewModel.IsToolSurfaceOpen, "back did not leave the tool surface"),
                "back did not consume the tool surface");
            Assert.False(workspace.IsToolPageActive);

            var listCallsBefore = host.ListToolsRequests.Count;
            ApplyHostEvent(window, workspace, "registry.loaded");
            PumpUntil(window, () => host.ListToolsRequests.Count > listCallsBefore, "the workspace catalog was never reloaded");
            Assert.False(workspace.IsToolPageActive);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The phone 常用 page must keep its tiles, rows and the hero action inside the viewport at the
    /// narrowest supported width instead of clipping them like the old squeezed desktop panel.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(390)]
    public void Phone_home_keeps_its_tiles_and_rows_inside_the_viewport(int width)
    {
        var devices = new FakeMobileDeviceService
        {
            Snapshot = new MyPowerTools.Shell.Avalonia.Services.Mobile.MobileDeviceSnapshot(
                LocalDeviceName: "测试手机",
                Receiving: true,
                Peers:
                [
                    new MyPowerTools.Shell.Avalonia.Services.Mobile.MobilePeerInfo(
                        "peer-1",
                        "书房的电脑",
                        "100.64.0.2",
                        MyPowerTools.Shell.Avalonia.Services.Mobile.MobilePeerConnectionState.Online,
                        DateTimeOffset.Now)
                ],
                RelayConfigured: true,
                RelayRunning: true,
                RelayDescription: "已连接",
                Activities:
                [
                    new MyPowerTools.Shell.Avalonia.Services.Mobile.MobileTransferActivity(
                        "a1", "设计稿.pdf", "已接收", "receive", "书房的电脑", 1024, DateTimeOffset.Now)
                ])
        };
        var chrome = CreateChrome(_ => { });
        var (shell, _, _, window) = CreateShell(chrome, width, devices);
        try
        {
            PumpUntil(window, () => shell.CurrentPhonePageKey == MobilePageKeys.Home && shell.PageLoad.IsCompleted, "home never loaded");
            Pump(window);

            foreach (var control in LayoutNodes(shell.PhoneHost))
            {
                if (control.Bounds.Width <= 0)
                {
                    continue;
                }

                var origin = control.TranslatePoint(default, shell);
                Assert.NotNull(origin);
                Assert.True(origin!.Value.X >= -1 && origin.Value.X + control.Bounds.Width <= width + 1,
                    $"{control.GetType().Name} overflows the {width}px viewport (x={origin.Value.X:0.0}, width={control.Bounds.Width:0.0})");
            }

            Assert.Contains(shell.PhoneHost.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "发送文件");
        }
        finally
        {
            window.Close();
        }
    }

    private static Button? FindToolRow(MobileShellView shell, string toolId)
    {
        foreach (var control in shell.PhoneHost.GetVisualDescendants().OfType<Control>())
        {
            if (control.DataContext is MobileToolItemViewModel item &&
                string.Equals(item.ToolId, toolId, StringComparison.OrdinalIgnoreCase) &&
                control is Button button &&
                button.Classes.Contains("MptMobileToolRow"))
            {
                return button;
            }
        }

        return null;
    }

    /// <summary>
    /// Layout nodes whose width is expected to fit the viewport. Content inside a deliberately
    /// horizontally scrollable strip (the device chips) is skipped: it is scrolled, not clipped.
    /// </summary>
    private static IEnumerable<Control> LayoutNodes(Visual root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is ScrollViewer { HorizontalScrollBarVisibility: not global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled })
            {
                yield return (Control)child;
                continue;
            }

            if (child is Control control)
            {
                if (control.GetType().Name.Contains("ScrollBar", StringComparison.Ordinal) ||
                    control.GetType().Name.Contains("Viewbox", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return control;
            }

            foreach (var descendant in LayoutNodes(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>Runs the real host-event handler through the real refresh router.</summary>
    private static void ApplyHostEvent(Window window, ShellWorkspaceController workspace, string type)
    {
        var method = typeof(ShellWorkspaceController)
            .GetMethod("ApplyHostEventAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var evt = new HostProto.HostEvent { Type = type, SourceId = "file-transfer" };
        var task = (Task)method.Invoke(workspace, [evt])!;
        PumpUntil(window, () => task.IsCompleted, $"host event {type} did not settle");
        task.GetAwaiter().GetResult();
    }

    private static ShellWorkspaceController WorkspaceOf(MobileShellView shell) =>
        (ShellWorkspaceController)typeof(MobileShellView)
            .GetField("_workspace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(shell)!;

    private static string CurrentToolId(ShellWorkspaceController workspace) =>
        (string)typeof(ShellWorkspaceController)
            .GetField("_currentToolId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workspace)!;

    /// <summary>Runs an activation without blocking the test thread on UI continuations.</summary>
    private static void RunActivation(Window window, MobileShellView shell, ToolActivationRequest request, Func<bool> observed, string message)
    {
        var task = shell.ActivateAsync(request);
        PumpUntil(window, () => task.IsCompleted && observed(), message);
        task.GetAwaiter().GetResult();
    }

    private static bool RunBack(Window window, MobileShellView shell, Func<bool> observed, string message)
    {
        var task = shell.HandleBackAsync();
        PumpUntil(window, () => task.IsCompleted && observed(), message);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>
    /// Pumps the dispatcher until the condition holds or the deadline passes. A loading dotnet surface
    /// needs a real assembly-context load, so this waits on wall-clock time rather than a pass count.
    /// </summary>
    internal static void PumpUntil(Window window, Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Thread.Sleep(2);
        }

        Assert.True(condition(), message);
    }

    private sealed class RecordingActivationHandler : Control, IMptAvaloniaSurfaceActivationHandler
    {
        public List<ToolActivationRequest> Requests { get; } = [];

        public ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(true);
        }
    }
}
