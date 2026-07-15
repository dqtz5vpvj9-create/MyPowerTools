using System.Globalization;
using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class HomeViewModel : ToolProductPageViewModel
{
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
        Func<Task>? retry = null)
        : base(
            "Dashboard",
            "Your tools, current work, and the next useful action in one place.",
            ResolveState(productState, favoriteTools, recentTools, activities),
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

        DashboardTools = favoriteTools
            .Concat(recentTools)
            .DistinctBy(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        ActionableTools = DashboardTools
            .Where(tool => tool.CanOpen)
            .ToArray();

        var quickAccessActions = DashboardTools
            .Where(tool => tool.IsAvailable)
            .Take(2)
            .Select(tool => new HomeDashboardActionViewModel(
                tool.PrimaryActionLabel,
                tool.Title,
                tool.IconGlyph,
                tool.OpenCommand))
            .ToList();
        quickAccessActions.Add(new HomeDashboardActionViewModel(
            "Browse tools",
            $"{totalToolCount.ToString(CultureInfo.InvariantCulture)} registered",
            "\uE71D",
            BrowseToolsCommand,
            IsSymbolIcon: true));
        quickAccessActions.Add(new HomeDashboardActionViewModel(
            "Refresh status",
            "Check availability",
            "\uE72C",
            RefreshCommand,
            IsSymbolIcon: true));
        QuickAccessActions = quickAccessActions;
    }

    public IReadOnlyList<ToolCardViewModel> FavoriteTools { get; }
    public IReadOnlyList<ToolCardViewModel> RecentTools { get; }
    public IReadOnlyList<HomeActivityItemViewModel> Activities { get; }
    public IReadOnlyList<ToolCardViewModel> DashboardTools { get; }
    public IReadOnlyList<ToolCardViewModel> ActionableTools { get; }
    public IReadOnlyList<HomeDashboardActionViewModel> QuickAccessActions { get; }
    public int TotalToolCount { get; }
    public ICommand BrowseToolsCommand { get; }
    public ICommand OpenActivityCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public bool HasFavoriteTools => FavoriteTools.Count > 0;
    public bool HasRecentTools => RecentTools.Count > 0;
    public bool HasActivities => Activities.Count > 0;
    public bool HasDashboardTools => DashboardTools.Count > 0;
    public bool HasActionableTools => ActionableTools.Count > 0;
    public bool HasQuickAccessActions => QuickAccessActions.Count > 0;
    public int ReadyToolCount => DashboardTools.Count(tool => tool.IsAvailable);
    public int PendingToolCount => DashboardTools.Count(tool => tool.IsInDevelopment);
    public int PausedToolCount => DashboardTools.Count(tool => tool.IsPaused);
    public int UnavailableToolCount => DashboardTools.Count(tool => tool.IsUnavailable);
    public string ToolCountSummary => TotalToolCount == 1
        ? "1 tool is ready to open"
        : $"{TotalToolCount.ToString(CultureInfo.InvariantCulture)} tools registered";
    public string DashboardStatusLabel => ReadyToolCount == 1
        ? "1 tool workspace available"
        : $"{ReadyToolCount.ToString(CultureInfo.InvariantCulture)} tool workspaces available";
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

    public ToolCatalogViewModel(
        IReadOnlyList<ToolCardViewModel> tools,
        ToolProductState productState = ToolProductState.Ready,
        string errorMessage = "",
        Func<Task>? refresh = null,
        Func<Task>? retry = null)
        : base(
            "Tools",
            "Open a tool to complete a task. Runtime modules and diagnostics live in System & Maintenance.",
            productState == ToolProductState.Ready && tools.Count == 0 ? ToolProductState.Empty : productState,
            errorMessage)
    {
        Tools = tools;
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

    public IReadOnlyList<ToolCardViewModel> VisibleTools
    {
        get
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
    }

    public bool IsSearchEmpty => IsReady && Tools.Count > 0 && VisibleTools.Count == 0;
    public bool HasSearchResults => IsReady && VisibleTools.Count > 0;
    public string ResultSummary => VisibleTools.Count == 1
        ? "1 tool"
        : $"{VisibleTools.Count.ToString(CultureInfo.InvariantCulture)} tools";
}
