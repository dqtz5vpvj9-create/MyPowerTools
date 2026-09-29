using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FileTransfer.Core;

/// <summary>
/// Protocol v3: identity hello, item-scoped transfer and delivery receipts.
///
/// The framing is unchanged (big-endian int32 JSON length, UTF-8 JSON, then exact payload bytes), so
/// one receiver can answer v1 file offers, v2 reachability probes and v3 frames on the same port.
/// Every v3 frame carries the item id, which is what makes a retry idempotent: the receiving side
/// deduplicates on the id instead of on a file name, and the sender only reports "delivered" after
/// the receiver acknowledged that it saved the item.
/// </summary>
public static class AssistantWire
{
    /// <summary>Frame version of the item protocol.</summary>
    public const int Version = 3;
    public const string ItemKind = "item";
    public const string ReceiptKind = "receipt";
    public const string TextItem = "text";
    public const string ImageItem = "image";
    public const string FileItem = "file";
    /// <summary>Reply state for an item the receiver committed and acknowledged.</summary>
    public const string DeliveredState = "delivered";
    /// <summary>Reply state for a first-contact frame that is waiting for the user to accept it.</summary>
    public const string PendingState = "pending";

    /// <summary>
    /// Superset of every frame version the receiver accepts. A v1 offer and a v2 probe deserialize
    /// here with their own fields, so a legacy peer keeps working without a separate code path.
    /// </summary>
    public sealed record Frame(
        int Version,
        string? Kind = null,
        string? Token = null,
        string? Name = null,
        long Size = 0,
        string? DeviceId = null,
        string? ItemId = null,
        string? ConversationId = null,
        string? ItemKind = null,
        string? Text = null,
        string? SenderName = null,
        string? TargetDeviceId = null,
        string? Platform = null,
        string? Address = null,
        string? Scope = null);

    /// <summary>The public half of a device's identity: returned to an unauthenticated hello.</summary>
    /// <summary>
    /// Item admission and final answer. <paramref name="Pending"/> means the receiver is waiting for
    /// its user to accept the first contact; <paramref name="State"/> is "delivered" only after the
    /// item was saved locally.
    /// </summary>
    public sealed record ItemReply(bool Ok, string Message, bool Pending = false, string? RequestId = null, string? State = null, string? Name = null);

    /// <summary>A delivery receipt pushed to a device that already holds the item.</summary>
    public sealed record ReceiptFrame(int Version, string Kind, string ItemId, string ConversationId, string DeviceId, string State, DateTimeOffset At);
    /// <summary>A receipt frame reuses <c>State</c> semantics in the frame's text slot.</summary>
    public static Frame Receipt(string itemId, string conversationId, string deviceId, string state, DateTimeOffset at) =>
        new(Version, ReceiptKind, ItemId: itemId, ConversationId: conversationId, DeviceId: deviceId, Text: state, Address: at.ToString("O"));

    /// <summary>
    /// Sends one assistant item. Returns the receiver's real answer: pending first contact, refused,
    /// or delivered after the payload was saved. A re-run with the same <paramref name="frame"/>
    /// id is idempotent on the receiving side.
    /// </summary>
    /// <param name="connectBudget">
    /// Bounds only the TCP connect. A dead candidate must not hold the exchange budget: the caller uses a
    /// short connect window and keeps the longer budget for the transfer itself.
    /// </param>
    public static async Task<ItemReply> SendItemAsync(string address, int port, Frame frame, string? payloadPath,
        Action<long, long>? progress, TimeSpan budget, CancellationToken token, TimeSpan? connectBudget = null)
    {
        var ip = IPAddress.Parse(address);
        DirectTransfer.RequirePrivateAddress(ip);
        using var client = new TcpClient(ip.AddressFamily);
        using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
        window.CancelAfter(budget);
        if (connectBudget is { } connect && connect < budget)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(window.Token);
            attempt.CancelAfter(connect);
            try { await client.ConnectAsync(ip, port, attempt.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !window.IsCancellationRequested)
            {
                throw new IOException("连接对端超时。");
            }
        }
        else
        {
            await client.ConnectAsync(ip, port, window.Token);
        }
        var stream = client.GetStream();
        await DirectTransfer.WriteJsonAsync(stream, frame, window.Token);
        var admitted = await DirectTransfer.ReadJsonAsync<ItemReply>(stream, window.Token);
        // A duplicate id is already answered, and a refused admission has nothing to stream.
        if (!admitted.Ok || admitted.Pending) return admitted;
        if (string.Equals(admitted.State, DeliveredState, StringComparison.Ordinal)) return admitted;
        if (frame.Size == 0 || payloadPath is null)
        {
            // Text and zero-byte files have no payload: the receiver still confirms the save.
            // Text is answered once (the admission read already was the final answer); a zero-byte
            // file still gets the two answers of the file path.
            return frame.ItemKind == TextItem ? admitted : await DirectTransfer.ReadJsonAsync<ItemReply>(stream, window.Token);
        }
        await using (var input = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
        {
            await TransferFiles.CopyAsync(input, stream, input.Length, (done, total) =>
            {
                window.CancelAfter(budget);
                progress?.Invoke(done, total);
            }, window.Token);
        }
        return await DirectTransfer.ReadJsonAsync<ItemReply>(stream, window.Token);
    }
}

/// <summary>
/// One item the receiver persisted before it acknowledged. The module owns the timeline and the
/// duplicate set, so the receiver reports the saved metadata instead of writing history itself.
/// </summary>
public sealed record ReceivedItem(string ItemId, string ConversationId, string DeviceId, string SenderName,
    string ItemKind, string? Text, string? Name, string? Path, long Size, DateTimeOffset At, string? TargetDeviceId = null, string? Scope = null);
