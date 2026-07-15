using System.ComponentModel;
using System.Windows.Input;

namespace MyPowerTools.AvaloniaSdk;

/// <summary>
/// Optional reusable Service UI data types for dotnet-surface tools. Tools can compose these into
/// their own product UI (badges, buttons, cards, log previews), or build entirely custom visuals —
/// the SDK does not mandate their use or enforce a status bar height / page position.
/// </summary>

/// <summary>Record describing a service unit's display state for the standard components.</summary>
public sealed record ServiceUnitDisplayState(
    string State,
    int? Pid,
    TimeSpan? Uptime,
    int RestartCount,
    string? LastError,
    bool Ready)
{
    public string StateLabel => State?.ToLowerInvariant() ?? "unknown";
    public string Summary => IsRunning ? $"active · pid {Pid ?? 0}" : StateLabel;
    public bool IsRunning => State is "active" or "degraded";
    public bool HasError => !string.IsNullOrWhiteSpace(LastError);
    public string UptimeText => Uptime is null ? "—" : Uptime.Value.TotalDays >= 1
        ? $"{(int)Uptime.Value.TotalDays}d"
        : Uptime.Value.TotalHours >= 1 ? $"{(int)Uptime.Value.TotalHours}h" : $"{(int)Uptime.Value.TotalMinutes}m";
}

/// <summary>View model for a service status badge: state + color hint + label.</summary>
public sealed class ServiceStatusBadgeViewModel : MptObservableViewModel
{
    private string _state = "inactive";
    private string _label = "";

    public string State { get => _state; set => SetProperty(ref _state, value); }
    public string Label { get => _label; set => SetProperty(ref _label, value); }
    public string ColorHint => State.ToLowerInvariant() switch
    {
        "active" => "green",
        "degraded" => "orange",
        "failed" => "red",
        "activating" or "deactivating" => "yellow",
        _ => "gray"
    };
}

/// <summary>View model for a service recovery card: error + retry command.</summary>
public sealed class ServiceRecoveryCardViewModel : MptObservableViewModel
{
    private string _errorMessage = "";
    public string ErrorMessage { get => _errorMessage; set => SetProperty(ref _errorMessage, value); }
    public ICommand? RestartCommand { get; init; }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
}

/// <summary>View model for a service log preview entry.</summary>
public sealed record ServiceLogPreviewEntry(string Time, string Level, string Message);
