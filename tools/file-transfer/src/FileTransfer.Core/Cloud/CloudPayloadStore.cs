using System.Text.Json;
using FileTransfer.Core.Assistant;
namespace FileTransfer.Core.Cloud;

/// <summary>Only objects this device created. Account credentials and administrator tokens never enter this file.</summary>
public sealed class CloudPayloadStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<CloudPayloadMapping[]> ReadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await ReadCoreAsync(token); }
        finally { _gate.Release(); }
    }
    private async Task<CloudPayloadMapping[]> ReadCoreAsync(CancellationToken token) => File.Exists(path)
        ? JsonSerializer.Deserialize<CloudPayloadMapping[]>(await File.ReadAllTextAsync(path, token), AssistantJson.Options) ?? [] : [];
    public async Task SaveAsync(CloudPayloadMapping mapping, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var records = await ReadCoreAsync(token);
            var updated = records.Where(r => r.Offer.ConversationId != mapping.Offer.ConversationId || r.Offer.Message.Id != mapping.Offer.Message.Id).Append(mapping).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path + ".new", JsonSerializer.Serialize(updated, AssistantJson.Options), token);
            File.Move(path + ".new", path, true);
        }
        finally { _gate.Release(); }
    }
}
