namespace FileTransfer.Core.Assistant;

/// <summary>Local grouping follows the authenticated transport scope, never the sender alone.</summary>
public static class AssistantConversations
{
    public const string History = "history";
    public const string LocalShared = "local-shared";
    public const string LocalDevice = "local-device";
    public const string SharedRelay = "shared-relay";
    public const string PairedInbox = "paired-inbox";
    public const string DirectShared = "direct-shared";
    public const string DirectDevice = "direct-device";

    public static string Shared(string id) => "shared:" + TransferFiles.DeviceId(id);
    public static string Device(string id) => "device:" + TransferFiles.DeviceId(id);
    public static bool IsShared(AssistantItem item, string conversationId) =>
        item.ConversationId == conversationId && item.Provenance is LocalShared or SharedRelay or DirectShared;
    public static bool IsPrivate(AssistantItem item) => item.Provenance is LocalDevice or PairedInbox or DirectDevice;
    public static string Key(AssistantItem item, string localDeviceId)
    {
        if (item.ConversationId is { Length: > 0 } id && IsShared(item, id)) return Shared(id);
        if (IsPrivate(item))
        {
            var peer = item.SenderDeviceId == localDeviceId ? item.TargetDeviceId : item.SenderDeviceId;
            if (!string.IsNullOrEmpty(peer) && peer != localDeviceId) return Device(peer);
        }
        return History;
    }
    public static string DraftKey(AssistantIdentity identity, AssistantPreferences? draft) =>
        draft?.TargetDeviceId is { Length: > 0 } target && target != identity.DeviceId ? Device(target) : Shared(identity.ConversationId);

    /// <summary>Move the legacy single draft into its original identity's namespace exactly once.</summary>
    public static void MigrateDrafts(AssistantState state)
    {
        state.Drafts ??= new(StringComparer.Ordinal);
        if (state.ActiveConversationKey is not null || state.Identity is null) return;
        var key = DraftKey(state.Identity, state.Preferences);
        if (state.Preferences is not null && !state.Drafts.ContainsKey(key)) state.Drafts[key] = state.Preferences.Copy();
        state.ActiveConversationKey = key;
    }

    public static string ValidateKey(string key)
    {
        if (key == History) return key;
        if (key.StartsWith("shared:", StringComparison.Ordinal)) return Shared(key[7..]);
        if (key.StartsWith("device:", StringComparison.Ordinal)) return Device(key[7..]);
        throw new ArgumentException("会话标识无效。", nameof(key));
    }
}
