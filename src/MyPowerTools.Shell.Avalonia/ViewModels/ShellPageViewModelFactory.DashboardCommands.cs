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

public static partial class ShellPageViewModelFactory
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

    public static CommandPaletteViewModel FromCommands(
        string query,
        HostProto.ListCommandsResponse response,
        Func<string, JsonObject, string, CancellationToken, IAsyncEnumerable<CommandExecutionStatus>>? executeCommand = null,
        Func<string, Task<CommandCancellationStatus>>? cancelCommand = null)
    {
        var commands = response.Commands.Select(command =>
        {
            var parameters = command.Parameters
                .Select(parameter => new CommandParameterViewModel(
                    parameter.Id,
                    parameter.Label,
                    parameter.Type,
                    parameter.Required,
                    parameter.DefaultValue))
                .ToArray();

            return new CommandItemViewModel(
                command.CommandId,
                command.ModuleId,
                command.Title,
                command.Subtitle,
                command.DangerLevel,
                command.RequiresElevation,
                string.IsNullOrWhiteSpace(command.ModuleId) ? "Module: unknown" : $"Module: {command.ModuleId}",
                command.RequiresElevation ? $"{command.DangerLevel} - elevation" : command.DangerLevel,
                "",
                parameters.Length > 0,
                executeCommand,
                cancelCommand,
                parameters);
        }).ToArray();

        return new CommandPaletteViewModel(query, commands);
    }

    public static ModulesViewModel FromModules(
        HostProto.ListModulesResponse response,
        Func<string, Task>? showDetails = null,
        Func<string, Task>? showSettings = null,
        Func<string, Task>? showLogs = null,
        Func<string, bool, Task>? setModuleEnabled = null)
    {
        var modules = response.Modules
            .OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(module =>
            {
                var permissionSummary = module.Permissions.Count == 0
                    ? "Permissions: none"
                    : $"Permissions: {module.Permissions.Count} declared";
                var hasElevatedPermissions = module.Permissions.Any(permission =>
                    permission.Level is "broker" or "elevated" or "service");

                return new ModuleSummaryItemViewModel(
                    module.ModuleId,
                    module.PackageId,
                    module.DisplayName,
                    module.State,
                    module.Summary,
                    module.Enabled,
                    $"{module.PackageId} · {module.ModuleId}",
                    $"{permissionSummary} · Requirements: {module.Requirements.Count}",
                    module.Enabled ? "Disable" : "Enable",
                    hasElevatedPermissions,
                    new AsyncRelayCommand(() => showDetails?.Invoke(module.ModuleId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => showSettings?.Invoke(module.ModuleId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => showLogs?.Invoke(module.ModuleId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => setModuleEnabled?.Invoke(module.ModuleId, !module.Enabled) ?? Task.CompletedTask));
            }).ToArray();

        return new ModulesViewModel(modules);
    }

    public static ModuleDetailViewModel FromModuleDetail(
        HostProto.ModuleDetail detail,
        HostProto.ListCommandsResponse commands,
        Func<string, bool, Task>? setModuleEnabled = null,
        Func<string, Task>? executeCommand = null)
    {
        var enabled = !string.Equals(detail.State, "disabled", StringComparison.OrdinalIgnoreCase);
        var metrics = new[]
        {
            new MetricViewModel("Package", detail.PackageId),
            new MetricViewModel("Module", detail.ModuleId),
            new MetricViewModel("Diagnostics", detail.Diagnostics.Count.ToString()),
            new MetricViewModel("Permissions", detail.Permissions.Count.ToString())
        };

        var permissions = detail.Permissions
            .OrderBy(permission => permission.Level, StringComparer.OrdinalIgnoreCase)
            .ThenBy(permission => permission.Id, StringComparer.OrdinalIgnoreCase)
            .Select(permission => new ModulePermissionViewModel(
                permission.Id,
                permission.Level,
                string.IsNullOrWhiteSpace(permission.Capability) ? "No capability" : permission.Capability,
                permission.Reason))
            .ToArray();

        var requirements = detail.Requirements
            .OrderByDescending(requirement => requirement.Required)
            .ThenBy(requirement => requirement.Capability, StringComparer.OrdinalIgnoreCase)
            .Select(requirement => new ModuleRequirementViewModel(
                requirement.Capability,
                requirement.Required ? "required" : "optional",
                requirement.Reason))
            .ToArray();

        var diagnostics = detail.Diagnostics
            .Select(diagnostic => new ModuleDiagnosticItemViewModel(
                diagnostic.Label,
                diagnostic.State,
                diagnostic.Detail))
            .ToArray();

        var commandItems = commands.Commands
            .Where(command => string.Equals(command.ModuleId, detail.ModuleId, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .Select(command => new CommandItemViewModel(
                command.CommandId,
                command.ModuleId,
                command.Title,
                command.Subtitle,
                command.DangerLevel,
                command.RequiresElevation,
                string.IsNullOrWhiteSpace(command.ModuleId) ? "Module: unknown" : $"Module: {command.ModuleId}",
                command.RequiresElevation ? $"{command.DangerLevel} - elevation" : command.DangerLevel,
                "",
                false,
                (commandId, _, _, cancellationToken) => ExecuteModuleDetailCommandAsync(commandId, executeCommand, cancellationToken)))
            .ToArray();

        return new ModuleDetailViewModel(
            detail.ModuleId,
            detail.PackageId,
            detail.DisplayName,
            detail.State,
            detail.Summary,
            metrics,
            permissions,
            requirements,
            diagnostics,
            commandItems,
            new AsyncRelayCommand(() => setModuleEnabled?.Invoke(detail.ModuleId, !enabled) ?? Task.CompletedTask));
    }

    private static async IAsyncEnumerable<CommandExecutionStatus> ExecuteModuleDetailCommandAsync(
        string commandId,
        Func<string, Task>? executeCommand,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new CommandExecutionStatus("running", $"running: {commandId}", false, 1);
        cancellationToken.ThrowIfCancellationRequested();
        if (executeCommand is not null)
        {
            await executeCommand(commandId);
        }

        yield return new CommandExecutionStatus("succeeded", $"succeeded: {commandId}", true, 2);
    }
}
