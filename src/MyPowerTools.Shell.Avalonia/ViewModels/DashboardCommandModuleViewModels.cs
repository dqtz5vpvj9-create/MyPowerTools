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
    public string RunningCount => Cards.Count(card => string.Equals(card.State, "running", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
    public string DegradedCount => Cards.Count(card => string.Equals(card.State, "degraded", StringComparison.OrdinalIgnoreCase)).ToString(CultureInfo.InvariantCulture);
    public string CommandCount => Cards.Sum(card => card.Actions.Count).ToString(CultureInfo.InvariantCulture);
    public string NotificationCount => Alerts.Count.ToString(CultureInfo.InvariantCulture);
    public bool HasAlerts => Alerts.Count > 0;
}

public sealed class CommandPaletteViewModel : ShellPageViewModel
{
    public CommandPaletteViewModel(string query, IReadOnlyList<CommandItemViewModel> commands)
        : base("Command Palette", $"{commands.Count} commands", commands.Count == 0 ? "empty" : "ready")
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
        SelectedCommand = Commands.FirstOrDefault();
    }

    public string Query { get; }
    public IReadOnlyList<CommandItemViewModel> Commands { get; }
    public IReadOnlyList<CommandProviderGroupViewModel> ProviderGroups { get; }
    public IReadOnlyList<CommandPaletteHistoryItemViewModel> RecentCommands { get; }
    public CommandItemViewModel? SelectedCommand { get; }
    public bool IsEmpty => Commands.Count == 0;
    public bool HasSelection => SelectedCommand is not null;
    public bool HasRecentCommands => RecentCommands.Count > 0;
    public string KeyboardSelectionHint => "Keyboard selection ready";
    public string SelectionPreview => SelectedCommand?.ExecutionPreview ?? "No command selected.";
    public string DangerousConfirmationText => SelectedCommand?.DangerConfirmationText ?? "";
    public bool RequiresDangerousConfirmation => SelectedCommand?.RequiresDangerousConfirmation == true;

    private static IReadOnlyList<CommandItemViewModel> RankCommands(string query, IReadOnlyList<CommandItemViewModel> commands)
    {
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
