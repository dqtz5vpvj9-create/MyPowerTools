using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _isRunning;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !_isRunning && (_canExecute?.Invoke() ?? true);
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _execute();
        }
        finally
        {
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public abstract class ObservableViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public abstract class ShellPageViewModel : ObservableViewModel
{
    private string _title;
    private string _subtitle;
    private string _state;

    protected ShellPageViewModel(string title, string subtitle = "", string state = "ready")
    {
        _title = title;
        _subtitle = subtitle;
        _state = state;
    }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public string State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }
}

public sealed class DashboardViewModel : ShellPageViewModel
{
    public DashboardViewModel(string subtitle, IReadOnlyList<DashboardCardViewModel> cards, IReadOnlyList<ShellAlertViewModel> alerts)
        : base("Dashboard", subtitle)
    {
        Cards = cards;
        Alerts = alerts;
    }

    public IReadOnlyList<DashboardCardViewModel> Cards { get; }
    public IReadOnlyList<ShellAlertViewModel> Alerts { get; }
}

public sealed class CommandPaletteViewModel : ShellPageViewModel
{
    public CommandPaletteViewModel(string query, IReadOnlyList<CommandItemViewModel> commands)
        : base("Command Palette", $"{commands.Count} commands")
    {
        Query = query;
        Commands = commands;
    }

    public string Query { get; }
    public IReadOnlyList<CommandItemViewModel> Commands { get; }
}

public sealed class SettingsCenterViewModel : ShellPageViewModel
{
    public SettingsCenterViewModel(string selectedModuleId, string selectedModuleName, ulong revision, IReadOnlyList<SettingsFieldViewModel> fields)
        : base("Settings", selectedModuleName, fields.Count == 0 ? "empty" : "ready")
    {
        SelectedModuleId = selectedModuleId;
        Revision = revision;
        Fields = fields;
    }

    public string SelectedModuleId { get; }
    public ulong Revision { get; }
    public IReadOnlyList<SettingsFieldViewModel> Fields { get; }
}

public sealed class LogsViewModel : ShellPageViewModel
{
    public LogsViewModel(string selectedModuleName, IReadOnlyList<LogLineViewModel> lines)
        : base("Logs", selectedModuleName, lines.Count == 0 ? "empty" : "ready")
    {
        Lines = lines;
    }

    public IReadOnlyList<LogLineViewModel> Lines { get; }
}

public sealed class PackageManagerViewModel : ShellPageViewModel
{
    public PackageManagerViewModel(IReadOnlyList<PackageSummaryViewModel> packages)
        : base("Packages", $"{packages.Count} packages")
    {
        Packages = packages;
    }

    public IReadOnlyList<PackageSummaryViewModel> Packages { get; }
}

public sealed class DiagnosticsViewModel : ShellPageViewModel
{
    public DiagnosticsViewModel(string subtitle, IReadOnlyList<MetricViewModel> metrics, IReadOnlyList<RuntimeProcessViewModel> processes)
        : base("Diagnostics", subtitle)
    {
        Metrics = metrics;
        Processes = processes;
    }

    public IReadOnlyList<MetricViewModel> Metrics { get; }
    public IReadOnlyList<RuntimeProcessViewModel> Processes { get; }
}

public sealed record DashboardCardViewModel(
    string ModuleId,
    string PackageId,
    string Title,
    string State,
    string Summary,
    IReadOnlyList<MetricViewModel> Metrics,
    IReadOnlyList<ShellActionViewModel> Actions,
    ICommand DetailsCommand);

public sealed record CommandItemViewModel(
    string CommandId,
    string ModuleId,
    string Title,
    string Subtitle,
    string DangerLevel,
    bool RequiresElevation);

public sealed record PackageSummaryViewModel(
    string PackageId,
    string DisplayName,
    string Version,
    string Publisher,
    string TrustState,
    uint ModuleCount);

public sealed record RuntimeProcessViewModel(
    string TransportKind,
    string PoolKey,
    string State,
    uint ProcessId,
    string RestartPolicy,
    IReadOnlyList<string> ModuleIds);

public sealed record SettingsFieldViewModel(string Key, string Label, string EditorType, string Description, string Value);
public sealed record LogLineViewModel(string Time, string Level, string Message);
public sealed record ShellAlertViewModel(string Id, string Level, string Title, string Body);
public sealed record ShellActionViewModel(string CommandId, string Title, string Style, ICommand ExecuteCommand);
public sealed record MetricViewModel(string Label, string Value);

public static class ShellPageViewModelFactory
{
    public static DashboardViewModel FromDashboard(
        HostProto.DashboardSnapshot snapshot,
        Func<string, Task>? showDetails = null,
        Func<string, Task>? executeAction = null)
    {
        var cards = snapshot.Cards.Select(card => new DashboardCardViewModel(
            card.ModuleId,
            card.PackageId,
            card.Title,
            card.State,
            card.Summary,
            card.Metrics.Select(metric => new MetricViewModel(metric.Label, metric.Value)).ToArray(),
            card.Actions.Select(action => new ShellActionViewModel(
                action.CommandId,
                action.Title,
                action.Style,
                new AsyncRelayCommand(() => executeAction?.Invoke(action.CommandId) ?? Task.CompletedTask))).ToArray(),
            new AsyncRelayCommand(() => showDetails?.Invoke(card.ModuleId) ?? Task.CompletedTask))).ToArray();

        var alerts = snapshot.Alerts.Select(alert => new ShellAlertViewModel(
            alert.Id,
            alert.Level,
            alert.Title,
            alert.Body)).ToArray();

        return new DashboardViewModel($"{cards.Length} modules indexed, event seq {snapshot.EventSeq}", cards, alerts);
    }

    public static CommandPaletteViewModel FromCommands(string query, HostProto.ListCommandsResponse response)
    {
        var commands = response.Commands.Select(command => new CommandItemViewModel(
            command.CommandId,
            command.ModuleId,
            command.Title,
            command.Subtitle,
            command.DangerLevel,
            command.RequiresElevation)).ToArray();

        return new CommandPaletteViewModel(query, commands);
    }

    public static PackageManagerViewModel FromPackages(HostProto.ListPackagesResponse response)
    {
        var packages = response.Packages.Select(package => new PackageSummaryViewModel(
            package.PackageId,
            package.DisplayName,
            package.Version,
            package.Publisher,
            package.TrustState,
            package.ModuleCount)).ToArray();

        return new PackageManagerViewModel(packages);
    }

    public static DiagnosticsViewModel FromDiagnostics(HostProto.RuntimeDiagnostics diagnostics)
    {
        var metrics = new[]
        {
            new MetricViewModel("Packages", diagnostics.Counts.PackageCount.ToString()),
            new MetricViewModel("Modules", diagnostics.Counts.ModuleCount.ToString()),
            new MetricViewModel("Commands", diagnostics.Counts.CommandCount.ToString()),
            new MetricViewModel("Events", diagnostics.CurrentEventSeq.ToString())
        };

        var processes = diagnostics.Processes.Select(process => new RuntimeProcessViewModel(
            process.TransportKind,
            process.PoolKey,
            process.State,
            process.ProcessId,
            process.RestartPolicy,
            process.ModuleIds.ToArray())).ToArray();

        return new DiagnosticsViewModel(
            $"Collected {diagnostics.CollectedAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
            metrics,
            processes);
    }
}
