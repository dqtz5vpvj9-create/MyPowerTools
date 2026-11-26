using System.Text.Json;

namespace MyPowerTools.Runtime;

public static class ToolDiscoveryConfiguration
{
    public const string EnvironmentVariable = "MPT_TOOL_DIRS";

    /// <summary>
    /// The default user-writable drop folder for custom tools and quick web panels
    /// (single-file { "title", "url" } manifests). Always discovered, created by the
    /// Runner at startup.
    /// </summary>
    public const string CustomToolsDirectoryName = "custom-tools";

    public static string CustomToolsDirectory(string dataRoot) =>
        Path.Combine(dataRoot, CustomToolsDirectoryName);

    public static IReadOnlyList<string> Resolve(
        string applicationRoot,
        string dataRoot,
        IEnumerable<string>? commandLineDirectories = null)
    {
        var paths = new List<string>();
        var fullAppRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(applicationRoot));
        Add(paths, Path.Combine(fullAppRoot, "tools"));
        Add(paths, CustomToolsDirectory(dataRoot));

        foreach (var sibling in DiscoverSiblingPortableToolDirectories(fullAppRoot))
        {
            Add(paths, sibling);
        }

        var configuredPath = Path.Combine(dataRoot, "settings", "tool-directories.json");
        if (File.Exists(configuredPath))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configuredPath));
                var root = document.RootElement;
                var directories = root.ValueKind == JsonValueKind.Array
                    ? root
                    : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("directories", out var configured)
                        ? configured
                        : default;
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

    private static IEnumerable<string> DiscoverSiblingPortableToolDirectories(string fullAppRoot)
    {
        DirectoryInfo? parent;
        try
        {
            parent = Directory.GetParent(fullAppRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            yield break;
        }

        if (parent is null || !parent.Exists)
        {
            yield break;
        }

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(parent.FullName)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var directory in candidates)
        {
            if (string.Equals(Path.GetFullPath(directory), fullAppRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var folderName = Path.GetFileName(directory);
            if (!folderName.StartsWith("Mpt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ContainsStandaloneManifest(directory))
            {
                yield return directory;
            }
        }
    }

    private static bool ContainsStandaloneManifest(string directory)
    {
        return File.Exists(Path.Combine(directory, "module.json")) ||
               File.Exists(Path.Combine(directory, "tool.json")) ||
               File.Exists(Path.Combine(directory, "ui", "tool.json")) ||
               File.Exists(Path.Combine(directory, "app", "module.json")) ||
               File.Exists(Path.Combine(directory, "app", "tool.json"));
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
