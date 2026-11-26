using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileTransfer.Core;

/// <summary>
/// Which of the two independent pairing-inbox credentials one <see cref="PublicInboxClient"/> holds.
/// The roles are deliberately not interchangeable: a deposit credential can never list, read a
/// payload or write a receipt, and the owner credential can never deliver.
/// </summary>
public enum PublicInboxRole
{
    Owner = 0,
    Deposit = 1
}

/// <summary>Content kind of one inbox item; the wire values are <c>text</c>, <c>image</c> and <c>file</c>.</summary>
public enum PublicInboxItemKind
{
    Text = 0,
    Image = 1,
    File = 2
}

/// <summary>Wire spelling of the bounded <c>X-MPT-*</c> metadata values (PROTOCOL.md §6.3).</summary>
public static class PublicInboxWire
{
    public const string TextKind = "text";
    public const string ImageKind = "image";
    public const string FileKind = "file";

    public static string ToWire(this PublicInboxItemKind kind) => kind switch
    {
        PublicInboxItemKind.Text => TextKind,
        PublicInboxItemKind.Image => ImageKind,
        _ => FileKind
    };

    public static bool TryParseKind(string? value, out PublicInboxItemKind kind)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case TextKind:
                kind = PublicInboxItemKind.Text;
                return true;
            case ImageKind:
                kind = PublicInboxItemKind.Image;
                return true;
            case FileKind:
                kind = PublicInboxItemKind.File;
                return true;
            default:
                kind = PublicInboxItemKind.File;
                return false;
        }
    }
}

/// <summary>
/// Percent-encoded UTF-8 metadata headers. Anything outside <c>A-Za-z0-9-_.~</c> is encoded, which
/// is exactly what the relay's <c>unquote(value, errors="strict")</c> expects, so a Chinese or emoji
/// file name survives an HTTP header that may only carry ASCII.
/// </summary>
public static class PublicInboxHeader
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var octet in Encoding.UTF8.GetBytes(value))
        {
            var character = (char)octet;
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~')
                builder.Append(character);
            else
                builder.Append('%').Append(octet.ToString("X2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>Strict inverse of <see cref="Encode"/>; a malformed value is a protocol violation.</summary>
    public static string Decode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = new List<byte>(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length ||
                    !TryHex(value[index + 1], out var high) ||
                    !TryHex(value[index + 2], out var low))
                    throw new PublicInboxProtocolException("收件箱元信息头不是合法的百分号编码。");
                bytes.Add((byte)((high << 4) | low));
                index += 2;
                continue;
            }
            if (character > 0x7f)
                throw new PublicInboxProtocolException("收件箱元信息头包含未编码的非 ASCII 字符。");
            bytes.Add((byte)character);
        }
        try
        {
            return StrictUtf8.GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException error)
        {
            throw new PublicInboxProtocolException("收件箱元信息头不是合法的 UTF-8。", inner: error);
        }
    }

    private static bool TryHex(char character, out int value)
    {
        value = character switch
        {
            >= '0' and <= '9' => character - '0',
            >= 'a' and <= 'f' => character - 'a' + 10,
            >= 'A' and <= 'F' => character - 'A' + 10,
            _ => -1
        };
        return value >= 0;
    }
}

/// <summary>Generation and shape checks for the three offline inbox values (PROTOCOL.md §6.1).</summary>
public static class PublicInboxIds
{
    /// <summary>Both credentials are independently random 256 bit values written as lower-case hex.</summary>
    public const int KeyHexLength = 64;

    /// <summary>One item id is 128 random bits written as 32 lower-case hex characters.</summary>
    public const int ItemIdHexLength = 32;

    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    public static string NewItemId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>The protocol's suggested shape: <c>inbox-&lt;32hex&gt;</c>.</summary>
    public static string NewInboxId() => "inbox-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool IsKey([NotNullWhen(true)] string? value) =>
        value is { Length: KeyHexLength } && value.All(Uri.IsHexDigit);

    public static bool IsItemId([NotNullWhen(true)] string? value) =>
        value is { Length: ItemIdHexLength } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static bool IsInboxId([NotNullWhen(true)] string? value) =>
        value is { Length: >= 1 and <= 64 } && value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');
}

/// <summary>
/// The receiving device's private inbox identity. <see cref="OwnerKey"/> is sent only to the relay over HTTPS, never in a pairing code:
/// it is the only credential that can list pending items, download payloads, write receipts or delete.
/// The module owns persistence (secret store); the client only speaks the protocol.
/// </summary>
public sealed record PublicInboxIdentity(string InboxId, string OwnerKey, string DepositKey)
{
    /// <summary>Generates all three values offline, with two independent random keys.</summary>
    public static PublicInboxIdentity CreateNew(string? inboxId = null) =>
        new(inboxId ?? PublicInboxIds.NewInboxId(), PublicInboxIds.NewKey(), PublicInboxIds.NewKey());

    /// <summary>What the sending device needs: the inbox id plus the deposit-only credential.</summary>
    public PublicInboxPairing Pairing => new(InboxId, DepositKey);

    public string ToJson() => JsonSerializer.Serialize(
        new Dictionary<string, string> { ["inboxId"] = InboxId, ["ownerKey"] = OwnerKey, ["depositKey"] = DepositKey });

    public static bool TryParseJson(string? json, [NotNullWhen(true)] out PublicInboxIdentity? identity)
    {
        identity = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var inboxId = PublicInboxJson.OptionalString(root, "inboxId");
            var ownerKey = PublicInboxJson.OptionalString(root, "ownerKey");
            var depositKey = PublicInboxJson.OptionalString(root, "depositKey");
            if (!PublicInboxIds.IsInboxId(inboxId) || !PublicInboxIds.IsKey(ownerKey) || !PublicInboxIds.IsKey(depositKey))
                return false;
            identity = new PublicInboxIdentity(inboxId, ownerKey, depositKey);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Keeps both keys out of logs; the deposit key is a capability too.</summary>
    public override string ToString() => $"PublicInboxIdentity {{ InboxId = {InboxId}, OwnerKey = <hidden>, DepositKey = <hidden> }}";
}

/// <summary>
/// A pairing code payload: only <c>inboxId</c> + <c>depositKey</c>. The owner key and the user's own
/// conversation key are structurally absent, so showing or scanning this code can never hand over
/// control of the inbox or of the conversation.
/// </summary>
public sealed record PublicInboxPairing(string InboxId, string DepositKey)
{
    /// <summary>Self-contained textual form; a module may embed the same three fields in its own code.</summary>
    public const string CodeScheme = "mpt://inbox/";

    public string Encode()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, string> { ["inboxId"] = InboxId, ["depositKey"] = DepositKey });
        return CodeScheme + System.Buffers.Text.Base64Url.EncodeToString(payload);
    }

    public static bool TryParse(string? code, [NotNullWhen(true)] out PublicInboxPairing? pairing)
    {
        pairing = null;
        if (string.IsNullOrWhiteSpace(code) || !code.StartsWith(CodeScheme, StringComparison.Ordinal)) return false;
        try
        {
            var bytes = System.Buffers.Text.Base64Url.DecodeFromChars(code.AsSpan(CodeScheme.Length));
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var inboxId = PublicInboxJson.OptionalString(root, "inboxId");
            var depositKey = PublicInboxJson.OptionalString(root, "depositKey");
            if (!PublicInboxIds.IsInboxId(inboxId) || !PublicInboxIds.IsKey(depositKey)) return false;
            pairing = new PublicInboxPairing(inboxId, depositKey.ToLowerInvariant());
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Never prints the deposit key, which is itself a capability for one item's receipt.</summary>
    public override string ToString() => $"PublicInboxPairing {{ InboxId = {InboxId}, DepositKey = <hidden> }}";
}

/// <summary>Answer of <c>POST /v1/inboxes</c>: 201 the first time, 200 for the idempotent repeat.</summary>
public sealed record PublicInboxRegistration(string InboxId, bool Created, long Revision);

/// <summary>
/// One published inbox item. Everything except the payload itself; a text item has no
/// <see cref="Name"/> by protocol.
/// </summary>
public sealed record PublicInboxItem
{
    public required string ItemId { get; init; }
    public PublicInboxItemKind Kind { get; init; }
    public string? Name { get; init; }
    public long Size { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? SenderDeviceId { get; init; }
    public string? SenderName { get; init; }
    public string? TargetDeviceId { get; init; }

    public bool IsText => Kind == PublicInboxItemKind.Text;

    /// <summary>Parses the descriptor shape shared by the list API and <c>item.json</c>.</summary>
    public static PublicInboxItem FromJson(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new PublicInboxProtocolException("收件箱条目不是 JSON 对象。");
        var itemId = PublicInboxJson.RequiredString(element, "itemId");
        if (!PublicInboxIds.IsItemId(itemId))
            throw new PublicInboxProtocolException($"收件箱条目的 itemId 不是 32 位小写十六进制：{itemId}");
        var kindText = PublicInboxJson.OptionalString(element, "kind") ?? PublicInboxWire.FileKind;
        if (!PublicInboxWire.TryParseKind(kindText, out var kind))
            throw new PublicInboxProtocolException($"收件箱条目类型非法：{kindText}");
        var size = PublicInboxJson.OptionalInt64(element, "size") ?? 0;
        if (size < 0)
            throw new PublicInboxProtocolException("收件箱条目长度不能为负数。");
        var name = PublicInboxJson.OptionalString(element, "name");
        if (kind == PublicInboxItemKind.Text)
        {
            if (name is not null) throw new PublicInboxProtocolException("收件箱响应给文本条目带了文件名。");
        }
        else if (!PublicInboxValidation.IsValidFileName(name, out var nameReason))
        {
            // The module turns this name into a local file; a relay that answers with a path-like
            // name must not be able to steer that write.
            throw new PublicInboxProtocolException($"收件箱条目的文件名不可用：{nameReason}");
        }
        var senderName = PublicInboxJson.OptionalString(element, "senderName");
        if (senderName is not null &&
            (senderName.Length is < 1 or > PublicInboxValidation.MaxSenderNameChars || senderName.Any(character => character < 32)))
            throw new PublicInboxProtocolException("收件箱条目的发送设备名超过协议上限或含控制字符。");
        return new PublicInboxItem
        {
            ItemId = itemId,
            Kind = kind,
            Name = name,
            Size = size,
            CreatedAt = PublicInboxJson.OptionalTimestamp(element, "createdAt") ?? default,
            SenderDeviceId = PublicInboxJson.OptionalString(element, "senderDeviceId"),
            SenderName = senderName,
            TargetDeviceId = PublicInboxJson.OptionalString(element, "targetDeviceId")
        };
    }
}

/// <summary>The long-poll answer of <c>GET /v1/inboxes/items</c>: pending items without a receipt.</summary>
public sealed record PublicInboxPage(long Revision, IReadOnlyList<PublicInboxItem> Items, bool HasMore)
{
    /// <summary>The relay's own bound; a larger array is a protocol violation, not something to buffer.</summary>
    public const int MaxItems = 200;

    public static int ClampLimit(int? limit) => limit is null ? 50 : Math.Clamp(limit.Value, 1, MaxItems);

    public static PublicInboxPage Empty(long revision) => new(revision, [], false);

    public static PublicInboxPage FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new PublicInboxProtocolException("收件箱列表响应不是 JSON 对象。");
        var revision = PublicInboxJson.RequiredInt64(root, "revision");
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new PublicInboxProtocolException("收件箱列表响应缺少 items 数组。");
        if (items.GetArrayLength() > MaxItems)
            throw new PublicInboxProtocolException($"收件箱列表返回了超过 {MaxItems} 条条目，拒绝继续解析。");
        var parsed = new List<PublicInboxItem>(items.GetArrayLength());
        foreach (var element in items.EnumerateArray()) parsed.Add(PublicInboxItem.FromJson(element));
        return new PublicInboxPage(revision, parsed, PublicInboxJson.OptionalBool(root, "hasMore") ?? false);
    }
}

/// <summary>Answer of a deposit: 201 for a new item, 200 with <see cref="Duplicate"/> for a retry of the same id.</summary>
public sealed record PublicInboxDepositResult(string ItemId, long Size, long Revision, bool Duplicate, int Attempts = 1);

/// <summary>One payload download: the descriptor from the <c>X-MPT-*</c> headers plus the byte count written.</summary>
public sealed record PublicInboxPayload(PublicInboxItem Item, long Bytes);

/// <summary>
/// A deposit request: the immutable metadata the sender controls. <c>itemId</c> is generated by the
/// sender and must be reused verbatim by a retry, which is what makes the relay's duplicate detection work.
/// </summary>
public sealed record PublicInboxDeposit
{
    public required string ItemId { get; init; }
    public PublicInboxItemKind Kind { get; init; } = PublicInboxItemKind.File;

    /// <summary>Required for file/image, forbidden for text.</summary>
    public string? Name { get; init; }

    /// <summary>Declared length; when null the body is sent chunked and the relay measures it.</summary>
    public long? Length { get; init; }

    public string? SenderDeviceId { get; init; }
    public string? SenderName { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? TargetDeviceId { get; init; }
}

/// <summary>
/// A receipt: written only by the owner (<c>POST</c>) and readable by either credential (<c>GET</c>).
/// <see cref="Saved"/> true means the receiving device really stored the payload, never merely that
/// the relay received it.
/// </summary>
public sealed record PublicInboxReceipt
{
    public required string ItemId { get; init; }
    public bool Saved { get; init; }
    public long Size { get; init; }
    public DateTimeOffset? SavedAt { get; init; }
    public long? Bytes { get; init; }
    public string? DeviceId { get; init; }
    public string? DeviceName { get; init; }
    public bool Duplicate { get; init; }

    /// <summary>
    /// Handles both shapes: <c>GET …/receipt</c> (<c>size</c>, <c>saved</c>) and
    /// <c>POST …/receipt</c> (<c>duplicate</c>, <c>bytes</c>, no <c>size</c>).
    /// </summary>
    public static PublicInboxReceipt FromJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new PublicInboxProtocolException("收件箱回执不是 JSON 对象。");
        var itemId = PublicInboxJson.RequiredString(root, "itemId");
        if (!PublicInboxIds.IsItemId(itemId))
            throw new PublicInboxProtocolException($"回执的 itemId 不是 32 位小写十六进制：{itemId}");
        var bytes = PublicInboxJson.OptionalInt64(root, "bytes");
        if (bytes is < 0)
            throw new PublicInboxProtocolException("回执的 bytes 不能为负数。");
        var size = PublicInboxJson.OptionalInt64(root, "size") ?? bytes ?? 0;
        if (size < 0)
            throw new PublicInboxProtocolException("回执的 size 不能为负数。");
        var deviceName = PublicInboxJson.OptionalString(root, "deviceName");
        if (deviceName is not null &&
            (deviceName.Length is < 1 or > PublicInboxValidation.MaxSenderNameChars || deviceName.Any(character => character < 32)))
            throw new PublicInboxProtocolException("回执的设备名超过协议上限或含控制字符。");
        return new PublicInboxReceipt
        {
            ItemId = itemId,
            Saved = PublicInboxJson.OptionalBool(root, "saved") ?? false,
            Size = size,
            SavedAt = PublicInboxJson.OptionalTimestamp(root, "savedAt"),
            Bytes = bytes,
            DeviceId = PublicInboxJson.OptionalString(root, "deviceId"),
            DeviceName = deviceName,
            Duplicate = PublicInboxJson.OptionalBool(root, "duplicate") ?? false
        };
    }
}

/// <summary>
/// Bounded automatic retry for the one state the protocol calls retryable: <c>503 inbox_not_ready</c>
/// (the receiver has not registered yet) plus the relay's own 429/502/504. A 401/403/413/507 is never
/// retried, and <see cref="MaxDelay"/> caps any honoured <c>Retry-After</c> so a caller is never parked
/// longer than it asked for.
/// </summary>
public sealed record PublicInboxRetryPolicy
{
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Never retry: one attempt, then the typed failure.</summary>
    public static PublicInboxRetryPolicy None { get; } = new() { MaxAttempts = 1 };

    public static PublicInboxRetryPolicy Default { get; } = new();

    internal int EffectiveAttempts => Math.Max(1, MaxAttempts);

    internal TimeSpan Backoff(int attempt)
    {
        var scaled = InitialDelay.Ticks * Math.Pow(2, Math.Max(0, attempt - 1));
        if (double.IsNaN(scaled) || scaled >= MaxDelay.Ticks) return MaxDelay;
        return TimeSpan.FromTicks((long)scaled);
    }

    internal TimeSpan Clamp(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? TimeSpan.Zero : delay >= MaxDelay ? MaxDelay : delay;
}

/// <summary>
/// Base class of every inbox failure. <see cref="UserMessage"/> is the stable, user-facing Chinese
/// text the module may show verbatim; <see cref="Detail"/> keeps the server's own words for logs only.
/// </summary>
public class PublicInboxException : Exception
{
    public PublicInboxException(
        string message,
        string? code = null,
        string? detail = null,
        HttpStatusCode? status = null,
        int attempts = 1,
        Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        Detail = detail;
        Status = status;
        Attempts = attempts;
    }

    /// <summary>The relay's machine-readable error code, e.g. <c>inbox_not_ready</c>.</summary>
    public string? Code { get; }

    /// <summary>The relay's own detail text (Chinese); logs only, never a user-facing string.</summary>
    public string? Detail { get; }

    public HttpStatusCode? Status { get; }

    /// <summary>How many HTTP attempts were spent before this failure was raised.</summary>
    public int Attempts { get; }

    public string UserMessage => Message;

    public override string ToString() =>
        Code is null && Detail is null ? base.ToString() : $"{base.ToString()} (code={Code ?? "-"}, detail={Detail ?? "-"})";
}

/// <summary>The credential was rejected (401). Permanent: re-scan the pairing code, never auto-retry.</summary>
public sealed class PublicInboxAuthException : PublicInboxException
{
    public PublicInboxAuthException(string message, string? code = null, string? detail = null, int attempts = 1)
        : base(message, code, detail, HttpStatusCode.Unauthorized, attempts)
    {
    }
}

/// <summary>This credential may not use this route (403): e.g. a deposit key trying to list.</summary>
public sealed class PublicInboxPermissionException : PublicInboxException
{
    public PublicInboxPermissionException(string message, string? code = null, string? detail = null, int attempts = 1)
        : base(message, code, detail, HttpStatusCode.Forbidden, attempts)
    {
    }
}

/// <summary>The relay rejected the request fields (400); retrying the same bytes cannot help.</summary>
public sealed class PublicInboxRequestException : PublicInboxException
{
    public PublicInboxRequestException(string message, string? code = null, string? detail = null, HttpStatusCode? status = null, int attempts = 1)
        : base(message, code, detail, status ?? HttpStatusCode.BadRequest, attempts)
    {
    }
}

/// <summary>The item id is unknown (404) — deleted, or never delivered into this inbox.</summary>
public sealed class PublicInboxNotFoundException : PublicInboxException
{
    public PublicInboxNotFoundException(string message, string? code = null, string? detail = null, int attempts = 1)
        : base(message, code, detail, HttpStatusCode.NotFound, attempts)
    {
    }
}

/// <summary>A 409: <c>deposit_key_locked</c> (use the old code) or <c>item_conflict</c> (new item id).</summary>
public sealed class PublicInboxConflictException : PublicInboxException
{
    public PublicInboxConflictException(string message, string? code = null, string? detail = null, int attempts = 1)
        : base(message, code, detail, HttpStatusCode.Conflict, attempts)
    {
    }
}

/// <summary>413 single-file limit or 507 namespace/global quota; the message is safe to show as-is.</summary>
public sealed class PublicInboxQuotaException : PublicInboxException
{
    public PublicInboxQuotaException(string message, string? code = null, string? detail = null, HttpStatusCode? status = null, int attempts = 1)
        : base(message, code, detail, status, attempts)
    {
    }

    /// <summary>True for the single-file limit (413), false for the space/entry quota (507).</summary>
    public bool TooLarge => Status == HttpStatusCode.RequestEntityTooLarge;
}

/// <summary>Retryable: 503 <c>inbox_not_ready</c>/busy, 429 rate limiting, or a transport timeout.</summary>
public sealed class PublicInboxUnavailableException : PublicInboxException
{
    public PublicInboxUnavailableException(string message, string? code = null, string? detail = null, HttpStatusCode? status = null, TimeSpan? retryAfter = null, int attempts = 1, Exception? inner = null)
        : base(message, code, detail, status, attempts, inner)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>The server's <c>Retry-After</c> hint, when it sent one.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>
/// The relay answered with something the fixed protocol cannot produce (malformed or oversized JSON,
/// an illegal header, or a redirect). Never retried: a redirect is refused so the Basic credential is
/// not forwarded to a third party.
/// </summary>
public sealed class PublicInboxProtocolException : PublicInboxException
{
    public PublicInboxProtocolException(string message, string? code = null, string? detail = null, int attempts = 1, Exception? inner = null)
        : base(message, code, detail, status: null, attempts, inner)
    {
    }
}

/// <summary>Maps one relay status onto the typed failure above, with the user-facing text per PROTOCOL.md §5.</summary>
internal static class PublicInboxErrors
{
    public static PublicInboxException FromStatus(
        HttpStatusCode status,
        string operation,
        string? code,
        string? detail,
        TimeSpan? retryAfter,
        PublicInboxRole role,
        int attempts)
    {
        var hint = string.IsNullOrWhiteSpace(detail) ? "" : $"（服务器：{detail.Trim()}）";
        var named = string.IsNullOrWhiteSpace(code) ? "" : $"{code}: ";
        return (int)status switch
        {
            400 => new PublicInboxRequestException(
                $"{operation}的参数不被中转服务接受：{named}{hint}", code, detail, status, attempts),
            401 => new PublicInboxAuthException(
                role == PublicInboxRole.Deposit
                    ? "配对码里的投递密钥与服务器不一致，请让收件设备重新出示配对码。"
                    : "收件箱 owner 密钥与服务器不一致，无法继续使用这个收件箱。",
                code, detail, attempts),
            403 => new PublicInboxPermissionException(
                role == PublicInboxRole.Deposit
                    ? "投递密钥只能投递和查询自己那一条回执，不能列出、读取内容或写回执。"
                    : "该操作需要投递密钥，owner 凭据不能用于投递。",
                code, detail, attempts),
            404 => new PublicInboxNotFoundException(
                $"{operation}失败：收件箱里没有这个条目（可能已被删除，或 itemId 不属于本收件箱）。",
                code, detail, attempts),
            409 => new PublicInboxConflictException(
                string.Equals(code, "deposit_key_locked", StringComparison.Ordinal)
                    ? "该收件箱已经用另一个投递密钥注册；旧配对码仍然有效，请让收件设备重新出示配对码。"
                    : "同一条目 id 已经存在且内容不同，请换一个新的 itemId 重试。",
                code, detail, attempts),
            413 => new PublicInboxQuotaException(
                "文件超过中转单文件上限，无法投递。", code, detail, status, attempts),
            429 => new PublicInboxUnavailableException(
                "操作过于频繁（HTTP 429），请稍后重试。", code, detail, status, retryAfter, attempts),
            503 => new PublicInboxUnavailableException(
                string.Equals(code, "inbox_not_ready", StringComparison.Ordinal)
                    ? "收件端还没有注册这个收件箱（HTTP 503），稍后会自动重试。"
                    : "中转服务暂时不可用（HTTP 503），请稍后重试。",
                code, detail, status, retryAfter, attempts),
            507 => new PublicInboxQuotaException(
                "中转空间不足，请先在收件设备上清理已接收的文件。", code, detail, status, attempts),
            _ => new PublicInboxException(
                $"{operation}失败：中转服务返回 HTTP {(int)status}。{hint}", code, detail, status, attempts)
        };
    }
}

/// <summary>Local mirrors of the relay's field rules, so an obviously invalid call fails before the network.</summary>
internal static class PublicInboxValidation
{
    public const int MaxNameChars = 180;
    public const int MaxSenderNameChars = 100;

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsValidFileName(string? name, [NotNullWhen(false)] out string? reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(name) || name.Length > MaxNameChars || name is "." or "..")
        {
            reason = $"文件名必须是 1–{MaxNameChars} 个字符。";
            return false;
        }
        if (name.Any(character => character < 32 || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'))
        {
            reason = "文件名含不支持的字符。";
            return false;
        }
        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            reason = "文件名不能以点或空格结尾。";
            return false;
        }
        if (WindowsReserved.Contains(name.Split('.')[0]))
        {
            reason = "文件名是系统保留名称。";
            return false;
        }
        return true;
    }

    public static void EnsureFileName(string? name)
    {
        if (!IsValidFileName(name, out var reason)) throw new PublicInboxRequestException(reason);
    }

    public static void EnsureSenderName(string? name)
    {
        if (name is null) return;
        if (name.Length is < 1 or > MaxSenderNameChars || name.Any(character => character < 32))
            throw new PublicInboxRequestException($"发送设备名必须是 1–{MaxSenderNameChars} 个字符且不含控制字符。");
    }

    public static void EnsureDeviceId(string? deviceId, string field)
    {
        if (deviceId is null) return;
        if (!PublicInboxIds.IsInboxId(deviceId))
            throw new PublicInboxRequestException($"{field} 必须是 1–64 位英文字母、数字、短横线或下划线。");
    }
}

/// <summary>Strict, bounded readers for the small JSON documents the relay returns.</summary>
internal static class PublicInboxJson
{
    public static string RequiredString(JsonElement element, string name)
    {
        var value = OptionalString(element, name);
        if (value is null) throw new PublicInboxProtocolException($"收件箱响应缺少 {name}。");
        return value;
    }

    public static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String)
            throw new PublicInboxProtocolException($"收件箱响应的 {name} 不是字符串。");
        return property.GetString();
    }

    public static long RequiredInt64(JsonElement element, string name) =>
        OptionalInt64(element, name) ?? throw new PublicInboxProtocolException($"收件箱响应缺少 {name}。");

    public static long? OptionalInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var value))
            throw new PublicInboxProtocolException($"收件箱响应的 {name} 不是整数。");
        return value;
    }

    public static bool? OptionalBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new PublicInboxProtocolException($"收件箱响应的 {name} 不是布尔值。")
        };
    }

    public static DateTimeOffset? OptionalTimestamp(JsonElement element, string name)
    {
        var text = OptionalString(element, name);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
            throw new PublicInboxProtocolException($"收件箱响应的 {name} 不是 ISO-8601 时间。");
        return value;
    }
}
