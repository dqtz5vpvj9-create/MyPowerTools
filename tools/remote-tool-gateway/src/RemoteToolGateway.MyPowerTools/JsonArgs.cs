using System.Text.Json.Nodes;

namespace RemoteToolGateway.MyPowerTools;

/// <summary>
/// Tolerant readers for module command arguments. The Shell command UI, the desktop surface and
/// tests all produce slightly different JSON shapes (numbers as strings, booleans as text), so the
/// module normalizes here instead of failing a user action on a representation detail.
/// </summary>
internal static class JsonArgs
{
    public static string String(JsonObject? args, string key)
    {
        if (args is null || !args.TryGetPropertyValue(key, out var node) || node is null) return "";
        try { return node.GetValue<string>().Trim(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            try { return node.ToJsonString().Trim('"'); }
            catch (Exception inner) when (inner is InvalidOperationException or NotSupportedException) { return ""; }
        }
    }

    public static int Int(JsonObject? args, string key, int fallback)
    {
        if (args is null || !args.TryGetPropertyValue(key, out var node) || node is null) return fallback;
        try { return node.GetValue<int>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return int.TryParse(String(args, key), out var parsed) ? parsed : fallback;
        }
    }

    public static bool Bool(JsonObject? args, string key, bool fallback = false)
    {
        if (args is null || !args.TryGetPropertyValue(key, out var node) || node is null) return fallback;
        try { return node.GetValue<bool>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            var text = String(args, key);
            return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";
        }
    }

    /// <summary>Accepts a JSON array or a comma/newline separated string of command ids.</summary>
    public static IReadOnlyList<string> StringList(JsonObject? args, string key)
    {
        if (args is null || !args.TryGetPropertyValue(key, out var node) || node is null) return [];
        if (node is JsonArray array)
        {
            var values = new List<string>();
            foreach (var item in array)
            {
                try { values.Add(item?.GetValue<string>() ?? ""); }
                catch (Exception ex) when (ex is InvalidOperationException or FormatException) { }
            }

            return values.Where(value => value.Length > 0).ToArray();
        }

        return String(args, key)
            .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    public static JsonObject Merge(JsonObject baseline, JsonObject patch)
    {
        var merged = (JsonObject)baseline.DeepClone();
        foreach (var pair in patch) merged[pair.Key] = pair.Value?.DeepClone();
        return merged;
    }
}
