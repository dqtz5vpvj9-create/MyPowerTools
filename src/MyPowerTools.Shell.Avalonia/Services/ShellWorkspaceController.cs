using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using MyPowerTools.Runtime;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.ServiceManager.Client;
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
    // Tool page labels removed: navigation is now fully dynamic via the Tool Catalog.
    // Individual tools are opened by tool id, not by a hardcoded page label.
    private const string PackagesPage = "Packages";
    private const string DiagnosticsPage = "Diagnostics";
    private const string ServicesPage = "Services";

    private readonly MptSearchBox _searchBox;
    private readonly ContentControl _contentHost;
    private readonly ContentControl _commandPanel;
    private readonly ContentControl _permissionPanel;
    private readonly ContentControl _auditPanel;
    private readonly Panel? _webSurfaceHost;
    private readonly ShellChromeViewModel _chromeViewModel;
    private readonly Lazy<ShellCommandExecutionService> _commandExecutionServiceFactory = new(static () => new());
    private readonly Lazy<ShellHostActionService> _hostActionsFactory = new(static () => new());
    private readonly Lazy<ShellPageDataService> _pageDataFactory = new(static () => new());
    private readonly Lazy<ShellToolEventService> _toolEventsFactory = new(static () => new());
    private readonly Lazy<ShellRunnerEventService> _runnerEventsFactory = new(static () => new());
    private readonly Lazy<ServiceUnitEventStreamMonitor> _unitEventsFactory =
        new(static () => new ServiceUnitEventStreamMonitor(new ServiceManagerUnitEventSource()));
    private readonly Lazy<ServiceManagerAdminClient> _serviceManagerAdminFactory =
        new(ServiceManagerAdminClient.ForDefaultEndpoint);
    private readonly Lazy<DotnetSurfaceLoader> _dotnetSurfaceLoaderFactory = new(static () => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools", "state", "surface-shadow")));
    private readonly Lazy<ShellSettingsService> _settingsServiceFactory = new(static () => new());
   private readonly Lazy<ShellAppearanceService> _appearanceFactory = new(static () => new());
    private readonly Lazy<DevSourceSyncService> _devSourceFactory = new(static () => new());
    private readonly ShellToolProductService _toolProducts = new();
    private readonly ShellNavigationService _navigation = new();
    private readonly ShellWorkspaceIdentity _workspaceIdentity = new();
    private readonly ShellTerminalFaultRecovery _terminalFaultRecovery = new();
    private readonly Lazy<IMptWebSurfaceService?> _webSurfaceServiceFactory;
    private readonly HashSet<string> _handledFaultInvocations = new(StringComparer.Ordinal);
    private readonly object _handledFaultGate = new();
    private readonly Dictionary<string, CachedWebToolPage> _cachedWebTools =
        new(StringComparer.OrdinalIgnoreCase);
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
    private string? _logPageModuleId;
    private string _currentToolId = "";
    private string _currentToolRouteId = "";
    private IReadOnlyList<HostProto.ToolDescriptor>? _startupToolDescriptors;
    private int _eventSubscriptionsAttached;
    private int _eventMonitorsStarted;
    private int _homeLoadDeferred;
    private int _disposed;

    private ShellRunnerEventService _runnerEvents => _runnerEventsFactory.Value;
    private ServiceUnitEventStreamMonitor _unitEvents => _unitEventsFactory.Value;
    private ServiceManagerAdminClient _serviceManagerAdmin => _serviceManagerAdminFactory.Value;
    private DotnetSurfaceLoader _dotnetSurfaceLoader => _dotnetSurfaceLoaderFactory.Value;
    private ShellAppearanceService _appearance => _appearanceFactory.Value;
    private DevSourceSyncService _devSource => _devSourceFactory.Value;
    private ShellCommandExecutionService _commandExecutionService => _commandExecutionServiceFactory.Value;
    private ShellHostActionService _hostActions => _hostActionsFactory.Value;
    private ShellPageDataService _pageData => _pageDataFactory.Value;
    private ShellToolEventService _toolEvents => _toolEventsFactory.Value;
    private ShellSettingsService _settingsService => _settingsServiceFactory.Value;
    private IMptWebSurfaceService? _webSurfaceService => _webSurfaceServiceFactory.Value;

    public ShellWorkspaceController(
        ShellChromeViewModel chromeViewModel,
        MptSearchBox searchBox,
        ContentControl contentHost,
        ContentControl commandPanel,
        ContentControl permissionPanel,
        ContentControl auditPanel,
        Func<IMptWebSurfaceService?>? webSurfaceServiceFactory = null,
        Panel? webSurfaceHost = null)
    {
        _chromeViewModel = chromeViewModel;
        _searchBox = searchBox;
        _contentHost = contentHost;
        _commandPanel = commandPanel;
        _permissionPanel = permissionPanel;
        _auditPanel = auditPanel;
        _webSurfaceHost = webSurfaceHost;
        _webSurfaceServiceFactory = new Lazy<IMptWebSurfaceService?>(
            () => webSurfaceServiceFactory?.Invoke());

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
        _faultSink.Faulted += OnShellCommandFaulted;
        AttachCurrentFaultOwner(_chromeViewModel);
    }

    public static IReadOnlyList<string> PageLabels { get; } =
        [
            HomePage,
            ToolsPage,
            ActivityPage,
            SettingsPage,
            SystemPage
        ];

    public async Task OpenAsync(IReadOnlyList<HostProto.ToolDescriptor>? startupTools = null)
    {
        _startupToolDescriptors = startupTools;
        await RefreshShellDataAsync(includeAuxiliaryData: false);
        CompleteStartup();
    }

    internal void CompleteStartup()
    {
        Dispatcher.UIThread.Post(StartEventMonitors, DispatcherPriority.Background);
        Dispatcher.UIThread.Post(() => _ = CheckLastOtaUpdateAsync(), DispatcherPriority.Background);
    }

    public async Task RefreshAsync()
    {
        EnsureEventSubscriptions();
        await _runnerEvents.CheckOnceAsync();
        await RefreshShellDataAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (!TryBeginDispose())
        {
            return;
        }

        UnsubscribeShellEvents();
        if (_pageDataFactory.IsValueCreated)
        {
            _pageData.Dispose();
        }
        _commandSearchCancellation?.Cancel();
        _commandSearchCancellation?.Dispose();
        _commandSearchCancellation = null;
        try
        {
            if (_runnerEventsFactory.IsValueCreated)
            {
                await _runnerEvents.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Dispose Runner event service", ex, "dispose");
        }

        try
        {
            if (_unitEventsFactory.IsValueCreated)
            {
                await _unitEvents.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Dispose ServiceManager unit event monitor", ex, "dispose");
        }

        try
        {
            if (_dotnetSurfaceLoaderFactory.IsValueCreated)
            {
                _dotnetSurfaceLoader.UnloadAll();
            }
        }
        catch (Exception ex)
        {
            ShellCommandFaultLog.Write("Unload dotnet surfaces", ex, "dispose");
        }
        finally
        {
            if (_serviceManagerAdminFactory.IsValueCreated)
            {
                _serviceManagerAdmin.Dispose();
            }
            DisposeCachedWebTools();
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

    private void ShowInfoBar(InfoBarSeverity severity, string message, string? actionLabel = null, Func<Task>? action = null, int? autoDismissMs = null)
    {
        if (!IsDisposed)
        {
            _chromeViewModel.ShowInfoBar(new InfoBarItem(severity, message, actionLabel, action, autoDismissMs));
        }
    }

    private void SetRunnerStatus(string text)
    {
        if (!IsDisposed)
        {
            _chromeViewModel.RunnerStatusText = text;
        }
    }

    private async Task RefreshShellDataAsync(bool includeAuxiliaryData = true)
    {
        if (!string.IsNullOrWhiteSpace(_currentToolId))
        {
            await ShowToolPageAsync(_currentToolId, _currentToolRouteId);
        }
        else
        {
            await ShowPageAsync(_currentPage);
        }
        if (includeAuxiliaryData)
        {
            await Task.WhenAll(
                LoadCommandsAsync(_searchBox.Text ?? ""),
                LoadBrokerAuditAsync());
        }
    }

    private void StartEventMonitors()
    {
        if (IsDisposed || Interlocked.Exchange(ref _eventMonitorsStarted, 1) != 0)
        {
            return;
        }

        EnsureEventSubscriptions();
        _runnerEvents.Start();
        _unitEvents.Start();
    }

    private void EnsureEventSubscriptions()
    {
        if (IsDisposed || Interlocked.Exchange(ref _eventSubscriptionsAttached, 1) != 0)
        {
            return;
        }

        _runnerEvents.StatusChanged += _runnerStatusChangedHandler;
        _runnerEvents.RunnerStatusChanged += _runnerStateChangedHandler;
        _runnerEvents.RunnerRecovered += _runnerRecoveredHandler;
        _runnerEvents.HostEventReceived += _hostEventReceivedHandler;
        _unitEvents.UnitEventReceived += _unitEventReceivedHandler;
        _unitEvents.StreamFaulted += _unitStreamFaultedHandler;
        _unitEvents.StreamRecovered += _unitStreamRecoveredHandler;
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

        if (plan.ReloadHomeTools)
        {
            // The tool catalog changed at the Runner (tool directory watcher, refresh
            // command): reconcile Home tool cards and the chrome navigation. On the
            // Home page this re-renders Home; elsewhere only the nav updates. The
            // Tools page reloads through ReloadCurrentPage.
            try
            {
                var descriptors = await _toolProducts.LoadToolDescriptorsAsync();
                await ReconcileHomeToolsAsync(descriptors);
            }
            catch (Exception ex)
            {
                ShellCommandFaultLog.Write("Reload tool catalog", ex, "registry.loaded");
            }
        }

        if (plan.ReloadCurrentPage)
        {
            await ShowPageAsync(_currentPage);
        }
    }

}
