using System.Text.Json;
using System.Text.Json.Nodes;

namespace FileTransfer.Core;

/// <summary>
/// Persists the transfer history, the last emitted event sequence and the transfer that was in
/// flight. History survives a restart and a long-lived event cursor never rewinds, so subscribers
/// keep receiving events after a module reload instead of silently losing them.
/// </summary>
public sealed class TransferStore(string directory)
{
    public const int MaxRecords = 50;
    private const string FileName = "history.json";
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly string _path = Path.Combine(directory, FileName);
    private long _sequence;

    public sealed record Snapshot(long Sequence, IReadOnlyList<JsonObject> Records, JsonObject? Active);

    public long Sequence => Interlocked.Read(ref _sequence);
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    public async Task<Snapshot> LoadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return new(0, [], null);
        JsonObject document;
        try { document = JsonNode.Parse(await File.ReadAllTextAsync(_path, token))?.AsObject() ?? throw new JsonException(); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Never rewrite or discard the user's record; report it so it can be repaired or removed.
            throw new InvalidDataException($"传输历史文件已损坏，未做任何修改：{_path}。请修复或重命名该文件后重新加载工具。", ex);
        }
        var records = new List<JsonObject>();
        if (document["records"] is not null and not JsonArray)
            throw new InvalidDataException($"传输历史文件格式无效：{_path}。请修复或重命名该文件后重新加载工具。");
        foreach (var node in document["records"]?.AsArray() ?? [])
        {
            if (node is not JsonObject record || record["name"] is null || record["state"] is null)
                throw new InvalidDataException($"传输历史文件中存在无法识别的记录：{_path}。请修复或重命名该文件后重新加载工具。");
            records.Add((JsonObject)record.DeepClone());
        }
        records = Trim(records);
        var sequence = document["sequence"]?.GetValue<long>() ?? 0;
        _sequence = Math.Max(sequence, records.Count);
        return new(_sequence, records, document["active"] is JsonObject active ? (JsonObject)active.DeepClone() : null);
    }

    public async Task SaveAsync(IReadOnlyList<JsonObject> records, JsonObject? active, CancellationToken token)
    {
        await _write.WaitAsync(token);
        try
        {
            var payload = new JsonObject
            {
                ["sequence"] = Sequence,
                ["active"] = active?.DeepClone(),
                ["records"] = new JsonArray(Trim(records).Select(item => (JsonNode?)item.DeepClone()).ToArray())
            };
            var temporary = _path + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, payload.ToJsonString(), token);
                File.Move(temporary, _path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _write.Release(); }
    }

    private static List<JsonObject> Trim(IReadOnlyList<JsonObject> records) =>
        records.Count <= MaxRecords ? [.. records] : [.. records.Skip(records.Count - MaxRecords)];
}
