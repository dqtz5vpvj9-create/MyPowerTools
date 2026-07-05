using System.Text.Json.Nodes;
using System.Text.Json;
using MyPowerTools.Protocol;
using Sdk = MyPowerTools.Abstractions;

namespace MyPowerTools.Runtime;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, Sdk.SettingsSnapshotDocument> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _rootDirectory;

    public SettingsStore(string? rootDirectory = null)
    {
        _rootDirectory = rootDirectory;
        if (_rootDirectory is not null)
        {
            Directory.CreateDirectory(_rootDirectory);
        }
    }

    public Sdk.SettingsSnapshotDocument Get(string moduleId)
    {
        lock (_gate)
        {
            if (_settings.TryGetValue(moduleId, out var snapshot))
            {
                return snapshot;
            }

            snapshot = Load(moduleId) ?? new Sdk.SettingsSnapshotDocument(moduleId, 1, new JsonObject(), DateTimeOffset.UtcNow);
            _settings[moduleId] = snapshot;
            return snapshot;
        }
    }

    public Sdk.SettingsSnapshotDocument Update(Sdk.SettingsPatch patch)
    {
        lock (_gate)
        {
            if (!_settings.TryGetValue(patch.ModuleId, out var current))
            {
                current = new Sdk.SettingsSnapshotDocument(patch.ModuleId, 1, new JsonObject(), DateTimeOffset.UtcNow);
                _settings[patch.ModuleId] = current;
            }

            if (current.Revision != patch.ExpectedRevision)
            {
                throw new SettingsConflictException(patch.ModuleId, current.Revision, patch.ExpectedRevision);
            }

            var values = (JsonObject)current.Values.DeepClone();
            foreach (var pair in patch.Patch)
            {
                values[pair.Key] = pair.Value?.DeepClone();
            }

            var next = current with
            {
                Revision = current.Revision + 1,
                Values = values,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _settings[patch.ModuleId] = next;
            SaveBackup(current);
            Save(next);
            return next;
        }
    }

    public string Export(string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        var path = Path.Combine(destinationDirectory, $"settings-export-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
        lock (_gate)
        {
            var export = _settings.Values.OrderBy(snapshot => snapshot.ModuleId, StringComparer.OrdinalIgnoreCase).Select(ToPersisted).ToArray();
            AtomicWrite(path, JsonSerializer.Serialize(export, JsonOptions));
        }

        return path;
    }

    public void Import(string sourcePath)
    {
        var snapshots = JsonSerializer.Deserialize<PersistedSettingsSnapshot[]>(File.ReadAllText(sourcePath), JsonOptions) ?? [];
        lock (_gate)
        {
            foreach (var persisted in snapshots)
            {
                var snapshot = FromPersisted(persisted);
                _settings[snapshot.ModuleId] = snapshot;
                Save(snapshot);
            }
        }
    }

    public Sdk.SettingsSnapshotDocument Rollback(string moduleId)
    {
        if (_rootDirectory is null)
        {
            throw new InvalidOperationException("Settings rollback requires a persistent settings directory.");
        }

        lock (_gate)
        {
            var backupPath = GetPath(moduleId) + ".bak";
            if (!File.Exists(backupPath))
            {
                throw new FileNotFoundException("Settings backup was not found.", backupPath);
            }

            File.Copy(backupPath, GetPath(moduleId), overwrite: true);
            var snapshot = Load(moduleId) ?? throw new InvalidDataException($"Could not load rolled back settings for {moduleId}.");
            _settings[moduleId] = snapshot;
            return snapshot;
        }
    }

    private Sdk.SettingsSnapshotDocument? Load(string moduleId)
    {
        if (_rootDirectory is null)
        {
            return null;
        }

        var path = GetPath(moduleId);
        if (!File.Exists(path))
        {
            return null;
        }

        var persisted = JsonSerializer.Deserialize<PersistedSettingsSnapshot>(File.ReadAllText(path), JsonOptions);
        return persisted is null ? null : FromPersisted(persisted);
    }

    private void Save(Sdk.SettingsSnapshotDocument snapshot)
    {
        if (_rootDirectory is null)
        {
            return;
        }

        var path = GetPath(snapshot.ModuleId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        AtomicWrite(path, JsonSerializer.Serialize(ToPersisted(snapshot), JsonOptions));
    }

    private void SaveBackup(Sdk.SettingsSnapshotDocument snapshot)
    {
        if (_rootDirectory is null)
        {
            return;
        }

        var path = GetPath(snapshot.ModuleId) + ".bak";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite(path, JsonSerializer.Serialize(ToPersisted(snapshot), JsonOptions));
    }

    private string GetPath(string moduleId) => Path.Combine(_rootDirectory!, $"{moduleId}.settings.json");

    private static void AtomicWrite(string path, string content)
    {
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, content);
        if (File.Exists(path))
        {
            File.Replace(tempPath, path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    private static PersistedSettingsSnapshot ToPersisted(Sdk.SettingsSnapshotDocument snapshot)
    {
        return new PersistedSettingsSnapshot(snapshot.ModuleId, snapshot.Revision, snapshot.Values.ToJsonString(), snapshot.UpdatedAt);
    }

    private static Sdk.SettingsSnapshotDocument FromPersisted(PersistedSettingsSnapshot persisted)
    {
        var values = JsonNode.Parse(persisted.ValuesJson) as JsonObject ?? new JsonObject();
        return new Sdk.SettingsSnapshotDocument(persisted.ModuleId, persisted.Revision, values, persisted.UpdatedAt);
    }
}

public sealed record PersistedSettingsSnapshot(string ModuleId, ulong Revision, string ValuesJson, DateTimeOffset UpdatedAt);

public sealed record SettingsUpdateResult(Sdk.SettingsSnapshotDocument Snapshot, string ApplyState, string ApplyMessage);

public sealed class SettingsConflictException : Exception
{
    public SettingsConflictException(string moduleId, ulong currentRevision, ulong expectedRevision)
        : base($"{MptErrorCodes.SettingsConflict}: {moduleId} current revision is {currentRevision}, expected {expectedRevision}.")
    {
        ModuleId = moduleId;
        CurrentRevision = currentRevision;
        ExpectedRevision = expectedRevision;
    }

    public string ModuleId { get; }
    public ulong CurrentRevision { get; }
    public ulong ExpectedRevision { get; }
}

public sealed class SettingsValidationException : Exception
{
    public SettingsValidationException(string moduleId, IReadOnlyList<string> messages, MyPowerTools.Abstractions.MptRuntimeError? error)
        : base($"{MptErrorCodes.ValidationFailed}: {moduleId} settings validation failed. {BuildMessage(messages, error)}")
    {
        ModuleId = moduleId;
        Messages = messages;
        Error = error;
    }

    public string ModuleId { get; }
    public IReadOnlyList<string> Messages { get; }
    public MyPowerTools.Abstractions.MptRuntimeError? Error { get; }

    private static string BuildMessage(IReadOnlyList<string> messages, MyPowerTools.Abstractions.MptRuntimeError? error)
    {
        if (messages.Count > 0)
        {
            return string.Join("; ", messages);
        }

        return error?.Message ?? "Validation returned no details.";
    }
}
