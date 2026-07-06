using System.Text.Json;
using System.Text.Json.Nodes;
using SdkMptModuleEvent = MyPowerTools.Abstractions.MptModuleEvent;

namespace MyPowerTools.Runtime;

public sealed class ModuleEventStore
{
    private const int MaxEventsPerModule = 1000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _root;
    private readonly string _cursorPath;
    private readonly Dictionary<string, ulong> _cursors;

    public ModuleEventStore(string stateRoot)
    {
        _root = Path.Combine(stateRoot, "module-events");
        _cursorPath = Path.Combine(_root, "cursors.json");
        Directory.CreateDirectory(_root);
        _cursors = LoadCursorFile();
    }

    public IReadOnlyDictionary<string, ulong> LoadCursors()
    {
        lock (_sync)
        {
            return new Dictionary<string, ulong>(_cursors, StringComparer.OrdinalIgnoreCase);
        }
    }

    public ulong CursorFor(string moduleId)
    {
        lock (_sync)
        {
            return _cursors.TryGetValue(moduleId, out var seq) ? seq : 0UL;
        }
    }

    public void Record(SdkMptModuleEvent evt)
    {
        lock (_sync)
        {
            var current = _cursors.TryGetValue(evt.ModuleId, out var seq) ? seq : 0UL;
            _cursors[evt.ModuleId] = Math.Max(current, evt.Seq);
            SaveCursors();
            AppendHistory(evt);
        }
    }

    public IReadOnlyList<SdkMptModuleEvent> ReadHistory(string moduleId)
    {
        lock (_sync)
        {
            var path = HistoryPath(moduleId);
            if (!File.Exists(path))
            {
                return [];
            }

            return File.ReadAllLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonSerializer.Deserialize<ModuleEventRecord>(line))
                .Where(record => record is not null)
                .Select(record => record!.ToEvent())
                .ToArray();
        }
    }

    private Dictionary<string, ulong> LoadCursorFile()
    {
        if (!File.Exists(_cursorPath))
        {
            return new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        }

        var values = JsonSerializer.Deserialize<Dictionary<string, ulong>>(File.ReadAllText(_cursorPath))
            ?? new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        return new Dictionary<string, ulong>(values, StringComparer.OrdinalIgnoreCase);
    }

    private void SaveCursors()
    {
        Directory.CreateDirectory(_root);
        var temp = _cursorPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_cursors, JsonOptions));
        File.Move(temp, _cursorPath, overwrite: true);
    }

    private void AppendHistory(SdkMptModuleEvent evt)
    {
        var path = HistoryPath(evt.ModuleId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var line = JsonSerializer.Serialize(ModuleEventRecord.FromEvent(evt));
        File.AppendAllText(path, line + Environment.NewLine);

        var lines = File.ReadAllLines(path);
        if (lines.Length <= MaxEventsPerModule)
        {
            return;
        }

        var temp = path + ".tmp";
        File.WriteAllLines(temp, lines[^MaxEventsPerModule..]);
        File.Move(temp, path, overwrite: true);
    }

    private string HistoryPath(string moduleId)
    {
        return Path.Combine(_root, SafeFileName(moduleId) + ".jsonl");
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(value.Select(ch => invalid.Contains(ch) ? '_' : ch));
    }

    private sealed record ModuleEventRecord(string ModuleId, ulong Seq, string Type, DateTimeOffset Time, JsonObject Payload)
    {
        public static ModuleEventRecord FromEvent(SdkMptModuleEvent evt)
        {
            return new ModuleEventRecord(
                evt.ModuleId,
                evt.Seq,
                evt.Type,
                evt.Time,
                (evt.Payload.DeepClone() as JsonObject) ?? new JsonObject());
        }

        public SdkMptModuleEvent ToEvent()
        {
            return new SdkMptModuleEvent(
                ModuleId,
                Seq,
                Type,
                Time,
                (Payload.DeepClone() as JsonObject) ?? new JsonObject());
        }
    }
}
