using System.Text.Json;

namespace MyPowerTools.Runtime;

public sealed class HotkeyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private HotkeyStoreSnapshot? _cache;

    public HotkeyStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "hotkeys.json");
    }

    public HotkeyOverride? Get(string moduleId, string hotkeyId)
    {
        lock (_gate)
        {
            return Load().Overrides.FirstOrDefault(item =>
                string.Equals(item.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.HotkeyId, hotkeyId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public HotkeyStoreSnapshot Set(string moduleId, string hotkeyId, string gesture, bool disabled, string commandArgsJson)
    {
        lock (_gate)
        {
            var snapshot = Load();
            var next = snapshot.Overrides
                .Where(item => !string.Equals(item.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase) ||
                               !string.Equals(item.HotkeyId, hotkeyId, StringComparison.OrdinalIgnoreCase))
                .Append(new HotkeyOverride(moduleId, hotkeyId, gesture.Trim(), disabled, string.IsNullOrWhiteSpace(commandArgsJson) ? "{}" : commandArgsJson))
                .OrderBy(item => item.ModuleId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.HotkeyId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Save(snapshot with { Revision = snapshot.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow, Overrides = next });
        }
    }

    public HotkeyStoreSnapshot Reset(string moduleId, string hotkeyId)
    {
        lock (_gate)
        {
            var snapshot = Load();
            var next = snapshot.Overrides
                .Where(item => !string.Equals(item.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase) ||
                               !string.Equals(item.HotkeyId, hotkeyId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return Save(snapshot with { Revision = snapshot.Revision + 1, UpdatedAt = DateTimeOffset.UtcNow, Overrides = next });
        }
    }

    private HotkeyStoreSnapshot Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(_path))
        {
            _cache = new HotkeyStoreSnapshot(0, [], DateTimeOffset.UtcNow);
            return _cache;
        }

        _cache = JsonSerializer.Deserialize<HotkeyStoreSnapshot>(File.ReadAllText(_path), JsonOptions)
            ?? new HotkeyStoreSnapshot(0, [], DateTimeOffset.UtcNow);
        return _cache;
    }

    private HotkeyStoreSnapshot Save(HotkeyStoreSnapshot snapshot)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(temp, _path, overwrite: true);
        _cache = snapshot;
        return snapshot;
    }
}

public sealed record HotkeyStoreSnapshot(ulong Revision, IReadOnlyList<HotkeyOverride> Overrides, DateTimeOffset UpdatedAt);

public sealed record HotkeyOverride(string ModuleId, string HotkeyId, string Gesture, bool Disabled, string CommandArgsJson);
