using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

/// <summary>
/// Page ViewModel for the unified <c>System &gt; Services</c> page. Lists every ServiceManager unit
/// across all tools (administration view) and exposes Start/Stop/Restart plus a refresh action.
/// </summary>
public sealed class ServicesViewModel : ShellPageViewModel
{
    public ServicesViewModel(
        string subtitle,
        IReadOnlyList<ServiceUnitViewModel> units,
        ICommand? refreshCommand = null,
        ICommand? reloadManifestsCommand = null)
        : base("Services", subtitle, state: units.Count == 0 ? "empty" : "ready")
    {
        Units = units;
        RefreshCommand = refreshCommand;
        ReloadManifestsCommand = reloadManifestsCommand;
    }

    public IReadOnlyList<ServiceUnitViewModel> Units { get; }

    public ICommand? RefreshCommand { get; }

    public ICommand? ReloadManifestsCommand { get; }

    public bool HasNoUnits => Units.Count == 0;

    public int ActiveCount => Units.Count(u => string.Equals(u.State, "active", StringComparison.OrdinalIgnoreCase));
    public int FailedCount => Units.Count(u => string.Equals(u.State, "failed", StringComparison.OrdinalIgnoreCase));
}
