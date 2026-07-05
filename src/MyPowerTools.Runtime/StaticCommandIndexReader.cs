using System.Text.Json;
using System.Text.Json.Nodes;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class StaticCommandIndexReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public IReadOnlyList<Sdk.MptCommandDescriptor> Read(RuntimeModuleRecord module)
    {
        var commandsPath = ResolveCommandsIndexPath(module);
        if (commandsPath is null || !File.Exists(commandsPath))
        {
            return [];
        }

        var root = JsonNode.Parse(File.ReadAllText(commandsPath));
        var commandNodes = root switch
        {
            JsonArray array => array,
            JsonObject obj when obj["commands"] is JsonArray commands => commands,
            _ => []
        };

        var result = new List<Sdk.MptCommandDescriptor>();
        foreach (var commandNode in commandNodes)
        {
            if (commandNode is not JsonObject command)
            {
                continue;
            }

            var id = command["id"]?.GetValue<string>() ?? "";
            var title = command["title"]?.GetValue<string>() ?? id;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            result.Add(new Sdk.MptCommandDescriptor(
                id,
                module.Module.Manifest.Id,
                title,
                command["subtitle"]?.GetValue<string>() ?? module.Module.Manifest.DisplayName,
                command["kind"]?.GetValue<string>() ?? "action",
                command["requiresElevation"]?.GetValue<bool>() ?? false,
                command["icon"]?.GetValue<string>() ?? "",
                command["dangerLevel"]?.GetValue<string>() ?? "",
                command["category"]?.GetValue<string>() ?? module.Module.Manifest.DisplayName,
                command["timeoutMs"]?.GetValue<int>() ?? 30000,
                command["execution"]?.DeepClone() as JsonObject,
                ReadParameters(command),
                ReadConstraints(command)));
        }

        return result;
    }

    private static IReadOnlyList<string> ReadConstraints(JsonObject command)
    {
        var constraints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddConstraints(command["constraints"], constraints);
        if (command["execution"] is JsonObject execution)
        {
            AddConstraints(execution["constraints"], constraints);
        }

        AddConstraintIfTrue(command, "mutatesSystemState", Sdk.MptOperationConstraints.MutatesSystemState, constraints);
        AddConstraintIfTrue(command, "requiresElevatedWrites", Sdk.MptOperationConstraints.RequiresElevatedWrites, constraints);
        AddConstraintIfTrue(command, "usesNativeHardware", Sdk.MptOperationConstraints.UsesNativeHardware, constraints);
        AddConstraintIfTrue(command, "runsExternalProcesses", Sdk.MptOperationConstraints.RunsExternalProcesses, constraints);
        AddConstraintIfTrue(command, "requiresLongRunningLoop", Sdk.MptOperationConstraints.RequiresLongRunningLoop, constraints);

        if (command["requiresElevation"]?.GetValue<bool>() == true)
        {
            constraints.Add(Sdk.MptOperationConstraints.RequiresElevatedWrites);
        }

        if (string.Equals(command["execution"]?["type"]?.GetValue<string>(), "broker.request", StringComparison.OrdinalIgnoreCase))
        {
            constraints.Add(Sdk.MptOperationConstraints.MutatesSystemState);
            constraints.Add(Sdk.MptOperationConstraints.RequiresElevatedWrites);
        }

        return constraints.Count == 0 ? [] : constraints.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddConstraints(JsonNode? node, ISet<string> constraints)
    {
        if (node is not JsonArray values)
        {
            return;
        }

        foreach (var value in values)
        {
            var constraint = value?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(constraint))
            {
                constraints.Add(constraint);
            }
        }
    }

    private static void AddConstraintIfTrue(JsonObject command, string propertyName, string constraint, ISet<string> constraints)
    {
        if (command[propertyName]?.GetValue<bool>() == true)
        {
            constraints.Add(constraint);
        }
    }

    private static IReadOnlyList<Sdk.CommandParameterDescriptor> ReadParameters(JsonObject command)
    {
        if (command["parameters"] is not JsonArray parameters)
        {
            return [];
        }

        return parameters
            .OfType<JsonObject>()
            .Select(parameter =>
            {
                var id = parameter["id"]?.GetValue<string>()
                    ?? parameter["key"]?.GetValue<string>()
                    ?? "";
                if (string.IsNullOrWhiteSpace(id))
                {
                    return null;
                }

                return new Sdk.CommandParameterDescriptor(
                    id,
                    parameter["label"]?.GetValue<string>() ?? id,
                    parameter["type"]?.GetValue<string>() ?? "text",
                    parameter["required"]?.GetValue<bool>() ?? false,
                    ReadScalar(parameter["defaultValue"] ?? parameter["default"]));
            })
            .Where(parameter => parameter is not null)
            .Cast<Sdk.CommandParameterDescriptor>()
            .ToArray();
    }

    private static string ReadScalar(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var stringValue))
            {
                return stringValue;
            }

            if (value.TryGetValue<bool>(out var boolValue))
            {
                return boolValue ? "true" : "false";
            }

            if (value.TryGetValue<int>(out var intValue))
            {
                return intValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return node.ToJsonString(JsonOptions);
    }

    private static string? ResolveCommandsIndexPath(RuntimeModuleRecord module)
    {
        if (module.Module.Manifest.StaticIndexes is not null &&
            module.Module.Manifest.StaticIndexes.TryGetValue("commands", out var element) &&
            element.ValueKind == JsonValueKind.String)
        {
            var relative = element.GetString();
            return string.IsNullOrWhiteSpace(relative)
                ? null
                : Path.GetFullPath(Path.Combine(module.Package.Directory, relative));
        }

        var defaultPath = Path.Combine(module.Module.Directory, "commands.index.json");
        return File.Exists(defaultPath) ? defaultPath : null;
    }
}
