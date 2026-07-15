using System.Text.Json;

namespace MyPowerTools.Runtime;

public static class ToolDiscoveryConfiguration
{
    public const string EnvironmentVariable = "MPT_TOOL_DIRS";

    public static IReadOnlyList<string> Resolve(
        string applicationRoot,
        string dataRoot,
        IEnumerable<string>? commandLineDirectories = null)
    {
        var paths = new List<string>();
        Add(paths, Path.Combine(applicationRoot, "tools"));

        var configuredPath = Path.Combine(dataRoot, "settings", "tool-directories.json");
        if (File.Exists(configuredPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configuredPath));
                var root = document.RootElement;
                var directories = root.ValueKind == JsonValueKind.Array
                    ? root
                    : root.TryGetProperty("directories", out var configured) ? configured : default;
                if (directories.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in directories.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                        {
                            Add(paths, item.GetString());
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A malformed optional developer file must not prevent Runner startup.
            }
        }

        var environment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            foreach (var path in environment.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Add(paths, path);
            }
        }

        foreach (var path in commandLineDirectories ?? [])
        {
            Add(paths, path);
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Add(List<string> paths, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        paths.Add(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim())));
    }
}
