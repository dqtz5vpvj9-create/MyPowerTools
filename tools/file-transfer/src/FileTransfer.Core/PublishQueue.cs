using System.Text.Json;

namespace FileTransfer.Core;

/// <summary>
/// Files that were received but could not be published to the system Downloads collection.
/// They stay in the private inbox and are published again on the next receive start or app launch,
/// so a platform failure is recoverable instead of losing the file for the user.
/// </summary>
public sealed class PublishQueue(string directory)
{
    public const int MaxEntries = 100;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(directory, "publish-pending.json");

    public sealed record Entry(string Path, string Name, DateTimeOffset Time);

    public async Task<IReadOnlyList<Entry>> LoadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await ReadAsync(token); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(string path, string name, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var entries = (await ReadAsync(token)).Where(entry => entry.Path != path).ToList();
            entries.Add(new Entry(path, name, DateTimeOffset.UtcNow));
            await WriteAsync(entries, token);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAsync(string path, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var entries = (await ReadAsync(token)).Where(entry => entry.Path != path).ToList();
            await WriteAsync(entries, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<Entry>> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(await File.ReadAllTextAsync(_path, token), DirectTransfer.Json) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Never silently drop pending work; report the file so the user can repair or remove it.
            throw new InvalidDataException($"待发布记录已损坏，未做任何修改：{_path}。请修复或重命名该文件后重试。", ex);
        }
    }

    private async Task WriteAsync(IReadOnlyList<Entry> entries, CancellationToken token)
    {
        if (entries.Count == 0)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        var temporary = _path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(entries.TakeLast(MaxEntries).ToArray(), DirectTransfer.Json), token);
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
