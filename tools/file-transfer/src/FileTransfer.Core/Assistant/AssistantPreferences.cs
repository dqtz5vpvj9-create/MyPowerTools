namespace FileTransfer.Core.Assistant;

/// <summary>
/// The composer's durable draft: the unfinished text, the attachment references the user prepared and
/// the chosen recipient. It is a local preference, never a conversation entry: it is not published,
/// produces no receipt, creates no item and is not part of the message timeline. One snapshot is
/// bounded, so a UI that keeps appending cannot grow the state file without limit.
/// </summary>
public sealed record AssistantPreferences
{
    /// <summary>The unfinished text; null means the user cleared the field or never typed anything.</summary>
    public string? DraftText { get; set; }

    /// <summary>Absolute local paths of attachments prepared but not sent yet.</summary>
    public List<string> AttachmentPaths { get; set; } = [];

    /// <summary>The stable device id of the chosen recipient; null means "send to myself".</summary>
    public string? TargetDeviceId { get; set; }

    /// <summary>When this snapshot last changed; null for a draft that was never saved.</summary>
    public DateTimeOffset? SavedAt { get; set; }

    /// <summary>A detached copy used as the rollback point of one store transaction.</summary>
    public AssistantPreferences Copy() => this with { AttachmentPaths = [.. AttachmentPaths] };
}

/// <summary>
/// Shape and bound rules for the local draft. Unlike a message, an empty draft is a real state (the
/// user cleared the field), so empty input clears instead of failing. A path that no longer exists is
/// not a validation error either: the store keeps only usable references and reports the rest, because
/// a temporarily unavailable file must not destroy the draft.
/// </summary>
public static class AssistantPreferenceRules
{
    /// <summary>Same bound as one batch send, so a draft can always be sent as one action.</summary>
    public const int MaxAttachmentPaths = 1000;

    /// <summary>
    /// Attachment references are file paths: Windows volumes treat two spellings of one path as the
    /// same file, while Linux and Android keep two files that differ only by case apart. Deduplication
    /// must follow the volume, not one platform's rule.
    /// </summary>
    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Empty clears; otherwise the same bound and character rules as a message body.</summary>
    public static string? Text(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > AssistantLimits.MaxTextCharacters)
            throw new ArgumentException($"草稿不能超过 {AssistantLimits.MaxTextCharacters} 个字符。", nameof(value));
        if (value.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
            throw new ArgumentException("草稿包含不支持的控制字符。", nameof(value));
        return value;
    }

    /// <summary>Empty means "self"; otherwise the stable device id must be well formed.</summary>
    public static string? Target(string? value) => string.IsNullOrEmpty(value) ? null : TransferFiles.DeviceId(value);

    /// <summary>Shape check for one snapshot: absolute, deduplicated and bounded; order is preserved.</summary>
    public static List<string> Attachments(IEnumerable<string>? paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("附件路径必须是绝对路径。", nameof(paths));
            if (seen.Add(path)) result.Add(path);
        }
        if (result.Count > MaxAttachmentPaths)
            throw new ArgumentException($"待发送附件最多 {MaxAttachmentPaths} 个。", nameof(paths));
        return result;
    }

    /// <summary>
    /// Load-time repair: a preference that no longer satisfies its own rules is dropped or clamped
    /// instead of breaking the conversation. Item content keeps its strict validation; this snapshot is
    /// recoverable UI state, not user content.
    /// </summary>
    public static AssistantPreferences? Normalize(AssistantPreferences? preferences)
    {
        if (preferences is null) return null;
        var result = new AssistantPreferences { SavedAt = preferences.SavedAt };
        try { result.DraftText = Text(preferences.DraftText); }
        catch (ArgumentException) { }
        try { result.TargetDeviceId = Target(preferences.TargetDeviceId); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { }
        var kept = new List<string>();
        foreach (var path in preferences.AttachmentPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) continue;
            if (kept.Count >= MaxAttachmentPaths) break;
            if (!kept.Contains(path, PathComparer)) kept.Add(path);
        }
        result.AttachmentPaths = kept;
        return result;
    }
}
