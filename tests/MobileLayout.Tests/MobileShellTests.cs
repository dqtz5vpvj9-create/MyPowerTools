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
using MyPowerTools.Shell.Avalonia.Views;
using Grpc.Core;
using MyPowerTools.HostControl;
using HostProto = MyPowerTools.Protocol.HostControl.V1;
using MyPowerTools.UI.Controls;

namespace MobileLayout.Tests;

/// <summary>
/// Covers the two host behaviours the Android build depends on: every top-level page stays reachable
/// from the touch bar, and an external activation reaches the surface that is already open instead of
/// rebuilding it (a rebuilt surface dropped the file selection on a multi-file share).
///
/// The workspace is never disposed here on purpose: <see cref="ShellWorkspaceController.DisposeAsync"/>
/// waits on Runner event streams that do not exist in a UI-only test, which would hang the run. The
/// headless session owns the controls and the test process exits with them.
/// </summary>
public sealed class MobileShellTests
{
    private static readonly System.Reflection.FieldInfo ContentHostField = typeof(ShellWorkspaceController)
        .GetField("_contentHost", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static readonly System.Reflection.MethodInfo ActivateToolAsyncMethod = typeof(ShellWorkspaceController)
        .GetMethod("ActivateToolAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

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
        int width)
    {
        var search = new MptSearchBox();
        var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var workspace = new ShellWorkspaceController(chrome, search, pageHost, new ContentControl(), new ContentControl(), new ContentControl());
        var shell = new MobileShellView(workspace, chrome, search);
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
    public void Mobile_shell_exposes_every_top_level_page_and_keeps_touch_targets_usable(int width)
    {
        var navigations = new List<string>();
        var chrome = CreateChrome(navigations.Add);
        var (shell, _, _, window) = CreateShell(chrome, width);
        try
        {
            Assert.Equal(ShellWorkspaceController.PageLabels, chrome.NavigationItems.Select(item => item.Label));
            var bar = shell.NavigationButtons;
            Assert.Equal(5, bar.Count);
            Assert.Equal(chrome.NavigationItems.Count, bar.Count);
            Assert.Equal(["首页", "工具", "活动", "设置", "系统"],
                bar.Select(button => AutomationProperties.GetName(button)).ToArray());

            for (var index = 0; index < bar.Count; index++)
            {
                var button = bar[index];
                Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(button)), $"tap target {index} has no automation name");
                Assert.True(button.Bounds.Width >= 48, $"{button.Bounds.Width:0.0} wide tap target at {width}");
                Assert.True(button.Bounds.Height >= 48, $"{button.Bounds.Height:0.0} tall tap target at {width}");
                var origin = button.TranslatePoint(default, window);
                Assert.NotNull(origin);
                Assert.True(origin!.Value.X >= -1 && origin.Value.X + button.Bounds.Width <= width + 1,
                    $"{AutomationProperties.GetName(button)} left the viewport at {width}");
            }

            // Every top-level page is reachable from the bar with the command the desktop chrome uses.
            foreach (var item in chrome.NavigationItems)
            {
                item.NavigateCommand.Execute(null);
            }

            Assert.Equal(ShellWorkspaceController.PageLabels, navigations);
            Assert.Equal("Home", chrome.NavigationItems[0].Label);

            window.Width = width == 320 ? 768 : 320;
            Pump(window);
            Assert.Equal(5, shell.NavigationButtons.Count);
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

            // The host the workspace writes pages into is rendered by this shell.
            var pageHost = shell.PageHost;
            Assert.Contains(pageHost, shell.GetVisualDescendants().OfType<ContentControl>());
            var pageContent = new TextBlock { Text = "page" };
            pageHost.Content = pageContent;
            Pump(window);
            Assert.Contains(pageContent, shell.GetVisualDescendants());

            // The command panel the workspace fills is rendered by this shell.
            var commandPanel = (ContentControl)typeof(ShellWorkspaceController)
                .GetField("_commandPanel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(workspace)!;
            Assert.Contains(commandPanel, shell.GetVisualDescendants().OfType<ContentControl>());

            // The permission panel the workspace fills is rendered by this shell.
            var permissionPanel = (ContentControl)typeof(ShellWorkspaceController)
                .GetField("_permissionPanel", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(workspace)!;
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
    /// On the phone the default constructor is the only one used. Its chrome used to be created with
    /// no delegates at all, so every tab tap, the refresh button and the overlays were silent no-ops
    /// while the selection state still moved. This drives the real commands and asserts the real
    /// workspace reacted — it must not install its own callbacks.
    /// </summary>
    [AvaloniaFact]
    public void Default_shell_navigation_commands_reach_the_real_workspace()
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
            var bar = shell.NavigationButtons;
            Assert.Equal(5, bar.Count);

            // Both desktop and phone-only tabs reach the workspace and reflect the selected page.
            bar[3].Command!.Execute(null);
            Pump(window);
            Assert.True(chrome.NavigationItems[3].IsSelected, "tapping Settings did not select the Settings page");

            bar[2].Command!.Execute(null);
            Pump(window);
            Assert.Equal("Activity", CurrentPage(workspace));
            Assert.True(bar[2].Classes.Contains("selected"));

            chrome.OpenCommandPaletteCommand.Execute(null);
            Pump(window);
            Assert.True(chrome.IsCommandPaletteOpen, "open command palette did not reach the workspace");
            chrome.CloseCommandPaletteCommand.Execute(null);
            Pump(window);
            Assert.False(chrome.IsCommandPaletteOpen, "close command palette did not reach the workspace");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The Quick access template used a fixed button height; at 320px the wrapped label and the
    /// three-line detail overflowed the card bottom. A real Home page must keep every quick action
    /// inside its card.
    /// </summary>
    [AvaloniaFact]
    public void Real_home_page_keeps_quick_actions_inside_their_card_at_phone_width()
    {
        var shell = new MobileShellView();
        var window = new Window { Width = 320, Height = 800, Content = shell };
        try
        {
            window.Show();
            Pump(window);

            var tools = new[]
            {
                new ToolCardViewModel("file-transfer", "文件互传", "通过 Tailscale 直传或 OpenList 网盘中转发送文件。", "文件", "⇄", "Ready", "Windows · macOS · Android", ToolAvailability.Available, true)
            };
            var home = new HomeView { DataContext = new HomeViewModel(tools, tools, [], 1) };
            shell.PageHost.Content = home;
            Pump(window);
            Pump(window);

            var cards = home.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("PowerQuickAction")).ToArray();
            Assert.NotEmpty(cards);

            foreach (var card in cards)
            {
                var cardBorder = card.GetVisualAncestors().OfType<Border>()
                    .First(border => border.Classes.Contains("PowerDashboardCard"));
                var origin = card.TranslatePoint(default, cardBorder);
                Assert.NotNull(origin);
                var bottom = origin!.Value.Y + card.Bounds.Height;
                Assert.True(bottom <= cardBorder.Bounds.Height + 1,
                    $"quick action overflows its card: action bottom {bottom:0.0}, card height {cardBorder.Bounds.Height:0.0}");

                foreach (var text in card.GetVisualDescendants().OfType<TextBlock>())
                {
                    var textOrigin = text.TranslatePoint(default, card);
                    Assert.NotNull(textOrigin);
                    Assert.True(textOrigin!.Value.Y + text.Bounds.Height <= card.Bounds.Height + 1,
                        $"'{text.Text}' is clipped at the card bottom");
                }
            }
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
    /// load, so the user landed back on the gallery with an empty selection. The activation now waits
    /// for <see cref="MobileShellView.Ready"/>. Nothing is seeded here — the descriptor comes through
    /// the real HostControl client and the real page loader.
    /// </summary>
    [AvaloniaFact]
    public void Activation_that_races_the_initial_open_still_lands_on_the_tool_page()
    {
        using var host = new StubToolHost("file-transfer", "文件互传");
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

            // Let anything the initial open queued behind it settle, then re-check the page survived.
            Pump(window);
            Pump(window);
            Assert.True(workspace.IsToolPageOpen, "the late initial open navigated away from the activated tool");
            Assert.Equal("Tools", workspace.CurrentPageKey);
            Assert.Equal("file-transfer", CurrentToolId(workspace));
            Assert.IsType<ExternalSdkToolView>(shell.PageHost.Content);
            Assert.False(workspace.IsHomePage, "the activation was overwritten by the initial Home navigation");
            Assert.True(activation.IsCompleted, "activation did not finish");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A sequential share (one activation per file) must not rebuild the tool page between files: the
    /// surface that received the first file is the one that receives the last. The page is opened by the
    /// real activation path and the surface is hosted through the same call the loader uses, so the
    /// lookup under test is the production live-surface discovery.
    /// </summary>
    [AvaloniaFact]
    public void Sequential_share_activations_keep_one_tool_page_instance()
    {
        using var host = new StubToolHost("file-transfer", "文件互传");
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");

            // The tool is already open the way a catalog tap leaves it, then the files arrive one by one.
            PumpUntil(window, () => FindToolCard(shell, "file-transfer") is not null, "the tool card was never rendered");
            FindToolCard(shell, "file-transfer")!.OpenCommand.Execute(null);
            PumpUntil(window, () => shell.PageHost.Content is ExternalSdkToolView, "the catalog tap did not open the tool page");
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
    /// A tool the user opened from the catalog is in no activation map, but it is the live surface for
    /// that tool. A share arriving afterwards must reach it instead of rebuilding the page and losing
    /// the files already selected there.
    /// </summary>
    [AvaloniaFact]
    public void Manually_opened_tool_surface_receives_a_later_share()
    {
        using var host = new StubToolHost("file-transfer", "文件互传");
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");

            // Open the tool the way a tap on the Home card does - no activation involved yet.
            PumpUntil(window, () => FindToolCard(shell, "file-transfer") is not null, "the tool card was never rendered");
            FindToolCard(shell, "file-transfer")!.OpenCommand.Execute(null);
            PumpUntil(window, () => shell.PageHost.Content is ExternalSdkToolView, "the catalog tap did not open the tool page");

            var openedView = Assert.IsType<ExternalSdkToolView>(shell.PageHost.Content);
            var handler = new RecordingActivationHandler();
            openedView.SetManagedSurface(handler);
            Pump(window);

            RunActivation(window, shell,
                new ToolActivationRequest("file-transfer", "main", "file:///data/cache/shares/shared.txt"),
                () => handler.Requests.Count == 1, "the share never reached the manually opened surface");

            Assert.Same(openedView, shell.PageHost.Content);
            Assert.Same(handler, openedView.ManagedSurface);
            Assert.Single(handler.Requests);
        }
        finally
        {
            window.Close();
        }
    }

    private static ToolCardViewModel? FindToolCard(MobileShellView shell, string toolId) =>
        shell.GetVisualDescendants().OfType<Control>()
            .Select(control => control.DataContext)
            .OfType<ToolCardViewModel>()
            .FirstOrDefault(card => string.Equals(card.ToolId, toolId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Cold start regression: the runtime emits its registry.loaded / module.enabled burst after the
    /// shell is up, and that refresh used to reload the "Tools" page key while a share activation was
    /// still loading its surface. The reload cleared the current tool, started a new workspace
    /// generation and replaced the loading tool page with the gallery, so the shared files were lost.
    /// The real router and the real host-event handler are exercised here; only the transport is stubbed.
    /// </summary>
    [AvaloniaFact]
    public void Background_registry_refresh_does_not_evict_the_open_tool_page()
    {
        using var host = new StubToolHost("file-transfer", "文件互传");
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

            // The tool page and its surface survived the startup refresh burst.
            Assert.Same(opened, shell.PageHost.Content);
            Assert.Same(handler, opened.ManagedSurface);
            Assert.Equal("file-transfer", CurrentToolId(workspace));
            Assert.Equal("Tools", workspace.CurrentPageKey);

            // A later share must still reach that same live surface. The first activation ran before a
            // surface was hosted, so this is the first request the attached handler sees.
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
    /// The guard must not disable the refresh for the catalog itself: with no tool open, a registry
    /// refresh still reloads the Tools gallery.
    /// </summary>
    [AvaloniaFact]
    public void Background_registry_refresh_still_reloads_the_tool_catalog()
    {
        using var host = new StubToolHost("file-transfer", "文件互传");
        var shell = new MobileShellView();
        var window = new Window { Width = 360, Height = 800, Content = shell };
        try
        {
            window.Show();
            host.CompleteInitialLoad();
            PumpUntil(window, () => shell.Ready.IsCompleted, "shell never became ready");
            var workspace = WorkspaceOf(shell);

            PumpUntil(window, () => FindToolCard(shell, "file-transfer") is not null, "the tool card was never rendered");
            FindToolCard(shell, "file-transfer")!.OpenCommand.Execute(null);
            PumpUntil(window, () => shell.PageHost.Content is ExternalSdkToolView, "the catalog tap did not open the tool page");
            Assert.True(RunBack(window, shell, () => shell.PageHost.Content is not ExternalSdkToolView || !workspace.IsToolPageActive, "back did not leave the tool page"),
                "back did not consume the tool page");
            PumpUntil(window, () => shell.PageHost.Content is ToolCatalogView, "the gallery never became current");
            Assert.False(workspace.IsToolPageActive);

            ApplyHostEvent(window, workspace, "registry.loaded");

            Assert.False(workspace.IsToolPageActive);
            Assert.Equal("Tools", workspace.CurrentPageKey);
            Assert.IsType<ToolCatalogView>(shell.PageHost.Content);
        }
        finally
        {
            window.Close();
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

    /// <summary>
    /// Back must walk tool page -> gallery -> Home before it lets Android background the app. The old
    /// check used the navigation highlight, which reports Home while a shared file is open.
    /// </summary>
    [AvaloniaFact]
    public void Back_leaves_the_tool_page_before_it_lets_the_app_background()
    {
        using var host = new StubToolHost("file-transfer", "文件互传");
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

            Assert.True(RunBack(window, shell, () => !workspace.IsToolPageOpen, "back did not leave the tool page"),
                "back left the app while a tool page was open");
            Assert.Equal("Tools", workspace.CurrentPageKey);

            Assert.True(RunBack(window, shell, () => workspace.IsHomePage, "back did not return Home"),
                "back left the app from the tools gallery");
            Assert.Equal("Home", workspace.CurrentPageKey);

            Assert.False(RunBack(window, shell, () => true, "back did not settle from Home"),
                "back must let Android background the app from Home");
        }
        finally
        {
            window.Close();
        }
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
    private static void PumpUntil(Window window, Func<bool> condition, string message)
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

    /// <summary>
    /// Serves tool descriptors through the real HostControl client so activations exercise the
    /// production page loader without a Runner process.
    /// </summary>
    private sealed class StubToolHost : IDisposable
    {
        private readonly CallInvoker? _previous;
        private readonly TaskCompletionSource _listToolsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _getToolCalls;

        public StubToolHost(string toolId, string title, string? surfaceAssembly = null, string? surfaceType = null)
        {
            Descriptor = new HostProto.ToolDescriptor
            {
                ToolId = toolId,
                Title = title,
                Description = "test tool",
                ToolType = surfaceAssembly is null ? "native-tool" : "dotnet-surface",
                PrimaryRouteId = "main",
                Availability = "available",
                State = "ready",
                SourceDirectory = "/nonexistent"
            };
            Descriptor.Routes.Add(surfaceAssembly is null
                ? new HostProto.ToolRoute { RouteId = "main", Title = title, SurfaceKind = "native" }
                : new HostProto.ToolRoute
                {
                    RouteId = "main",
                    Title = title,
                    SurfaceKind = "dotnet",
                    Assembly = surfaceAssembly,
                    Type = surfaceType ?? ""
                });
            _previous = HostControlClient.EmbeddedInvoker;
            HostControlClient.EmbeddedInvoker = new DescriptorInvoker(this);
        }

        public HostProto.ToolDescriptor Descriptor { get; }

        public int GetToolCalls => Volatile.Read(ref _getToolCalls);

        /// <summary>Releases the initial catalog load, which models a Runner that answers slowly.</summary>
        public void CompleteInitialLoad() => _listToolsGate.TrySetResult();

        internal Task WaitInitialLoadAsync() => _listToolsGate.Task;

        internal void CountGetTool() => Interlocked.Increment(ref _getToolCalls);

        public void Dispose() => HostControlClient.EmbeddedInvoker = _previous;
    }

    private sealed class DescriptorInvoker(StubToolHost host) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? hostName,
            CallOptions options,
            TRequest request)
        {
            if (method.Name == "GetTool")
            {
                host.CountGetTool();
                return Unary((TResponse)(object)host.Descriptor);
            }

            if (method.Name == "ListTools")
            {
                var response = new HostProto.ListToolsResponse();
                response.Tools.Add(host.Descriptor);
                return Unary((TResponse)(object)response, host.WaitInitialLoadAsync());
            }

            return Failure<TResponse>(method.Name);
        }

        private static AsyncUnaryCall<T> Unary<T>(T response, Task? responseTask = null) => new(
            responseTask is null ? Task.FromResult(response) : responseTask.ContinueWith(_ => response, TaskScheduler.Default),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => new Metadata(),
            () => { });

        private static AsyncUnaryCall<T> Failure<T>(string name) => new(
            Task.FromException<T>(new RpcException(new Status(StatusCode.Unimplemented, name))),
            Task.FromResult(new Metadata()),
            () => new Status(StatusCode.Unimplemented, name),
            () => new Metadata(),
            () => { });

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? hostName, CallOptions options) => throw new NotSupportedException(method.Name);

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? hostName, CallOptions options) => throw new NotSupportedException(method.Name);

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? hostName, CallOptions options, TRequest request) => throw new NotSupportedException(method.Name);

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? hostName, CallOptions options, TRequest request) => throw new NotSupportedException(method.Name);
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
