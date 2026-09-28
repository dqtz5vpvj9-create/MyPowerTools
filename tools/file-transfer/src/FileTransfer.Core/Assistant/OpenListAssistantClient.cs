using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace FileTransfer.Core.Assistant;

/// <summary>Bounded, incremental listing request. Known ids are never fetched again.</summary>
public sealed record AssistantListRequest(
    int Limit = AssistantListRequest.DefaultLimit,
    IReadOnlyCollection<string>? KnownItemIds = null,
    string? Cursor = null)
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
}

/// <summary>
/// One page of the relay conversation. <see cref="InvalidItemIds"/> are manifests that parsed as JSON but
/// violate the protocol; they are reported instead of silently poisoning the whole timeline.
/// </summary>
public sealed record AssistantPage(
    IReadOnlyList<AssistantManifest> Items,
    IReadOnlyList<string> InvalidItemIds,
    bool HasMore,
    int DiscoveredCount,
    string? NextCursor)
{
    public int InvalidCount => InvalidItemIds.Count;
}

/// <summary>
/// Assistant relay storage on the existing OpenList client. Layout is fixed:
/// <c>assistant/&lt;conversationId&gt;/&lt;itemId&gt;/{payload,manifest.json,receipts/&lt;deviceId&gt;.json}</c>.
/// Credentials and cross-site redirect rules are inherited from the root client, never re-implemented.
/// </summary>
public sealed partial class OpenListClient
{
    private const string AssistantRoot = "assistant";
    private const string AssistantManifestName = "manifest.json";
    private const string AssistantPayloadName = "payload";
    private const string AssistantReceiptsName = "receipts";

    /// <summary>
    /// Uploads the payload first and publishes the immutable manifest only after the storage driver accepted
    /// the whole payload. An interrupted publish therefore leaves no manifest and other devices never see it.
    /// Re-publishing the same id returns the stored manifest instead of writing a second copy.
    /// </summary>
    public async Task<AssistantManifest> PublishAssistantAsync(string conversationId, AssistantManifest manifest,
        string? payloadPath, Action<long, long>? progress, CancellationToken token)
    {
        AssistantValidation.ConversationId(conversationId);
        manifest = AssistantValidation.Manifest(manifest);
        var text = manifest.Kind == AssistantItemKind.Text;
        if (!text && payloadPath is null) throw new ArgumentException("附件条目发布时必须提供待发副本。", nameof(payloadPath));
        if (text && payloadPath is not null) throw new ArgumentException("文本条目不需要 payload。", nameof(payloadPath));
        if (!text && !File.Exists(payloadPath)) throw new FileNotFoundException($"找不到待发副本：{payloadPath}", payloadPath);

        await AssistantEnsureConversationAsync(conversationId, token);
        await AssistantMkcolAsync(AssistantCollectionUrl(AssistantRoot, conversationId, manifest.Id), token);

        // An existing manifest is an immutable message. A truncated or foreign record is repaired instead.
        AssistantManifest? existing = null;
        try { existing = await AssistantReadManifestAsync(conversationId, manifest.Id, token); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or NotSupportedException) { }
        if (existing is not null)
        {
            if (!AssistantValidation.SameMessage(existing, manifest))
                throw new InvalidDataException($"中转上已存在同 id 但内容不同的条目：{manifest.Id}。");
            return existing;
        }

        if (!text)
        {
            await using var input = new FileStream(payloadPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            if (input.Length != manifest.Size) throw new InvalidDataException("待发副本长度与条目记录不符。");
            using var uploaded = await RequestAsync(HttpMethod.Put,
                AssistantFileUrl(AssistantRoot, conversationId, manifest.Id, AssistantPayloadName),
                new UploadContent(input, manifest.Size, progress), token);
            Check(uploaded);
        }

        using var published = await RequestAsync(HttpMethod.Put,
            AssistantFileUrl(AssistantRoot, conversationId, manifest.Id, AssistantManifestName),
            new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(manifest, AssistantJson.Options)), token);
        Check(published);
        return manifest;
    }

    /// <summary>
    /// Incremental listing: one Depth-1 PROPFIND, then at most <c>Limit</c> manifest GETs for ids that are
    /// not remembered yet. A repeated sync with nothing new costs one PROPFIND and no download at all.
    /// </summary>
    public async Task<AssistantPage> ListAssistantAsync(string conversationId, AssistantListRequest request, CancellationToken token)
    {
        AssistantValidation.ConversationId(conversationId);
        ArgumentNullException.ThrowIfNull(request);
        var limit = Math.Clamp(request.Limit, 1, AssistantListRequest.MaxLimit);
        var known = new HashSet<string>(request.KnownItemIds ?? [], StringComparer.Ordinal);
        var collection = AssistantCollectionUrl(AssistantRoot, conversationId);

        using var response = await RequestAsync(new HttpMethod("PROPFIND"), collection, null, token, "1");
        // A missing collection is an empty conversation; some drivers answer 409 for a missing parent.
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return new([], [], false, 0, null);
        Check(response);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(token));
        XNamespace dav = "DAV:";
        var self = collection.AbsolutePath.TrimEnd('/');
        var candidates = new List<(string Id, DateTimeOffset Modified)>();
        foreach (var row in xml.Descendants(dav + "response"))
        {
            var href = row.Element(dav + "href")?.Value;
            if (href is null) continue;
            var path = new Uri(_root, href).AbsolutePath.TrimEnd('/');
            if (string.Equals(path, self, StringComparison.Ordinal)) continue;
            var id = Uri.UnescapeDataString(path.Split('/').Last());
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            candidates.Add((id, LastModified(row, dav)));
        }
        // Directories report their modification time; when a driver omits it the order is stable but arbitrary,
        // and progress is still guaranteed because every attempted id is remembered by the caller.
        candidates.Sort((left, right) =>
        {
            var byTime = right.Modified.CompareTo(left.Modified);
            return byTime != 0 ? byTime : string.CompareOrdinal(right.Id, left.Id);
        });

        var start = 0;
        if (!string.IsNullOrEmpty(request.Cursor))
        {
            var index = candidates.FindIndex(candidate => string.Equals(candidate.Id, request.Cursor, StringComparison.Ordinal));
            if (index >= 0) start = index + 1;
        }

        var items = new List<AssistantManifest>();
        var invalid = new List<string>();
        string? last = null;
        var scanned = 0;
        var hasMore = false;
        for (var i = start; i < candidates.Count; i++)
        {
            var id = candidates[i].Id;
            if (known.Contains(id)) continue;
            if (scanned >= limit) { hasMore = true; break; }
            scanned++;
            last = id;
            AssistantManifest? manifest;
            try { manifest = await AssistantReadManifestAsync(conversationId, id, token); }
            catch (InvalidDataException) { invalid.Add(id); continue; }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { continue; }
            // No manifest yet means the publisher was interrupted before the payload completed: not an entry.
            if (manifest is not null) items.Add(manifest);
        }
        return new(items, invalid, hasMore, candidates.Count, hasMore ? last : null);
    }

    /// <summary>Atomically saves an incoming attachment under <paramref name="directory"/>; text entries have no payload.</summary>
    public async Task<string> DownloadAssistantAsync(string conversationId, AssistantManifest manifest, string directory,
        Action<long, long>? progress, CancellationToken token)
    {
        AssistantValidation.ConversationId(conversationId);
        manifest = AssistantValidation.Manifest(manifest);
        if (manifest.Kind == AssistantItemKind.Text) throw new ArgumentException("文本条目不需要下载。", nameof(manifest));
        Directory.CreateDirectory(directory);
        var temporary = TransferFiles.PartialPath(directory);
        try
        {
            using var initial = await RequestAsync(HttpMethod.Get,
                AssistantFileUrl(AssistantRoot, conversationId, manifest.Id, AssistantPayloadName), null, token);
            using var redirected = await FollowDownloadAsync(initial, token);
            var response = redirected ?? initial;
            Check(response);
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
            {
                await TransferFiles.CopyAsync(input, output, manifest.Size, progress, token);
                if (await input.ReadAsync(new byte[1], token) != 0) throw new InvalidDataException("网盘文件长度与助手条目记录不符。");
            }
            return TransferFiles.Commit(temporary, directory, manifest.Name!);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// Writes this device's own receipt file. Receipts are per device, so two devices acknowledging the same
    /// entry never overwrite each other, and there is no global index to lose.
    /// </summary>
    public async Task<AssistantReceipt> WriteAssistantReceiptAsync(string conversationId, AssistantReceipt receipt, CancellationToken token)
    {
        AssistantValidation.ConversationId(conversationId);
        AssistantValidation.Receipt(receipt);
        await AssistantEnsureConversationAsync(conversationId, token);
        await AssistantMkcolAsync(AssistantCollectionUrl(AssistantRoot, conversationId, receipt.ItemId), token);
        await AssistantMkcolAsync(AssistantCollectionUrl(AssistantRoot, conversationId, receipt.ItemId, AssistantReceiptsName), token);
        using var response = await RequestAsync(HttpMethod.Put,
            AssistantFileUrl(AssistantRoot, conversationId, receipt.ItemId, AssistantReceiptsName, receipt.DeviceId + ".json"),
            new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(receipt, AssistantJson.Options)), token);
        Check(response);
        return receipt;
    }

    /// <summary>Reads every device receipt of one entry. Bounded: at most <see cref="AssistantLimits.MaxReceiptsPerItem"/> files.</summary>
    public async Task<IReadOnlyList<AssistantReceipt>> ListAssistantReceiptsAsync(string conversationId, string itemId, CancellationToken token)
    {
        AssistantValidation.ConversationId(conversationId);
        AssistantValidation.ItemId(itemId);
        using var response = await RequestAsync(new HttpMethod("PROPFIND"),
            AssistantCollectionUrl(AssistantRoot, conversationId, itemId, AssistantReceiptsName), null, token, "1");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict) return [];
        Check(response);
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(token));
        XNamespace dav = "DAV:";
        var names = new List<string>();
        foreach (var row in xml.Descendants(dav + "response"))
        {
            var href = row.Element(dav + "href")?.Value;
            if (href is null) continue;
            var name = Uri.UnescapeDataString(new Uri(_root, href).AbsolutePath.TrimEnd('/').Split('/').Last());
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            var device = name[..^".json".Length];
            if (device.Length is < 1 or > 64 || device.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) continue;
            names.Add(name);
        }
        names.Sort(StringComparer.Ordinal);

        var receipts = new Dictionary<string, AssistantReceipt>(StringComparer.Ordinal);
        foreach (var name in names.Take(AssistantLimits.MaxReceiptsPerItem))
        {
            using var initial = await RequestAsync(HttpMethod.Get,
                AssistantFileUrl(AssistantRoot, conversationId, itemId, AssistantReceiptsName, name), null, token);
            if (initial.StatusCode == HttpStatusCode.NotFound) continue;
            using var redirected = await FollowDownloadAsync(initial, token);
            var file = redirected ?? initial;
            Check(file);
            AssistantReceipt? receipt;
            try { receipt = await file.Content.ReadFromJsonAsync<AssistantReceipt>(AssistantJson.Options, token); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException) { continue; }
            if (receipt is null) continue;
            try { AssistantValidation.Receipt(receipt, itemId); }
            catch (InvalidDataException) { continue; }
            // A device's newest receipt wins; an older duplicate is ignored rather than counted twice.
            if (!receipts.TryGetValue(receipt.DeviceId, out var previous) || receipt.SavedAt > previous.SavedAt)
                receipts[receipt.DeviceId] = receipt;
        }
        return [.. receipts.Values.OrderBy(receipt => receipt.SavedAt)];
    }

    private Uri AssistantCollectionUrl(params string[] segments) =>
        new(_root, string.Join('/', segments.Select(Uri.EscapeDataString)) + "/");

    private Uri AssistantFileUrl(params string[] segments) =>
        new(_root, string.Join('/', segments.Select(Uri.EscapeDataString)));

    /// <summary>
    /// Creates <c>assistant/</c> and <c>assistant/&lt;conversationId&gt;/</c>. Both are needed before the first
    /// publish, because WebDAV answers 409 when a collection's parent does not exist yet.
    /// </summary>
    private async Task AssistantEnsureConversationAsync(string conversationId, CancellationToken token)
    {
        await AssistantMkcolAsync(AssistantCollectionUrl(AssistantRoot), token);
        await AssistantMkcolAsync(AssistantCollectionUrl(AssistantRoot, conversationId), token);
    }

    /// <summary>Creating an existing collection is reported as 405 by WebDAV servers; that is success here.</summary>
    private async Task AssistantMkcolAsync(Uri uri, CancellationToken token)
    {
        using var response = await RequestAsync(new HttpMethod("MKCOL"), uri, null, token);
        if (response.StatusCode != HttpStatusCode.MethodNotAllowed) Check(response);
    }

    /// <summary>
    /// Reads one manifest. Invalid protocol content throws <see cref="InvalidDataException"/>; an unreadable
    /// body throws <see cref="JsonException"/> so a truncated record is retried instead of being marked known.
    /// </summary>
    private async Task<AssistantManifest?> AssistantReadManifestAsync(string conversationId, string itemId, CancellationToken token)
    {
        using var initial = await RequestAsync(HttpMethod.Get,
            AssistantFileUrl(AssistantRoot, conversationId, itemId, AssistantManifestName), null, token);
        if (initial.StatusCode == HttpStatusCode.NotFound) return null;
        using var redirected = await FollowDownloadAsync(initial, token);
        var response = redirected ?? initial;
        Check(response);
        var manifest = await response.Content.ReadFromJsonAsync<AssistantManifest>(AssistantJson.Options, token);
        if (manifest is null) throw new InvalidDataException("助手条目记录为空。");
        if (!string.Equals(manifest.Id, itemId, StringComparison.Ordinal)) throw new InvalidDataException("助手条目记录与目录不一致。");
        return AssistantValidation.Manifest(manifest);
    }

    private static DateTimeOffset LastModified(XElement row, XNamespace dav)
    {
        var value = row.Descendants(dav + "getlastmodified").FirstOrDefault()?.Value;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : DateTimeOffset.MinValue;
    }
}
