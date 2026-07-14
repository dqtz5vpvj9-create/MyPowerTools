using System.Windows.Input;
using SM = MyPowerTools.Protocol.ServiceManager.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public static partial class ShellPageViewModelFactory
{
    public static ServicesViewModel FromServiceUnits(
        SM.ListUnitsResponse response,
        Func<string, Task>? startUnit = null,
        Func<string, Task>? stopUnit = null,
        Func<string, Task>? restartUnit = null,
        Func<Task>? refresh = null,
        Func<Task>? reloadManifests = null)
    {
        var units = response.Units
            .OrderBy(u => u.ToolId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(u => ToServiceUnitViewModel(u, startUnit, stopUnit, restartUnit))
            .ToArray();

        var refreshCommand = refresh is null ? null : new AsyncRelayCommand(refresh, operationName: "ServicesRefresh");
        var reloadCommand = reloadManifests is null ? null : new AsyncRelayCommand(reloadManifests, operationName: "ServicesReload");

        var subtitle = units.Length == 0
            ? "No Service Units registered."
            : $"{units.Length} unit(s) · {units.Count(u => string.Equals(u.State, "active", StringComparison.OrdinalIgnoreCase))} active";

        return new ServicesViewModel(subtitle, units, refreshCommand, reloadCommand);
    }

    private static ServiceUnitViewModel ToServiceUnitViewModel(
        SM.UnitSnapshot unit,
        Func<string, Task>? startUnit,
        Func<string, Task>? stopUnit,
        Func<string, Task>? restartUnit)
    {
        var state = MapUnitState(unit.State);
        var pid = unit.Pid > 0 ? unit.Pid : 0;
        var uptime = unit.Uptime is not null && (unit.Uptime.Seconds != 0 || unit.Uptime.Nanos != 0)
            ? FormatUptime(unit.Uptime.ToTimeSpan())
            : "—";
        var ready = unit.Readiness is not null && unit.Readiness.Ok;
        var lastError = string.IsNullOrWhiteSpace(unit.LastError) ? "" : unit.LastError;

        ICommand? startCmd = startUnit is null ? null : new AsyncRelayCommand(() => startUnit(unit.UnitId), operationName: $"StartUnit:{unit.UnitId}");
        ICommand? stopCmd = stopUnit is null ? null : new AsyncRelayCommand(() => stopUnit(unit.UnitId), operationName: $"StopUnit:{unit.UnitId}");
        ICommand? restartCmd = restartUnit is null ? null : new AsyncRelayCommand(() => restartUnit(unit.UnitId), operationName: $"RestartUnit:{unit.UnitId}");

        return new ServiceUnitViewModel(
            UnitId: unit.UnitId,
            ToolId: unit.ToolId,
            DisplayName: string.IsNullOrWhiteSpace(unit.DisplayName) ? unit.UnitId : unit.DisplayName,
            State: state,
            StateSummary: DescribeState(unit.State, pid, ready),
            Pid: pid,
            Uptime: uptime,
            Version: unit.Version,
            Autostart: unit.Autostart,
            RestartCount: unit.RestartCount,
            LastError: lastError,
            Ready: ready,
            StartCommand: startCmd,
            StopCommand: stopCmd,
            RestartCommand: restartCmd);
    }

    private static string MapUnitState(SM.UnitState state) => state switch
    {
        SM.UnitState.Inactive => "inactive",
        SM.UnitState.Activating => "activating",
        SM.UnitState.Active => "active",
        SM.UnitState.Degraded => "degraded",
        SM.UnitState.Failed => "failed",
        SM.UnitState.Deactivating => "deactivating",
        _ => "unknown"
    };

    private static string DescribeState(SM.UnitState state, int pid, bool ready)
    {
        var label = MapUnitState(state);
        if (pid > 0)
        {
            label += $" · pid {pid}";
        }

        if (ready)
        {
            label += " · ready";
        }

        return label;
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h";
        }

        if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        }

        if (uptime.TotalMinutes >= 1)
        {
            return $"{(int)uptime.TotalMinutes}m";
        }

        return $"{(int)uptime.TotalSeconds}s";
    }
}
