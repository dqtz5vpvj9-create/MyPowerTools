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
    public static SettingsCenterViewModel FromSettings(
        HostProto.ListModulesResponse modules,
        HostProto.ModuleSummary? selected,
        string schemaJson,
        JsonObject values,
        string rawJson,
        ulong revision,
        DateTimeOffset updatedAt,
        Func<string, Task>? selectModule = null,
        Func<SettingsCenterViewModel, Task>? saveSettings = null)
    {
        var selectedModuleId = selected?.ModuleId ?? "";
        var picker = modules.Modules
            .OrderBy(module => module.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(module => new ModulePickerItemViewModel(
                module.ModuleId,
                module.DisplayName,
                string.Equals(module.ModuleId, selectedModuleId, StringComparison.OrdinalIgnoreCase),
                string.Equals(module.ModuleId, selectedModuleId, StringComparison.OrdinalIgnoreCase) ? "Selected" : "",
                new AsyncRelayCommand(() => selectModule?.Invoke(module.ModuleId) ?? Task.CompletedTask)))
            .ToArray();

        var fields = selected is null ? [] : BuildSettingsFields(schemaJson, values);
        var statusText = selected is null
            ? "No modules."
            : $"Revision {revision} - {updatedAt:yyyy-MM-dd HH:mm:ss} - Schema fields {fields.Count}";

        return new SettingsCenterViewModel(
            selectedModuleId,
            selected?.DisplayName ?? "No modules.",
            revision,
            rawJson,
            statusText,
            picker,
            fields,
            saveSettings);
    }

    public static JsonObject BuildSettingsPatch(SettingsCenterViewModel viewModel)
    {
        if (viewModel.UsesRawJson)
        {
            return ParseRawSettings(viewModel.RawJson);
        }

        var patch = new JsonObject();
        foreach (var field in viewModel.Fields)
        {
            patch[field.Key] = field.EditorType switch
            {
                "boolean" => JsonValue.Create(field.BooleanValue),
                "integer" => JsonValue.Create(ParseLong(field.Value, field.Key)),
                "number" => JsonValue.Create(ParseDouble(field.Value, field.Key)),
                "object" => ParseCompositeSetting(field.Value, field.Key, "{}"),
                "array" => ParseCompositeSetting(field.Value, field.Key, "[]"),
                "enum" => JsonValue.Create(field.SelectedOption),
                _ => JsonValue.Create(field.Value)
            };
        }

        return patch;
    }

    public static PermissionPromptViewModel FromPermissionPrompt(
        HostProto.CommandExecutionResponse result,
        Func<Task>? showAudit = null)
    {
        var details = result.ErrorDetails;
        var actionId = ReadDetailString(details, "actionId", result.ErrorCode);
        var scope = ReadDetailString(details, "scope", "");
        var reason = ReadDetailString(details, "reason", result.ErrorMessage);
        var applyCount = CountNestedList(details, "expectedChange", "apply");
        var removeCount = CountNestedList(details, "expectedChange", "remove");
        var rollbackCount = CountList(details, "rollback");

        var rows = new[]
        {
            new MetricViewModel("Action", string.IsNullOrWhiteSpace(actionId) ? "-" : actionId),
            new MetricViewModel("Scope", string.IsNullOrWhiteSpace(scope) ? "-" : scope),
            new MetricViewModel("Reason", string.IsNullOrWhiteSpace(reason) ? "-" : reason),
            new MetricViewModel("Expected change", $"{applyCount} apply, {removeCount} remove"),
            new MetricViewModel("Rollback", $"{rollbackCount} step(s)")
        };

        return new PermissionPromptViewModel(rows, new AsyncRelayCommand(() => showAudit?.Invoke() ?? Task.CompletedTask));
    }

    public static BrokerAuditViewModel FromBrokerAudit(HostProto.ListBrokerAuditResponse audit)
    {
        var entries = audit.Entries.Select(entry => new BrokerAuditSidebarEntryViewModel(
            $"{entry.Result} - {entry.ActionId}",
            $"{entry.ModuleId} - {entry.Scope}")).ToArray();

        return new BrokerAuditViewModel(entries);
    }

    public static BrokerAuditViewModel FromBrokerAuditError(string message)
    {
        return new BrokerAuditViewModel([], $"Audit unavailable: {message}");
    }

    public static PackageManagerViewModel FromPackages(
        HostProto.ListPackagesResponse response,
        Func<string, Task>? installPackage = null,
        Func<string, Task>? rollbackPackage = null,
        Func<string, Task>? repairPackage = null,
        Func<string, Task>? uninstallPackage = null,
        Func<string, Task>? showModuleDetails = null)
    {
        var packages = response.Packages
            .OrderBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(package =>
            {
                var hashes = string.IsNullOrWhiteSpace(package.Hashes) ? "-" : package.Hashes;
                var signaturePath = string.IsNullOrWhiteSpace(package.SignaturePath) ? "-" : package.SignaturePath;
                var trustPolicy = string.IsNullOrWhiteSpace(package.TrustPolicy) ? "-" : package.TrustPolicy;
                var metrics = new[]
                {
                    new MetricViewModel("Version", package.Version),
                    new MetricViewModel("Modules", package.ModuleCount.ToString()),
                    new MetricViewModel("Runtimes", package.SharedRuntimeCount.ToString()),
                    new MetricViewModel("Trust", trustPolicy),
                    new MetricViewModel("Issues", package.TrustIssueCount.ToString()),
                    new MetricViewModel("Hashes", hashes),
                    new MetricViewModel("Signature", signaturePath)
                };
                var moduleLinks = package.ModuleIds
                    .Take(3)
                    .Select(moduleId => new PackageModuleLinkViewModel(
                        moduleId,
                        new AsyncRelayCommand(() => showModuleDetails?.Invoke(moduleId) ?? Task.CompletedTask)))
                    .ToArray();

                return new PackageSummaryViewModel(
                    package.PackageId,
                    package.DisplayName,
                    package.Version,
                    package.Publisher,
                    package.Directory,
                    hashes,
                    trustPolicy,
                    signaturePath,
                    package.TrustState,
                    package.ModuleCount,
                    package.SharedRuntimeCount,
                    package.TrustIssueCount,
                    package.ModuleIds.Count == 0 ? "No modules." : string.Join(", ", package.ModuleIds),
                    metrics,
                    moduleLinks,
                    new AsyncRelayCommand(() => repairPackage?.Invoke(package.PackageId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => uninstallPackage?.Invoke(package.PackageId) ?? Task.CompletedTask),
                    new AsyncRelayCommand(() => rollbackPackage?.Invoke(package.PackageId) ?? Task.CompletedTask));
            }).ToArray();

        return new PackageManagerViewModel(packages, installPackage, rollbackPackage);
    }

    private static string ReadDetailString(Struct details, string key, string fallback)
    {
        if (details.Fields.TryGetValue(key, out var value))
        {
            return DetailValueToText(value);
        }

        return fallback;
    }

    private static int CountNestedList(Struct details, string objectKey, string listKey)
    {
        if (details.Fields.TryGetValue(objectKey, out var outer) &&
            outer.KindCase == Value.KindOneofCase.StructValue &&
            outer.StructValue.Fields.TryGetValue(listKey, out var inner) &&
            inner.KindCase == Value.KindOneofCase.ListValue)
        {
            return inner.ListValue.Values.Count;
        }

        return 0;
    }

    private static int CountList(Struct details, string key)
    {
        if (details.Fields.TryGetValue(key, out var value) && value.KindCase == Value.KindOneofCase.ListValue)
        {
            return value.ListValue.Values.Count;
        }

        return 0;
    }

    private static string DetailValueToText(Value value)
    {
        return value.KindCase switch
        {
            Value.KindOneofCase.StringValue => value.StringValue,
            Value.KindOneofCase.NumberValue => value.NumberValue.ToString("0.##", CultureInfo.InvariantCulture),
            Value.KindOneofCase.BoolValue => value.BoolValue ? "true" : "false",
            Value.KindOneofCase.ListValue => $"{value.ListValue.Values.Count} item(s)",
            Value.KindOneofCase.StructValue => $"{value.StructValue.Fields.Count} field(s)",
            _ => ""
        };
    }

}
