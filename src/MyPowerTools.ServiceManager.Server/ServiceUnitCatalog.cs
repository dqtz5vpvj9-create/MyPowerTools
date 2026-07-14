using System.Collections.Concurrent;
using System.Text.Json;
using MyPowerTools.Abstractions;

namespace MyPowerTools.ServiceManager.Server;

/// <summary>
/// Loads and tracks Service Unit manifests from a deploy root.
/// The deploy root is versioned and separate from source <c>bin/Debug</c>; ordinary
/// Shell/Runner builds never touch activated units here.
///
/// Manifest layout (one JSON file per unit under <c>&lt;deployRoot&gt;/units/&lt;id&gt;.json</c>):
/// <code>
/// {
///   "id": "screenease.service",
///   "toolId": "screenease",
///   "displayName": "ScreenEase Service",
///   "exec": "ScreenEase.Service.exe",
///   "arguments": ["--pipe-only"],
///   "workingDirectory": "",
///   "environment": { "ScreenEase__Transport": "pipe" },
///   "autostart": true,
///   "restartPolicy": { "maxRestarts": 3, "backoffMs": 2000 },
///   "readiness": { "kind": "pipe", "address": "screenease.core", "timeoutMs": 5000 },
///   "stopTimeoutMs": 5000,
///   "dataRoots": ["%LOCALAPPDATA%/ScreenEase"],
///   "dependsOn": [],
///   "instanceToken": "..."
/// }
/// </code>
/// </summary>
public sealed class ServiceUnitCatalog
{
    private readonly string _deployRoot;
    private readonly string _unitsDirectory;
    private readonly ConcurrentDictionary<string, ServiceUnitManifest> _manifests = new(StringComparer.OrdinalIgnoreCase);

    public ServiceUnitCatalog(string deployRoot)
    {
        _deployRoot = deployRoot;
        _unitsDirectory = Path.Combine(deployRoot, "units");
    }

    public string DeployRoot => _deployRoot;

    public IReadOnlyCollection<ServiceUnitManifest> Manifests => _manifests.Values.ToArray();

    /// <summary>Reloads manifests from disk, returning the count loaded.</summary>
    public int Reload()
    {
        _manifests.Clear();
        if (!Directory.Exists(_unitsDirectory))
        {
            return 0;
        }

        foreach (var file in Directory.EnumerateFiles(_unitsDirectory, "*.json"))
        {
            try
            {
                var manifest = ParseManifest(File.ReadAllText(file), Path.GetDirectoryName(file));
                if (manifest is not null)
                {
                    _manifests[manifest.Id] = manifest;
                }
            }
            catch
            {
                // A single bad manifest must not prevent the rest from loading.
            }
        }

        return _manifests.Count;
    }

    public ServiceUnitManifest? TryGet(string unitId)
        => _manifests.TryGetValue(unitId, out var manifest) ? manifest : null;

    private static ServiceUnitManifest? ParseManifest(string json, string? manifestDirectory)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("id", out var idEl) || idEl.GetString() is not { } id)
        {
            return null;
        }

        var toolId = root.TryGetProperty("toolId", out var t) ? t.GetString() ?? "" : "";
        var displayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? id : id;
        var execRaw = root.TryGetProperty("exec", out var e) ? e.GetString() ?? "" : "";
        var exec = ResolvePath(execRaw, manifestDirectory);

        var arguments = new List<string>();
        if (root.TryGetProperty("arguments", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in argsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } a)
                {
                    arguments.Add(a);
                }
            }
        }

        var workingDirectory = root.TryGetProperty("workingDirectory", out var wd) ? ResolvePath(wd.GetString() ?? "", manifestDirectory) : null;

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("environment", out var envEl) && envEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in envEl.EnumerateObject())
            {
                environment[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        var autostart = root.TryGetProperty("autostart", out var au) && au.GetBoolean();
        var stopTimeoutMs = root.TryGetProperty("stopTimeoutMs", out var st) ? st.GetInt32() : 5000;

        ServiceUnitRestartPolicy? restartPolicy = null;
        if (root.TryGetProperty("restartPolicy", out var rpEl) && rpEl.ValueKind == JsonValueKind.Object)
        {
            var max = rpEl.TryGetProperty("maxRestarts", out var mr) ? mr.GetInt32() : 3;
            var backoffMs = rpEl.TryGetProperty("backoffMs", out var bf) ? bf.GetInt32() : 2000;
            restartPolicy = new ServiceUnitRestartPolicy(max, TimeSpan.FromMilliseconds(backoffMs));
        }

        ServiceUnitReadiness? readiness = null;
        if (root.TryGetProperty("readiness", out var rdEl) && rdEl.ValueKind == JsonValueKind.Object)
        {
            var kind = rdEl.TryGetProperty("kind", out var k) ? k.GetString() ?? "none" : "none";
            var address = rdEl.TryGetProperty("address", out var ad) ? ad.GetString() : null;
            var timeoutMs = rdEl.TryGetProperty("timeoutMs", out var to) ? to.GetInt32() : 5000;
            readiness = new ServiceUnitReadiness(kind, address, TimeSpan.FromMilliseconds(timeoutMs));
        }

        var dataRoots = new List<string>();
        if (root.TryGetProperty("dataRoots", out var drEl) && drEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in drEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } d)
                {
                    dataRoots.Add(Expand(d));
                }
            }
        }

        var dependsOn = new List<string>();
        if (root.TryGetProperty("dependsOn", out var depEl) && depEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in depEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } dp)
                {
                    dependsOn.Add(dp);
                }
            }
        }

        var instanceToken = root.TryGetProperty("instanceToken", out var it) ? it.GetString() ?? "" : "";

        return new ServiceUnitManifest(
            Id: id,
            ToolId: toolId,
            DisplayName: displayName,
            Exec: exec,
            Arguments: arguments,
            WorkingDirectory: string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            Environment: environment,
            Autostart: autostart,
            RestartPolicy: restartPolicy,
            Readiness: readiness,
            StopTimeout: TimeSpan.FromMilliseconds(stopTimeoutMs),
            DataRoots: dataRoots,
            DependsOn: dependsOn,
            InstanceToken: instanceToken);
    }

    private static string ResolvePath(string path, string? relativeTo)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return string.IsNullOrEmpty(relativeTo) ? path : Path.GetFullPath(Path.Combine(relativeTo, path));
    }

    private static string Expand(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        return path
            .Replace("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase);
    }
}
