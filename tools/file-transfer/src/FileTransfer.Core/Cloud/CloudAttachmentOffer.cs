using System.Security.Cryptography;
using FileTransfer.Core.Assistant;

namespace FileTransfer.Core.Cloud;

public sealed record CloudAttachmentOffer(int Version, string ConversationId, AssistantManifest Message,
    string Capability, string SenderDeviceId, DateTimeOffset ExpiresAt, string Route = "mpt-cloud-stream-v1")
{
    public static CloudAttachmentOffer Create(string conversationId, AssistantManifest message) =>
        new(1, AssistantValidation.ConversationId(conversationId), AssistantValidation.Manifest(message),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), message.SenderDeviceId, DateTimeOffset.UtcNow.AddDays(6));
    public bool Matches(CloudPayloadRequest request, string conversationId, DateTimeOffset now) =>
        ConversationId == conversationId && Message.Id == request.ItemId && Capability == request.Capability
        && Message.Size == request.Size && Message.TargetDeviceId is null && Message.Kind != AssistantItemKind.Text
        && now < ExpiresAt && now < request.ExpiresAt;
}
public sealed record CloudPayloadRequest(string RequestId, string ItemId, string Capability, long Size, DateTimeOffset ExpiresAt);
public sealed record CloudPayloadMapping(CloudAttachmentOffer Offer, string AccountId, string MountPath, string ObjectPath, bool Uploaded,
    QuarkShareDescriptor? Share = null);

/// <summary>Single-file sharing capability; never includes the owner's provider credentials.</summary>
public sealed record CloudShareLocator(int Version, string ConversationId, AssistantManifest Message,
    DateTimeOffset ExpiresAt, QuarkShareDescriptor Share)
{
    public void Validate(string conversation, AssistantManifest expected)
    {
        if (Version != 1 || ConversationId != conversation || expected.TargetDeviceId is not null ||
            expected.Kind == AssistantItemKind.Text || ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("网盘分享已失效或不属于当前会话。");
        SharedLocatorRules.SameMessage(expected, Message);
        if (Share is null) throw new InvalidDataException("网盘分享信息不完整。");
    }
}
