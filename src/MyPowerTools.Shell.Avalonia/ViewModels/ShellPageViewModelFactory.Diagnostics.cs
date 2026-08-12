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
    public static LogsViewModel FromLogs(
        HostProto.ListModulesResponse modules,
        HostProto.ModuleSummary? selectedModule,
        IReadOnlyList<HostProto.LogEntry> entries,
        Func<string, Task>? selectModule = null,
        Func<Task>? refresh = null,
        string? errorMessage = null)
    {
        var selectedModuleId = selectedModule?.ModuleId ?? "";
        var moduleItems = modules.Modules
            .OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(module =>
            {
                var isSelected = string.Equals(module.ModuleId, selectedModuleId, StringComparison.OrdinalIgnoreCase);
                return new ModulePickerItemViewModel(
                    module.ModuleId,
                    module.DisplayName,
                    isSelected,
                    isSelected ? "Selected" : "",
                    new AsyncRelayCommand(() => selectModule?.Invoke(module.ModuleId) ?? Task.CompletedTask));
            }).ToArray();

        var lines = entries.Select(entry => new LogLineViewModel(
            entry.Time.ToDateTimeOffset().ToString("HH:mm:ss"),
            entry.Level,
            entry.Message,
            entry.Time.ToDateTimeOffset(),
            entry.ModuleId)).ToArray();

        return new LogsViewModel(
            selectedModule?.DisplayName ?? "",
            moduleItems,
            lines,
            refresh,
            errorMessage);
    }

    public static NotificationsViewModel FromNotifications(HostProto.ListNotificationsResponse response)
    {
        return new NotificationsViewModel(ToNotificationItems(response));
    }

    public static IReadOnlyList<NotificationItemViewModel> ToNotificationItems(HostProto.ListNotificationsResponse response)
    {
        return response.Notifications.Select(notification => new NotificationItemViewModel(
            notification.Id,
            notification.Time.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"),
            notification.ModuleId,
            notification.Level,
            notification.Title,
            notification.Body,
            notification.IsRead)).ToArray();
    }

    public static DiagnosticsViewModel FromDiagnostics(
        HostProto.RuntimeDiagnostics diagnostics,
        HostProto.ListBrokerAuditResponse? audit = null,
        Func<string, string, Task>? restartProcess = null,
        Func<string, string, bool, DateTimeOffset?, string?, Task>? setRestartPolicy = null)
    {
        var metrics = new[]
        {
            new MetricViewModel("Runner", diagnostics.RunnerVersion),
            new MetricViewModel("Host IPC", diagnostics.HostControlProtocolVersion),
            new MetricViewModel("Module IPC", diagnostics.ModuleProtocolVersion),
            new MetricViewModel("Platform", diagnostics.PlatformRid),
            new MetricViewModel("Packages", diagnostics.Counts.PackageCount.ToString()),
            new MetricViewModel("Modules", diagnostics.Counts.ModuleCount.ToString()),
            new MetricViewModel("Enabled", diagnostics.Counts.EnabledModuleCount.ToString()),
            new MetricViewModel("Commands", diagnostics.Counts.CommandCount.ToString()),
            new MetricViewModel("Running", diagnostics.Counts.RunningModuleCount.ToString()),
            new MetricViewModel("Degraded", diagnostics.Counts.DegradedModuleCount.ToString()),
            new MetricViewModel("Errors", diagnostics.Counts.ErrorModuleCount.ToString()),
            new MetricViewModel("Events", diagnostics.CurrentEventSeq.ToString())
        };

        var paths = new[]
        {
            new MetricViewModel("Root", diagnostics.Paths.Root),
            new MetricViewModel("Settings", diagnostics.Paths.Settings),
            new MetricViewModel("Logs", diagnostics.Paths.Logs),
            new MetricViewModel("State", diagnostics.Paths.State),
            new MetricViewModel("Packages", diagnostics.Paths.Packages),
            new MetricViewModel("Package Root", diagnostics.Paths.PackageRoot)
        };

        var transports = diagnostics.Transports.Select(transport => new RuntimeTransportViewModel(
            transport.Kind,
            transport.RuntimeRegistered ? "registered" : "manifest",
            transport.ModuleCount.ToString())).ToArray();

        var processes = diagnostics.Processes.Select(process => new RuntimeProcessViewModel(
            process.TransportKind,
            process.PoolKey,
            process.State,
            process.ProcessId,
            process.ProcessId == 0 ? "external" : process.ProcessId.ToString(),
            process.Endpoint,
            $"{process.StartCount}/{process.RestartLimit}",
            process.RestartPolicy,
            string.IsNullOrWhiteSpace(process.PolicyReason) ? process.RestartPolicy : $"{process.RestartPolicy} - {process.PolicyReason}",
            process.PolicyExpiresAt is null ? "" : process.PolicyExpiresAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"),
            process.ModuleIds.Count == 0 ? "none" : string.Join(", ", process.ModuleIds),
            process.LastStartedAt is null ? "" : process.LastStartedAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"),
            process.StdoutLineCount == 0 ? "0 lines" : $"{process.StdoutLineCount}: {process.LastStdout}",
            process.StderrLineCount == 0 ? "0 lines" : $"{process.StderrLineCount}: {process.LastStderr}",
            string.Equals(process.RestartPolicy, "paused", StringComparison.OrdinalIgnoreCase),
            !string.Equals(process.RestartPolicy, "paused", StringComparison.OrdinalIgnoreCase),
            string.Equals(process.RestartPolicy, "paused", StringComparison.OrdinalIgnoreCase) ? "Resume" : "Pause",
            new AsyncRelayCommand(() => restartProcess?.Invoke(process.TransportKind, process.PoolKey) ?? Task.CompletedTask),
            new AsyncRelayCommand(() =>
            {
                var paused = string.Equals(process.RestartPolicy, "paused", StringComparison.OrdinalIgnoreCase);
                return setRestartPolicy?.Invoke(process.TransportKind, process.PoolKey, !paused, null, null) ?? Task.CompletedTask;
            }),
            new AsyncRelayCommand(() => setRestartPolicy?.Invoke(
                process.TransportKind,
                process.PoolKey,
                true,
                DateTimeOffset.UtcNow.AddHours(1),
                "Shell Diagnostics maintenance window") ?? Task.CompletedTask))).ToArray();

        var processPolicyHistory = diagnostics.ProcessPolicyHistory.Select(entry => new RuntimeProcessPolicyHistoryItemViewModel(
            $"{entry.RestartPolicy} - {entry.PoolKey}",
            $"{entry.Source} - {entry.TransportKind} - rev {entry.Revision} - {entry.Time.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
            entry.ExpiresAt is null
                ? (string.IsNullOrWhiteSpace(entry.Reason) ? "No reason recorded." : entry.Reason)
                : $"{(string.IsNullOrWhiteSpace(entry.Reason) ? "No reason recorded." : entry.Reason)} - expires {entry.ExpiresAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}")).ToArray();

        var modules = diagnostics.Modules.Select(module => new RuntimeModuleDiagnosticViewModel(
            module.DisplayName,
            module.State,
            new[]
            {
                new MetricViewModel("Module", module.ModuleId),
                new MetricViewModel("Package", module.PackageId),
                new MetricViewModel("Transport", module.TransportKind),
                new MetricViewModel("Selection", module.TransportSelectionReason),
                new MetricViewModel("Candidates", module.TransportSelectionDiagnostics.Count == 0 ? "none" : string.Join(" | ", module.TransportSelectionDiagnostics)),
                new MetricViewModel("Summary", module.Summary),
                new MetricViewModel("Diagnostics", module.DiagnosticCount.ToString()),
                new MetricViewModel("Supervisor", $"{module.SupervisorState} - failures {module.ConsecutiveFailureCount} - observations {module.ObservationCount}"),
                new MetricViewModel("Action", module.SupervisorAction),
                new MetricViewModel("Updated", module.UpdatedAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss")),
                new MetricViewModel("Observed", module.LastObservedAt is null ? "" : module.LastObservedAt.ToDateTimeOffset().ToString("yyyy-MM-dd HH:mm:ss"))
            })).ToArray();

        var recentCommands = diagnostics.RecentCommands.Select(command => new RuntimeCommandHistoryItemViewModel(
            $"{command.State} - {command.CommandId}",
            $"{command.ModuleId} - {command.StartedAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
            command.Summary)).ToArray();

        var brokerAudit = (audit?.Entries ?? []).Select(entry => new BrokerAuditEntryViewModel(
            $"{entry.Result} - {entry.ActionId}",
            $"{entry.ModuleId} - {entry.PermissionLevel} - {entry.Time.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
            string.IsNullOrWhiteSpace(entry.Reason) ? entry.Scope : $"{entry.Scope} - {entry.Reason}",
            string.IsNullOrWhiteSpace(entry.Rollback) ? "No rollback." : entry.Rollback)).ToArray();

        return new DiagnosticsViewModel(
            $"Collected {diagnostics.CollectedAt.ToDateTimeOffset():yyyy-MM-dd HH:mm:ss}",
            metrics,
            paths,
            transports,
            processes,
            processPolicyHistory,
            modules,
            recentCommands,
            brokerAudit);
    }
}
