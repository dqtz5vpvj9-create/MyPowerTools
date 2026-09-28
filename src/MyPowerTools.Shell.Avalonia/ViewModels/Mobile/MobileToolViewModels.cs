using System.Collections.ObjectModel;
using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

/// <summary>Phone search over the real catalog entry (name, description, keywords, tool id).</summary>
internal static class MobileToolSearch
{
    public static bool Matches(MobileToolEntry entry, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.All(token =>
            entry.Title.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            entry.Description.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            entry.Keywords.Contains(token, StringComparison.OrdinalIgnoreCase) ||
            entry.ToolId.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>One row in the phone tool library, bound to the live card for favorite state.</summary>
public sealed class MobileToolItemViewModel : ObservableViewModel
{
    private readonly MobileToolEntry _entry;
    private readonly IMobileNavigator _navigator;

    public MobileToolItemViewModel(MobileToolEntry entry, IMobileNavigator navigator)
    {
        _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        OpenCommand = new AsyncRelayCommand(
            () => _navigator.OpenToolSurfaceAsync(_entry.ImplementationId),
            () => CanOpen,
            operationName: $"Open {_entry.ToolId}");
        ShowDetailCommand = new AsyncRelayCommand(
            () => _navigator.ShowToolDetailAsync(_entry.ToolId),
            operationName: $"Show {_entry.ToolId} details");
        ToggleFavoriteCommand = new AsyncRelayCommand(
            ToggleFavoriteAsync,
            operationName: $"Favorite {_entry.ToolId}");
        if (_entry.Card is { } card)
        {
            card.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ToolCardViewModel.IsFavorite))
                {
                    RaiseFavoriteChanged();
                }
            };
        }
    }

    public MobileToolEntry Entry => _entry;
    public string ToolId => _entry.ToolId;
    public string ProductId => _entry.ProductId;
    public string ImplementationId => _entry.ImplementationId;
    public string Title => _entry.Title;
    public string Description => _entry.Description;
    public string Group => _entry.Group;
    public string IconGlyph => _entry.IconGlyph;
    public string StatusLabel => _entry.StatusLabel;
    public string StatusDetail => _entry.StatusDetail;
    public bool CanOpen => _entry.CanOpen;
    public bool IsComputerTool => _entry.Platform == MobileToolPlatform.Computer;
    public string PlatformLabel => _entry.ExecutionLocation switch
    {
        MobileToolPlatform.Computer => "电脑",
        MobileToolPlatform.ThisDevice => "手机",
        _ => "待适配"
    };
    public bool IsAvailable => _entry.Availability == ToolAvailability.Available;
    public bool NeedsAttention => !CanOpen;
    public string AutomationName => $"{Title}，{PlatformLabel}，{StatusLabel}";

    public bool IsFavorite => _entry.Card?.IsFavorite ?? false;
    public string FavoriteActionLabel => IsFavorite ? "已加入常用" : "加入常用";
    public string FavoriteAutomationName => IsFavorite ? $"将{Title}移出常用" : $"将{Title}加入常用";
    public string FavoriteGlyph => IsFavorite ? "\u2605" : "\u2606";

    public ICommand OpenCommand { get; }
    public ICommand ShowDetailCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }

    public bool Matches(string? query) => MobileToolSearch.Matches(_entry, query);

    /// <summary>Persists through the card's shared preferences store; public so tests can await it.</summary>
    public async Task ToggleFavoriteAsync()
    {
        if (_entry.Card is not { } card)
        {
            return;
        }

        if (card.ToggleFavoriteCommand is AsyncRelayCommand command)
        {
            await command.ExecuteAsync(null).ConfigureAwait(true);
        }
        else
        {
            card.ToggleFavoriteCommand.Execute(null);
        }

        RaiseFavoriteChanged();
        _navigator.ShowToast(IsFavorite ? $"{Title} 已加入常用" : $"{Title} 已从常用移除");
    }

    private void RaiseFavoriteChanged()
    {
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteActionLabel));
        OnPropertyChanged(nameof(FavoriteAutomationName));
        OnPropertyChanged(nameof(FavoriteGlyph));
    }
}

/// <summary>A 用途分组 of the tool library.</summary>
public sealed class MobileToolGroupViewModel
{
    public MobileToolGroupViewModel(string title, IReadOnlyList<MobileToolItemViewModel> items)
    {
        Title = title;
        Items = items;
    }

    public string Title { get; }
    public IReadOnlyList<MobileToolItemViewModel> Items { get; }
}

/// <summary>A library filter chip (全部 / 手机可用 / 电脑工具 / 已收藏).</summary>
public sealed class MobileFilterViewModel : ObservableViewModel
{
    private bool _isSelected;

    public MobileFilterViewModel(string label, Func<string, Task> select)
    {
        Label = label;
        SelectCommand = new AsyncRelayCommand(() => select(label));
    }

    public string Label { get; }
    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>工具 tab: real catalog, real search, real favorites, honest phone/computer split.</summary>
public sealed class MobileToolsViewModel : ObservableViewModel
{
    public const string FilterAll = "全部";
    public const string FilterThisDevice = "手机可用";
    public const string FilterComputer = "电脑工具";
    public const string FilterFavorites = "已收藏";

    private readonly MobileShellData _data;
    private readonly IMobileNavigator _navigator;
    private readonly List<MobileToolItemViewModel> _all = [];
    private string _searchText = "";
    private string _selectedFilter = FilterAll;
    private bool _isLoading;
    private string _errorMessage = "";

    public MobileToolsViewModel(MobileShellData data, IMobileNavigator navigator)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        Filters =
        [
            new MobileFilterViewModel(FilterAll, SelectFilterAsync),
            new MobileFilterViewModel(FilterThisDevice, SelectFilterAsync),
            new MobileFilterViewModel(FilterComputer, SelectFilterAsync),
            new MobileFilterViewModel(FilterFavorites, SelectFilterAsync)
        ];
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync(force: true), operationName: "Refresh tool library");
        ClearSearchCommand = new AsyncRelayCommand(ClearSearchAsync, operationName: "Clear tool search");
    }

    public ObservableCollection<MobileToolGroupViewModel> Groups { get; } = [];
    public IReadOnlyList<MobileFilterViewModel> Filters { get; }
    public ICommand ReloadCommand { get; }
    public ICommand ClearSearchCommand { get; }

    /// <summary>Number of loaded library items (diagnostics and tests).</summary>
    public int AllCount => _all.Count;

    /// <summary>The live item for a tool id, or null when the catalog does not offer it.</summary>
    public MobileToolItemViewModel? Find(string toolId) => _all.FirstOrDefault(item =>
        string.Equals(item.ProductId, ToolProductIdentity.ProductId(toolId), StringComparison.OrdinalIgnoreCase));

    public string Title => "工具";
    public string Subtitle => "每一种本领，都有用武之地。";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? ""))
            {
                ApplyFilters();
            }
        }
    }

    public string SelectedFilter => _selectedFilter;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorMessage.Length > 0;
    public bool IsEmpty => !IsLoading && !HasError && Groups.Count == 0;
    public int VisibleCount => Groups.Sum(group => group.Items.Count);
    public string ResultSummary => VisibleCount == 1 ? "1 个工具" : $"{VisibleCount} 个工具";
    public string EmptyTitle => ActiveFilter() == FilterFavorites ? "还没有常用工具" : "换个词试试";
    public string EmptyDetail => ActiveFilter() == FilterFavorites
        ? "在工具行上点星标，就会出现在这里和“常用”。"
        : "可以搜索“文件”“通知”或“电脑”。";

    public async Task LoadAsync(bool force = false)
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            await _data.GetLibraryAsync(force).ConfigureAwait(true);
            var error = _data.LibraryError;
            if (error.Length > 0)
            {
                ErrorMessage = error;
            }

            RebuildItems();
        }
        finally
        {
            IsLoading = false;
            ApplyFilters();
        }
    }

    public Task SelectFilterAsync(string filter)
    {
        _selectedFilter = filter;
        OnPropertyChanged(nameof(SelectedFilter));
        ApplyFilters();
        return Task.CompletedTask;
    }

    private Task ClearSearchAsync()
    {
        SearchText = "";
        _selectedFilter = FilterAll;
        OnPropertyChanged(nameof(SelectedFilter));
        ApplyFilters();
        return Task.CompletedTask;
    }

    private void RebuildItems()
    {
        _all.Clear();
        if (_data.Library is { } library)
        {
            foreach (var entry in library.Entries)
            {
                _all.Add(new MobileToolItemViewModel(entry, _navigator));
            }
        }
    }

    private string ActiveFilter() => _selectedFilter;

    private void ApplyFilters()
    {
        foreach (var filter in Filters)
        {
            filter.IsSelected = string.Equals(filter.Label, _selectedFilter, StringComparison.Ordinal);
        }

        var query = _searchText;
        var matches = _all
            .Where(item => MatchesFilter(item))
            .Where(item => item.Matches(query))
            .ToArray();

        Groups.Clear();
        if (_data.Library is { } library)
        {
            foreach (var group in library.Groups)
            {
                var items = matches
                    .Where(item => string.Equals(item.Group, group, StringComparison.Ordinal))
                    .ToArray();
                if (items.Length > 0)
                {
                    Groups.Add(new MobileToolGroupViewModel(group, items));
                }
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDetail));
    }

    private bool MatchesFilter(MobileToolItemViewModel item) => _selectedFilter switch
    {
        FilterThisDevice => !item.IsComputerTool && item.CanOpen,
        FilterComputer => item.IsComputerTool,
        FilterFavorites => item.IsFavorite,
        _ => true
    };
}

/// <summary>
/// Tool detail page. Local tools open their real surface; computer tools state what they actually
/// need (a paired, authorised computer) and never offer an action the phone cannot perform.
/// </summary>
public sealed class MobileToolDetailViewModel : ObservableViewModel
{
    private readonly MobileToolItemViewModel _item;
    private MobileControlDevice? _selectedControlDevice;

    public MobileToolDetailViewModel(
        MobileToolItemViewModel item,
        IMobileNavigator navigator,
        MobileControlDevice? controlDevice = null,
        IReadOnlyList<MobileControlDevice>? controlDevices = null)
    {
        _item = item ?? throw new ArgumentNullException(nameof(item));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        ControlDevices = (controlDevices ?? (controlDevice is null ? [] : new[] { controlDevice }))
            .DistinctBy(device => device.DeviceId, StringComparer.Ordinal).ToArray();
        _selectedControlDevice = ControlDevices.Count == 1 ? ControlDevices[0] : null;
        OpenCommand = item.OpenCommand;
        ToggleFavoriteCommand = item.ToggleFavoriteCommand;
        OpenOnComputerCommand = new AsyncRelayCommand(
            OpenOnComputerAsync,
            () => CanOpenOnComputer,
            operationName: $"Open {item.Title} on the computer");
        ConnectComputerCommand = new AsyncRelayCommand(
            () => navigator.ActivateToolAsync(
                MobileControlDeviceService.ToolId,
                MobileControlDeviceService.FallbackRouteId,
                MobileControlDeviceService.BuildActivationUri("")),
            operationName: "Connect a computer");
        _item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MobileToolItemViewModel.IsFavorite) or nameof(MobileToolItemViewModel.FavoriteActionLabel))
            {
                OnPropertyChanged(args.PropertyName);
            }
        };
    }

    public IMobileNavigator Navigator { get; }
    public string ToolId => _item.ToolId;
    public string Title => _item.Title;
    public string Description => _item.Description;
    public string IconGlyph => _item.IconGlyph;
    public string StatusLabel => _item.StatusLabel;
    public string StatusDetail => _item.StatusDetail;
    public bool CanOpen => _item.CanOpen;
    public bool IsComputerTool => _item.IsComputerTool;
    public string PlatformLabel => _item.PlatformLabel;
    public string FavoriteActionLabel => _item.FavoriteActionLabel;
    public string FavoriteGlyph => _item.FavoriteGlyph;
    public ICommand OpenCommand { get; }
    public ICommand ToggleFavoriteCommand { get; }
    public ICommand OpenOnComputerCommand { get; }
    public ICommand ConnectComputerCommand { get; }

    public IReadOnlyList<MobileControlDevice> ControlDevices { get; }
    public bool HasControlDevice => ControlDevices.Count > 0;
    public bool CanOpenOnComputer => IsComputerTool && SelectedControlDevice?.CredentialConfigured == true;
    public MobileControlDevice? SelectedControlDevice
    {
        get => _selectedControlDevice;
        set
        {
            var selected = value is null ? null : ControlDevices.FirstOrDefault(device => device.DeviceId == value.DeviceId);
            if (!SetProperty(ref _selectedControlDevice, selected)) return;
            OnPropertyChanged(nameof(CanOpenOnComputer));
            OnPropertyChanged(nameof(ControlDeviceName));
            OnPropertyChanged(nameof(ControlDeviceState));
            OnPropertyChanged(nameof(RequirementTitle));
            OnPropertyChanged(nameof(RequirementDetail));
            (OpenOnComputerCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
    }
    public string ControlDeviceName => SelectedControlDevice?.Name is { Length: > 0 } name ? name : "请选择电脑";
    public string ControlDeviceState => SelectedControlDevice is null ? "选择后查看这台电脑的工具权限。"
        : !SelectedControlDevice.CredentialConfigured ? "授权凭据缺失，请重新连接这台电脑。"
        : $"{SelectedControlDevice.PlatformLabel} · {SelectedControlDevice.StateLabel}";

    public string ControlDeviceLabel(MobileControlDevice device)
    {
        var suffix = ControlDevices.Count(candidate => candidate.Name == device.Name) > 1
            ? $" · {device.DeviceId[^Math.Min(6, device.DeviceId.Length)..]}" : "";
        return $"{device.Name} · {device.PlatformLabel}{suffix}";
    }

    /// <summary>The device capability this tool actually needs, in the user's words.</summary>
    public string RequirementTitle => IsComputerTool
        ? (SelectedControlDevice is not null ? $"在 {ControlDeviceName} 上运行" : HasControlDevice ? "选择执行电脑" : "需要一台电脑")
        : CanOpen ? "在本机运行" : "此设备暂不可用";

    public string RequirementDetail => IsComputerTool
        ? (HasControlDevice
            ? "选择电脑后查看它允许使用的工具；授权、确认和结果由该电脑提供。"
            : "此工具在电脑上运行。先连接电脑（需要电脑端已启用远程工具访问并授权这台手机），再从这里打开。")
        : CanOpen ? "此工具在手机上运行，点“打开”进入它的界面。" : _item.Entry.OpenUnavailableReason;

    public bool HasStatusDetail => StatusDetail.Length > 0;

    private Task OpenOnComputerAsync() => !CanOpenOnComputer
        ? Task.CompletedTask
        : Navigator.ActivateToolAsync(
            MobileControlDeviceService.ToolId,
            MobileControlDeviceService.FallbackRouteId,
            MobileControlDeviceService.BuildActivationUri(SelectedControlDevice!.DeviceId, ToolId));
}
