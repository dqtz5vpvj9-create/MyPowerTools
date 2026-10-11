using System.Text;
using System.Text.Json;

namespace FileTransfer.Core.Assistant;

/// <summary>
/// Durable local state for the assistant: the unified timeline, the pending payload copies, the
/// downloaded inbox and the local composer draft. Everything the user sent survives a crash, a restart
/// and a network outage; nothing is trimmed to the legacy 50-record transfer history cap, and no file
/// outside this store's own directory is ever touched or deleted.
/// </summary>
public sealed partial class AssistantStore
{
    /// <summary>Bookkeeping bound only. Trimming an id may cost one extra manifest GET, never a payload download.</summary>
    public const int MaxKnownRemoteIds = 2000;

    private const string StateFileName = "assistant.json";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private AssistantState? _cache;

    public AssistantStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("助手存储目录不能为空。", nameof(directory));
        Directory = directory;
        _path = Path.Combine(directory, StateFileName);
        PayloadRoot = Path.Combine(directory, "payload");
        InboxRoot = Path.Combine(directory, "inbox");
    }

    public string Directory { get; }
    public string PayloadRoot { get; }
    public string InboxRoot { get; }

    /// <summary>
    /// Serializes the long-running sync passes of every <see cref="AssistantSync"/> that shares this store,
    /// so M4 may create a fresh sync instance each round without two rounds racing inside one snapshot.
    /// The short store transactions keep using their own gate and are never blocked by the network.
    /// </summary>
    internal SemaphoreSlim SyncGate { get; } = new(1, 1);

    /// <summary>
    /// Returns the process-wide snapshot. The first call also performs restart recovery: entries stuck in
    /// <c>sending</c>/<c>downloading</c> return to a retryable state and the tool's own partial files are swept.
    /// One store instance must be shared by the module and its background scheduler.
    /// </summary>
    public async Task<AssistantState> LoadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return await LoadCoreAsync(token); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Captures a committed, detached view for UI serialization. Reading the live item outside this
    /// gate can mix a failed state with the cleared error of a retry that starts during serialization.
    /// </summary>
    public async Task<AssistantState> SnapshotAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { return (await LoadCoreAsync(token)).Copy(); }
        finally { _gate.Release(); }
    }

    public async Task<AssistantState> ConfigureAsync(AssistantIdentity identity, CancellationToken token)
    {
        AssistantValidation.Identity(identity);
        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadCoreAsync(token);
            if (state.Identity != identity || state.ActiveConversationKey is null)
                await CommitAsync(state, () =>
                {
                    AssistantConversations.MigrateDrafts(state);
                    state.Identity = identity;
                    AssistantConversations.MigrateDrafts(state);
                }, token);
            return state;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Persists a send request: text, attachments, or both. Every attachment is copied into the tool's own
    /// durable directory before the state file is written, and the whole batch is one transaction, so a
    /// returned list is fully durable and a failure leaves neither payload copies nor a conversation entry.
    /// </summary>
    public async Task<IReadOnlyList<AssistantItem>> EnqueueAsync(AssistantIdentity identity, AssistantDraft draft,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(draft);
        AssistantValidation.Identity(identity);
        var target = draft.TargetDeviceId is null ? null : TransferFiles.DeviceId(draft.TargetDeviceId);
        var paths = draft.Paths ?? [];
        if (draft.Text is null && paths.Count == 0) throw new ArgumentException("请提供要发送的文字或文件。");
        // Text and attachments together are one send: a caption with files is normal use, not two messages.

        var created = new List<AssistantItem>();
        var directories = new List<string>();
        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadCoreAsync(token);
            try
            {
                if (draft.Text is not null)
                {
                    var text = AssistantValidation.Text(draft.Text);
                    created.Add(NewItem(identity, target, AssistantItemKind.Text, text, null, Encoding.UTF8.GetByteCount(text)));
                }
                foreach (var path in paths) created.Add(await CopyPayloadAsync(identity, target, path, directories, token));
                await CommitAsync(state, () =>
                {
                    state.Identity = identity;
                    foreach (var item in created) state.Add(item);
                }, token);
            }
            catch
            {
                foreach (var directory in directories) RemoveDirectory(directory);
                throw;
            }
            return created;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Adopts an entry that arrived over the direct transport and is already saved locally, so it joins the
    /// same durable timeline. Duplicate ids never create a second message: an entry that was only known from
    /// the relay (no local content) is upgraded to <c>available</c> with the real path, an entry that already
    /// has local content is left untouched, and a cancelled entry is never revived.
    /// </summary>
    public async Task<AssistantItem> AdoptAsync(AssistantIdentity identity, AssistantItem item, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(item);
        AssistantValidation.Identity(identity);
        var manifest = AssistantValidation.Manifest(item.ToManifest());
        if (string.Equals(manifest.SenderDeviceId, identity.DeviceId, StringComparison.Ordinal))
            throw new ArgumentException("本机发送的条目请使用 EnqueueAsync。", nameof(item));
        var hasContent = manifest.Kind == AssistantItemKind.Text
            ? manifest.Text is not null
            : !string.IsNullOrEmpty(item.LocalPath) && File.Exists(item.LocalPath);

        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadCoreAsync(token);
            var existing = state.Find(manifest.Id);
            // A duplicate frame for an entry this device already holds is simply ignored.
            if (existing is not null && (AssistantContent.HasLocalContent(existing) || existing.Kind == AssistantItemKind.Text))
                return existing;
            // Metadata alone is never "saved": the caller must hand over real, openable content.
            if (!hasContent)
                throw new ArgumentException("直接接收的附件必须已经保存到本地，只有中转记录不算已保存。", nameof(item));
            if (existing is not null)
            {
                if (existing.State == AssistantItemState.Cancelled) return existing;   // a cancel is never undone
                await CommitAsync(state, () =>
                {
                    existing.LocalPath = item.LocalPath;
                    existing.TransportRoute = item.TransportRoute;
                    existing.State = AssistantItemState.Available;
                    existing.BytesDone = existing.Size;
                    existing.Error = null;
                    existing.Attempts = 0;
                }, token);
                return existing;
            }
            var adopted = NewItem(identity, manifest.TargetDeviceId, manifest.Kind, manifest.Text, manifest.Name, manifest.Size) with
            {
                Id = manifest.Id,
                CreatedAt = manifest.CreatedAt,
                SenderDeviceId = manifest.SenderDeviceId,
                SenderName = manifest.SenderName,
                LocalPath = item.LocalPath,
                ConversationId = item.ConversationId,
                Provenance = item.Provenance,
                SourceRelay = item.SourceRelay,
                TransportRoute = item.TransportRoute,
                State = AssistantItemState.Available,
                BytesDone = manifest.Size
            };
            await CommitAsync(state, () => state.Add(adopted), token);
            return adopted;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Persists a snapshot the caller has already edited. This is the one operation that cannot roll back an
    /// in-memory edit (the caller owns it), so if the write fails the live snapshot is restored from the last
    /// generation that actually reached disk — the same thing a restart would load. Prefer
    /// <see cref="MutateAsync"/>, which restores exactly and needs no disk read.
    /// </summary>
    public async Task SaveAsync(AssistantState state, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(token);
        try
        {
            if (!ReferenceEquals(state, _cache))
            {
                await WriteAsync(state, token);
                _cache = state;
                return;
            }
            try { await WriteAsync(state, token); }
            catch
            {
                // Writes are atomic, so the state file still holds the last committed generation.
                try { state.RestoreFrom(await ReadAsync(CancellationToken.None)); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomic read-modify-write for modules that update delivery state from their own code paths.</summary>
    public async Task<AssistantState> MutateAsync(Action<AssistantState> change, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadCoreAsync(token);
            await CommitAsync(state, () => change(state), token);
            return state;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Atomic read-modify-write for a local preference that changes far more often than the timeline:
    /// <paramref name="change"/> reports whether anything really changed, and an unchanged snapshot
    /// skips the disk write. It must only mutate the snapshot when it returns true. The decision is made
    /// inside the same gate as a commit, so a no-op can never race a concurrent transaction, and a
    /// failed write still restores the snapshot exactly.
    /// </summary>
    public async Task<AssistantState> MutateIfChangedAsync(Func<AssistantState, bool> change, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(token);
        try
        {
            var state = await LoadCoreAsync(token);
            var backup = state.Copy();
            try
            {
                if (change(state)) await WriteAsync(state, token);
            }
            catch
            {
                state.RestoreFrom(backup);
                throw;
            }
            return state;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Display-only transfer progress. It takes the store gate, so it can never mutate the snapshot while a
    /// transaction serializes it, and it never blocks a transfer: a busy gate just skips the tick. The
    /// authoritative value is committed when the transfer finishes.
    /// </summary>
    /// <summary>
    /// Records transfer progress for one entry. Public so the module's direct transport reports progress
    /// through the store instead of writing the shared snapshot from its own thread.
    /// </summary>
    public bool ReportProgress(string itemId, long bytesDone)
    {
        if (!_gate.Wait(TimeSpan.FromMilliseconds(50))) return false;
        try
        {
            var item = _cache?.Find(itemId);
            if (item is null || item.State is not (AssistantItemState.Sending or AssistantItemState.Downloading)
                || item.BytesDone == bytesDone) return false;
            item.BytesDone = bytesDone;
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>The durable copy of an outgoing attachment; null for text entries, which never have a payload.</summary>
    public string? GetPayloadPath(AssistantItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind == AssistantItemKind.Text) return null;
        return Path.Combine(PayloadRoot, AssistantValidation.ItemId(item.Id), TransferFiles.FileName(item.Name ?? ""));
    }

    /// <summary>Where an incoming attachment is committed. Only meaningful for image/file entries.</summary>
    public string GetInboxDirectory(AssistantItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Path.Combine(InboxRoot, AssistantValidation.ItemId(item.Id));
    }

    public string GetInboxPath(AssistantItem item) =>
        Path.Combine(GetInboxDirectory(item), TransferFiles.FileName(item.Name ?? ""));

    /// <summary>Removes only this tool's own <c>.mpt-*.part</c> leftovers, never a user file.</summary>
    public int SweepPartials()
    {
        var removed = 0;
        foreach (var root in new[] { PayloadRoot, InboxRoot })
        {
            if (!System.IO.Directory.Exists(root)) continue;
            try
            {
                foreach (var path in System.IO.Directory.EnumerateFiles(root, "*" + TransferFiles.PartialSuffix, SearchOption.AllDirectories))
                {
                    if (!Path.GetFileName(path).StartsWith(TransferFiles.PartialPrefix, StringComparison.Ordinal)) continue;
                    try { File.Delete(path); removed++; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }

    private async Task<AssistantState> LoadCoreAsync(CancellationToken token)
    {
        if (_cache is not null) return _cache;
        SweepPartials();
        _cache = await ReadAsync(token);
        return _cache;
    }

    /// <summary>
    /// One store transaction: apply <paramref name="change"/> to the live snapshot, commit it to disk and
    /// revert the in-memory snapshot exactly when the commit fails. State is only published after the disk
    /// accepted it, so a failed write can never leave entries that exist in memory but not on disk.
    /// The caller must already hold <see cref="_gate"/>.
    /// </summary>
    private async Task CommitAsync(AssistantState state, Action? change, CancellationToken token)
    {
        var backup = state.Copy();
        try
        {
            change?.Invoke();
            await WriteAsync(state, token);
        }
        catch
        {
            state.RestoreFrom(backup);
            throw;
        }
    }

    private static AssistantItem NewItem(AssistantIdentity identity, string? target, AssistantItemKind kind,
        string? text, string? name, long size) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Text = text,
            Name = name,
            Size = size,
            CreatedAt = DateTimeOffset.UtcNow,
            SenderDeviceId = identity.DeviceId,
            SenderName = identity.Name,
            TargetDeviceId = target,
            ConversationId = target is null ? identity.ConversationId : null,
            Provenance = target is null ? AssistantConversations.LocalShared : AssistantConversations.LocalDevice,
            State = AssistantItemState.Queued
        };

    private async Task<AssistantState> ReadAsync(CancellationToken token)
    {
        if (!File.Exists(_path)) return new AssistantState();
        AssistantState? state;
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            state = await JsonSerializer.DeserializeAsync<AssistantState>(stream, AssistantJson.Options, token);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // Never rewrite or discard the user's conversation; report it so it can be repaired or removed.
            throw new InvalidDataException($"助手会话文件已损坏，未做任何修改：{_path}。请修复或重命名该文件后重新加载工具。", ex);
        }
        state ??= new AssistantState();
        if (state.Items is null) state.Items = [];
        if (state.KnownRemoteIds is null) state.KnownRemoteIds = [];
        if (state.Identity is not null) AssistantValidation.Identity(state.Identity);
        foreach (var item in state.Items) Normalize(item);
        // The draft is recoverable local UI state, so a preference that no longer satisfies its own
        // rules is repaired instead of blocking the whole conversation the way a damaged message does.
        state.Preferences = AssistantPreferenceRules.Normalize(state.Preferences);
        state.Drafts = (state.Drafts ?? []).ToDictionary(pair => pair.Key,
            pair => AssistantPreferenceRules.Normalize(pair.Value) ?? new AssistantPreferences(), StringComparer.Ordinal);
        AssistantConversations.MigrateDrafts(state);
        return state;
    }

    /// <summary>Validates one stored entry and applies restart recovery. Item content is never rewritten.</summary>
    private void Normalize(AssistantItem item)
    {
        try
        {
            AssistantValidation.ItemId(item.Id);
            if (!Enum.IsDefined(item.Kind)) throw new InvalidDataException("助手条目类型无效。");
            AssistantValidation.DeviceId(item.SenderDeviceId);
            if (item.TargetDeviceId is not null) AssistantValidation.DeviceId(item.TargetDeviceId);
            if (item.CreatedAt == default) throw new InvalidDataException("助手条目时间无效。");
            if (item.Kind == AssistantItemKind.Text)
            {
                if (string.IsNullOrEmpty(item.Text)) throw new InvalidDataException("文本条目缺少正文。");
            }
            else if (!string.IsNullOrEmpty(item.Name))
            {
                TransferFiles.FileName(item.Name);
            }
            else
            {
                throw new InvalidDataException("附件条目缺少文件名。");
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            throw new InvalidDataException($"助手会话文件中存在无法识别的条目：{_path}。请修复或重命名该文件后重新加载工具。", ex);
        }
        // Receipts are re-fetchable cache, so a damaged one is dropped instead of breaking the timeline.
        item.Receipts = (item.Receipts ?? []).Where(receipt => receipt is not null && !string.IsNullOrEmpty(receipt.DeviceId)).ToList();
        if (item.State == AssistantItemState.Sending) item.State = AssistantItemState.Queued;
        else if (item.State == AssistantItemState.Downloading) item.State = AssistantItemState.Stored;
    }

    private async Task<AssistantItem> CopyPayloadAsync(AssistantIdentity identity, string? target, string path,
        List<string> directories, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException($"找不到要发送的文件：{path}", path);
        var name = TransferFiles.FileName(Path.GetFileName(path));
        var length = new FileInfo(path).Length;
        if (length > AssistantLimits.MaxPayloadBytes)
            throw new ArgumentException($"文件超过 {AssistantLimits.MaxPayloadBytes >> 30} GiB，无法发送。", nameof(path));
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(PayloadRoot, id);
        System.IO.Directory.CreateDirectory(directory);
        directories.Add(directory);
        var temporary = TransferFiles.PartialPath(directory);
        var final = Path.Combine(directory, name);
        try
        {
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await input.CopyToAsync(output, token);
            // The copy is the durable message body: a source that changed mid-copy must not be published half-way.
            if (new FileInfo(temporary).Length != length)
                throw new InvalidDataException($"文件在复制过程中被修改，请重试：{name}。");
            File.Move(temporary, final);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return new AssistantItem
        {
            Id = id,
            Kind = DetectKind(name),
            Name = name,
            Size = length,
            CreatedAt = DateTimeOffset.UtcNow,
            SenderDeviceId = identity.DeviceId,
            SenderName = identity.Name,
            TargetDeviceId = target,
            ConversationId = target is null ? identity.ConversationId : null,
            Provenance = target is null ? AssistantConversations.LocalShared : AssistantConversations.LocalDevice,
            State = AssistantItemState.Queued,
            LocalPath = final
        };
    }

    private static AssistantItemKind DetectKind(string name) =>
        AssistantLimits.ImageExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            ? AssistantItemKind.Image
            : AssistantItemKind.File;

    private async Task WriteAsync(AssistantState state, CancellationToken token)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temporary = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await JsonSerializer.SerializeAsync(stream, state, AssistantJson.Options, token);
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RemoveDirectory(string directory)
    {
        try { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
