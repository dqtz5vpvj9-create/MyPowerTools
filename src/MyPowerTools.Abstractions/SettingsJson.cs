using System.Text.Json.Nodes;

namespace MyPowerTools.Abstractions;

public static class SettingsJson
{
    public static JsonObject Merge(JsonObject current, JsonObject patch)
    {
        var merged = (JsonObject)current.DeepClone();
        foreach (var pair in patch)
        {
            merged[pair.Key] = pair.Value?.DeepClone();
        }

        return merged;
    }

    public static string? ReadString(JsonObject values, string key)
    {
        if (!values.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static bool? ReadBool(JsonObject values, string key)
    {
        if (!values.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public static int? ReadInt(JsonObject values, string key)
    {
        if (!values.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            try
            {
                return checked((int)node.GetValue<long>());
            }
            catch
            {
                return null;
            }
        }
    }

    public static double? ReadDouble(JsonObject values, string key)
    {
        if (!values.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<double>();
        }
        catch (InvalidOperationException)
        {
            if (ReadInt(values, key) is { } intValue)
            {
                return intValue;
            }

            return null;
        }
    }

    public static IReadOnlyList<string> ReadStringArray(JsonObject values, string key)
    {
        if (!values.TryGetPropertyValue(key, out var node) || node is not JsonArray array)
        {
            return [];
        }

        return array
            .Select(item =>
            {
                try
                {
                    return item?.GetValue<string>() ?? "";
                }
                catch (InvalidOperationException)
                {
                    return "";
                }
            })
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Insert(array.Count, JsonValue.Create(value));
        }

        return array;
    }
}
