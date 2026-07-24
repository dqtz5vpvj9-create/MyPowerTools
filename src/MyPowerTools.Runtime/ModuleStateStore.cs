using System.Text.Json;

namespace MyPowerTools.Runtime;

public sealed class ModuleStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly IReadOnlyList<string>? _initialEnabledModules;
    private ModuleStateSnapshot? _snapshot;

    public ModuleStateStore(string stateDirectory, IEnumerable<string>? initialEnabledModules = null)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "modules.enabled.json");
        _initialEnabledModules = initialEnabledModules?
            .Where(moduleId => !string.IsNullOrWhiteSpace(moduleId))
            .Select(moduleId => moduleId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(moduleId => moduleId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool IsEnabled(string moduleId)
    {
        lock (_gate)
        {
            var snapshot = Load();
            if (string.Equals(snapshot.DefaultMode, "allowlist", StringComparison.OrdinalIgnoreCase))
            {
                return (snapshot.EnabledModules ?? []).Contains(moduleId, StringComparer.OrdinalIgnoreCase) &&
                       !snapshot.DisabledModules.Contains(moduleId, StringComparer.OrdinalIgnoreCase);
            }
            return !snapshot.DisabledModules.Contains(moduleId, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlySet<string> DisabledModules()
    {
        lock (_gate)
        {
            return Load().DisabledModules.ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public ModuleStateSnapshot SetModuleEnabled(string moduleId, bool enabled)
    {
        lock (_gate)
        {
            var current = Load();
            var disabled = current.DisabledModules.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var enabledModules = (current.EnabledModules ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (string.Equals(current.DefaultMode, "allowlist", StringComparison.OrdinalIgnoreCase))
            {
                if (enabled)
                {
                    enabledModules.Add(moduleId);
                    disabled.Remove(moduleId);
                }
                else
                {
                    enabledModules.Remove(moduleId);
                    disabled.Add(moduleId);
                }
            }
            else
            {
                if (enabled)
                {
                    disabled.Remove(moduleId);
                }
                else
                {
                    disabled.Add(moduleId);
                }
            }

            var next = new ModuleStateSnapshot(
                current.Revision + 1,
                disabled.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                DateTimeOffset.UtcNow,
                current.DefaultMode,
                enabledModules.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray());
            _snapshot = next;
            Save(next);
            return next;
        }
    }

    private ModuleStateSnapshot Load()
    {
        if (_snapshot is not null)
        {
            return _snapshot;
        }

        if (!File.Exists(_path))
        {
            _snapshot = _initialEnabledModules is null
                ? new ModuleStateSnapshot(1, [], DateTimeOffset.UtcNow)
                : new ModuleStateSnapshot(
                    1,
                    [],
                    DateTimeOffset.UtcNow,
                    "allowlist",
                    _initialEnabledModules);
            if (_initialEnabledModules is not null)
            {
                Save(_snapshot);
            }
            return _snapshot;
        }

        var snapshot = JsonSerializer.Deserialize<ModuleStateSnapshot>(File.ReadAllText(_path), JsonOptions);
        _snapshot = snapshot ?? new ModuleStateSnapshot(1, [], DateTimeOffset.UtcNow);
        return _snapshot;
    }

    private void Save(ModuleStateSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, JsonOptions));
        MoveTempIntoPlace(tempPath);
    }

    private void MoveTempIntoPlace(string tempPath)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(_path))
                {
                    File.Replace(tempPath, _path, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tempPath, _path);
                }

                return;
            }
            catch (Exception) when (attempt < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }

        if (File.Exists(_path))
        {
            File.Replace(tempPath, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _path);
        }
    }
}

public sealed record ModuleStateSnapshot(
    ulong Revision,
    IReadOnlyList<string> DisabledModules,
    DateTimeOffset UpdatedAt,
    string DefaultMode = "enabled",
    IReadOnlyList<string>? EnabledModules = null);
