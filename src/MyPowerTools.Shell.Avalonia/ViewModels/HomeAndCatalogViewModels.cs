using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class HomeViewModel : ToolProductPageViewModel
{
    private const int MaxActionShortcuts = 3;
    private const int MaxDashboardTools = 5;
    private const int MaxAttentionTools = 6;

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
            "Home",
            "Here's how your tools are doing.",
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
        AttentionTools = AllTools
            .Where(tool => tool.NeedsAttention)
            .Take(MaxAttentionTools)
            .ToArray();

        QuickAccessActions =
        [
            new HomeDashboardActionViewModel("Browse tools", "See everything you can open", "\uE71D", BrowseToolsCommand, IsSymbolIcon: true),
            new HomeDashboardActionViewModel("Refresh", "Check tool status again", "\uE72C", RefreshCommand, IsSymbolIcon: true)
        ];
    }

    public IReadOnlyList<ToolCardViewModel> FavoriteTools { get; }
    public IReadOnlyList<ToolCardViewModel> RecentTools { get; }
    public IReadOnlyList<HomeActivityItemViewModel> Activities { get; }
    public IReadOnlyList<ToolCardViewModel> AllTools { get; }
    public IReadOnlyList<ToolCardViewModel> DashboardTools { get; }
    public IReadOnlyList<ToolCardViewModel> ActionableTools { get; }
    public IReadOnlyList<ToolCardViewModel> AttentionTools { get; }
    public IReadOnlyList<HomeDashboardActionViewModel> QuickAccessActions { get; }
    public int TotalToolCount { get; }
    public ICommand BrowseToolsCommand { get; }
    public ICommand OpenActivityCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }

    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Good evening",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening"
    };

    public string DashboardToolsTitle => HasFavoriteTools ? "Favorites & recent" : HasRecentTools ? "Recent" : "Tools";
    public string ActionShortcutsDetail => HasFavoriteTools || HasRecentTools ? "Your frequently used tools" : "Get started";
    public bool HasFavoriteTools => FavoriteTools.Count > 0;
    public bool HasRecentTools => RecentTools.Count > 0;
    public bool HasActivities => Activities.Count > 0;
    public bool HasDashboardTools => DashboardTools.Count > 0;
    public bool HasActionableTools => ActionableTools.Count > 0;
    public bool HasQuickAccessActions => QuickAccessActions.Count > 0;
    public bool HasAttentionTools => AttentionTools.Count > 0;
    public int ReadyToolCount => AllTools.Count(tool => tool.IsAvailable);
    public int PendingToolCount => AllTools.Count(tool => tool.IsInDevelopment);
    public int PausedToolCount => AllTools.Count(tool => tool.IsPaused);
    public int UnavailableToolCount => AllTools.Count(tool => tool.IsUnavailable);
    public int AttentionToolCount => AllTools.Count(tool => tool.IsAttentionStatus);
    public int NeedsAttentionCount => AllTools.Count(tool => tool.NeedsAttention);
    public int NotYetAvailableCount => PendingToolCount + PausedToolCount;
    public bool AllToolsHealthy => NeedsAttentionCount == 0;

    public string ToolCountSummary => TotalToolCount == 1
        ? "1 tool installed"
        : $"{Count(TotalToolCount)} tools installed";

    public string DashboardStatusLabel => NeedsAttentionCount switch
    {
        0 => ReadyToolCount == 0 ? "No tools are ready yet" : "All tools are ready",
        1 => "1 tool needs attention",
        var count => $"{Count(count)} tools need attention"
    };

    public bool DashboardHasWarning => NeedsAttentionCount > 0;

    public string DashboardStatusDetail
    {
        get
        {
            var details = new List<string>();
            if (ReadyToolCount > 0) details.Add($"{Count(ReadyToolCount)} ready");
            if (NotYetAvailableCount > 0) details.Add($"{Count(NotYetAvailableCount)} coming soon");
            if (UnavailableToolCount > 0) details.Add($"{Count(UnavailableToolCount)} can't be opened");
            return details.Count == 0 ? "Everything is running normally" : string.Join(" · ", details);
        }
    }

    public string ActivitySummary => Activities.Count == 1
        ? "1 recent run"
        : $"{Count(Activities.Count)} recent runs";

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

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
    private const string AllFilter = "all";
    private const string ReadyFilter = "ready";
    private const string LaterFilter = "later";
    private const string FavoritesFilter = "favorites";

    private string _query = "";
    private string _filter = AllFilter;
    private IReadOnlyList<ToolCardViewModel> _visibleTools;

    public ToolCatalogViewModel(
        IReadOnlyList<ToolCardViewModel> tools,
        ToolProductState productState = ToolProductState.Ready,
        string errorMessage = "",
        Func<Task>? refresh = null,
        Func<Task>? retry = null)
        : base(
            "All tools",
            "Everything you can open in MyPowerTools. Star a tool to keep it on Home.",
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
        ShowAllCommand = FilterCommand(AllFilter);
        ShowReadyCommand = FilterCommand(ReadyFilter);
        ShowLaterCommand = FilterCommand(LaterFilter);
        ShowFavoritesCommand = FilterCommand(FavoritesFilter);

        foreach (var tool in tools)
        {
            tool.PropertyChanged += OnToolPropertyChanged;
        }
    }

    public IReadOnlyList<ToolCardViewModel> Tools { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ShowAllCommand { get; }
    public ICommand ShowReadyCommand { get; }
    public ICommand ShowLaterCommand { get; }
    public ICommand ShowFavoritesCommand { get; }

    public string Query
    {
        get => _query;
        set
        {
            if (!SetProperty(ref _query, value ?? ""))
            {
                return;
            }

            OnPropertyChanged(nameof(HasQuery));
            Refilter();
            if (ClearSearchCommand is AsyncRelayCommand clearCommand)
            {
                clearCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasQuery => Query.Length > 0;
    public IReadOnlyList<ToolCardViewModel> VisibleTools => _visibleTools;
    public bool IsAllFilter => _filter == AllFilter;
    public bool IsReadyFilter => _filter == ReadyFilter;
    public bool IsLaterFilter => _filter == LaterFilter;
    public bool IsFavoritesFilter => _filter == FavoritesFilter;
    public int ReadyCount => Tools.Count(tool => tool.CanOpen);
    public int LaterCount => Tools.Count(tool => !tool.CanOpen);
    public int FavoriteCount => Tools.Count(tool => tool.IsFavorite);
    public bool HasLaterTools => LaterCount > 0;
    public string AllFilterLabel => $"All  {Tools.Count.ToString(CultureInfo.InvariantCulture)}";
    public string ReadyFilterLabel => $"Ready  {ReadyCount.ToString(CultureInfo.InvariantCulture)}";
    public string LaterFilterLabel => $"Not available  {LaterCount.ToString(CultureInfo.InvariantCulture)}";
    public string FavoritesFilterLabel => $"Favorites  {FavoriteCount.ToString(CultureInfo.InvariantCulture)}";

    public bool IsSearchEmpty => IsReady && Tools.Count > 0 && VisibleTools.Count == 0;
    public bool HasSearchResults => IsReady && VisibleTools.Count > 0;
    public string EmptyResultTitle => HasQuery ? "No matching tools" : IsFavoritesFilter ? "No favorites yet" : "Nothing here";
    public string EmptyResultDetail => HasQuery
        ? "Try a tool name, category or task."
        : IsFavoritesFilter ? "Select the star next to a tool to add it here." : "Try another filter.";
    public string ResultSummary => VisibleTools.Count == 1
        ? "1 tool"
        : $"{VisibleTools.Count.ToString(CultureInfo.InvariantCulture)} tools";

    private ICommand FilterCommand(string filter) => new AsyncRelayCommand(() =>
    {
        _filter = filter;
        Refilter();
        return Task.CompletedTask;
    });

    private void OnToolPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ToolCardViewModel.IsFavorite))
        {
            return;
        }

        OnPropertyChanged(nameof(FavoriteCount));
        OnPropertyChanged(nameof(FavoritesFilterLabel));
        if (IsFavoritesFilter)
        {
            Refilter();
        }
    }

    private void Refilter()
    {
        _visibleTools = FilterTools();
        OnPropertyChanged(nameof(VisibleTools));
        OnPropertyChanged(nameof(IsSearchEmpty));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(IsAllFilter));
        OnPropertyChanged(nameof(IsReadyFilter));
        OnPropertyChanged(nameof(IsLaterFilter));
        OnPropertyChanged(nameof(IsFavoritesFilter));
        OnPropertyChanged(nameof(EmptyResultTitle));
        OnPropertyChanged(nameof(EmptyResultDetail));
    }

    private IReadOnlyList<ToolCardViewModel> FilterTools()
    {
        IEnumerable<ToolCardViewModel> source = _filter switch
        {
            ReadyFilter => Tools.Where(tool => tool.CanOpen),
            LaterFilter => Tools.Where(tool => !tool.CanOpen),
            FavoritesFilter => Tools.Where(tool => tool.IsFavorite),
            _ => Tools
        };

        if (string.IsNullOrWhiteSpace(Query))
        {
            return source.ToArray();
        }

        return source
            .Select(tool => (Tool: tool, Score: ToolSearchMatcher.Score(
                Query, tool.Title, tool.ToolId, tool.Description, tool.Category, tool.StatusDetail)))
            .Where(match => match.Score >= 0)
            .OrderByDescending(match => match.Score)
            .Select(match => match.Tool)
            .ToArray();
    }
}
