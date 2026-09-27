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

    private static int SurfaceActivations(ShellWorkspaceController workspace) => workspace.ActivationSurfaceActivations;

    private static string CurrentPage(ShellWorkspaceController workspace) =>
        (string)typeof(ShellWorkspaceController)
            .GetField("_currentPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workspace)!;

    private static void Pump(Window window)
    {
        for (var pass = 0; pass < 3; pass++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }

    private static async Task ActivateAsync(ShellWorkspaceController workspace, params ToolActivationRequest[] requests)
    {
        foreach (var request in requests)
        {
            await (Task)ActivateToolAsyncMethod.Invoke(workspace, [request])!;
        }
    }

    /// <summary>
    /// Seeds the target the way a loaded page does: the surface is hosted and registered as the live
    /// activation target for its tool. This runs the same forwarding path production uses, without the
    /// Runner round trip that a page load would need.
    /// </summary>
    private static void SeedOpenSurface(ShellWorkspaceController workspace, string toolId, string routeId, IMptAvaloniaSurfaceActivationHandler handler)
    {
        var targets = (System.Collections.IDictionary)typeof(ShellWorkspaceController)
            .GetField("_activeSurfaceTargets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(workspace)!;
        var targetType = typeof(ShellWorkspaceController).GetNestedType("SurfaceActivationTarget")!;
        targets[toolId] = Activator.CreateInstance(targetType, toolId, routeId, handler)!;
        typeof(ShellWorkspaceController)
            .GetField("_currentToolRouteId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(workspace, routeId);
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

    [AvaloniaFact]
    public async Task Activation_reuses_the_open_surface_and_forwards_every_share()
    {
        var navigations = new List<string>();
        var chrome = CreateChrome(navigations.Add);
        var (shell, workspace, host, window) = CreateShell(chrome, 360);
        var handler = new RecordingActivationHandler();
        var view = new ExternalSdkToolView();
        view.SetManagedSurface(handler);
        host.Content = view;
        var surface = view.ManagedSurface;
        SeedOpenSurface(workspace, "file-transfer", "main", handler);

        try
        {
            var shareA = new ToolActivationRequest("file-transfer", "main", "file:///data/cache/shares/a%20file.txt");
            var shareB = new ToolActivationRequest("file-transfer", "main", "file:///data/cache/shares/b.txt");
            await ActivateAsync(workspace, shareA, shareB).WaitAsync(TimeSpan.FromSeconds(10));

            // The surface that is already on screen received both files and was never rebuilt.
            Assert.Equal(0, SurfaceActivations(workspace));
            Assert.Equal([shareA, shareB], handler.Requests);
            Assert.Same(surface, view.ManagedSurface);
            Assert.Empty(navigations);
            Assert.Equal(5, shell.NavigationButtons.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Activation_for_a_different_tool_does_not_reach_the_open_surface()
    {
        var chrome = CreateChrome(_ => { });
        var (_, workspace, host, window) = CreateShell(chrome, 360);
        var handler = new RecordingActivationHandler();
        var view = new ExternalSdkToolView();
        view.SetManagedSurface(handler);
        host.Content = view;
        SeedOpenSurface(workspace, "file-transfer", "main", handler);

        try
        {
            await ActivateAsync(workspace, new ToolActivationRequest("paste-image", "main", "file:///data/cache/shares/c.png"))
                .WaitAsync(TimeSpan.FromSeconds(10));

            // Without a Runner there is no descriptor for the other tool, so no page can load. The
            // point is that the mismatch never reached the file-transfer surface in the content host.
            Assert.Empty(handler.Requests);
        }
        finally
        {
            window.Close();
        }
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
