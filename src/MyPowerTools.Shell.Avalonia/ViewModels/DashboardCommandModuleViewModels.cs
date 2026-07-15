using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Google.Protobuf.WellKnownTypes;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

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
    public IReadOnlyList<ShellAlertViewModel> VisibleAlerts => Alerts.Take(3).ToArray();
    public string RunningCount => Cards.Count(card => string.Equals(card.State, "running", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
    public string DegradedCount => Cards.Count(card => string.Equals(card.State, "degraded", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
    public string CommandCount => Cards.Sum(card => card.Actions.Count).ToString(CultureInfo.InvariantCulture);
    public string NotificationCount => Alerts.Count.ToString(CultureInfo.InvariantCulture);
    public bool HasAlerts => Alerts.Count > 0;
    public string AlertSummary => Alerts.Count <= 3
        ? $"{Alerts.Count} issue(s) need attention"
        : $"{Alerts.Count} issue(s) need attention, showing first 3";
}

public sealed class CommandPaletteViewModel : ShellPageViewModel
{
    private CommandSearchResultViewModel? _selectedResult;
    private bool _isDetailsOpen;

    public CommandPaletteViewModel(string query, IReadOnlyList<CommandItemViewModel> commands)
        : base(
            string.IsNullOrWhiteSpace(query) ? "Quick access" : "Search results",
            $"{commands.Count} results",
            commands.Count == 0 ? "empty" : "ready")
    {
        Query = query;
        Commands = RankCommands(query, commands);
        ProviderGroups = Commands
            .GroupBy(command => command.ModuleId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new CommandProviderGroupViewModel(
                group.Key.Length == 0 ? "unknown" : group.Key,
                group.Key.Length == 0 ? "Unknown provider" : group.Key,
                $"{group.Count()} command(s)",
                group.ToArray()))
            .ToArray();
        RecentCommands = Commands
            .Take(4)
            .Select(command => new CommandPaletteHistoryItemViewModel(command.CommandId, command.Title, command.ExecutionStateLabel))
            .ToArray();
        Results = Commands
            .Select(command => new CommandSearchResultViewModel(command, ActivateResultAsync))
            .ToArray();
        VisibleResults = Results
            .Take(6)
            .ToArray();
        BackToResultsCommand = new AsyncRelayCommand(() =>
        {
            IsDetailsOpen = false;
            return Task.CompletedTask;
        });
        SelectResult(VisibleResults.FirstOrDefault());
    }

    public string Query { get; }
    public IReadOnlyList<CommandItemViewModel> Commands { get; }
    public IReadOnlyList<CommandProviderGroupViewModel> ProviderGroups { get; }
    public IReadOnlyList<CommandPaletteHistoryItemViewModel> RecentCommands { get; }
    public IReadOnlyList<CommandSearchResultViewModel> Results { get; }
    public IReadOnlyList<CommandSearchResultViewModel> VisibleResults { get; }
    public ICommand BackToResultsCommand { get; }
    public CommandItemViewModel? SelectedCommand => _selectedResult?.Command;
    public bool IsEmpty => VisibleResults.Count == 0;
    public bool HasSelection => SelectedCommand is not null;
    public bool HasRecentCommands => RecentCommands.Count > 0;
    public bool IsResultsVisible => !IsDetailsOpen;
    public string ResultCountText => Commands.Count == VisibleResults.Count
        ? $"{Commands.Count} results"
        : $"{VisibleResults.Count} of {Commands.Count} results";
    public string KeyboardSelectionHint => "Use Up and Down to select, then press Enter.";
    public string SelectionPreview => SelectedCommand?.ExecutionPreview ?? "No command selected.";
    public string DangerousConfirmationText => SelectedCommand?.DangerConfirmationText ?? "";
    public bool RequiresDangerousConfirmation => SelectedCommand?.RequiresDangerousConfirmation == true;

    public bool IsDetailsOpen
    {
        get => _isDetailsOpen;
        private set
        {
            if (SetProperty(ref _isDetailsOpen, value))
            {
                OnPropertyChanged(nameof(IsResultsVisible));
            }
        }
    }

    public void MoveSelection(int delta)
    {
        if (VisibleResults.Count == 0 || IsDetailsOpen)
        {
            return;
        }

        var currentIndex = _selectedResult is null
            ? -1
            : Array.IndexOf(VisibleResults.ToArray(), _selectedResult);
        var nextIndex = currentIndex < 0
            ? 0
            : (currentIndex + delta + VisibleResults.Count) % VisibleResults.Count;
        SelectResult(VisibleResults[nextIndex]);
    }

    public Task ActivateSelectedAsync()
    {
        return _selectedResult?.ActivateAsync() ?? Task.CompletedTask;
    }

    private async Task ActivateResultAsync(CommandSearchResultViewModel result)
    {
        SelectResult(result);
        var command = result.Command;
        if (command.IsNavigation && !result.RequiresReview)
        {
            await command.ExecuteAsync();
            return;
        }

        IsDetailsOpen = true;
        if (!result.RequiresReview)
        {
            await command.ExecuteAsync();
        }
    }

    private void SelectResult(CommandSearchResultViewModel? result)
    {
        if (ReferenceEquals(_selectedResult, result))
        {
            return;
        }

        if (_selectedResult is not null)
        {
            _selectedResult.IsSelected = false;
        }

        _selectedResult = result;
        if (_selectedResult is not null)
        {
            _selectedResult.IsSelected = true;
        }

        OnPropertyChanged(nameof(SelectedCommand));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionPreview));
        OnPropertyChanged(nameof(DangerousConfirmationText));
        OnPropertyChanged(nameof(RequiresDangerousConfirmation));
    }

    private static IReadOnlyList<CommandItemViewModel> RankCommands(string query, IReadOnlyList<CommandItemViewModel> commands)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return commands;
        }

        return commands
            .Select(command => new { Command = command, Score = FuzzyScore(query, command) })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Command.ModuleId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Command.Title, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Command)
            .ToArray();
    }

    private static int FuzzyScore(string query, CommandItemViewModel command)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 0;
        }

        var normalizedQuery = query.Trim();
        var haystack = $"{command.Title} {command.Subtitle} {command.CommandId} {command.ModuleId}";
        if (haystack.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1000 + normalizedQuery.Length;
        }

        var score = 0;
        var cursor = 0;
        foreach (var ch in normalizedQuery)
        {
            var index = haystack.IndexOf(ch.ToString(), cursor, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            score += index == cursor ? 25 : 10;
            cursor = index + 1;
        }

        return score;
    }
}

public sealed class CommandSearchResultViewModel : ObservableViewModel
{
    private readonly Func<CommandSearchResultViewModel, Task> _activate;
    private bool _isSelected;

    public CommandSearchResultViewModel(
        CommandItemViewModel command,
        Func<CommandSearchResultViewModel, Task> activate)
    {
        Command = command;
        _activate = activate;
        ActivateCommand = new AsyncRelayCommand(ActivateAsync);
    }

    public CommandItemViewModel Command { get; }
    public string Title => Command.Title;
    public string Subtitle => Command.Subtitle;
    public string CategoryLabel => Command.CategoryLabel;
    public string IconGlyph => Command.IconGlyph;
    public bool RequiresReview => Command.HasParameters || Command.RequiresDangerousConfirmation;
    public string ActionHint => RequiresReview ? "Review" : Command.IsNavigation ? "Open" : "Run";
    public ICommand ActivateCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public Task ActivateAsync()
    {
        return _activate(this);
    }
}

public sealed record CommandProviderGroupViewModel(
    string ProviderId,
    string Label,
    string CountText,
    IReadOnlyList<CommandItemViewModel> Commands);

public sealed record CommandPaletteHistoryItemViewModel(string CommandId, string Title, string State);

public sealed class ModulesViewModel : ShellPageViewModel
{
    public ModulesViewModel(IReadOnlyList<ModuleSummaryItemViewModel> modules)
        : base("Modules", $"{modules.Count} modules", modules.Count == 0 ? "empty" : "ready")
    {
        Modules = modules;
    }

    public IReadOnlyList<ModuleSummaryItemViewModel> Modules { get; }
    public bool IsEmpty => Modules.Count == 0;
}

public sealed class ModuleDetailViewModel : ShellPageViewModel
{
    public ModuleDetailViewModel(
        string moduleId,
        string packageId,
        string displayName,
        string state,
        string summary,
        IReadOnlyList<MetricViewModel> metrics,
        IReadOnlyList<ModulePermissionViewModel> permissions,
        IReadOnlyList<ModuleRequirementViewModel> requirements,
        IReadOnlyList<ModuleDiagnosticItemViewModel> diagnostics,
        IReadOnlyList<CommandItemViewModel> commands,
        ICommand toggleEnabledCommand)
        : base(displayName, packageId, state)
    {
        ModuleId = moduleId;
        PackageId = packageId;
        StateLabel = state;
        Summary = summary;
        Metrics = metrics;
        Permissions = permissions;
        Requirements = requirements;
        Diagnostics = diagnostics;
        Commands = commands;
        ToggleEnabledCommand = toggleEnabledCommand;
    }

    public string ModuleId { get; }
    public string PackageId { get; }
    public string StateLabel { get; }
    public string Summary { get; }
    public IReadOnlyList<MetricViewModel> Metrics { get; }
    public IReadOnlyList<ModulePermissionViewModel> Permissions { get; }
    public IReadOnlyList<ModuleRequirementViewModel> Requirements { get; }
    public IReadOnlyList<ModuleDiagnosticItemViewModel> Diagnostics { get; }
    public IReadOnlyList<CommandItemViewModel> Commands { get; }
    public ICommand ToggleEnabledCommand { get; }
    public string ToggleEnabledLabel => string.Equals(StateLabel, "disabled", StringComparison.OrdinalIgnoreCase) ? "Enable" : "Disable";
    public bool HasNoPermissions => Permissions.Count == 0;
    public bool HasNoRequirements => Requirements.Count == 0;
    public bool HasNoDiagnostics => Diagnostics.Count == 0;
    public bool HasNoCommands => Commands.Count == 0;
}
