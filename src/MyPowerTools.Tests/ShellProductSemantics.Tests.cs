using System.Runtime.CompilerServices;
using Avalonia.Controls;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI.Controls;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class ShellProductSemanticsTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void General_page_contains_only_application_level_preferences()
    {
        var controller = Read("src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.Pages.cs");
        var controllerRoot = Read("src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.cs");
        var general = Read("src", "MyPowerTools.Shell.Avalonia", "Views", "GeneralSettingsView.axaml");

        Assert.Contains("new GeneralSettingsView", controller);
        Assert.Contains("ShellAppearanceService", controllerRoot);
        Assert.Contains("Appearance", general);
        Assert.Contains("Background operation", general);
        Assert.Contains("Keyboard", general);
        Assert.DoesNotContain("Module Settings", general, StringComparison.Ordinal);
        Assert.DoesNotContain("Advanced JSON", general, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsFieldViewModel", general, StringComparison.Ordinal);
    }

    [Fact]
    public void Module_configuration_remains_under_system_maintenance()
    {
        var controller = Read("src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.Pages.cs");
        var moduleSettings = Read("src", "MyPowerTools.Shell.Avalonia", "Views", "SettingsCenterView.axaml");

        Assert.Contains("_currentPage = ModulesPage", controller);
        Assert.Contains("_chromeViewModel.SelectPage(SystemPage)", controller);
        Assert.Contains("LoadModuleSettingsPageAsync", controller);
        Assert.DoesNotContain("Text=\"General\"", moduleSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Packages\"", moduleSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Logs\"", moduleSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"Categories\"", moduleSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void Tray_uses_product_language_and_configured_product_icon()
    {
        var runner = Read("src", "MyPowerTools.Runner", "Program.cs");
        var tray = Read("src", "MyPowerTools.Platform.Windows", "WindowsTrayService.cs");

        Assert.Contains("\"Exit MyPowerTools\"", runner);
        Assert.Contains("\"exit-application\"", runner);
        Assert.DoesNotContain("\"Quit Runner\"", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("\"MyPowerTools Runner\"", runner, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(root, \"assets\", \"MyPowerTools.ico\")", runner);
        Assert.Contains("options.IconPath", tray);
        Assert.Contains("LR_LOADFROMFILE", tray);
    }

    [Fact]
    public void Product_icon_contains_multiple_windows_icon_sizes()
    {
        var icon = File.ReadAllBytes(Path.Combine(Root, "assets", "MyPowerTools.ico"));

        Assert.True(icon.Length > 1_000);
        Assert.Equal(0, BitConverter.ToUInt16(icon, 0));
        Assert.Equal(1, BitConverter.ToUInt16(icon, 2));
        Assert.True(BitConverter.ToUInt16(icon, 4) >= 7);
        Assert.True(File.Exists(Path.Combine(Root, "assets", "MyPowerTools.svg")));
    }

    [Fact]
    public void Shell_wide_layout_uses_neutral_fluid_product_metrics()
    {
        var shell = Read("src", "MyPowerTools.Shell.Avalonia", "Views", "ShellChromeView.axaml");
        var shellCode = Read("src", "MyPowerTools.Shell.Avalonia", "Views", "ShellChromeView.axaml.cs");
        var home = Read("src", "MyPowerTools.Shell.Avalonia", "Views", "HomeView.axaml");
        var mainWindow = Read("src", "MyPowerTools.Shell.Avalonia", "MainWindow.cs");
        var density = Read("src", "MyPowerTools.UI", "Themes", "MptDensity.axaml");

        Assert.Contains("Background=\"{DynamicResource MptBrushAppBackground}\"", shell);
        Assert.Contains("Background=\"{DynamicResource MptBrushShellChrome}\"", shell);
        Assert.Contains("Background=\"{DynamicResource MptBrushNavigationBackground}\"", shell);
        Assert.Contains("MaxWidth=\"{DynamicResource MptLayoutPageMaxWidth}\"", shell);
        Assert.Contains("MptThemeTokens.LayoutTopBarHeight", mainWindow);
        Assert.Contains("ContentMaxWidth", shellCode);

        Assert.Contains("MptLayoutDashboardMaxWidth", home);
        Assert.Contains("ColumnDefinitions=\"5*,3*\"", home);
        Assert.DoesNotContain("MaxWidth=\"1000\"", home, StringComparison.Ordinal);
        Assert.DoesNotContain("ColumnDefinitions=\"568,400\"", home, StringComparison.Ordinal);

        Assert.Contains("MptLayoutSidebarWidth\">240", density);
        Assert.Contains("MptLayoutTopBarHeight\">56", density);
        Assert.Contains("MptLayoutPageMaxWidth\">1760", density);
        Assert.Contains("MptLayoutSearchMaxWidth\">640", density);
    }

    [Fact]
    public void General_uses_a_real_theme_control_and_bounded_responsive_layout()
    {
        var general = Read("src", "MyPowerTools.Shell.Avalonia", "Views", "GeneralSettingsView.axaml");
        var generalCode = Read("src", "MyPowerTools.Shell.Avalonia", "Views", "GeneralSettingsView.axaml.cs");
        var viewModel = Read("src", "MyPowerTools.Shell.Avalonia", "ViewModels", "GeneralSettingsViewModel.cs");

        Assert.Contains("MptLayoutSettingsMaxWidth", general);
        Assert.DoesNotContain("Margin=\"{DynamicResource MptPagePadding}\"", general, StringComparison.Ordinal);
        Assert.Contains("controls:MptComboBox", general);
        Assert.Contains("SelectedItem=\"{Binding SelectedTheme, Mode=TwoWay}\"", general);
        Assert.Contains("ThemeApplyStatus", general);
        Assert.DoesNotContain("ItemsControl ItemsSource=\"{Binding Themes}\"", general, StringComparison.Ordinal);
        // MptSettingsField is a real theme control (MyPowerTools.UI/Controls/MptSettingsField.axaml,
        // included by MptThemeDeferred): the settings page must use it, not ad-hoc styling.
        Assert.Contains("Classes=\"MptSettingsField\"", general, StringComparison.Ordinal);
        Assert.Contains("Available while running", general);
        Assert.Contains("TwoColumnMinWidth", generalCode);
        Assert.Contains("ApplyThemeAsync", viewModel);
    }

    [Fact]
    public async Task General_theme_selection_applies_the_selected_application_theme()
    {
        var applied = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var viewModel = new GeneralSettingsViewModel(
            ShellAppearanceService.SystemTheme,
            theme =>
            {
                applied.TrySetResult(theme);
                return Task.CompletedTask;
            },
            () => Task.CompletedTask);

        viewModel.SelectedTheme = Assert.Single(
            viewModel.Themes,
            theme => theme.Id == ShellAppearanceService.DarkTheme);

        Assert.Equal(
            ShellAppearanceService.DarkTheme,
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Dark applied.", viewModel.ThemeApplyStatus);
    }

    [Fact]
    public async Task Async_relay_command_contains_recoverable_exceptions_and_reenables_itself()
    {
        var observed = new TaskCompletionSource<ShellCommandFaultEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncRelayCommand(
            () => Task.FromException(new InvalidOperationException("synthetic shell command fault")),
            operationName: "fault-injection command");
        command.ExecutionFailed += (_, fault) => observed.TrySetResult(fault);

        command.Execute(null);
        var result = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var attempt = 0; attempt < 50 && !command.CanExecute(null); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("fault-injection command", result.Operation);
        Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.Contains("synthetic shell command fault", result.Exception.Message);
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task Controller_scoped_fault_sinks_do_not_cross_update_workspaces()
    {
        var firstIdentity = new ShellWorkspaceIdentity("controller-a");
        var secondIdentity = new ShellWorkspaceIdentity("controller-b");
        var firstSink = new ShellCommandFaultSink(firstIdentity.ControllerId);
        var secondSink = new ShellCommandFaultSink(secondIdentity.ControllerId);
        var firstObserved = new TaskCompletionSource<ShellCommandFaultEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCount = 0;
        firstSink.Faulted += (_, fault) => firstObserved.TrySetResult(fault);
        secondSink.Faulted += (_, _) => Interlocked.Increment(ref secondCount);
        var firstViewModel = new UnavailablePageViewModel(
            "first",
            "fault injection",
            retry: () => Task.FromException(new InvalidOperationException("first workspace fault")));
        var secondViewModel = new UnavailablePageViewModel(
            "second",
            "fault injection",
            retry: () => Task.FromException(new InvalidOperationException("second workspace fault")));
        ShellCommandFaultOwnership.Attach(firstViewModel, firstSink, firstIdentity.Capture());
        ShellCommandFaultOwnership.Attach(secondViewModel, secondSink, secondIdentity.Capture());

        firstViewModel.RetryCommand.Execute(null);
        var observed = await firstObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("controller-a", observed.Context.ControllerId);
        Assert.False(string.IsNullOrWhiteSpace(observed.Context.WorkspaceId));
        Assert.False(string.IsNullOrWhiteSpace(observed.Context.InvocationId));
        Assert.Equal(0, Volatile.Read(ref secondCount));
    }

    [Fact]
    public void Navigation_identity_rejects_same_route_ABA_callbacks()
    {
        var identity = new ShellWorkspaceIdentity("controller-aba");
        const string repeatedRoute = "adb-forwarder/rules";
        var firstVisit = identity.BeginNavigation().BeginInvocation();
        var firstRoute = repeatedRoute;
        _ = identity.BeginNavigation();
        const string intermediateRoute = "home";
        var secondVisit = identity.BeginNavigation().BeginInvocation();
        var secondRoute = repeatedRoute;

        Assert.Equal(firstRoute, secondRoute);
        Assert.NotEqual(intermediateRoute, secondRoute);
        Assert.NotEqual(firstVisit.WorkspaceId, secondVisit.WorkspaceId);
        Assert.True(secondVisit.NavigationGeneration > firstVisit.NavigationGeneration);
        Assert.False(identity.IsCurrent(firstVisit));
        Assert.True(identity.IsCurrent(secondVisit));
    }

    [Fact]
    public void Terminal_recovery_contains_primary_and_fallback_render_failures()
    {
        var recovery = new ShellTerminalFaultRecovery();
        var primaryAttempts = 0;
        var fallbackAttempts = 0;

        var escaped = Record.Exception(() => recovery.TryRecover(
            () => true,
            () =>
            {
                primaryAttempts++;
                throw new InvalidOperationException("primary recovery render failed");
            },
            () =>
            {
                fallbackAttempts++;
                throw new InvalidOperationException("terminal fallback render failed");
            }));
        var recursiveAttempt = recovery.TryRecover(
            () => true,
            () => primaryAttempts++,
            () => fallbackAttempts++);

        Assert.Null(escaped);
        Assert.Equal(1, primaryAttempts);
        Assert.Equal(1, fallbackAttempts);
        Assert.False(recursiveAttempt);
    }

    [Fact]
    public async Task Async_relay_command_allows_only_one_concurrent_execution()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var command = new AsyncRelayCommand(async () =>
        {
            Interlocked.Increment(ref executions);
            entered.TrySetResult();
            await release.Task;
            completed.TrySetResult();
        });

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => command.Execute(null))));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.TrySetResult();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref executions));
        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task Disposed_runner_service_drops_a_late_probe_callback()
    {
        var probe = new BlockingConnectionProbe();
        var connectionMonitor = new HostControlConnectionMonitor(
            probe,
            pollInterval: TimeSpan.FromMinutes(1),
            attemptTimeout: TimeSpan.FromMinutes(1));
        var eventMonitor = new HostControlEventStreamMonitor(new EmptyHostEventSource());
        var service = new ShellRunnerEventService(connectionMonitor, eventMonitor);
        var statusEvents = 0;
        service.StatusChanged += _ => Interlocked.Increment(ref statusEvents);
        service.RunnerStatusChanged += _ => Interlocked.Increment(ref statusEvents);

        var pending = service.CheckOnceAsync();
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.DisposeAsync();
        probe.Complete();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Volatile.Read(ref statusEvents));
    }

    [Fact]
    public async Task Disposed_workspace_ignores_late_search_focus()
    {
        ShellWorkspaceController? workspace = null;
        var chrome = new ShellChromeViewModel(
            ShellWorkspaceController.PageLabels,
            page => workspace?.ShowPageAsync(page) ?? Task.CompletedTask,
            () => workspace?.RefreshAsync() ?? Task.CompletedTask,
            () => workspace?.OpenCommandPaletteAsync() ?? Task.CompletedTask,
            () => workspace?.CloseCommandPaletteAsync() ?? Task.CompletedTask,
            () => workspace?.DismissPermissionPromptAsync() ?? Task.CompletedTask);
        workspace = new ShellWorkspaceController(
            chrome,
            new MptSearchBox(),
            new ContentControl(),
            new ContentControl(),
            new ContentControl(),
            new ContentControl());

        await workspace.DisposeAsync();
        workspace.OnSearchGotFocus(null, null);

        Assert.True(workspace.IsDisposed);
        Assert.False(chrome.IsCommandPaletteOpen);
    }

    [Fact]
    public void Discovered_tools_are_projected_into_shell_navigation()
    {
        var chrome = new ShellChromeViewModel(ShellWorkspaceController.PageLabels);
        var ready = new ToolCardViewModel(
            "sample.ready",
            "示例工具",
            "Ready sample",
            "Test",
            "ST",
            "Ready",
            "Ready",
            ToolAvailability.Available,
            false);
        var paused = new ToolCardViewModel(
            "sample.paused",
            "暂停工具",
            "Paused sample",
            "Test",
            "PT",
            "Paused",
            "Paused",
            ToolAvailability.Paused,
            false);

        chrome.SetDiscoveredTools([ready, paused], _ => Task.CompletedTask);

        Assert.Collection(
            chrome.ToolNavigationItems,
            item => Assert.Equal("All tools", item.DisplayLabel),
            item =>
            {
                Assert.Equal("示例工具", item.DisplayLabel);
                Assert.True(item.IsMonogram);
                Assert.True(item.IsEnabled);
            },
            item =>
            {
                Assert.Equal("暂停工具", item.DisplayLabel);
                Assert.False(item.IsEnabled);
            });

        chrome.SelectTool("sample.ready");
        Assert.True(chrome.ToolNavigationItems[1].IsSelected);
        Assert.False(chrome.ToolNavigationItems[0].IsSelected);
    }

    [Fact]
    public void Home_keeps_full_catalog_data_with_a_bounded_first_screen_projection()
    {
        var tools = Enumerable.Range(1, 9)
            .Select(index => new ToolCardViewModel(
                $"sample.{index}",
                $"Sample {index}",
                "Sample tool",
                "Test",
                $"S{index}",
                "Ready",
                "Ready",
                ToolAvailability.Available,
                false))
            .ToArray();

        var home = new HomeViewModel([], tools, [], tools.Length);

        Assert.Equal(9, home.AllTools.Count);
        Assert.Equal(5, home.DashboardTools.Count);
        Assert.Equal(3, home.ActionableTools.Count);
        Assert.Equal(2, home.QuickAccessActions.Count);
        Assert.Equal(9, home.ReadyToolCount);
        Assert.Equal("9 tools registered", home.ToolCountSummary);
    }

    [Fact]
    public void Fault_diagnostics_are_redacted_single_line_and_bounded()
    {
        var line = ShellCommandFaultLog.Format(
            "subscriber\r\noperation",
            new InvalidOperationException("Authorization: Bearer top-secret-token\nsecond line"),
            "subscriber");

        Assert.DoesNotContain("top-secret-token", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
        Assert.True(line.Length <= 1024);
    }

    private static string Read(params string[] segments)
    {
        return File.ReadAllText(Path.Combine([Root, .. segments]));
    }

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

        throw new DirectoryNotFoundException("MyPowerTools repository root was not found.");
    }

    private sealed class BlockingConnectionProbe : IHostControlConnectionProbe
    {
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<HostControlConnectionProbeResult> PingAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return new HostControlConnectionProbeResult("test", "running");
        }

        public void Complete()
        {
            _release.TrySetResult();
        }
    }

    private sealed class EmptyHostEventSource : IHostControlEventSource
    {
        public async IAsyncEnumerable<HostProto.HostEvent> SubscribeAsync(
            ulong lastEventSeq,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    [Fact]
    public void Global_hotkey_overview_maps_registration_states_like_powertoys()
    {
        var active = new GlobalHotkeyViewModel(
            "screenease", "screenease.toggle", "screenease.toggle", "Ctrl+Alt+S", "registered", "", true, "Ctrl+Alt+S");
        var conflict = new GlobalHotkeyViewModel(
            "runner", "command-palette", "shell.command-palette.open", "Ctrl+Alt+Space", "conflict", "Gesture is used twice.", false, "Ctrl+Alt+Space");
        var disabled = new GlobalHotkeyViewModel(
            "paste-image", "paste-image.upload", "paste-image.upload", "", "disabled", "", true, "Ctrl+Alt+V");

        Assert.True(active.IsRegistered);
        Assert.Equal("Active", active.StateLabel);
        Assert.True(conflict.IsConflict);
        Assert.Equal("Conflict", conflict.StateLabel);
        Assert.True(conflict.HasMessage);
        Assert.True(disabled.IsDisabled);
        Assert.Equal("Disabled", disabled.StateLabel);
        Assert.Equal("(unassigned)", disabled.Gesture);

        var viewModel = new GeneralSettingsViewModel(
            "system",
            theme => Task.CompletedTask,
            () => Task.CompletedTask,
            null,
            [active, conflict, disabled]);

        Assert.True(viewModel.HasGlobalHotkeys);
        Assert.Equal(3, viewModel.GlobalHotkeys.Count);
        Assert.Contains("1 conflict", viewModel.GlobalHotkeyStatusText);
    }

}
