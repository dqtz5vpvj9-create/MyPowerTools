using Sdk = MyPowerTools.Abstractions;
using System.Text.Json.Nodes;

namespace MyPowerTools.Runtime;

public sealed class CommandIndex
{
    private readonly List<Sdk.MptCommandDescriptor> _commands = [];
    private readonly StaticCommandIndexReader _staticReader = new();

    public IReadOnlyList<Sdk.MptCommandDescriptor> Commands => _commands;

    public void Rebuild(
        IEnumerable<RuntimeModuleRecord> modules,
        IEnumerable<Sdk.MptCommandDescriptor>? dynamicCommands = null,
        IEnumerable<Sdk.ToolDescriptor>? tools = null)
    {
        _commands.Clear();
        var activeModules = modules.ToArray();
        var activeModuleIds = activeModules
            .Select(module => module.Module.Manifest.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // External tool commands are the lowest-precedence executable descriptors.
        // Existing module built-ins/static indexes replace collisions below, and
        // dynamic commands retain their final override position.
        foreach (var tool in (tools ?? [])
            .Where(tool =>
                activeModuleIds.Contains(tool.OwnerModuleId) &&
                tool.LoadError is null &&
                tool.Runtime is not null &&
                !string.Equals(tool.Runtime.Transport, "none", StringComparison.OrdinalIgnoreCase))
            .OrderBy(tool => tool.ToolId, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var command in (tool.Commands ?? [])
                .OrderBy(command => command.Id, StringComparer.OrdinalIgnoreCase))
            {
                AddOrReplace(ToExternalToolCommand(tool, command));
            }
        }

        foreach (var module in activeModules.OrderBy(module => module.Module.Manifest.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var id = module.Module.Manifest.Id;
            AddOrReplace(new Sdk.MptCommandDescriptor(
                $"{id}.open",
                id,
                $"Open {module.Module.Manifest.DisplayName}",
                module.Module.Manifest.DisplayName,
                "open",
                Execution: new() { ["type"] = "open", ["surface"] = "detail" }));
            AddOrReplace(new Sdk.MptCommandDescriptor(
                $"{id}.status.refresh",
                id,
                $"Refresh {module.Module.Manifest.DisplayName}",
                "Update status snapshot",
                "action",
                Execution: new() { ["type"] = "host.status.refresh" }));

            if (module.Module.Manifest.Capabilities.Contains("settings", StringComparer.OrdinalIgnoreCase))
            {
                AddOrReplace(new Sdk.MptCommandDescriptor(
                    $"{id}.settings.open",
                    id,
                    $"Open {module.Module.Manifest.DisplayName} settings",
                    "Settings Center",
                    "open",
                    Execution: new() { ["type"] = "open", ["surface"] = "settings" }));
            }

            foreach (var command in _staticReader.Read(module))
            {
                AddOrReplace(command);
            }
        }

        if (dynamicCommands is not null)
        {
            foreach (var command in dynamicCommands)
            {
                AddOrReplace(command);
            }
        }
    }

    private static Sdk.MptCommandDescriptor ToExternalToolCommand(
        Sdk.ToolDescriptor tool,
        Sdk.ToolCommand command)
    {
        var isHttp = string.Equals(
            tool.Runtime?.Transport,
            "loopback-http",
            StringComparison.OrdinalIgnoreCase);
        var extensionData = command.ExtensionData;
        var execution = extensionData?["execution"] is JsonObject declaredExecution
            ? declaredExecution.DeepClone().AsObject()
            : new JsonObject();
        var executionType = ReadString(
            execution,
            "type",
            isHttp ? "http.request" : "tool.runtime");
        execution["type"] = executionType;
        execution["toolId"] ??= tool.ToolId;
        if (string.Equals(executionType, "http.request", StringComparison.OrdinalIgnoreCase))
        {
            execution["method"] = string.IsNullOrWhiteSpace(command.Method)
                ? "POST"
                : command.Method;
            execution["path"] = command.Path;
        }

        var runtimeTimeoutMs = tool.Runtime?.TimeoutMs is > 0
            ? tool.Runtime.TimeoutMs
            : 30_000;
        var timeoutMs = ReadPositiveInt(extensionData, "timeoutMs", runtimeTimeoutMs);
        return new Sdk.MptCommandDescriptor(
            command.Id,
            tool.OwnerModuleId,
            command.Title,
            command.Description,
            ReadString(extensionData, "kind", "action"),
            RequiresElevation: ReadBool(extensionData, "requiresElevation"),
            Icon: tool.Icon,
            DangerLevel: ReadString(extensionData, "dangerLevel", ""),
            Category: ReadString(extensionData, "category", tool.Category),
            TimeoutMs: timeoutMs,
            Execution: execution,
            Constraints: ReadStringList(extensionData, "constraints"),
            SupportsProgress: ReadBool(extensionData, "supportsProgress"),
            SupportsCancellation: ReadBool(extensionData, "supportsCancellation", fallback: true));
    }

    private static string ReadString(JsonObject? source, string propertyName, string fallback)
    {
        return source?[propertyName] is JsonValue value &&
               value.TryGetValue<string>(out var result) &&
               !string.IsNullOrWhiteSpace(result)
            ? result
            : fallback;
    }

    private static int ReadPositiveInt(JsonObject? source, string propertyName, int fallback)
    {
        return source?[propertyName] is JsonValue value &&
               value.TryGetValue<int>(out var result) &&
               result > 0
            ? result
            : fallback;
    }

    private static bool ReadBool(
        JsonObject? source,
        string propertyName,
        bool fallback = false)
    {
        return source?[propertyName] is JsonValue value &&
               value.TryGetValue<bool>(out var result)
            ? result
            : fallback;
    }

    private static IReadOnlyList<string> ReadStringList(
        JsonObject? source,
        string propertyName)
    {
        if (source?[propertyName] is not JsonArray values)
        {
            return [];
        }

        return values
            .OfType<JsonValue>()
            .Select(value => value.TryGetValue<string>(out var item) ? item : "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<Sdk.MptCommandDescriptor> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _commands;
        }

        return _commands
            .Where(command =>
                command.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.ModuleId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public Sdk.MptCommandDescriptor? Find(string commandId)
    {
        return _commands.FirstOrDefault(command => string.Equals(command.Id, commandId, StringComparison.OrdinalIgnoreCase));
    }

    private void AddOrReplace(Sdk.MptCommandDescriptor command)
    {
        var existing = _commands.FindIndex(candidate => string.Equals(candidate.Id, command.Id, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _commands[existing] = command;
            return;
        }

        _commands.Add(command);
    }
}
