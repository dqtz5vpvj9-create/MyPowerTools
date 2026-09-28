using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

/// <summary>
/// Navigation state of the phone shell: four一级入口, a real back stack, the single bottom sheet and
/// the borrowed workspace tool surface. The shell view renders whatever this model says is current.
/// </summary>
public sealed class MobileShellViewModel : ObservableViewModel
{
    private readonly List<(string PageKey, string? Argument)> _backStack = [];
    private string _currentPageKey = MobilePageKeys.Home;
    private string? _currentArgument;
    private string _sheetKey = "";
    private string? _sheetArgument;
    private bool _isToolSurfaceOpen;

    public MobileShellViewModel(MobileShellServices services, IMobileNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(navigator);
        Data = new MobileShellData(services.Catalog, services.Devices);
        Tools = new MobileToolsViewModel(Data, navigator);
        Home = new MobileHomeViewModel(Data, Tools, navigator);
        Devices = new MobileDevicesViewModel(Data, services.Devices, services.ControlDevices, navigator);
        Activity = new MobileActivityViewModel(Data, Tools, navigator);
        Settings = new MobileSettingsViewModel(Data, services, navigator);
        Tabs =
        [
            new MobileTabViewModel(
                MobilePageKeys.Home,
                "常用",
                "\u2302",
                new AsyncRelayCommand(() => navigator.ShowRootPageAsync(MobilePageKeys.Home), operationName: "Show 常用")),
            new MobileTabViewModel(
                MobilePageKeys.Tools,
                "工具",
                "\u25A6",
                new AsyncRelayCommand(() => navigator.ShowRootPageAsync(MobilePageKeys.Tools), operationName: "Show 工具")),
            new MobileTabViewModel(
                MobilePageKeys.Devices,
                "设备",
                "\u25A3",
                new AsyncRelayCommand(() => navigator.ShowRootPageAsync(MobilePageKeys.Devices), operationName: "Show 设备")),
            new MobileTabViewModel(
                MobilePageKeys.Activity,
                "动态",
                "\u21BB",
                new AsyncRelayCommand(() => navigator.ShowRootPageAsync(MobilePageKeys.Activity), operationName: "Show 动态"))
        ];
    }

    public MobileShellData Data { get; }
    public MobileHomeViewModel Home { get; }
    public MobileToolsViewModel Tools { get; }
    public MobileDevicesViewModel Devices { get; }
    public MobileActivityViewModel Activity { get; }
    public MobileSettingsViewModel Settings { get; }
    public IReadOnlyList<MobileTabViewModel> Tabs { get; }

    public MobileToolDetailViewModel? ToolDetail { get; private set; }
    public MobileDeviceDetailViewModel? DeviceDetail { get; private set; }

    public string CurrentPageKey
    {
        get => _currentPageKey;
        private set => SetProperty(ref _currentPageKey, value);
    }

    public string? CurrentArgument
    {
        get => _currentArgument;
        private set => SetProperty(ref _currentArgument, value);
    }

    public int BackStackDepth => _backStack.Count;

    /// <summary>The highlighted tab; detail and settings pages keep the prototype's no-highlight look.</summary>
    public string SelectedTabKey => Tabs.Any(tab => string.Equals(tab.PageKey, CurrentPageKey, StringComparison.Ordinal))
        ? CurrentPageKey
        : "";

    public bool CanGoBack => _backStack.Count > 0 || IsToolSurfaceOpen;

    /// <summary>True while the shared workspace's tool surface occupies the content area.</summary>
    public bool IsToolSurfaceOpen
    {
        get => _isToolSurfaceOpen;
        set
        {
            if (SetProperty(ref _isToolSurfaceOpen, value))
            {
                OnPropertyChanged(nameof(IsPhoneContentVisible));
                RaiseNavigationChanged();
            }
        }
    }

    /// <summary>The phone pages are visible exactly when no tool surface owns the content area.</summary>
    public bool IsPhoneContentVisible => !_isToolSurfaceOpen;

    public string SheetKey
    {
        get => _sheetKey;
        private set
        {
            if (SetProperty(ref _sheetKey, value))
            {
                OnPropertyChanged(nameof(IsSheetOpen));
            }
        }
    }

    public string? SheetArgument
    {
        get => _sheetArgument;
        private set => SetProperty(ref _sheetArgument, value);
    }

    public bool IsSheetOpen => SheetKey.Length > 0;

    public void NavigateRoot(string pageKey)
    {
        if (!MobilePageKeys.Tabs.Contains(pageKey, StringComparer.Ordinal))
        {
            throw new ArgumentException($"'{pageKey}' is not a top-level phone page.", nameof(pageKey));
        }

        // A tab tap starts a new stack, exactly like the prototype's go(page, root): the previous
        // page must not stay on the back stack or back would return to the tab the user just left.
        _backStack.Clear();
        CurrentArgument = null;
        CurrentPageKey = pageKey;
        RaiseNavigationChanged();
    }

    public void Navigate(string pageKey, string? argument)
    {
        if (!string.Equals(pageKey, CurrentPageKey, StringComparison.Ordinal) ||
            !string.Equals(argument, CurrentArgument, StringComparison.Ordinal))
        {
            _backStack.Add((CurrentPageKey, CurrentArgument));
        }

        CurrentArgument = argument;
        CurrentPageKey = pageKey;
        RaiseNavigationChanged();
    }

    /// <summary>Pops one page; returns false when there is nothing left to pop.</summary>
    public bool GoBack()
    {
        if (_backStack.Count == 0)
        {
            return false;
        }

        var (pageKey, argument) = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        CurrentArgument = argument;
        CurrentPageKey = pageKey;
        RaiseNavigationChanged();
        return true;
    }

    public void SetToolDetail(MobileToolDetailViewModel? detail)
    {
        ToolDetail = detail;
        OnPropertyChanged(nameof(ToolDetail));
    }

    public void SetDeviceDetail(MobileDeviceDetailViewModel? detail)
    {
        DeviceDetail = detail;
        OnPropertyChanged(nameof(DeviceDetail));
    }

    public void OpenSheet(string sheetKey, string? argument)
    {
        SheetArgument = argument;
        SheetKey = sheetKey;
    }

    public void CloseSheet()
    {
        SheetKey = "";
        SheetArgument = null;
    }

    private void RaiseNavigationChanged()
    {
        OnPropertyChanged(nameof(SelectedTabKey));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(BackStackDepth));
    }
}
