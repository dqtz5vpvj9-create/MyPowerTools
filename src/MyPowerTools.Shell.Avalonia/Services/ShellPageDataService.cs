using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf.WellKnownTypes;
using MyPowerTools.HostControl;
using MyPowerTools.Shell.Avalonia.ViewModels;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed class ShellPageDataService
{
    public async Task<ShellPageDataResult<DashboardViewModel>> LoadDashboardAsync(
        Func<string, Task>? showModuleDetails = null,
        Func<string, Task>? executeCommand = null,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var snapshot = await client.GetDashboardSnapshotAsync(cancellationToken);
        var viewModel = ShellPageViewModelFactory.FromDashboard(snapshot, showModuleDetails, executeCommand);
        return new ShellPageDataResult<DashboardViewModel>(viewModel, viewModel.Subtitle);
    }

    public async Task<ShellPageDataResult<ModulesViewModel>> LoadModulesAsync(
        Func<string, Task>? showModuleDetails = null,
        Func<string, Task>? loadSettings = null,
        Func<string, Task>? loadLogs = null,
        Func<string, bool, Task>? setEnabled = null,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.ListModulesAsync(cancellationToken);
        var viewModel = ShellPageViewModelFactory.FromModules(
            response,
            showModuleDetails,
            loadSettings,
            loadLogs,
            setEnabled);
        return new ShellPageDataResult<ModulesViewModel>(viewModel, $"{viewModel.Modules.Count} modules loaded");
    }

    public async Task<ShellPageDataResult<ModuleDetailViewModel>> LoadModuleDetailAsync(
        string moduleId,
        Func<string, bool, Task>? setEnabled = null,
        Func<string, Task>? executeCommand = null,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var detail = await client.GetModuleDetailAsync(moduleId, cancellationToken);
        var commands = await client.ListCommandsAsync(moduleId, cancellationToken);
        var viewModel = ShellPageViewModelFactory.FromModuleDetail(
            detail,
            commands,
            setEnabled,
            executeCommand);
        return new ShellPageDataResult<ModuleDetailViewModel>(viewModel, $"{detail.DisplayName} detail loaded");
    }

    public async Task<ShellPageDataResult<SettingsCenterViewModel>> LoadSettingsAsync(
        string? selectedModuleId = null,
        Func<string, Task>? selectModule = null,
        Func<SettingsCenterViewModel, Task>? saveSettings = null,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var modules = await client.ListModulesAsync(cancellationToken);
        var selected = PickModule(modules, selectedModuleId);
        if (selected is null)
        {
            var emptyViewModel = ShellPageViewModelFactory.FromSettings(
                modules,
                null,
                "",
                new JsonObject(),
                "{}",
                0,
                DateTimeOffset.MinValue,
                selectModule,
                saveSettings);
            return new ShellPageDataResult<SettingsCenterViewModel>(emptyViewModel, emptyViewModel.StatusText);
        }

        var schema = await client.GetSettingsSchemaAsync(selected.ModuleId, cancellationToken);
        var snapshot = await client.GetSettingsAsync(selected.ModuleId, cancellationToken);
        var values = JsonStructMapper.ToJsonObject(snapshot.Values);
        var viewModel = ShellPageViewModelFactory.FromSettings(
            modules,
            selected,
            schema.SchemaJson,
            values,
            PrettyJson(snapshot.Values),
            snapshot.Revision,
            snapshot.UpdatedAt.ToDateTimeOffset(),
            selectModule,
            saveSettings);
        return new ShellPageDataResult<SettingsCenterViewModel>(viewModel, viewModel.StatusText);
    }

    public async Task<ShellPageDataResult<LogsViewModel>> LoadLogsAsync(
        string? selectedModuleId = null,
        Func<string, Task>? selectModule = null,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var modules = await client.ListModulesAsync(cancellationToken);
        var selected = PickModule(modules, selectedModuleId);
        IReadOnlyList<HostProto.LogEntry> entries = selected is null
            ? []
            : await client.TailLogsAsync(selected.ModuleId, cancellationToken);
        var viewModel = ShellPageViewModelFactory.FromLogs(modules, selected, entries, selectModule);
        var statusText = selected is null
            ? "No modules."
            : $"{entries.Count} log entries for {selected.ModuleId}";
        return new ShellPageDataResult<LogsViewModel>(viewModel, statusText);
    }

    public async Task<ShellPageDataResult<NotificationsViewModel>> LoadNotificationsAsync(
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.ListNotificationsAsync(80, cancellationToken: cancellationToken);
        var viewModel = ShellPageViewModelFactory.FromNotifications(response);
        return new ShellPageDataResult<NotificationsViewModel>(
            viewModel,
            $"{viewModel.Notifications.Count} notifications loaded");
    }

    public async Task<ShellPageDataResult<PackageManagerViewModel>> LoadPackagesAsync(
        Func<string, Task>? installPackage = null,
        Func<string, Task>? rollbackPackage = null,
        Func<string, Task>? repairPackage = null,
        Func<string, Task>? uninstallPackage = null,
        Func<string, Task>? showModuleDetails = null,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.ListPackagesAsync(cancellationToken);
        var viewModel = ShellPageViewModelFactory.FromPackages(
            response,
            installPackage,
            rollbackPackage,
            repairPackage,
            uninstallPackage,
            showModuleDetails);
        return new ShellPageDataResult<PackageManagerViewModel>(
            viewModel,
            $"{response.Packages.Count} packages loaded");
    }

    public async Task<ShellPageDataResult<DiagnosticsViewModel>> LoadDiagnosticsAsync(
        Func<string, string, Task>? restartRuntimeProcess = null,
        Func<string, string, bool, DateTimeOffset?, string?, Task>? setRestartPolicy = null,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var diagnostics = await client.GetRuntimeDiagnosticsAsync(cancellationToken);
        var audit = await client.ListBrokerAuditAsync(5, cancellationToken: cancellationToken);
        var viewModel = ShellPageViewModelFactory.FromDiagnostics(
            diagnostics,
            audit,
            restartRuntimeProcess,
            setRestartPolicy);
        return new ShellPageDataResult<DiagnosticsViewModel>(
            viewModel,
            $"Diagnostics loaded for {diagnostics.Counts.ModuleCount} modules");
    }

    public async Task<CommandPaletteViewModel> LoadCommandsAsync(
        string query,
        Func<string, JsonObject, Task>? executeCommand = null,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var response = await client.ListCommandsAsync(query, cancellationToken);
        if (response.Commands.Count > 30)
        {
            var limited = new HostProto.ListCommandsResponse();
            limited.Commands.AddRange(response.Commands.Take(30));
            response = limited;
        }

        return ShellPageViewModelFactory.FromCommands(query, response, executeCommand);
    }

    public async Task<BrokerAuditViewModel> LoadBrokerAuditAsync(
        int limit = 6,
        CancellationToken cancellationToken = default)
    {
        using var client = HostControlClient.ForDefaultEndpoint();
        var audit = await client.ListBrokerAuditAsync(limit, cancellationToken: cancellationToken);
        return ShellPageViewModelFactory.FromBrokerAudit(audit);
    }

    public BrokerAuditViewModel CreateBrokerAuditError(string message)
    {
        return ShellPageViewModelFactory.FromBrokerAuditError(message);
    }

    private static HostProto.ModuleSummary? PickModule(HostProto.ListModulesResponse modules, string? selectedModuleId)
    {
        if (!string.IsNullOrWhiteSpace(selectedModuleId))
        {
            var selected = modules.Modules.FirstOrDefault(module => string.Equals(
                module.ModuleId,
                selectedModuleId,
                StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
        }

        return modules.Modules.OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static string PrettyJson(Struct value)
    {
        if (value.Fields.Count == 0)
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(value.ToString());
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return value.ToString();
        }
    }
}

public sealed record ShellPageDataResult<TViewModel>(TViewModel ViewModel, string StatusText);
