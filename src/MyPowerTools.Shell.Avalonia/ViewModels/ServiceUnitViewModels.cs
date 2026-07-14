using System.Windows.Input;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

/// <summary>
/// One row in the unified Services page: a ServiceManager unit with its live state and control actions.
/// </summary>
public sealed record ServiceUnitViewModel(
    string UnitId,
    string ToolId,
    string DisplayName,
    string State,
    string StateSummary,
    int Pid,
    string Uptime,
    string Version,
    bool Autostart,
    int RestartCount,
    string LastError,
    bool Ready,
    ICommand? StartCommand = null,
    ICommand? StopCommand = null,
    ICommand? RestartCommand = null)
{
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);
    public bool IsControlEnabled => StartCommand is not null;
}
