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
    private static IReadOnlyList<SettingsFieldViewModel> BuildSettingsFields(string schemaJson, JsonObject values)
    {
        var schema = TryParseSettingsSchema(schemaJson);
        if (schema is null ||
            !schema.TryGetPropertyValue("properties", out var propertiesNode) ||
            propertiesNode is not JsonObject properties)
        {
            return [];
        }

        var fields = new List<SettingsFieldViewModel>();
        foreach (var propertyPair in properties.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (propertyPair.Value is not JsonObject property)
            {
                continue;
            }

            var key = propertyPair.Key;
            var label = GetSchemaString(property, "title", key);
            var description = GetSchemaString(property, "description", "");
            var type = GetSchemaString(property, "type", "string").ToLowerInvariant();
            values.TryGetPropertyValue(key, out var currentValue);
            property.TryGetPropertyValue("default", out var defaultValue);
            var effectiveValue = currentValue ?? defaultValue;
            var editorType = type;
            IReadOnlyList<string> options = [];
            var selectedOption = "";

            if (property.TryGetPropertyValue("enum", out var enumNode) && enumNode is JsonArray enumValues)
            {
                editorType = "enum";
                options = enumValues
                    .Select(NodeToEditorText)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                var selected = NodeToEditorText(effectiveValue);
                selectedOption = options.Contains(selected, StringComparer.OrdinalIgnoreCase)
                    ? selected
                    : options.FirstOrDefault() ?? "";
            }

            var textValue = NodeToEditorText(effectiveValue);
            fields.Add(new SettingsFieldViewModel(
                key,
                label,
                editorType,
                description,
                textValue,
                NodeToBool(effectiveValue),
                options,
                selectedOption));
        }

        return fields;
    }

    private static JsonObject ParseRawSettings(string rawJson)
    {
        var text = string.IsNullOrWhiteSpace(rawJson) ? "{}" : rawJson;
        return JsonNode.Parse(text) as JsonObject
            ?? throw new FormatException("Raw settings must be a JSON object.");
    }

    private static JsonObject? TryParseSettingsSchema(string schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(schemaJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string GetSchemaString(JsonObject schema, string key, string fallback)
    {
        return schema.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : fallback;
    }

    private static bool NodeToBool(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<bool>(out var result) && result;
    }

    private static string NodeToEditorText(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = node is JsonObject or JsonArray });
    }

    private static long ParseLong(string value, string key)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{key} must be an integer.");
    }

    private static double ParseDouble(string value, string key)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        throw new FormatException($"{key} must be a number.");
    }

    private static JsonNode ParseCompositeSetting(string value, string key, string emptyValue)
    {
        var text = string.IsNullOrWhiteSpace(value) ? emptyValue : value;
        return JsonNode.Parse(text)
            ?? throw new FormatException($"{key} must be valid JSON.");
    }
}
