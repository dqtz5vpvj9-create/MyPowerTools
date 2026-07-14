using System.Collections.ObjectModel;
using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

/// <summary>
/// Page ViewModel for the unified <c>System &gt; Services</c> page. Lists every ServiceManager unit
/// across all tools (administration view), supports search + state filter, and exposes Start/Stop/
/// Restart/Tail logs/Open tool/Toggle autostart plus page-level Refresh/Reload.
/// </summary>
public sealed class ServicesViewModel : ShellPageViewModel
{
    private string _searchText = "";
    private string _stateFilter = "all";
    private bool _disconnected;

    public ServicesViewModel(
        string subtitle,
        IReadOnlyList<ServiceUnitViewModel> units,
        ICommand? refreshCommand = null,
        ICommand? reloadManifestsCommand = null)
        : base("Services", subtitle, state: units.Count == 0 ? "empty" : "ready")
    {
        _allUnits = units.ToArray();
        Units = new ObservableCollection<ServiceUnitViewModel>(units);
        RefreshCommand = refreshCommand;
        ReloadManifestsCommand = reloadManifestsCommand;
        ApplyFilter();
    }

    private readonly ServiceUnitViewModel[] _allUnits;
    public ObservableCollection<ServiceUnitViewModel> Units { get; }

    public ICommand? RefreshCommand { get; }
    public ICommand? ReloadManifestsCommand { get; }

    public bool HasNoUnits => Units.Count == 0;

    public int ActiveCount => _allUnits.Count(u => string.Equals(u.State, "active", StringComparison.OrdinalIgnoreCase));
    public int FailedCount => _allUnits.Count(u => string.Equals(u.State, "failed", StringComparison.OrdinalIgnoreCase));
    public int InactiveCount => _allUnits.Count(u => string.Equals(u.State, "inactive", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the ServiceManager stream is faulted and showing the last snapshot.</summary>
    public bool Disconnected
    {
        get => _disconnected;
        set => SetProperty(ref _disconnected, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    /// <summary>One of: all, active, inactive, failed, degraded.</summary>
    public string StateFilter
    {
        get => _stateFilter;
        set
        {
            if (SetProperty(ref _stateFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public IReadOnlyList<string> StateFilters { get; } = new[] { "all", "active", "inactive", "degraded", "failed" };

    private void ApplyFilter()
    {
        var query = _searchText.Trim();
        IEnumerable<ServiceUnitViewModel> view = _allUnits;

        if (!string.IsNullOrEmpty(query))
        {
            view = view.Where(u =>
                u.UnitId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                u.ToolId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                u.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(_stateFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            view = view.Where(u => string.Equals(u.State, _stateFilter, StringComparison.OrdinalIgnoreCase));
        }

        Units.Clear();
        foreach (var item in view)
        {
            Units.Add(item);
        }

        OnPropertyChanged(nameof(HasNoUnits));
    }
}
