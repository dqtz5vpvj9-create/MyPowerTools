using System.Windows.Input;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

/// <summary>
/// One row in the unified Services page: a ServiceManager unit with its live state and control actions.
/// Commands are wired by the factory from controller callbacks so the gRPC client lifecycle stays in the controller.
/// </summary>
public sealed class ServiceUnitViewModel : ObservableViewModel
{
    public ServiceUnitViewModel(
        string unitId,
        string toolId,
        string displayName,
        string state,
        string stateSummary,
        int pid,
        string uptime,
        string version,
        bool autostart,
        int restartCount,
        string lastError,
        bool ready,
        ICommand? startCommand = null,
        ICommand? stopCommand = null,
        ICommand? restartCommand = null,
        ICommand? tailLogsCommand = null,
        ICommand? openToolCommand = null,
        ICommand? toggleAutostartCommand = null)
    {
        UnitId = unitId;
        ToolId = toolId;
        DisplayName = displayName;
        _state = state;
        StateSummary = stateSummary;
        Pid = pid;
        Uptime = uptime;
        Version = version;
        _autostart = autostart;
        RestartCount = restartCount;
        LastError = lastError;
        Ready = ready;
        StartCommand = startCommand;
        StopCommand = stopCommand;
        RestartCommand = restartCommand;
        TailLogsCommand = tailLogsCommand;
        OpenToolCommand = openToolCommand;
        ToggleAutostartCommand = toggleAutostartCommand;
    }

    public string UnitId { get; }
    public string ToolId { get; }
    public string DisplayName { get; }
    public string StateSummary { get; }
    public int Pid { get; }
    public string Uptime { get; }
    public string Version { get; }
    public int RestartCount { get; }
    public string LastError { get; }
    public bool Ready { get; }

    private string _state;
    public string State { get => _state; set => SetProperty(ref _state, value); }

    private bool _autostart;
    public bool Autostart { get => _autostart; set => SetProperty(ref _autostart, value); }

    public ICommand? StartCommand { get; }
    public ICommand? StopCommand { get; }
    public ICommand? RestartCommand { get; }
    public ICommand? TailLogsCommand { get; }
    public ICommand? OpenToolCommand { get; }
    public ICommand? ToggleAutostartCommand { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);
    public bool IsControlEnabled => StartCommand is not null;
    public string AutostartLabel => Autostart ? "Autostart: on" : "Autostart: off";
}

/// <summary>One tailed log line shown in the unit detail / logs flyout.</summary>
public sealed record ServiceUnitLogViewModel(string Time, string Level, string Message);
