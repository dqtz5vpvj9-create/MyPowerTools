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
}

public sealed class CommandPaletteViewModel : ShellPageViewModel
{
    public CommandPaletteViewModel(string query, IReadOnlyList<CommandItemViewModel> commands)
        : base("Command Palette", $"{commands.Count} commands", commands.Count == 0 ? "empty" : "ready")
    {
        Query = query;
        Commands = commands;
    }

    public string Query { get; }
    public IReadOnlyList<CommandItemViewModel> Commands { get; }
    public bool IsEmpty => Commands.Count == 0;
}

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
