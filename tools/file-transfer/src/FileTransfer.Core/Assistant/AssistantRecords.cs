using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileTransfer.Core.Assistant;

/// <summary>Kind of a conversation entry. Text entries carry their content inline and never need a payload.</summary>
[JsonConverter(typeof(AssistantEnumConverter<AssistantItemKind>))]
public enum AssistantItemKind
{
    Text,
    Image,
    File
}

/// <summary>
/// Delivery state of one entry. <c>Stored</c> only means the relay saved it; <c>Delivered</c> requires a
/// receipt written by the target device after it actually saved the content.
/// </summary>
[JsonConverter(typeof(AssistantEnumConverter<AssistantItemState>))]
public enum AssistantItemState
{
    Queued,
    Sending,
    Stored,
    Delivered,
    Downloading,
    Available,
    Failed,
    Cancelled
}

/// <summary>The local device identity. <c>ConversationId</c> is the shared cross-device conversation (M4 owns it).</summary>
public sealed record AssistantIdentity(string DeviceId, string Name, string ConversationId);

/// <summary>
/// One immutable message plus its local delivery metadata. Content fields are written once at enqueue;
/// only the delivery fields change afterwards. The published manifest never contains local metadata.
/// </summary>
public sealed record AssistantItem
{
    public string Id { get; init; } = "";
    public AssistantItemKind Kind { get; init; }
    public string? Text { get; init; }
    public string? Name { get; init; }
    public long Size { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string SenderDeviceId { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string? TargetDeviceId { get; init; }

    public AssistantItemState State { get; set; } = AssistantItemState.Queued;
    public long BytesDone { get; set; }
    public string? LocalPath { get; set; }
    public string? Error { get; set; }
    public List<AssistantReceipt> Receipts { get; set; } = [];
    public int Attempts { get; set; }
    public DateTimeOffset? ReceiptAt { get; set; }
    /// <summary>When this device last looked for receipts of this entry; drives the bounded fair rotation.</summary>
    public DateTimeOffset? ReceiptCheckedAt { get; set; }

    /// <summary>A detached copy used for rollback; the receipt list is copied so mutations cannot leak.</summary>
    public AssistantItem Copy() => this with { Receipts = [.. Receipts] };

    /// <summary>
    /// Reverts the delivery metadata in place from a snapshot. The identity fields of an entry are written
    /// once and never change, so restoring the mutable ones is enough and keeps external references to this
    /// instance valid after a failed store transaction.
    /// </summary>
    public void RestoreFrom(AssistantItem backup)
    {
        State = backup.State;
        BytesDone = backup.BytesDone;
        LocalPath = backup.LocalPath;
        Error = backup.Error;
        Receipts = [.. backup.Receipts];
        Attempts = backup.Attempts;
        ReceiptAt = backup.ReceiptAt;
        ReceiptCheckedAt = backup.ReceiptCheckedAt;
    }

    /// <summary>Cancel is refused once another device confirmed it saved the content.</summary>
    [JsonIgnore]
    public bool CanCancel => State is AssistantItemState.Queued or AssistantItemState.Sending
        or AssistantItemState.Stored or AssistantItemState.Failed or AssistantItemState.Downloading
        && Receipts is not { Count: > 0 };

    /// <summary>The immutable projection that is published to the relay. No paths, no state, no secrets.</summary>
    public AssistantManifest ToManifest() => new()
    {
        Version = 1,
        Id = Id,
        Kind = Kind,
        Text = Text,
        Name = Name,
        Size = Size,
        CreatedAt = CreatedAt,
        SenderDeviceId = SenderDeviceId,
        SenderName = SenderName,
        TargetDeviceId = TargetDeviceId
    };
}

/// <summary>
/// A device's confirmation that it saved this entry locally. It never means the entry was read:
/// there is deliberately no read flag anywhere in the protocol.
/// </summary>
public sealed record AssistantReceipt
{
    public string ItemId { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public DateTimeOffset SavedAt { get; init; }
    public long Bytes { get; init; }
}

/// <summary>What is published under <c>assistant/&lt;conversationId&gt;/&lt;itemId&gt;/manifest.json</c>.</summary>
public sealed record AssistantManifest
{
    public int Version { get; init; } = 1;
    public string Id { get; init; } = "";
    public AssistantItemKind Kind { get; init; }
    public string? Text { get; init; }
    public string? Name { get; init; }
    public long Size { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string SenderDeviceId { get; init; } = "";
    public string SenderName { get; init; } = "";
    public string? TargetDeviceId { get; init; }
}

/// <summary>Result of <c>assistant.open</c>. Text is returned inline; files download first when needed.</summary>
public sealed record AssistantOpenTarget(string? Path, string? Text, bool NeedsDownload);

/// <summary>One send request: either text or one-or-more local paths, never both, never neither.</summary>
public sealed record AssistantDraft
{
    public string? Text { get; init; }
    public IReadOnlyList<string> Paths { get; init; } = [];
    public string? TargetDeviceId { get; init; }

    public static AssistantDraft ForText(string text, string? targetDeviceId = null) =>
        new() { Text = text, TargetDeviceId = targetDeviceId };

    public static AssistantDraft ForPaths(IEnumerable<string> paths, string? targetDeviceId = null) =>
        new() { Paths = [.. paths], TargetDeviceId = targetDeviceId };

    /// <summary>
    /// One compose action: text, attachments, or both. Both are legal together, because a user writing a
    /// caption and attaching files is one send, not two.
    /// </summary>
    public static AssistantDraft ForContent(string? text, IEnumerable<string>? paths = null, string? targetDeviceId = null) =>
        new() { Text = text, Paths = paths is null ? [] : [.. paths], TargetDeviceId = targetDeviceId };
}

/// <summary>Fixed protocol limits, shared by the store, the client and the sync loop.</summary>
public static class AssistantLimits
{
    public const int MaxTextCharacters = 8192;
    public const int MaxSenderNameLength = 100;
    public const long MaxPayloadBytes = 16L << 30;
    public const int MaxReceiptsPerItem = 64;
    public static readonly string[] ImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic", ".heif", ".avif", ".tif", ".tiff"];
}

/// <summary>Serializer settings that reproduce the contract's camelCase JSON with lowercase enum values.</summary>
public static class AssistantJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>Read-only helpers over stored entries; the Surface uses them for opening and badges.</summary>
public static class AssistantContent
{
    public static bool HasLocalContent(AssistantItem item) => item.Kind == AssistantItemKind.Text
        ? item.Text is not null
        : item.LocalPath is { Length: > 0 } path && File.Exists(path);

    public static AssistantOpenTarget OpenTarget(AssistantItem item)
    {
        if (item.Kind == AssistantItemKind.Text)
            return item.Text is null ? new(null, null, true) : new(null, item.Text, false);
        return HasLocalContent(item) ? new(item.LocalPath, null, false) : new(null, null, true);
    }
}

/// <summary>
/// The local conversation snapshot. Items are never trimmed: the old 50-record transfer history cap must
/// not drop queued assistant work. <see cref="KnownRemoteIds"/> is bounded bookkeeping only.
/// The two lists are replaced as a whole (copy on write) instead of being mutated in place, so a reader
/// or enumerator never observes a list that another thread is appending to.
/// </summary>
public sealed record AssistantState
{
    public int Version { get; set; } = 1;
    public AssistantIdentity? Identity { get; set; }
    public List<AssistantItem> Items { get; set; } = [];
    public List<string> KnownRemoteIds { get; set; } = [];

    public AssistantItem? Find(string itemId) => Items.FirstOrDefault(item => item.Id == itemId);

    public IEnumerable<AssistantItem> Outgoing(string deviceId) =>
        Items.Where(item => string.Equals(item.SenderDeviceId, deviceId, StringComparison.Ordinal));

    public IEnumerable<AssistantItem> Incoming(string deviceId) =>
        Items.Where(item => !string.Equals(item.SenderDeviceId, deviceId, StringComparison.Ordinal));

    /// <summary>Appends without mutating the previous list instance.</summary>
    public AssistantItem Add(AssistantItem item)
    {
        Items = [.. Items, item];
        return item;
    }

    /// <summary>
    /// Remembers a discovered remote id. The newest call stays in front, so trimming drops the oldest
    /// bookkeeping first; feed the relay page oldest-first.
    /// </summary>
    public void Remember(string itemId) => RememberAll([itemId]);

    /// <summary>Same as <see cref="Remember"/> for a whole page, replacing the list once.</summary>
    public void RememberAll(IEnumerable<string> oldestFirst)
    {
        var ordered = new List<string>();
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in oldestFirst) if (fresh.Add(id)) ordered.Add(id);
        if (ordered.Count == 0) return;
        ordered.Reverse();   // newest first
        ordered.AddRange(KnownRemoteIds.Where(id => !fresh.Contains(id)));
        if (ordered.Count > AssistantStore.MaxKnownRemoteIds)
            ordered.RemoveRange(AssistantStore.MaxKnownRemoteIds, ordered.Count - AssistantStore.MaxKnownRemoteIds);
        KnownRemoteIds = ordered;
    }

    /// <summary>Cancelling is refused for anything already confirmed, and never deletes user content.</summary>
    public bool TryCancel(string itemId)
    {
        var item = Find(itemId);
        if (item is null || !item.CanCancel) return false;
        item.State = AssistantItemState.Cancelled;
        item.Error = null;
        return true;
    }

    /// <summary>Detached snapshot used as the rollback point of one store transaction.</summary>
    public AssistantState Copy() => new()
    {
        Version = Version,
        Identity = Identity,
        Items = [.. Items.Select(item => item.Copy())],
        KnownRemoteIds = [.. KnownRemoteIds]
    };

    /// <summary>
    /// Reverts this instance in place to a snapshot. Entries that still exist are restored on their original
    /// instances (so holders of an item keep a live view), entries added by the failed transaction disappear,
    /// and the list references are swapped rather than edited so concurrent enumerators stay valid.
    /// </summary>
    public void RestoreFrom(AssistantState backup)
    {
        Version = backup.Version;
        Identity = backup.Identity;
        var live = new Dictionary<string, AssistantItem>(StringComparer.Ordinal);
        foreach (var item in Items) live[item.Id] = item;
        var restored = new List<AssistantItem>(backup.Items.Count);
        foreach (var saved in backup.Items)
        {
            if (live.TryGetValue(saved.Id, out var current)) { current.RestoreFrom(saved); restored.Add(current); }
            else restored.Add(saved);
        }
        Items = restored;
        KnownRemoteIds = backup.KnownRemoteIds;
    }
}

/// <summary>Protocol validation. Everything arriving from the relay is untrusted input.</summary>
internal static class AssistantValidation
{
    public static string ConversationId(string value) => TransferFiles.DeviceId(value);

    public static string ItemId(string value) =>
        Guid.TryParseExact(value, "N", out _) ? value : throw new InvalidDataException("助手条目 id 无效。");

    public static string DeviceId(string value) => TransferFiles.DeviceId(value);

    public static void Identity(AssistantIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        DeviceId(identity.DeviceId);
        ConversationId(identity.ConversationId);
        Name(identity.Name);
    }

    public static string Name(string? value)
    {
        var name = value ?? "";
        if (name.Length is < 1 or > AssistantLimits.MaxSenderNameLength || name.Any(char.IsControl))
            throw new InvalidDataException("助手设备名称无效。");
        return name;
    }

    public static string Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("要发送的文字不能为空。", nameof(value));
        if (value.Length > AssistantLimits.MaxTextCharacters)
            throw new ArgumentException($"文字不能超过 {AssistantLimits.MaxTextCharacters} 个字符。", nameof(value));
        if (value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new ArgumentException("文字包含不支持的控制字符。", nameof(value));
        return value;
    }

    /// <summary>A manifest written by another device; every field is checked before it is stored.</summary>
    public static AssistantManifest Manifest(AssistantManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        // Field rules reuse the shared file/device validation, whose ArgumentException is a protocol
        // violation here: a bad peer record must never escape as an unexpected exception type.
        try { return ManifestCore(manifest); }
        catch (ArgumentException ex) { throw new InvalidDataException($"助手条目记录无效：{ex.Message}", ex); }
    }

    private static AssistantManifest ManifestCore(AssistantManifest manifest)
    {
        ItemId(manifest.Id);
        if (manifest.Version != 1) throw new InvalidDataException($"不支持的助手条目版本：{manifest.Version}。");
        if (!Enum.IsDefined(manifest.Kind)) throw new InvalidDataException("助手条目类型无效。");
        DeviceId(manifest.SenderDeviceId);
        Name(manifest.SenderName);
        if (manifest.TargetDeviceId is not null) DeviceId(manifest.TargetDeviceId);
        if (manifest.Size < 0 || manifest.Size > AssistantLimits.MaxPayloadBytes)
            throw new InvalidDataException("助手条目长度无效。");
        if (manifest.CreatedAt == default) throw new InvalidDataException("助手条目时间无效。");
        if (manifest.Kind == AssistantItemKind.Text)
        {
            if (manifest.Name is not null) throw new InvalidDataException("文本条目不应带文件名。");
            if (manifest.Text is null || manifest.Text.Length is 0 or > AssistantLimits.MaxTextCharacters)
                throw new InvalidDataException("助手文本条目内容无效。");
            if (manifest.Text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
                throw new InvalidDataException("助手文本条目包含控制字符。");
            if (manifest.Size != Encoding.UTF8.GetByteCount(manifest.Text))
                throw new InvalidDataException("助手文本条目长度与内容不符。");
        }
        else
        {
            if (manifest.Text is not null) throw new InvalidDataException("非文本条目不应带正文。");
            manifest = manifest with { Name = TransferFiles.FileName(manifest.Name ?? "") };
        }
        return manifest;
    }

    public static AssistantReceipt Receipt(AssistantReceipt receipt, string? expectedItemId = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        try { return ReceiptCore(receipt, expectedItemId); }
        catch (ArgumentException ex) { throw new InvalidDataException($"助手回执记录无效：{ex.Message}", ex); }
    }

    private static AssistantReceipt ReceiptCore(AssistantReceipt receipt, string? expectedItemId)
    {
        ItemId(receipt.ItemId);
        if (expectedItemId is not null && !string.Equals(receipt.ItemId, expectedItemId, StringComparison.Ordinal))
            throw new InvalidDataException("助手回执与条目不一致。");
        DeviceId(receipt.DeviceId);
        Name(receipt.DeviceName);
        if (receipt.SavedAt == default) throw new InvalidDataException("助手回执时间无效。");
        if (receipt.Bytes < 0 || receipt.Bytes > AssistantLimits.MaxPayloadBytes)
            throw new InvalidDataException("助手回执长度无效。");
        return receipt;
    }

    /// <summary>Two manifests with the same id must describe exactly the same immutable message.</summary>
    public static bool SameMessage(AssistantManifest left, AssistantManifest right) =>
        left.Id == right.Id && left.Version == right.Version && left.Kind == right.Kind &&
        left.Text == right.Text && left.Name == right.Name && left.Size == right.Size &&
        left.SenderDeviceId == right.SenderDeviceId && left.SenderName == right.SenderName &&
        left.TargetDeviceId == right.TargetDeviceId && left.CreatedAt == right.CreatedAt;
}

/// <summary>Serializes the state enums as the lowercase strings the contract specifies, whatever options are used.</summary>
internal sealed class AssistantEnumConverter<TEnum> : JsonConverterFactory where TEnum : struct, Enum
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(TEnum);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) => new Converter();

    private sealed class Converter : JsonConverter<TEnum>
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number) && Enum.IsDefined((TEnum)(object)number))
                return (TEnum)(object)number;
            var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (text is not null && Enum.TryParse<TEnum>(text, true, out var value) && Enum.IsDefined(value)) return value;
            throw new JsonException($"无法识别的助手字段值：{text ?? reader.TokenType.ToString()}。");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
            writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}
