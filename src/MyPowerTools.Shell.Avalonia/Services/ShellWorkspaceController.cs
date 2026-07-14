using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MyPowerTools.Runtime;
using MyPowerTools.Shell.Avalonia.Navigation;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.UI.Controls;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController : IAsyncDisposable
{
    private const string HomePage = "Home";
    private const string ToolsPage = "Tools";
    private const string ActivityPage = "Activity";
    private const string SystemPage = "System";
    private const string DashboardPage = "Dashboard";
    private const string ModulesPage = "Modules";
    private const string CommandsPage = "Commands";
    private const string SettingsPage = "Settings";
    private const string LogsPage = "Logs";
    private const string NotificationsPage = "Notifications";
    private const string AdbForwarderPage = "ADB Forwarder";
    private const string ScreenEasePage = "ScreenEase";
    private const string DoubaoAgentPage = "Doubao Agent";
    private const string SmartBirdThermostatPage = "SmartBird";
    private const string PackagesPage = "Packages";
    private const string DiagnosticsPage = "Diagnostics";
    private const string ServicesPage = "Services";

    private readonly MptSearchBox _searchBox;
    private readonly ContentControl _contentHost;
    private readonly ContentControl _commandPanel;
    private readonly ContentControl _permissionPanel;
    private readonly ContentControl _auditPanel;
    private readonly ShellChromeViewModel _chromeViewModel;
    private readonly ShellCommandExecutionService _commandExecutionService = new();
    private readonly ShellHostActionService _hostActions = new();
    private readonly ShellPageDataService _pageData = new();
    private readonly ShellRunnerEventService _runnerEvents = new();
    private readonly ServiceUnitEventStreamMonitor _unitEvents = new(new ServiceManagerUnitEventSource());
    private readonly ShellSettingsService _settingsService = new();
    private readonly ShellAppearanceService _appearance = new();
    private readonly ShellToolProductService _toolProducts = new();
    private readonly ShellNavigationService _navigation = new();
    private readonly ShellWorkspaceIdentity _workspaceIdentity = new();
    private readonly ShellTerminalFaultRecovery _terminalFaultRecovery = new();
    private readonly HashSet<string> _handledFaultInvocations = new(StringComparer.Ordinal);
    private readonly object _handledFaultGate = new();
    private readonly ShellCommandFaultSink _faultSink;
    private readonly EventHandler<TextChangedEventArgs> _searchTextChangedHandler;
    private readonly EventHandler<FocusChangedEventArgs> _searchGotFocusHandler;
    private readonly Action<string> _runnerStatusChangedHandler;
    private readonly Action<string> _runnerStateChangedHandler;
    private readonly Action _runnerRecoveredHandler;
    private readonly Action<HostProto.HostEvent> _hostEventReceivedHandler;
    private readonly EventHandler<MyPowerTools.Protocol.ServiceManager.V1.UnitEvent> _unitEventReceivedHandler;
    private readonly EventHandler<Exception> _unitStreamFaultedHandler;
    private readonly EventHandler _unitStreamRecoveredHandler;
    private CancellationTokenSource? _commandSearchCancellation;
    private CommandPaletteViewModel? _commandPaletteViewModel;
    private long _commandSearchVersion;
    private string _currentPage = HomePage;
    private string _currentToolId = "";
    private string _currentToolRouteId = "";
    private int _disposed;

    public ShellWorkspaceController(
        ShellChromeViewModel chromeViewModel,
        MptSearchBox searchBox,
        ContentControl contentHost,
        ContentControl commandPanel,
        ContentControl permissionPanel,
        ContentControl auditPanel)
    {
        _chromeViewModel = chromeViewModel;
        _searchBox = searchBox;
        _contentHost = contentHost;
        _commandPanel = commandPanel;
        _permissionPanel = permissionPanel;
        _auditPanel = auditPanel;

        _faultSink = new ShellCommandFaultSink(_workspaceIdentity.ControllerId);
        _searchTextChangedHandler = OnSearchTextChanged;
        _searchGotFocusHandler = OnSearchGotFocus;
        _runnerStatusChangedHandler = OnRunnerStatusChanged;
        _runnerStateChangedHandler = OnRunnerStateChanged;
        _runnerRecoveredHandler = OnRunnerRecovered;
        _hostEventReceivedHandler = OnHostEventReceived;
        _unitEventReceivedHandler = OnUnitEventReceived;
        _unitStreamFaultedHandler = OnUnitStreamFaulted;
        _unitStreamRecoveredHandler = OnUnitStreamRecovered;

        _searchBox.TextChanged += _searchTextChangedHandler;
        _searchBox.KeyDown += OnCommandSearchKeyDown;
        _searchBox.GotFocus += _searchGotFocusHandler;
        _runnerEvents.StatusChanged += _runnerStatusChangedHandler;
        _runnerEvents.RunnerStatusChanged += _runnerStateChangedHandler;
        _runnerEvents.RunnerRecovered += _runnerRecoveredHandler;
        _runnerEvents.HostEventReceived += _hostEventReceivedHandler;
        _unitEvents.UnitEventReceived += _unitEventReceivedHandler;
        _unitEvents.StreamFaulted += _unitStreamFaultedHandler;
        _unitEvents.StreamRecovered += _unitStreamRecoveredHandler;
        _faultSink.Faulted += OnShellCommandFaulted;
        AttachCurrentFaultOwner(_chromeViewModel);
    }

    public static IReadOnlyList<string> PageLabels { get; } =
        [
            HomePage,
            ToolsPage,
            ActivityPage,
            NotificationsPage,
            AdbForwarderPage,
            ScreenEasePage,
            DoubaoAgentPage,
            SmartBirdThermostatPage,
            SettingsPage,
            SystemPage
        ];

    public async Task OpenAsync()
    {
        _pageData.StartBackgroundServices();
        _runnerEvents.Start();
        _unitEvents.Start();
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        await _runnerEvents.CheckOnceAsync();
        await RefreshShellDataAsync();
    }

    public async Task ShowPageAsync(string page)
    {
        if (IsDisposed)
        {
            return;
        }

        if (string.Equals(page, DashboardPage, StringComparison.OrdinalIgnoreCase))
        {
            page = HomePage;
        }

        if (string.Equals(page, NotificationsPage, StringComparison.OrdinalIgnoreCase))
        {
            await ShowToolPageAsync(RemoteNotificationsToolId, "inbox");
            return;
        }

        if (string.Equals(page, AdbForwarderPage, StringComparison.OrdinalIgnoreCase))
        {
            await ShowToolPageAsync(AdbForwarderToolId, "forward");
            return;
        }

        if (string.Equals(page, ScreenEasePage, StringComparison.OrdinalIgnoreCase))
        {
            await ShowToolPageAsync(ScreenEaseToolId, "profiles");
            return;
        }

        if (string.Equals(page, DoubaoAgentPage, StringComparison.OrdinalIgnoreCase))
        {
            await ShowToolPageAsync(DoubaoAgentToolId, "services");
            return;
        }

        if (string.Equals(page, SmartBirdThermostatPage, StringComparison.OrdinalIgnoreCase))
        {
            await ShowToolPageAsync(SmartBirdThermostatToolId, "overview");
            return;
        }

        if (string.Equals(page, CommandsPage, StringComparison.OrdinalIgnoreCase))
        {
            _chromeViewModel.SelectPage(CommandsPage);
            await OpenCommandPaletteAsync();
            return;
        }

        BeginWorkspace();
        _currentPage = page;
        _currentToolId = "";
        _currentToolRouteId = "";
        var navigationPage = IsSystemDestination(page) ? SystemPage : page;
        _chromeViewModel.SelectPage(navigationPage);
        _navigation.Navigate(ToShellRoute(page), addToHistory: true);
        if (!string.Equals(page, CommandsPage, StringComparison.OrdinalIgnoreCase))
        {
            _chromeViewModel.IsCommandPaletteOpen = false;
        }

        SetStatus($"Loading {page}");

        switch (page)
        {
            case HomePage:
                await LoadHomePageAsync();
                break;
            case ToolsPage:
                await LoadToolsPageAsync();
                break;
            case ActivityPage:
                LoadActivityPage();
                break;
            case SystemPage:
                LoadSystemPage();
                break;
            case DashboardPage:
                await LoadDashboardPageAsync();
                break;
            case ModulesPage:
                await LoadModulesPageAsync();
                break;
            case CommandsPage:
                await OpenCommandPaletteAsync();
                break;
            case SettingsPage:
                LoadGeneralSettingsPage();
                break;
            case LogsPage:
                await LoadLogsPageAsync();
                break;
            case NotificationsPage:
                await LoadNotificationsPageAsync();
                break;
            case PackagesPage:
                await LoadPackagesPageAsync();
                break;
            case DiagnosticsPage:
                await LoadDiagnosticsPageAsync();
                break;
            case ServicesPage:
                await LoadServicesPageAsync();
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDispose())
        {
            return;
        }

        UnsubscribeShellEvents();
        _pageData.Dispose();
        _commandSearchCancellation?.Cancel();
        _commandSearchCancellation?.Dispose();
        _commandSearchCancellation = null;
        TryDispose(_doubaoAgentTools);
        TryDispose(_smartBirdThermostatTools);
        try
        {
            await _runnerEvents.DisposeAsync();
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Dispose Runner event service", ex, "dispose");
        }

        try
        {
            await _unitEvents.DisposeAsync();
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Dispose ServiceManager unit event monitor", ex, "dispose");
        }
        finally
        {
            DisposeHostedContent();
            _faultSink.Dispose();
        }
    }

    private void SetStatus(string text)
    {
        if (!IsDisposed)
        {
            _chromeViewModel.StatusText = text;
        }
    }

    private void SetRunnerStatus(string text)
    {
        if (!IsDisposed)
        {
            _chromeViewModel.RunnerStatusText = text;
        }
    }

    private async Task RefreshShellDataAsync()
    {
        if (!string.IsNullOrWhiteSpace(_currentToolId))
        {
            await ShowToolPageAsync(_currentToolId, _currentToolRouteId);
        }
        else
        {
            await ShowPageAsync(_currentPage);
        }
        await LoadCommandsAsync(_searchBox.Text ?? "");
        await LoadBrokerAuditAsync();
    }

    private static bool IsSystemDestination(string page)
    {
        return page is ModulesPage or PackagesPage or LogsPage or DiagnosticsPage or ServicesPage;
    }

    private static ShellRoute ToShellRoute(string page)
    {
        return page switch
        {
            HomePage => ShellRoute.Home,
            ToolsPage => ShellRoute.Tools,
            ActivityPage => ShellRoute.Activity,
            NotificationsPage => ShellRoute.Notifications,
            SettingsPage => ShellRoute.Settings,
            SystemPage => ShellRoute.System,
            ModulesPage => ShellRoute.Modules,
            PackagesPage => ShellRoute.Packages,
            LogsPage => ShellRoute.Logs,
            DiagnosticsPage => ShellRoute.RuntimeHealth,
            _ => ShellRoute.System
        };
    }

    private async Task ApplyKeyboardShortcutAsync(ShellKeyboardShortcutResult shortcut)
    {
        switch (shortcut.Action)
        {
            case ShellKeyboardAction.FocusCommandPalette:
                await FocusCommandPaletteAsync();
                break;
            case ShellKeyboardAction.ClearCommandPalette:
                await CloseCommandPaletteAsync();
                break;
            case ShellKeyboardAction.Refresh:
                await RefreshAsync();
                SetStatus($"{_currentPage} refreshed.");
                break;
            case ShellKeyboardAction.Navigate when shortcut.TargetPage is not null:
                await ShowPageAsync(shortcut.TargetPage);
                break;
        }
    }

    private async Task ApplyHostEventAsync(HostProto.HostEvent evt)
    {
        if (string.Equals(evt.SourceId, "android-tools.notifications", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(evt.Type, "message.received", StringComparison.OrdinalIgnoreCase))
        {
            var messageIds = evt.Payload.Fields.TryGetValue("messageIds", out var value)
                ? value.ListValue.Values
                    .Select(item => item.StringValue)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray()
                : [];
            await _pageData.PresentRemoteNotificationsAsync(messageIds);
            if (string.Equals(_currentToolId, RemoteNotificationsToolId, StringComparison.OrdinalIgnoreCase))
            {
                await ShowToolPageAsync(RemoteNotificationsToolId, _currentToolRouteId);
            }
            return;
        }

        var plan = ShellPageRefreshRouter.Route(_currentPage, evt);
        if (plan.ReloadBrokerAudit)
        {
            await LoadBrokerAuditAsync();
        }

        if (plan.ReloadCommands)
        {
            await LoadCommandsAsync(_searchBox.Text ?? "");
        }

        if (plan.ReloadNotifications)
        {
            await LoadNotificationsPageAsync();
        }

        if (plan.ReloadSettingsModuleId is not null)
        {
            await LoadModuleSettingsPageAsync(plan.ReloadSettingsModuleId);
        }

        if (plan.ReloadDiagnostics)
        {
            await LoadDiagnosticsPageAsync();
        }

        if (plan.ReloadCurrentPage)
        {
            await ShowPageAsync(_currentPage);
        }
    }

}
