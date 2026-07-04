using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyPowerTools.Runtime;

public sealed class StaticCommandIndexReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public IReadOnlyList<MptCommandDescriptor> Read(RuntimeModuleRecord module)
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

        var result = new List<MptCommandDescriptor>();
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

            result.Add(new MptCommandDescriptor(
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
                ReadParameters(command)));
        }

        return result;
    }

    private static IReadOnlyList<CommandParameterDescriptor> ReadParameters(JsonObject command)
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

                return new CommandParameterDescriptor(
                    id,
                    parameter["label"]?.GetValue<string>() ?? id,
                    parameter["type"]?.GetValue<string>() ?? "text",
                    parameter["required"]?.GetValue<bool>() ?? false,
                    ReadScalar(parameter["defaultValue"] ?? parameter["default"]));
            })
            .Where(parameter => parameter is not null)
            .Cast<CommandParameterDescriptor>()
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
