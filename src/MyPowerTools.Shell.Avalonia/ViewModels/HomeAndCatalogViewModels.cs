using System.Globalization;
using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class HomeViewModel : ToolProductPageViewModel
{
    private const int MaxActionShortcuts = 3;
    private const int MaxDashboardTools = 5;

    public HomeViewModel(
        IReadOnlyList<ToolCardViewModel> favoriteTools,
        IReadOnlyList<ToolCardViewModel> recentTools,
        IReadOnlyList<HomeActivityItemViewModel> activities,
        int totalToolCount,
        ToolProductState productState = ToolProductState.Ready,
        string errorMessage = "",
        Func<Task>? browseTools = null,
        Func<Task>? openActivity = null,
        Func<Task>? refresh = null,
        Func<Task>? retry = null,
        IReadOnlyList<ToolCardViewModel>? allTools = null)
        : base(
            "Dashboard",
            "Your tools, current work, and the next useful action in one place.",
            ResolveState(productState, allTools ?? favoriteTools, recentTools, activities),
            errorMessage)
    {
        FavoriteTools = favoriteTools;
        RecentTools = recentTools;
        Activities = activities;
        TotalToolCount = totalToolCount;
        BrowseToolsCommand = new AsyncRelayCommand(() => browseTools?.Invoke() ?? Task.CompletedTask);
        OpenActivityCommand = new AsyncRelayCommand(() => openActivity?.Invoke() ?? Task.CompletedTask);
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask);
        RetryCommand = new AsyncRelayCommand(() => retry?.Invoke() ?? refresh?.Invoke() ?? Task.CompletedTask);

        AllTools = (allTools ?? favoriteTools.Concat(recentTools).ToArray())
            .DistinctBy(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        DashboardTools = favoriteTools.Concat(recentTools).Concat(AllTools)
            .DistinctBy(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDashboardTools)
            .ToArray();
        ActionableTools = DashboardTools
            .Where(tool => tool.CanOpen)
            .Take(MaxActionShortcuts)
            .ToArray();

        QuickAccessActions =
        [
            new HomeDashboardActionViewModel(
                "Browse tools",
                $"{totalToolCount.ToString(CultureInfo.InvariantCulture)} registered",
                "\uE71D",
                BrowseToolsCommand,
                IsSymbolIcon: true),
            new HomeDashboardActionViewModel(
                "Refresh status",
                "Check availability",
                "\uE72C",
                RefreshCommand,
                IsSymbolIcon: true)
        ];
    }

    public IReadOnlyList<ToolCardViewModel> FavoriteTools { get; }
    public IReadOnlyList<ToolCardViewModel> RecentTools { get; }
    public IReadOnlyList<HomeActivityItemViewModel> Activities { get; }
    public IReadOnlyList<ToolCardViewModel> AllTools { get; }
    public IReadOnlyList<ToolCardViewModel> DashboardTools { get; }
    public IReadOnlyList<ToolCardViewModel> ActionableTools { get; }
    public IReadOnlyList<HomeDashboardActionViewModel> QuickAccessActions { get; }
    public int TotalToolCount { get; }
    public ICommand BrowseToolsCommand { get; }
    public ICommand OpenActivityCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public string DashboardToolsTitle => HasFavoriteTools ? "Favorites & recent tools" : HasRecentTools ? "Recent tools" : "Tools";
    public string ActionShortcutsDetail => HasFavoriteTools || HasRecentTools ? "Your frequently used tools" : "Primary tool actions";
    public bool HasFavoriteTools => FavoriteTools.Count > 0;
    public bool HasRecentTools => RecentTools.Count > 0;
    public bool HasActivities => Activities.Count > 0;
    public bool HasDashboardTools => DashboardTools.Count > 0;
    public bool HasActionableTools => ActionableTools.Count > 0;
    public bool HasQuickAccessActions => QuickAccessActions.Count > 0;
    public int ReadyToolCount => AllTools.Count(tool => tool.IsAvailable);
    public int PendingToolCount => AllTools.Count(tool => tool.IsInDevelopment);
    public int PausedToolCount => AllTools.Count(tool => tool.IsPaused);
    public int UnavailableToolCount => AllTools.Count(tool => tool.IsUnavailable);
    public int AttentionToolCount => AllTools.Count(tool => tool.IsAttentionStatus);
    public bool AllToolsHealthy => ReadyToolCount == TotalToolCount && TotalToolCount > 0;
    public string ToolCountSummary => TotalToolCount == 1
        ? "1 tool is ready to open"
        : $"{TotalToolCount.ToString(CultureInfo.InvariantCulture)} tools registered";
    public string DashboardStatusLabel => AllToolsHealthy
        ? $"{ReadyToolCount.ToString(CultureInfo.InvariantCulture)} tool workspaces available"
        : AttentionToolCount > 0
            ? $"{AttentionToolCount.ToString(CultureInfo.InvariantCulture)} tool(s) need attention"
            : $"{ReadyToolCount.ToString(CultureInfo.InvariantCulture)} tool workspaces available";
    public bool DashboardHasWarning => AttentionToolCount > 0 || UnavailableToolCount > 0;
    public string DashboardStatusDetail
    {
        get
        {
            var details = new List<string>();
            if (PendingToolCount > 0)
            {
                details.Add($"{PendingToolCount.ToString(CultureInfo.InvariantCulture)} coming soon");
            }

            if (PausedToolCount > 0)
            {
                details.Add($"{PausedToolCount.ToString(CultureInfo.InvariantCulture)} paused");
            }

            if (UnavailableToolCount > 0)
            {
                details.Add($"{UnavailableToolCount.ToString(CultureInfo.InvariantCulture)} unavailable");
            }

            if (AttentionToolCount > 0)
            {
                details.Insert(0, $"{AttentionToolCount.ToString(CultureInfo.InvariantCulture)} needs attention");
            }

            return details.Count == 0
                ? "All registered workspaces are available"
                : string.Join(" · ", details);
        }
    }
    public string ActivitySummary => Activities.Count == 1
        ? "1 recent run"
        : $"{Activities.Count.ToString(CultureInfo.InvariantCulture)} recent runs";

    private static ToolProductState ResolveState(
        ToolProductState requestedState,
        IReadOnlyList<ToolCardViewModel> favoriteTools,
        IReadOnlyList<ToolCardViewModel> recentTools,
        IReadOnlyList<HomeActivityItemViewModel> activities)
    {
        return requestedState == ToolProductState.Ready &&
               favoriteTools.Count == 0 &&
               recentTools.Count == 0 &&
               activities.Count == 0
            ? ToolProductState.Empty
            : requestedState;
    }
}

public sealed class ToolCatalogViewModel : ToolProductPageViewModel
{
    private string _query = "";
    private IReadOnlyList<ToolCardViewModel> _visibleTools;

    public ToolCatalogViewModel(
        IReadOnlyList<ToolCardViewModel> tools,
        ToolProductState productState = ToolProductState.Ready,
        string errorMessage = "",
        Func<Task>? refresh = null,
        Func<Task>? retry = null)
        : base(
            "All tools",
            "Open a tool to complete a task. Runtime modules and diagnostics live in System & Maintenance.",
            productState == ToolProductState.Ready && tools.Count == 0 ? ToolProductState.Empty : productState,
            errorMessage)
    {
        Tools = tools;
        _visibleTools = tools;
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask);
        RetryCommand = new AsyncRelayCommand(() => retry?.Invoke() ?? refresh?.Invoke() ?? Task.CompletedTask);
        ClearSearchCommand = new AsyncRelayCommand(() =>
        {
            Query = "";
            return Task.CompletedTask;
        }, () => Query.Length > 0);
    }

    public IReadOnlyList<ToolCardViewModel> Tools { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value ?? ""))
            {
                return;
            }

            _visibleTools = FilterTools();
            OnPropertyChanged(nameof(VisibleTools));
            OnPropertyChanged(nameof(IsSearchEmpty));
            OnPropertyChanged(nameof(HasSearchResults));
            OnPropertyChanged(nameof(ResultSummary));
            if (ClearSearchCommand is AsyncRelayCommand clearCommand)
            {
                clearCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public IReadOnlyList<ToolCardViewModel> VisibleTools => _visibleTools;

    private IReadOnlyList<ToolCardViewModel> FilterTools()
    {
        if (string.IsNullOrWhiteSpace(Query))
        {
            return Tools;
        }

        var query = Query.Trim();
        return Tools
            .Where(tool =>
                tool.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                tool.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                tool.Category.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                tool.StatusDetail.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public bool IsSearchEmpty => IsReady && Tools.Count > 0 && VisibleTools.Count == 0;
    public bool HasSearchResults => IsReady && VisibleTools.Count > 0;
    public string ResultSummary => VisibleTools.Count == 1
        ? "1 tool"
        : $"{VisibleTools.Count.ToString(CultureInfo.InvariantCulture)} tools";
}
