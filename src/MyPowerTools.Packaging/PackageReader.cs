using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyPowerTools.Packaging;

public sealed class PackageReader
{
    private static readonly Regex SettingToken = new(
        @"\$\{settings\.(?<name>[A-Za-z0-9_.-]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
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

    public IReadOnlyList<MptPackageDefinition> DiscoverDevelopmentTools(IEnumerable<string> roots)
    {
        var packages = new List<MptPackageDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawRoot in roots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawRoot));
            if (!Directory.Exists(root))
            {
                continue;
            }

            var candidates = File.Exists(Path.Combine(root, "tool.json"))
                ? new[] { root }
                : Directory.EnumerateDirectories(root)
                    .Where(directory => File.Exists(Path.Combine(directory, "tool.json")));
            foreach (var directory in candidates.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var toolPath = Path.Combine(directory, "tool.json");
                if (!seen.Add(Path.GetFullPath(toolPath)))
                {
                    continue;
                }

                packages.Add(ReadDevelopmentToolDirectory(directory));
            }
        }

        return packages;
    }

    public MptPackageDefinition ReadDevelopmentToolDirectory(string toolDirectory)
    {
        toolDirectory = Path.GetFullPath(toolDirectory);
        var toolPath = Path.Combine(toolDirectory, "tool.json");
        if (!File.Exists(toolPath))
        {
            throw new FileNotFoundException("Expected tool.json", toolPath);
        }

        var tool = ReadJson<MptToolManifest>(toolPath);
        if (string.IsNullOrWhiteSpace(tool.ToolId))
        {
            throw new InvalidDataException($"toolId is required: {toolPath}");
        }

        var moduleId = string.IsNullOrWhiteSpace(tool.OwnerModuleId) ? tool.ToolId : tool.OwnerModuleId;
        var version = tool.ExtensionData.TryGetValue("version", out var versionElement) &&
                      versionElement.ValueKind == JsonValueKind.String
            ? versionElement.GetString() ?? "0.1.0"
            : "0.1.0";
        var packageId = $"dev.{tool.ToolId}";
        var module = new MptModuleManifest
        {
            SchemaVersion = "1.0",
            Id = moduleId,
            PackageId = packageId,
            DisplayName = tool.Title,
            Version = version,
            ModuleSdk = "1.0",
            Entrypoints = CreateDevelopmentEntrypoints(tool, toolDirectory),
            Capabilities = ["status", "commands", "settings", "logs", "events", "detailPage"],
            Permissions = tool.Permissions,
            Tools = ["tool.json"]
        };
        var package = new MptPackageManifest
        {
            SchemaVersion = "1.0",
            Id = packageId,
            DisplayName = tool.Title,
            Version = version,
            Publisher = "development",
            MinHostVersion = "0.2.0",
            Modules = ["tool.json"]
        };
        return new MptPackageDefinition(
            toolDirectory,
            package,
            [new MptModuleDefinition(toolDirectory, toolPath, module)],
            IsDevelopmentTool: true);
    }

    private static List<MptEntrypointManifest> CreateDevelopmentEntrypoints(MptToolManifest tool, string toolDirectory)
    {
        if (tool.Runtime is null || string.Equals(tool.Runtime.Transport, "none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var kind = tool.Runtime.Transport switch
        {
            "loopback-http" => "http",
            "stdio-jsonrpc" => "jsonrpc-stdio",
            "named-pipe-grpc" => "grpc-ipc",
            _ => tool.Runtime.Transport
        };
        JsonElement? health = string.IsNullOrWhiteSpace(tool.Runtime.HealthPath)
            ? null
            : JsonSerializer.SerializeToElement(new { path = tool.Runtime.HealthPath });
        var settings = ReadDevelopmentSettingValues(tool, toolDirectory);
        return
        [
            new MptEntrypointManifest
            {
                Kind = kind,
                Priority = 100,
                Command = string.IsNullOrWhiteSpace(tool.Runtime.Command) ? null : tool.Runtime.Command,
                Args = tool.Runtime.Args,
                BaseUrl = string.IsNullOrWhiteSpace(tool.Runtime.Endpoint) ? null : ExpandDevelopmentSettings(tool.Runtime.Endpoint, settings),
                Health = health,
                Compat = kind == "jsonrpc-stdio"
            }
        ];
    }

    private static IReadOnlyDictionary<string, string> ReadDevelopmentSettingValues(MptToolManifest tool, string toolDirectory)
    {
        if (string.IsNullOrWhiteSpace(tool.Settings?.Values))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        var path = Path.GetFullPath(Path.Combine(toolDirectory, tool.Settings.Values));
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.ValueKind == JsonValueKind.Object
            ? document.RootElement.EnumerateObject().ToDictionary(
                property => property.Name,
                property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString(),
                StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static string ExpandDevelopmentSettings(string value, IReadOnlyDictionary<string, string> settings)
    {
        return SettingToken.Replace(value ?? "", match =>
        {
            var name = match.Groups["name"].Value;
            if (!settings.TryGetValue(name, out var settingValue) || string.IsNullOrWhiteSpace(settingValue))
            {
                throw new InvalidDataException($"Required tool setting '{name}' is missing or empty.");
            }
            return settingValue;
        });
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
