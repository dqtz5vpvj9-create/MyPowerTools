namespace FileTransfer.Core.Assistant;

public sealed record AssistantStoredFile(string ItemId, string Name, long Bytes);
public sealed record AssistantCleanupResult(long FreedBytes, IReadOnlyList<string> Removed, IReadOnlyList<string> Errors);

public sealed partial class AssistantStore
{
    private bool OwnsDownloadedFile(AssistantItem item)
    {
        if (item.LocalPath is not { Length: > 0 } path) return false;
        var root = Path.GetFullPath(InboxRoot) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        // Do not follow a substituted directory or file outside the received-content store.
        for (var current = full; current.Length >= root.Length; current = Path.GetDirectoryName(current)!)
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    public async Task<IReadOnlyList<AssistantStoredFile>> InspectStorageAsync(CancellationToken token)
    {
        var state = await SnapshotAsync(token);
        return state.Incoming(state.Identity?.DeviceId ?? "")
            .Where(item => item.Kind != AssistantItemKind.Text && item.State == AssistantItemState.Available
                && AssistantContent.HasLocalContent(item) && OwnsDownloadedFile(item))
            .Select(item => new AssistantStoredFile(item.Id, item.Name ?? "文件", new FileInfo(item.LocalPath!).Length))
            .OrderByDescending(item => item.Bytes).ToArray();
    }

    public async Task<AssistantCleanupResult> CleanStorageAsync(IReadOnlyCollection<string> ids, CancellationToken token)
    {
        var removed = new List<string>();
        var errors = new List<string>();
        long freed = 0;
        await SyncGate.WaitAsync(token);
        try
        {
            await _gate.WaitAsync(token);
            try
            {
                var state = await LoadCoreAsync(token);
                foreach (var id in ids.Distinct(StringComparer.Ordinal))
                {
                    var item = state.Find(id);
                    if (item is null || item.SenderDeviceId == state.Identity?.DeviceId || item.Kind == AssistantItemKind.Text
                        || item.State != AssistantItemState.Available) { errors.Add($"{id}：文件正在使用或不是已接收的附件。"); continue; }
                    try
                    {
                        if (AssistantContent.HasLocalContent(item))
                        {
                            if (!OwnsDownloadedFile(item)) { errors.Add($"{item.Name}：文件不在下载管理目录内。"); continue; }
                            var bytes = new FileInfo(item.LocalPath!).Length;
                            // Persist the opt-out first. A crash after deletion must not cause a background re-download.
                            await CommitAsync(state, () => item.ManualDownloadOnly = true, token);
                            File.Delete(item.LocalPath!);
                            freed += bytes;
                        }
                        await CommitAsync(state, () => { item.ManualDownloadOnly = true; item.LocalPath = null; item.BytesDone = 0; item.Error = null; }, token);
                        removed.Add(id);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { errors.Add($"{item.Name}：{ex.Message}"); }
                }
            }
            finally { _gate.Release(); }
        }
        finally { SyncGate.Release(); }
        return new(freed, removed, errors);
    }
}
