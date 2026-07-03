using System.Text.Json;

namespace MyPowerTools.Packaging;

public sealed class PackageReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public IReadOnlyList<MptPackageDefinition> DiscoverPackages(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException(rootDirectory);
        }

        if (File.Exists(Path.Combine(rootDirectory, "package.json")) || File.Exists(Path.Combine(rootDirectory, "module.json")))
        {
            return [ReadPackageDirectory(rootDirectory)];
        }

        return Directory.EnumerateDirectories(rootDirectory)
            .Where(dir => !Path.GetFileName(dir).EndsWith(".rollback", StringComparison.OrdinalIgnoreCase))
            .Where(dir => File.Exists(Path.Combine(dir, "package.json")) || File.Exists(Path.Combine(dir, "module.json")))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(ReadPackageDirectory)
            .ToArray();
    }

    public MptPackageDefinition ReadPackageDirectory(string packageDirectory)
    {
        var packagePath = Path.Combine(packageDirectory, "package.json");
        if (File.Exists(packagePath))
        {
            var package = ReadJson<MptPackageManifest>(packagePath);
            var modules = package.Modules
                .Select(path => Path.GetFullPath(Path.Combine(packageDirectory, path)))
                .Select(ReadModuleDefinition)
                .ToArray();

            return new MptPackageDefinition(Path.GetFullPath(packageDirectory), package, modules);
        }

        var modulePath = Path.Combine(packageDirectory, "module.json");
        if (!File.Exists(modulePath))
        {
            throw new FileNotFoundException("Expected package.json or module.json", packageDirectory);
        }

        var module = ReadModuleDefinition(modulePath);
        var synthetic = new MptPackageManifest
        {
            SchemaVersion = module.Manifest.SchemaVersion,
            Id = module.Manifest.PackageId,
            DisplayName = module.Manifest.DisplayName,
            Version = module.Manifest.Version,
            Modules = ["module.json"]
        };

        return new MptPackageDefinition(Path.GetFullPath(packageDirectory), synthetic, [module]);
    }

    public MptModuleDefinition ReadModuleDefinition(string moduleManifestPath)
    {
        var module = ReadJson<MptModuleManifest>(moduleManifestPath);
        return new MptModuleDefinition(Path.GetDirectoryName(Path.GetFullPath(moduleManifestPath))!, Path.GetFullPath(moduleManifestPath), module);
    }

    public T ReadJson<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Could not parse JSON file: {path}");
    }

    public static string ReadJsonText(string path) => File.ReadAllText(path);
}
