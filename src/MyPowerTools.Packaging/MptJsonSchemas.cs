using System.Collections.Concurrent;
using Json.Schema;

namespace MyPowerTools.Packaging;

/// <summary>
/// Process-wide cache of parsed JSON schemas. Json.Schema registers every parsed
/// document in a global registry and refuses to register the same document twice
/// ("Overwriting registered schemas is not permitted"), so every schema file must be
/// parsed exactly once per process no matter how many components validate against it
/// (CLI scaffolder, package validator, tests, UI tooling).
/// </summary>
public static class MptJsonSchemas
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static JsonSchema FromFile(string schemaPath)
    {
        return Cache.GetOrAdd(
            Path.GetFullPath(schemaPath),
            path => JsonSchema.FromText(File.ReadAllText(path)));
    }
}
