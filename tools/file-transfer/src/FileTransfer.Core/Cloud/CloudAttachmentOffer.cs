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
public sealed record CloudPayloadMapping(CloudAttachmentOffer Offer, string AccountId, string MountPath, string ObjectPath, bool Uploaded);
