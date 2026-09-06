using System.Text.Json;

namespace MyPowerTools.Shell.Avalonia.Services;

/// <summary>Per-user favorites and most-recently opened tools, independent of tool installation.</summary>
public sealed class ShellToolPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _path;
    private ShellToolPreferences? _current;
    private bool _preserveUnreadableFile;

    public ShellToolPreferencesStore(string? path = null)
    {
        var root = Environment.GetEnvironmentVariable("MPT_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPowerTools");
        }
        _path = Path.GetFullPath(path ?? Path.Combine(root, "state", "tool-preferences.json"));
    }

    public ShellToolPreferences Current => _current ??= Read();

    public Task SetFavoriteAsync(string toolId, bool favorite) => UpdateAsync(current => current with
    {
        FavoriteToolIds = favorite
            ? Normalize(current.FavoriteToolIds.Append(toolId))
            : current.FavoriteToolIds.Where(id => !string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase)).ToArray()
    });

    public Task RecordOpenedAsync(string toolId) => UpdateAsync(current => current with
    {
        RecentToolIds = Normalize(new[] { toolId }.Concat(current.RecentToolIds)).Take(20).ToArray()
    });

    private ShellToolPreferences Read()
    {
        try
        {
            if (!File.Exists(_path)) return new();
            var value = JsonSerializer.Deserialize<ShellToolPreferences>(File.ReadAllText(_path), JsonOptions) ?? new();
            return value with
            {
                FavoriteToolIds = Normalize(value.FavoriteToolIds ?? []),
                RecentToolIds = Normalize(value.RecentToolIds ?? []).Take(20).ToArray()
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Opening tools must still work. Keep the original bytes before a later explicit write.
            _preserveUnreadableFile = true;
            return new();
        }
    }

    private async Task UpdateAsync(Func<ShellToolPreferences, ShellToolPreferences> update)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        string? temporary = null;
        try
        {
            var next = update(Current);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_preserveUnreadableFile && File.Exists(_path))
            {
                File.Copy(_path, _path + $".unreadable-{Guid.NewGuid():N}");
                _preserveUnreadableFile = false;
            }
            temporary = _path + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(next, JsonOptions)).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            _current = next;
        }
        finally
        {
            try { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }
            finally { _writeGate.Release(); }
        }
    }

    private static string[] Normalize(IEnumerable<string> ids) => ids
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Select(id => id.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record ShellToolPreferences
{
    public string[] FavoriteToolIds { get; init; } = [];
    public string[] RecentToolIds { get; init; } = [];
}
