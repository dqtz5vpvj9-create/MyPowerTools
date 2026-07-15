using MyPowerTools.Abstractions;
using MyPowerTools.Packaging;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MyPowerTools.Runtime;

public sealed class ToolRegistry
{
    private static readonly Regex SettingToken = new(
        @"\$\{settings\.(?<name>[A-Za-z0-9_.-]+)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly PackageReader _reader;
    private readonly List<ToolDescriptor> _tools = [];

    public ToolRegistry(PackageReader reader)
    {
        _reader = reader;
    }

    public IReadOnlyList<ToolDescriptor> Tools => _tools;

    public void Load(IEnumerable<RuntimeModuleRecord> modules)
    {
        _tools.Clear();
        foreach (var module in modules)
        {
            foreach (var relativePath in module.Module.Manifest.Tools)
            {
                var manifestPath = ResolveManifestPath(module.Module.Directory, relativePath);
                var manifest = _reader.ReadJson<MptToolManifest>(manifestPath);
                var descriptor = ToDescriptor(module, manifest, manifestPath);
                if (_tools.Any(tool => string.Equals(tool.ToolId, descriptor.ToolId, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidDataException($"Duplicate tool id '{descriptor.ToolId}' declared by {manifestPath}.");
                }

                _tools.Add(descriptor);
            }
        }

        _tools.Sort((left, right) =>
        {
            var byOrder = left.HomeCard.Order.CompareTo(right.HomeCard.Order);
            return byOrder != 0
                ? byOrder
                : StringComparer.OrdinalIgnoreCase.Compare(left.Title, right.Title);
        });
    }

    public ToolDescriptor? Find(string toolId)
    {
        return _tools.FirstOrDefault(tool => string.Equals(tool.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveManifestPath(string moduleDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("Tool manifest path cannot be empty.");
        }

        var moduleRoot = Path.GetFullPath(moduleDirectory);
        var manifestPath = Path.GetFullPath(Path.Combine(moduleRoot, relativePath));
        var rootedPrefix = moduleRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!manifestPath.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Tool manifest path escapes module directory: {relativePath}");
        }

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("Tool manifest was not found.", manifestPath);
        }

        return manifestPath;
    }

    private static ToolDescriptor ToDescriptor(RuntimeModuleRecord module, MptToolManifest manifest, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifest.ToolId))
        {
            throw new InvalidDataException($"Tool id is required: {manifestPath}");
        }

        var ownerModuleId = string.IsNullOrWhiteSpace(manifest.OwnerModuleId)
            ? module.Module.Manifest.Id
            : manifest.OwnerModuleId;
        if (!string.Equals(ownerModuleId, module.Module.Manifest.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Tool '{manifest.ToolId}' owner '{ownerModuleId}' does not match module '{module.Module.Manifest.Id}'.");
        }

        var toolDirectory = Path.GetDirectoryName(manifestPath)!;
        var settingValues = LoadSettingValues(toolDirectory, manifest.Settings?.Values);
        var routes = manifest.Routes
            .Select(route =>
            {
                var surface = route.Surface;
                return new ToolRoute(
                    route.RouteId,
                    route.SurfaceId,
                    route.Title,
                    route.Icon,
                    surface?.Kind ?? "",
                    ResolveToolValue(toolDirectory, ExpandSettings(surface?.Source ?? "", settingValues)),
                    ResolveToolValue(toolDirectory, ExpandSettings(surface?.StaticRoot ?? "", settingValues)),
                    ResolveToolValue(toolDirectory, ExpandSettings(surface?.Assembly ?? "", settingValues)),
                    surface?.Type ?? "",
                    surface?.OpenExternal ?? false,
                    surface?.AllowedOrigins ?? []);
            })
            .ToArray();
        if (routes.Length == 0 || routes.Any(route => string.IsNullOrWhiteSpace(route.RouteId) || string.IsNullOrWhiteSpace(route.SurfaceId)))
        {
            throw new InvalidDataException($"Tool '{manifest.ToolId}' must declare valid routes.");
        }

        if (routes.Select(route => route.RouteId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != routes.Length)
        {
            throw new InvalidDataException($"Tool '{manifest.ToolId}' contains duplicate route ids.");
        }

        if (!routes.Any(route => string.Equals(route.RouteId, manifest.PrimaryRouteId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Tool '{manifest.ToolId}' primary route '{manifest.PrimaryRouteId}' was not declared.");
        }

        var availability = string.IsNullOrWhiteSpace(manifest.Availability)
            ? "available"
            : manifest.Availability.Trim().ToLowerInvariant();
        if (availability is not ("available" or "paused" or "in-development"))
        {
            throw new InvalidDataException($"Tool '{manifest.ToolId}' declares unsupported availability '{manifest.Availability}'.");
        }

        return new ToolDescriptor(
            manifest.ToolId,
            ownerModuleId,
            manifest.Title,
            manifest.Description,
            manifest.Icon,
            manifest.Category,
            manifest.PrimaryRouteId,
            routes,
            new ToolHomeCard(
                manifest.HomeCard.Summary,
                manifest.HomeCard.PrimaryActionLabel,
                manifest.HomeCard.StatusBinding,
                manifest.HomeCard.Order),
            availability,
            manifest.Type,
            toolDirectory,
            manifest.Runtime is null
                ? null
                : new ToolRuntime(
                    manifest.Runtime.Transport,
                    ExpandSettings(manifest.Runtime.Endpoint, settingValues),
                    ExpandSettings(manifest.Runtime.Command, settingValues),
                    manifest.Runtime.Args,
                    manifest.Runtime.HealthPath,
                    manifest.Runtime.LogsPath,
                    manifest.Runtime.TimeoutMs,
                    manifest.Runtime.Remote),
            manifest.Settings is null
                ? null
                : new ToolSettings(
                    ResolveToolValue(toolDirectory, manifest.Settings.Schema),
                    ResolveToolValue(toolDirectory, manifest.Settings.Values),
                    manifest.Settings.Secrets),
            manifest.Commands.Select(command => new ToolCommand(
                command.Id,
                command.Title,
                command.Description,
                command.Method,
                command.Path)).ToArray());
    }

    private static string ResolveToolValue(string toolDirectory, string value)
    {
        if (SettingToken.IsMatch(value))
        {
            throw new InvalidDataException($"Tool value contains an unresolved settings token: {value}");
        }
        if (string.IsNullOrWhiteSpace(value) ||
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return value;
        }

        return Path.GetFullPath(Path.Combine(toolDirectory, value));
    }

    private static IReadOnlyDictionary<string, string> LoadSettingValues(string toolDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var path = ResolveToolValue(toolDirectory, relativePath);
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject values)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return values
            .Where(item => item.Value is JsonValue)
            .ToDictionary(
                item => item.Key,
                item => item.Value!.ToJsonString().Trim('"'),
                StringComparer.OrdinalIgnoreCase);
    }

    private static string ExpandSettings(string value, IReadOnlyDictionary<string, string> settings)
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
}
