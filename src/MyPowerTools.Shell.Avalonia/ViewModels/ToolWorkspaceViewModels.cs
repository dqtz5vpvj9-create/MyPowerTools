using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed record ToolWorkspaceViewModel(
    string RouteId,
    string Label,
    string Title,
    string Subtitle,
    string Description,
    IReadOnlyList<MetricViewModel> Highlights,
    IReadOnlyList<ProductActionViewModel> Actions)
{
    public bool HasHighlights => Highlights.Count > 0;
    public bool HasActions => Actions.Count > 0;
}

public sealed class ToolSectionNavigationItemViewModel : ObservableViewModel
{
    private bool _isSelected;

    public ToolSectionNavigationItemViewModel(ToolWorkspaceViewModel workspace, ICommand navigateCommand)
    {
        Workspace = workspace;
        NavigateCommand = navigateCommand;
    }

    public ToolWorkspaceViewModel Workspace { get; }
    public string RouteId => Workspace.RouteId;
    public string Label => Workspace.Label;
    public ICommand NavigateCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}

public sealed class ToolHostViewModel : ToolProductPageViewModel
{
    private ToolWorkspaceViewModel? _currentWorkspace;

    public ToolHostViewModel(
        ToolCardViewModel tool,
        IReadOnlyList<ToolWorkspaceViewModel> workspaces,
        string initialRouteId = "",
        ToolProductState productState = ToolProductState.Ready,
        string errorMessage = "",
        Func<string, string, Task>? navigateRoute = null,
        Func<Task>? browseAllTools = null,
        Func<Task>? refresh = null,
        Func<Task>? retry = null)
        : base(
            tool.Title,
            tool.Description,
            productState == ToolProductState.Ready && workspaces.Count == 0 ? ToolProductState.Empty : productState,
            errorMessage)
    {
        Tool = tool;
        BrowseAllToolsCommand = new AsyncRelayCommand(() => browseAllTools?.Invoke() ?? Task.CompletedTask);
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask);
        RetryCommand = new AsyncRelayCommand(() => retry?.Invoke() ?? refresh?.Invoke() ?? Task.CompletedTask);

        var navigationItems = new List<ToolSectionNavigationItemViewModel>();
        foreach (var workspace in workspaces)
        {
            ToolSectionNavigationItemViewModel? item = null;
            item = new ToolSectionNavigationItemViewModel(
                workspace,
                new AsyncRelayCommand(async () =>
                {
                    SelectWorkspace(item!);
                    if (navigateRoute is not null)
                    {
                        await navigateRoute(Tool.ToolId, workspace.RouteId).ConfigureAwait(true);
                    }
                }));
            navigationItems.Add(item);
        }

        Sections = navigationItems;
        var selected = navigationItems.FirstOrDefault(item =>
                           string.Equals(item.RouteId, initialRouteId, StringComparison.OrdinalIgnoreCase))
                       ?? navigationItems.FirstOrDefault();
        if (selected is not null)
        {
            SelectWorkspace(selected);
        }
    }

    public ToolCardViewModel Tool { get; }
    public IReadOnlyList<ToolSectionNavigationItemViewModel> Sections { get; }
    public ICommand BrowseAllToolsCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
    public bool HasSections => Sections.Count > 0;
    public string DevelopmentTitle => $"{Tool.Title} interface is in development";
    public string DevelopmentMessage => string.IsNullOrWhiteSpace(Tool.StatusDetail)
        ? "The product workspace has not been delivered yet. You can return to the tool catalog or refresh after an update."
        : Tool.StatusDetail;

    public ToolWorkspaceViewModel? CurrentWorkspace
    {
        get => _currentWorkspace;
        private set => SetProperty(ref _currentWorkspace, value);
    }

    private void SelectWorkspace(ToolSectionNavigationItemViewModel selected)
    {
        foreach (var section in Sections)
        {
            section.IsSelected = ReferenceEquals(section, selected);
        }

        CurrentWorkspace = selected.Workspace;
    }
}

public sealed class SystemHubItemViewModel
{
    public SystemHubItemViewModel(
        string id,
        string title,
        string description,
        string iconGlyph,
        string statusLabel,
        Func<string, Task>? open = null)
    {
        Id = id;
        Title = title;
        Description = description;
        IconGlyph = iconGlyph;
        StatusLabel = statusLabel;
        OpenCommand = new AsyncRelayCommand(() => open?.Invoke(Id) ?? Task.CompletedTask);
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string IconGlyph { get; }
    public string StatusLabel { get; }
    public ICommand OpenCommand { get; }
    public string OpenActionLabel => $"Open {Title}";
}

public sealed class SystemHubViewModel : ToolProductPageViewModel
{
    public SystemHubViewModel(
        IReadOnlyList<SystemHubItemViewModel> destinations,
        ToolProductState productState = ToolProductState.Ready,
        string errorMessage = "",
        Func<Task>? refresh = null,
        Func<Task>? retry = null)
        : base(
            "System & Maintenance",
            "Runtime health, packages, modules, logs, permissions, and support diagnostics.",
            productState == ToolProductState.Ready && destinations.Count == 0 ? ToolProductState.Empty : productState,
            errorMessage)
    {
        Destinations = destinations;
        RefreshCommand = new AsyncRelayCommand(() => refresh?.Invoke() ?? Task.CompletedTask);
        RetryCommand = new AsyncRelayCommand(() => retry?.Invoke() ?? refresh?.Invoke() ?? Task.CompletedTask);
    }

    public IReadOnlyList<SystemHubItemViewModel> Destinations { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RetryCommand { get; }
}
