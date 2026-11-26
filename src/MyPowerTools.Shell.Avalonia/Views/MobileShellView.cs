using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.Services.Mobile;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels.Mobile;
using MyPowerTools.Shell.Avalonia.Views.Mobile;
using MyPowerTools.UI.Controls;

namespace MyPowerTools.Shell.Avalonia.Views;

/// <summary>
/// The phone shell: 常用 / 工具 / 设备 / 动态 with the 设置 page behind the avatar, a real back stack,
/// one bottom sheet, and the shared workspace underneath for tool surfaces, permission prompts and
/// share activations. Every phone page is built by this shell, so the phone never squeezes a desktop
/// panel into a narrow window.
/// </summary>
public sealed class MobileShellView : UserControl, IAsyncDisposable, IMobileNavigator
{
    private ShellWorkspaceController _workspace = null!;
    private ShellChromeViewModel _chrome = null!;
    private MptSearchBox _search = null!;
    private ContentControl _pageHost = null!;
    private ContentControl _commandHost = null!;
    private ContentControl _permissionHost = null!;
    private ContentControl _auditHost = null!;
    private MobileShellServices _services = null!;
    private MobileShellViewModel _viewModel = null!;
    private ContentControl _mobileHost = null!;
    private Border _workspaceFrame = null!;
    private Border _tabBar = null!;
    private ContentControl _sheetHost = null!;
    private Grid _sheetLayer = null!;
    private TextBlock _sheetTitle = null!;
    private TextBlock _sheetSubtitle = null!;
    private int _sheetGeneration;
    private MyPowerTools.AvaloniaSdk.Controls.MptQrCode? _pairingQr;
    private Func<CancellationToken, Task<string?>>? _scanConnectionCodeAsync;
    private Border _toast = null!;
    private TextBlock _toastText = null!;
    private DispatcherTimer _toastTimer = null!;
    private readonly List<Button> _navigationButtons = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IInputElement? _focusBeforeSheet;
    private InputElement? _lastFocusedOutsideSheet;
    private Task _pageLoad = Task.CompletedTask;
    private bool _opened;

    public MobileShellView()
    {
        // The chrome callbacks read the _workspace field, which Initialize assigns below; without them
        // every touch-bar tap, refresh and overlay command on the phone would be a silent no-op.
        var chrome = new ShellChromeViewModel(
            ShellWorkspaceController.PageLabels,
            page => _workspace?.ShowPageAsync(page) ?? Task.CompletedTask,
            () => _workspace?.RefreshAsync() ?? Task.CompletedTask,
            () => _workspace?.OpenCommandPaletteAsync() ?? Task.CompletedTask,
            () => _workspace?.CloseCommandPaletteAsync() ?? Task.CompletedTask,
            () => _workspace?.DismissPermissionPromptAsync() ?? Task.CompletedTask,
            runtimeModeLabel: "ANDROID",
            runtimeIdentityText: "MPT Android");
        var search = new MptSearchBox { PlaceholderText = "搜索工具与命令", MinHeight = 48 };
        // The workspace and the visual tree must share the exact same hosts: a page written into a
        // control that is not in this tree renders nothing.
        var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var commandHost = new ContentControl();
        var permissionHost = new ContentControl();
        var auditHost = new ContentControl();
        var workspace = new ShellWorkspaceController(chrome, search, pageHost, commandHost, permissionHost, auditHost);
        Initialize(
            workspace,
            chrome,
            search,
            pageHost,
            commandHost,
            permissionHost,
            auditHost,
            MobileShellServices.CreateDefault(workspace));
    }

    /// <summary>
    /// Builds the phone shell over the supplied workspace. The caller owns the workspace and can
    /// therefore drive the shell in tests without starting a Runner.
    /// </summary>
    internal MobileShellView(
        ShellWorkspaceController workspace,
        ShellChromeViewModel chrome,
        MptSearchBox search)
        : this(
            workspace,
            chrome,
            search,
            workspace.MobilePageHost,
            workspace.MobileCommandPanel,
            workspace.MobilePermissionPanel,
            workspace.MobileAuditPanel,
            MobileShellServices.CreateDefault(workspace))
    {
    }

    /// <summary>Test constructor: a caller-supplied service bundle with temporary preference paths.</summary>
    internal MobileShellView(
        ShellWorkspaceController workspace,
        ShellChromeViewModel chrome,
        MptSearchBox search,
        MobileShellServices services)
        : this(
            workspace,
            chrome,
            search,
            workspace.MobilePageHost,
            workspace.MobileCommandPanel,
            workspace.MobilePermissionPanel,
            workspace.MobileAuditPanel,
            services)
    {
    }

    private MobileShellView(
        ShellWorkspaceController workspace,
        ShellChromeViewModel chrome,
        MptSearchBox search,
        ContentControl pageHost,
        ContentControl commands,
        ContentControl permissions,
        ContentControl audit,
        MobileShellServices services)
    {
        Initialize(workspace, chrome, search, pageHost, commands, permissions, audit, services);
    }

    private void Initialize(
        ShellWorkspaceController workspace,
        ShellChromeViewModel chrome,
        MptSearchBox search,
        ContentControl pageHost,
        ContentControl commands,
        ContentControl permissions,
        ContentControl audit,
        MobileShellServices services)
    {
        _workspace = workspace;
        _chrome = chrome;
        _search = search;
        _pageHost = pageHost;
        _commandHost = commands;
        _permissionHost = permissions;
        _auditHost = audit;
        _services = services;
        _viewModel = new MobileShellViewModel(services, this);

        Classes.Add("mobile-shell");
        Classes.Add("MptMobileRoot");
        MobileThemeFallback.Apply(this);
        Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.Shell.Avalonia/"))
        { Source = new Uri("avares://MyPowerTools.Shell.Avalonia/Styles/Mobile.axaml") });
        this.Bind(BackgroundProperty, new DynamicResourceExtension("MptMobileBackgroundBrush"));

        _mobileHost = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        _pageHost.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _pageHost.VerticalContentAlignment = VerticalAlignment.Stretch;
        // The workspace writes _contentHost.IsVisible itself (web-tool hosting), so the phone shell
        // controls a wrapper instead: exactly one of the phone pages and the tool surface is on screen.
        _workspaceFrame = new Border { Child = _pageHost, IsVisible = false };
        _workspaceFrame.Classes.Add("MptMobileWorkspaceFrame");
        _workspaceFrame.Bind(IsVisibleProperty, new Binding(nameof(MobileShellViewModel.IsToolSurfaceOpen)) { Source = _viewModel });
        _mobileHost.Bind(IsVisibleProperty, new Binding(nameof(MobileShellViewModel.IsPhoneContentVisible)) { Source = _viewModel });

        var contentArea = new Grid { Children = { _mobileHost, _workspaceFrame } };
        var infoBars = BuildInfoBars(_chrome);
        _tabBar = BuildTabBar(_viewModel, this);

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(infoBars, 0);
        layout.Children.Add(infoBars);
        Grid.SetRow(contentArea, 1);
        layout.Children.Add(contentArea);
        Grid.SetRow(_tabBar, 2);
        layout.Children.Add(_tabBar);

        _toastText = MobileElements.Text("", "MptMobileToastText");
        _toast = new Border { Child = _toastText, IsVisible = false };
        _toast.Classes.Add("MptMobileToast");
        _toast.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileTextBrush"));
        _toastText.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileCardBrush"));
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast.IsVisible = false;
        };

        var commandOverlay = BuildCommandPalette(_chrome, search, commands);
        var permissionLayer = BuildPermissionLayer(chrome, permissions, audit);
        _sheetLayer = BuildSheetLayer(out _sheetHost, out _sheetTitle, out _sheetSubtitle);

        var root = new Grid { Children = { layout, commandOverlay, permissionLayer, _sheetLayer, _toast } };
        Content = root;

        _viewModel.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(MobileShellViewModel.CurrentPageKey):
                case nameof(MobileShellViewModel.CurrentArgument):
                    RebuildPage();
                    break;
                case nameof(MobileShellViewModel.SheetKey):
                    RebuildSheet();
                    break;
                case nameof(MobileShellViewModel.SelectedTabKey):
                    UpdateTabSelection();
                    break;
                case nameof(MobileShellViewModel.IsToolSurfaceOpen):
                    _workspaceFrame.IsVisible = _viewModel.IsToolSurfaceOpen;
                    break;
            }
        };
        foreach (var option in _viewModel.Settings.ThemeOptions)
        {
            option.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MobileThemeOptionViewModel.IsSelected) &&
                    string.Equals(_viewModel.SheetKey, MobileSheetKeys.Appearance, StringComparison.Ordinal))
                {
                    RebuildSheet();
                }
            };
        }

        // Focus restore for the bottom sheet: the focus manager can already report "nothing" by the time
        // a command runs (the platform clears focus when the sheet layer is prepared), so the shell also
        // remembers the last element that took focus outside the sheet.
        AddHandler(GotFocusEvent, OnAnyGotFocus, RoutingStrategies.Bubble);
        SizeChanged += (_, e) => Resources["MptLayoutPackageCardWidth"] = Math.Max(0, e.NewSize.Width - 32);
        AttachedToVisualTree += OnAttached;
        RebuildPage();
        UpdateTabSelection();
    }

    /// <summary>
    /// Completes once the workspace finished its initial open and the shell can accept activations.
    /// Hosts that deliver an activation from outside the UI (the Android share intent) should await
    /// this before calling <see cref="ActivateAsync"/>.
    /// </summary>
    public Task Ready => _ready.Task;

    /// <summary>The touch bar buttons in page order, for hosts that need to address the real instances.</summary>
    internal IReadOnlyList<Button> NavigationButtons => _navigationButtons;

    /// <summary>The host that receives the current workspace page. Also rendered by this shell.</summary>
    internal ContentControl PageHost => _pageHost;

    /// <summary>The phone content host, for tests that assert which phone page is up.</summary>
    internal ContentControl PhoneHost => _mobileHost;

    /// <summary>The phone page key currently shown.</summary>
    internal string CurrentPhonePageKey => _viewModel.CurrentPageKey;

    /// <summary>The phone navigation model.</summary>
    internal MobileShellViewModel ViewModel => _viewModel;

    /// <summary>The service bundle the shell was built with.</summary>
    internal MobileShellServices Services => _services;

    /// <summary>True while a bottom sheet is open; back must close it first.</summary>
    internal bool IsSheetOpen => _viewModel.IsSheetOpen;

    /// <summary>The element that had focus when the open sheet was requested (tests and diagnostics).</summary>
    internal IInputElement? FocusBeforeSheet => _focusBeforeSheet;

    /// <summary>What the last sheet open/close did about focus (tests and diagnostics).</summary>
    internal string FocusDiagnostics { get; private set; } = "";

    /// <summary>Last message shown by the toast, for hosts and tests.</summary>
    internal string LastToastMessage => _toastText.Text ?? "";

    /// <summary>Settles when the current phone page finished its data load.</summary>
    internal Task PageLoad => _pageLoad;

    /// <summary>
    /// Forwards the platform's native surface capabilities (camera scan, file open) to the shared
    /// workspace before any tool surface loads. The Android entry point calls this immediately after
    /// constructing the shell and before delivering activations.
    /// </summary>
    public void SetNativeSurfaceServices(
        Func<CancellationToken, Task<string?>>? scanConnectionCodeAsync,
        Func<string, CancellationToken, Task<bool>>? openFileAsync,
        Func<string, CancellationToken, Task<MyPowerTools.AvaloniaSdk.MptCloudAuthorizationResult?>>? authorizeCloudAccountAsync = null)
    {
        _scanConnectionCodeAsync = scanConnectionCodeAsync;
        _workspace.SetNativeSurfaceServices(scanConnectionCodeAsync, openFileAsync, authorizeCloudAccountAsync);
    }

    public async Task ActivateAsync(ToolActivationRequest request)
    {
        await Ready.ConfigureAwait(true);
        await _workspace.ActivateToolAsync(request).ConfigureAwait(true);
        // A share or deep link must land on the real surface, not behind the phone pages.
        _viewModel.IsToolSurfaceOpen = true;
    }

    /// <summary>
    /// Android back: sheet, then permission prompt, then command palette, then the open tool surface,
    /// then the phone back stack, then the 常用 tab. Only from 常用 does the app background.
    /// </summary>
    public async Task<bool> HandleBackAsync()
    {
        if (_viewModel.IsSheetOpen)
        {
            await CloseSheetAsync().ConfigureAwait(true);
            return true;
        }

        if (_chrome.IsPermissionPromptOpen)
        {
            await _workspace.DismissPermissionPromptAsync().ConfigureAwait(true);
            return true;
        }

        if (_chrome.IsCommandPaletteOpen)
        {
            await _workspace.CloseCommandPaletteAsync().ConfigureAwait(true);
            return true;
        }

        if (_viewModel.IsToolSurfaceOpen)
        {
            // A sheet, form or previous step inside the loaded surface gets Back first.
            if (TryHandleSurfaceBack())
            {
                return true;
            }

            _workspace.CloseToolSurface();
            _viewModel.IsToolSurfaceOpen = false;
            return true;
        }

        if (_viewModel.GoBack())
        {
            return true;
        }

        if (!string.Equals(_viewModel.CurrentPageKey, MobilePageKeys.Home, StringComparison.Ordinal))
        {
            _viewModel.NavigateRoot(MobilePageKeys.Home);
            return true;
        }

        return false;
    }

    // ---- IMobileNavigator -------------------------------------------------------------------

    public async Task OpenToolSurfaceAsync(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return;
        }

        var implementationId = _viewModel.Tools.Find(toolId)?.ImplementationId ?? toolId;
        await _workspace.OpenToolSurfaceAsync(implementationId).ConfigureAwait(true);
        _viewModel.IsToolSurfaceOpen = true;
    }

    public async Task ActivateToolAsync(string toolId, string routeId, string activationUri)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return;
        }

        await ActivateAsync(new ToolActivationRequest(
            toolId,
            routeId ?? "",
            string.IsNullOrWhiteSpace(activationUri) ? null : activationUri)).ConfigureAwait(true);
    }

    public async Task ShowToolDetailAsync(string toolId)
    {
        await _viewModel.Tools.LoadAsync().ConfigureAwait(true);
        var item = _viewModel.Tools.Find(toolId);
        IReadOnlyList<MobileControlDevice>? controlDevices = null;
        if (item is { IsComputerTool: true })
        {
            await _viewModel.Devices.LoadControlDevicesAsync().ConfigureAwait(true);
            controlDevices = _viewModel.Devices.ControlDevices.Select(entry => entry.Device).ToArray();
        }
        _viewModel.SetToolDetail(item is null ? null : new MobileToolDetailViewModel(item, this, controlDevices: controlDevices));
        _viewModel.Navigate(MobilePageKeys.ToolDetail, toolId);
    }

    /// <summary>
    /// Offers Back to the loaded surface page. The loaded control (or the surface it hosts) may
    /// implement <see cref="IMptAvaloniaSurfaceBackHandler"/>; nothing is reflected.
    /// </summary>
    private bool TryHandleSurfaceBack()
    {
        var handler = _pageHost.Content switch
        {
            IMptAvaloniaSurfaceBackHandler direct => direct,
            ExternalSdkToolView { ManagedSurface: IMptAvaloniaSurfaceBackHandler hosted } => hosted,
            _ => null
        };
        if (handler is null)
        {
            return false;
        }

        try
        {
            return handler.TryHandleBack();
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
            return false;
        }
    }

    public async Task ShowDeviceDetailAsync(string deviceId)
    {
        await _viewModel.Devices.LoadAsync().ConfigureAwait(true);
        var peer = _viewModel.Devices.FindPeer(deviceId);
        _viewModel.SetDeviceDetail(peer is null ? null : new MobileDeviceDetailViewModel(peer, this));
        _viewModel.Navigate(MobilePageKeys.DeviceDetail, deviceId);
    }

    public Task ShowPageAsync(string pageKey)
    {
        _viewModel.Navigate(pageKey, null);
        return Task.CompletedTask;
    }

    public Task ShowRootPageAsync(string pageKey)
    {
        _viewModel.NavigateRoot(pageKey);
        return Task.CompletedTask;
    }

    public Task GoBackPageAsync()
    {
        _viewModel.GoBack();
        return Task.CompletedTask;
    }

    public Task ShowSheetAsync(string sheetKey, string? argument = null)
    {
        _focusBeforeSheet = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as InputElement
            ?? _lastFocusedOutsideSheet;
        FocusDiagnostics = $"captured={_focusBeforeSheet?.GetType().Name ?? "null"}";
        _toastTimer.Stop();
        _toast.IsVisible = false;
        _viewModel.OpenSheet(sheetKey, argument);
        return Task.CompletedTask;
    }

    public Task CloseSheetAsync()
    {
        _viewModel.CloseSheet();
        var restore = _focusBeforeSheet;
        _focusBeforeSheet = null;
        if (restore is InputElement { Focusable: true } element)
        {
            // Posted so the restore happens after the sheet layer and the page below it have settled.
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (element.IsAttachedToVisualTree() && element.Focus())
                    {
                        _lastFocusedOutsideSheet = element;
                    }
                },
                DispatcherPriority.Input);
            FocusDiagnostics += " restored=posted";
        }
        else
        {
            FocusDiagnostics += " restored=skipped";
        }

        return Task.CompletedTask;
    }

    private void OnAnyGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (e.Source is InputElement element && !IsInsideSheet(element))
        {
            _lastFocusedOutsideSheet = element;
        }
    }

    private bool IsInsideSheet(Visual element)
    {
        for (Visual? current = element; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, _sheetLayer))
            {
                return true;
            }
        }

        return false;
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _toastText.Text = message;
        if (_viewModel.IsSheetOpen)
        {
            _toast.VerticalAlignment = VerticalAlignment.Top;
            _toast.Margin = new Thickness(20, 12, 20, 0);
        }
        else
        {
            _toast.ClearValue(VerticalAlignmentProperty);
            _toast.ClearValue(MarginProperty);
        }
        _toast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public Task RefreshAsync() => ReloadCurrentPageAsync(force: true);

    internal Task ShowPageFromChromeAsync(string page)
    {
        var mapped = MapChromePage(page);
        if (MobilePageKeys.Tabs.Contains(mapped, StringComparer.Ordinal))
        {
            _viewModel.NavigateRoot(mapped);
        }
        else
        {
            _viewModel.Navigate(mapped, null);
        }

        return PageLoad;
    }

    internal Task RefreshFromChromeAsync() => RefreshAsync();

    /// <summary>The desktop chrome still asks for its own page keys; map the ones with a phone page.</summary>
    private static string MapChromePage(string page) => page switch
    {
        "Home" => MobilePageKeys.Home,
        "Tools" => MobilePageKeys.Tools,
        "Activity" => MobilePageKeys.Activity,
        "Settings" => MobilePageKeys.Settings,
        _ => MobilePageKeys.Home
    };

    public async ValueTask DisposeAsync()
    {
        _toastTimer.Stop();
        await _workspace.DisposeAsync().ConfigureAwait(true);
    }

    // ---- visual construction ----------------------------------------------------------------

    private async void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_opened)
        {
            return;
        }

        _opened = true;
        try
        {
            await _workspace.OpenForMobileShellAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _mobileHost.Content = MobileElements.EmptyState("无法加载工作区", ex.Message, null, null);
        }
        finally
        {
            // Published after the initial open, not after construction: OpenAsync navigates and starts
            // a new workspace generation, so an activation that arrived before it finished would be
            // discarded and the user would land back on the phone home page.
            _ready.TrySetResult();
        }

        await ReloadCurrentPageAsync().ConfigureAwait(true);
    }

    private void RebuildPage()
    {
        var page = _viewModel.CurrentPageKey switch
        {
            MobilePageKeys.Home => (Control)new MobileHomeView(_viewModel.Home),
            MobilePageKeys.Tools => new MobileToolsView(_viewModel.Tools),
            MobilePageKeys.Devices => new MobileDevicesView(_viewModel.Devices, this),
            MobilePageKeys.Activity => new MobileActivityView(_viewModel.Activity),
            MobilePageKeys.Settings => new MobileSettingsView(_viewModel.Settings, this),
            MobilePageKeys.ToolDetail when _viewModel.ToolDetail is { } detail => new MobileToolDetailView(detail, this),
            MobilePageKeys.DeviceDetail when _viewModel.DeviceDetail is { } detail => new MobileDeviceDetailView(detail, this),
            _ => (Control)MobileElements.EmptyState("页面不存在", "返回上一页继续。", "返回", new AsyncRelayCommand(GoBackPageAsync))
        };
        _mobileHost.Content = page;
        _pageLoad = ReloadCurrentPageAsync();
    }

    private Task ReloadCurrentPageAsync(bool force = false)
    {
        var load = LoadCurrentPageAsync(_viewModel.CurrentPageKey, force);
        _pageLoad = load;
        return load;
    }

    private async Task LoadCurrentPageAsync(string pageKey, bool force)
    {
        try
        {
            switch (pageKey)
            {
                case MobilePageKeys.Home:
                    await _viewModel.Home.LoadAsync().ConfigureAwait(true);
                    break;
                case MobilePageKeys.Tools:
                    await _viewModel.Tools.LoadAsync(force).ConfigureAwait(true);
                    break;
                case MobilePageKeys.Devices:
                    await _viewModel.Devices.LoadAsync(force).ConfigureAwait(true);
                    await _viewModel.Devices.LoadControlDevicesAsync(force).ConfigureAwait(true);
                    break;
                case MobilePageKeys.Activity:
                    await _viewModel.Activity.LoadAsync(force).ConfigureAwait(true);
                    break;
                case MobilePageKeys.Settings:
                    await _viewModel.Settings.LoadAsync(force).ConfigureAwait(true);
                    break;
                case MobilePageKeys.ToolDetail:
                    await _viewModel.Tools.LoadAsync(force).ConfigureAwait(true);
                    break;
                case MobilePageKeys.DeviceDetail:
                    await _viewModel.Devices.LoadAsync(force).ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message);
        }
    }

    private static Border BuildTabBar(MobileShellViewModel viewModel, MobileShellView owner)
    {
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*") };
        bar.Classes.Add("MptMobileTabBarRow");
        for (var index = 0; index < viewModel.Tabs.Count; index++)
        {
            var tab = viewModel.Tabs[index];
            var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock
            {
                Text = tab.IconGlyph,
                FontSize = 20,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            stack.Children.Add(new TextBlock
            {
                Text = tab.Label,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            var button = new Button
            {
                Content = stack,
                Command = tab.NavigateCommand,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };
            button.Classes.Add("MptMobileTab");
            button.Classes.Add("MptMobileTabButton");
            foreach (var label in stack.Children.OfType<TextBlock>())
            {
                label.Bind(TextBlock.ForegroundProperty, new Binding(nameof(Button.Foreground)) { Source = button });
            }
            AutomationProperties.SetName(button, tab.AutomationName);
            Grid.SetColumn(button, index);
            bar.Children.Add(button);
            owner._navigationButtons.Add(button);
        }

        var frame = new Border { Child = bar };
        frame.Classes.Add("MptMobileTabBar");
        return frame;
    }

    private void UpdateTabSelection()
    {
        _tabBar.IsVisible = !_viewModel.IsToolSurfaceOpen &&
            MobilePageKeys.Tabs.Contains(_viewModel.CurrentPageKey, StringComparer.Ordinal);
        for (var index = 0; index < _navigationButtons.Count && index < _viewModel.Tabs.Count; index++)
        {
            var selected = string.Equals(_viewModel.Tabs[index].PageKey, _viewModel.SelectedTabKey, StringComparison.Ordinal);
            _navigationButtons[index].Classes.Set("selected", selected);
            _navigationButtons[index].Classes.Set("active", selected);
        }
    }

    private static Control BuildInfoBars(ShellChromeViewModel chrome)
    {
        var items = new ItemsControl { ItemsSource = chrome.InfoBars };
        items.ItemTemplate = new FuncDataTemplate<InfoBarItem>((item, _) =>
        {
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(MobileElements.Body(item.Message));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (item.HasAction)
            {
                actions.Children.Add(MobileElements.TextButton(item.ActionLabel ?? "操作", item.ActionCommand));
            }

            actions.Children.Add(MobileElements.TextButton("关闭", item.DismissCommand));
            stack.Children.Add(actions);
            var card = MobileElements.Banner("", "MptMobileBannerInfo");
            ((Border)card).Child = stack;
            card.Bind(IsVisibleProperty, item, nameof(InfoBarItem.IsVisible));
            return card;
        });
        items.Classes.Add("MptMobileInfoBars");
        return items;
    }

    private Grid BuildSheetLayer(out ContentControl body, out TextBlock title, out TextBlock subtitle)
    {
        var backdrop = new Border();
        backdrop.Classes.Add("MptMobileBackdrop");
        backdrop.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileBackdropBrush"));
        backdrop.PointerPressed += async (_, _) => await CloseSheetAsync().ConfigureAwait(true);
        backdrop.Bind(IsVisibleProperty, new Binding(nameof(MobileShellViewModel.IsSheetOpen)) { Source = _viewModel });

        var grabber = new Border { Height = 4, Width = 44, HorizontalAlignment = HorizontalAlignment.Center };
        grabber.Classes.Add("MptMobileGrabber");
        grabber.Classes.Add("MptMobileSheetGrabber");
        grabber.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileDividerBrush"));

        title = MobileElements.Text("", "MptMobileSheetTitle");
        subtitle = MobileElements.Text("", "MptMobileSheetSubtitle");
        var close = MobileElements.IconButton("\u2715", "关闭", new AsyncRelayCommand(CloseSheetAsync));
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(title);
        titles.Children.Add(subtitle);
        Grid.SetColumn(titles, 0);
        header.Children.Add(titles);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        body = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(grabber);
        content.Children.Add(header);
        content.Children.Add(MobileElements.Scroller(body));

        var sheet = new Border { Child = content, VerticalAlignment = VerticalAlignment.Bottom };
        sheet.Classes.Add("MptMobileSheet");
        sheet.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileCardBrush"));
        sheet.Bind(IsVisibleProperty, new Binding(nameof(MobileShellViewModel.IsSheetOpen)) { Source = _viewModel });

        var layer = new Grid { Children = { backdrop, sheet } };
        layer.Classes.Add("MptMobileSheetLayer");
        layer.Bind(IsVisibleProperty, new Binding(nameof(MobileShellViewModel.IsSheetOpen)) { Source = _viewModel });
        return layer;
    }

    private static Border BuildCommandPalette(ShellChromeViewModel chrome, MptSearchBox search, ContentControl commands)
    {
        var close = MobileElements.TextButton("关闭搜索", chrome.CloseCommandPaletteCommand, "关闭搜索");
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(search);
        stack.Children.Add(close);
        stack.Children.Add(commands);
        // The workspace palette view already scrolls its own results, and keeping the command host as a
        // direct child means the workspace's panel is part of the shell's visual tree before the
        // palette is opened, not only while it is on screen.
        var panel = new Border { Child = stack, Padding = new Thickness(16) };
        panel.Classes.Add("MptMobileOverlay");
        panel.Classes.Add("MptMobileCommandPalette");
        panel.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileBackgroundBrush"));
        panel.Bind(IsVisibleProperty, new Binding(nameof(ShellChromeViewModel.IsCommandPaletteOpen)) { Source = chrome });
        return panel;
    }

    private static Grid BuildPermissionLayer(ShellChromeViewModel chrome, ContentControl permissions, ContentControl audit)
    {
        var back = MobileElements.BackButton(new AsyncRelayCommand(() =>
        {
            chrome.DismissPermissionPromptCommand.Execute(null);
            return Task.CompletedTask;
        }));
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(back);
        content.Children.Add(permissions);
        content.Children.Add(audit);
        var overlay = new Border { Padding = new Thickness(16), Child = MobileElements.Scroller(content), VerticalAlignment = VerticalAlignment.Bottom };
        overlay.Classes.Add("MptMobileOverlay");
        overlay.Classes.Add("MptMobileSheet");
        overlay.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileCardBrush"));
        overlay.Bind(IsVisibleProperty, new Binding(nameof(ShellChromeViewModel.IsPermissionPromptOpen)) { Source = chrome });

        var backdrop = new Border();
        backdrop.Classes.Add("MptMobileBackdrop");
        backdrop.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileBackdropBrush"));
        backdrop.Bind(IsVisibleProperty, new Binding(nameof(ShellChromeViewModel.IsPermissionPromptOpen)) { Source = chrome });

        var layer = new Grid { Children = { backdrop, overlay } };
        layer.Classes.Add("MptMobileSheetLayer");
        layer.Bind(IsVisibleProperty, new Binding(nameof(ShellChromeViewModel.IsPermissionPromptOpen)) { Source = chrome });
        return layer;
    }

    private void RebuildSheet()
    {
        _sheetGeneration++;
        if (_pairingQr is not null) _pairingQr.Value = null;
        _pairingQr = null;
        if (!_viewModel.IsSheetOpen)
        {
            _sheetHost.Content = null;
            return;
        }

        var (title, subtitle, content) = _viewModel.SheetKey switch
        {
            MobileSheetKeys.PairDevice => BuildPairSheet(),
            MobileSheetKeys.LocalPairingCode => BuildPairingCodeSheet(),
            MobileSheetKeys.Relay => BuildRelaySheet(),
            MobileSheetKeys.Permissions => BuildPermissionsSheet(),
            MobileSheetKeys.Appearance => BuildAppearanceSheet(),
            MobileSheetKeys.About => BuildAboutSheet(),
            MobileSheetKeys.PeerActions => BuildPeerActionsSheet(_viewModel.SheetArgument),
            _ => ("操作", "", (Control)MobileElements.Caption("这个操作还没有接入。"))
        };
        _sheetTitle.Text = title;
        _sheetSubtitle.Text = subtitle;
        _sheetSubtitle.IsVisible = subtitle.Length > 0;
        _sheetHost.Content = content;
    }

    private (string Title, string Subtitle, Control Content) BuildPairSheet()
    {
        var viewModel = _viewModel.Devices;
        var generation = _sheetGeneration;
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(MobileElements.Banner(
            "在另一台设备打开“本机连接码”，扫描或粘贴到这里。",
            "MptMobileBannerQuiet"));
        stack.Children.Add(MobileElements.Caption("连接码"));
        var input = new TextBox { PlaceholderText = "连接码", MinHeight = 48 };
        input.Classes.Add("MptMobileField");
        AutomationProperties.SetName(input, "连接码");
        stack.Children.Add(input);
        var error = MobileElements.Text("", "MptMobileErrorText");
        error.IsVisible = false;
        stack.Children.Add(error);
        if (_scanConnectionCodeAsync is { } scan)
        {
            stack.Children.Insert(1, MobileElements.Secondary("扫描二维码", new AsyncRelayCommand(async () =>
            {
                try
                {
                    var value = await scan(CancellationToken.None).ConfigureAwait(true);
                    if (generation != _sheetGeneration || !_viewModel.IsSheetOpen || string.IsNullOrWhiteSpace(value)) return;
                    input.Text = value;
                    error.IsVisible = false;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (generation != _sheetGeneration || !_viewModel.IsSheetOpen) return;
                    error.Text = ex.Message;
                    error.IsVisible = true;
                }
            }), "扫描设备连接码"));
        }
        var connect = MobileElements.Primary("继续", null, "连接设备");
        connect.Command = new AsyncRelayCommand(async () =>
        {
            error.IsVisible = false;
            try
            {
                var code = (input.Text ?? "").Trim();
                if (Uri.TryCreate(code, UriKind.Absolute, out var link) &&
                    string.Equals(link.Scheme, "mpt", StringComparison.OrdinalIgnoreCase))
                {
                    var module = link.Host.ToLowerInvariant() switch
                    {
                        "pair" or "assistant" or "cloud" => "file-transfer",
                        "control" => MobileControlDeviceService.ToolId,
                        _ => throw new InvalidOperationException("这不是受支持的设备连接码。")
                    };
                    await ActivateToolAsync(module, "workspace", code).ConfigureAwait(true);
                    await CloseSheetAsync().ConfigureAwait(true);
                }
                else
                {
                    await viewModel.ImportPairingAsync(code).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
        });
        stack.Children.Add(connect);
        stack.Children.Add(MobileElements.Secondary("显示本机连接码", viewModel.ShowPairingCodeCommand, "显示本机连接码"));
        return ("添加设备", "粘贴对方的连接码，确认后添加。", stack);
    }

    private (string Title, string Subtitle, Control Content) BuildPairingCodeSheet()
    {
        var viewModel = _viewModel.Devices;
        var generation = _sheetGeneration;
        var stack = new StackPanel { Spacing = 12 };
        var qr = new MyPowerTools.AvaloniaSdk.Controls.MptQrCode
        {
            Width = 240, Height = 240,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsVisible = false
        };
        _pairingQr = qr;
        stack.Children.Add(qr);
        var code = MobileElements.Caption("正在生成连接码…");
        stack.Children.Add(code);
        stack.Children.Add(MobileElements.Caption("用另一台设备扫描，或复制连接码到“添加设备”。"));
        var copy = MobileElements.Secondary("复制连接码", null, "复制连接码");
        copy.IsEnabled = false;
        stack.Children.Add(copy);
        var error = MobileElements.Text("", "MptMobileErrorText");
        error.IsVisible = false;
        stack.Children.Add(error);
        _ = LoadCodeAsync();
        return ("本机连接码", viewModel.LocalName, stack);

        async Task LoadCodeAsync()
        {
            try
            {
                var value = await viewModel.GetPairingCodeAsync().ConfigureAwait(true);
                if (generation != _sheetGeneration || !_viewModel.IsSheetOpen) return;
                qr.Value = value;
                qr.IsVisible = true;
                code.IsVisible = false;
                copy.IsEnabled = true;
                copy.Command = new AsyncRelayCommand(async () =>
                {
                    if (generation != _sheetGeneration || !_viewModel.IsSheetOpen) return;
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    {
                        var data = new DataTransfer();
                        data.Add(DataTransferItem.CreateText(value));
                        await clipboard.SetDataAsync(data).ConfigureAwait(true);
                        ShowToast("连接码已复制");
                    }
                });
            }
            catch (Exception ex)
            {
                if (generation != _sheetGeneration || !_viewModel.IsSheetOpen) return;
                code.Text = "暂时无法生成连接码";
                error.Text = ex.Message;
                error.IsVisible = true;
            }
        }
    }

    private (string Title, string Subtitle, Control Content) BuildRelaySheet()
    {
        var viewModel = _viewModel.Devices;
        var stack = new StackPanel { Spacing = 12 };
        var state = MobileElements.ListRow("\u2601", viewModel.RelaySummary, viewModel.RelayDetail, "", null, "文件同步");
        stack.Children.Add(MobileElements.Card(state, "MptMobileListCard"));
        stack.Children.Add(MobileElements.Banner(
            "发送时自动选择可用连接。对方离线时先保留文件，上线后继续接收。",
            "MptMobileBannerQuiet"));
        stack.Children.Add(MobileElements.Secondary("刷新状态", new AsyncRelayCommand(() => viewModel.LoadAsync(force: true)), "刷新中转状态"));
        stack.Children.Add(MobileElements.Primary("完成", new AsyncRelayCommand(CloseSheetAsync), "完成"));
        return ("文件同步", viewModel.RelaySummary, stack);
    }

    private (string Title, string Subtitle, Control Content) BuildPermissionsSheet()
    {
        var stack = new StackPanel { Spacing = 12 };
        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(MobileElements.Caption("正在读取 Broker 审计记录…"));
        stack.Children.Add(MobileElements.Section("最近的权限记录", MobileElements.Card(list, "MptMobileListCard")));
        stack.Children.Add(MobileElements.Caption("需要管理员权限的操作由电脑端 Broker 处理；手机只负责发起和确认。"));
        stack.Children.Add(MobileElements.Primary("完成", new AsyncRelayCommand(CloseSheetAsync), "完成"));
        _ = LoadAuditAsync();
        return ("设备权限与审计", "这些是真实的授权记录。", stack);

        async Task LoadAuditAsync()
        {
            try
            {
                var audit = await _workspace.PageData.LoadBrokerAuditAsync(20).ConfigureAwait(true);
                list.Children.Clear();
                if (audit.HasError)
                {
                    list.Children.Add(MobileElements.Text(audit.ErrorMessage, "MptMobileErrorText"));
                    return;
                }

                if (audit.IsEmpty)
                {
                    list.Children.Add(MobileElements.Caption("还没有需要授权的操作记录。"));
                    return;
                }

                foreach (var entry in audit.Entries)
                {
                    list.Children.Add(MobileElements.ListRow("\u26E8", entry.Title, entry.Detail, "", null, entry.Title));
                }
            }
            catch (Exception ex)
            {
                list.Children.Clear();
                list.Children.Add(MobileElements.Text(ex.Message, "MptMobileErrorText"));
            }
        }
    }

    private (string Title, string Subtitle, Control Content) BuildAppearanceSheet()
    {
        var viewModel = _viewModel.Settings;
        var stack = new StackPanel { Spacing = 8 };
        foreach (var option in viewModel.ThemeOptions)
        {
            stack.Children.Add(MobileElements.ListRow(
                option.IsSelected ? "\u2713" : null,
                option.Label,
                "",
                option.IsSelected ? "当前" : "",
                option.SelectCommand,
                option.Label));
        }

        stack.Children.Add(MobileElements.Caption("外观写入与桌面版同一个偏好文件。"));
        return ("外观", viewModel.AppearanceLabel, stack);
    }

    private (string Title, string Subtitle, Control Content) BuildAboutSheet()
    {
        var viewModel = _viewModel.Settings;
        var rows = new StackPanel { Spacing = 8 };
        rows.Children.Add(MobileElements.ListRow(null, viewModel.VersionText, "Android 版手机体验", "", null, "版本"));
        rows.Children.Add(MobileElements.ListRow(null, "运行模式", _chrome.RuntimeModeLabel, "", null, "运行模式"));
        rows.Children.Add(MobileElements.ListRow(null, "运行时", _chrome.RuntimeIdentityText, "", null, "运行时"));
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(MobileElements.Card(rows, "MptMobileListCard"));
        stack.Children.Add(MobileElements.Caption("手机版复用同一个运行时、模块与权限边界；安装包版本以系统应用信息为准。"));
        stack.Children.Add(MobileElements.Primary("完成", new AsyncRelayCommand(CloseSheetAsync), "完成"));
        return ("关于", "", stack);
    }

    private (string Title, string Subtitle, Control Content) BuildPeerActionsSheet(string? deviceId)
    {
        var viewModel = _viewModel.Devices;
        var peer = string.IsNullOrWhiteSpace(deviceId) ? null : viewModel.FindPeer(deviceId);
        if (peer is null)
        {
            return ("设备", "", MobileElements.Caption("这台设备已不在配对列表中。"));
        }

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(MobileElements.Card(
            MobileElements.ListRow(peer.IconGlyph, peer.Name, peer.StateDetail, peer.StateLabel, null, peer.Name),
            "MptMobileListCard"));
        stack.Children.Add(MobileElements.Caption(peer.CapabilityLabel));
        stack.Children.Add(MobileElements.Secondary("检查连接", peer.CheckCommand, "检查连接"));
        stack.Children.Add(MobileElements.Banner("解除连接后，下次使用需要重新配对。", "MptMobileBannerQuiet"));
        var remove = MobileElements.Primary("解除与 " + peer.Name + " 的连接", new AsyncRelayCommand(() => viewModel.RemovePeerAsync(peer)), "解除连接");
        remove.Classes.Add("MptMobileDanger");
        stack.Children.Add(remove);
        return ("设备操作", peer.Name, stack);
    }
}
