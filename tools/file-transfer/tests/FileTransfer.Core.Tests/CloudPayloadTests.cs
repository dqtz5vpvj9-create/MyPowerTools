using FileTransfer.Core.Cloud;
using FileTransfer.Core.Assistant;

namespace FileTransfer.Tests;
public sealed class CloudPayloadTests
{
    [Theory]
    [InlineData(44)]
    [InlineData(-120)]
    public void RelayDeadlineRetainsItsLifetimeAcrossDifferentDeviceClocks(int offsetSeconds)
    {
        var serverTime = DateTimeOffset.UtcNow;
        var localTime = serverTime.AddSeconds(offsetSeconds);
        var offer = CloudAttachmentOffer.Create("self-test", Message);
        var request = new CloudPayloadRequest("request", offer.Message.Id, offer.Capability, 1024, serverTime.AddSeconds(30));
        var aligned = Assert.Single(CloudRelayClient.AlignRequestClocks([request], serverTime, localTime));
        Assert.Equal(localTime.AddSeconds(30), aligned.ExpiresAt);
        Assert.True(offer.Matches(aligned, "self-test", localTime.AddSeconds(10)));
        Assert.False(offer.Matches(aligned, "self-test", localTime.AddSeconds(31)));
    }
    private static AssistantManifest Message => new() { Id = new string('a', 32), Kind = AssistantItemKind.File, Name = "data.bin", Size = 1024,
        SenderDeviceId = "sender", SenderName = "Sender", CreatedAt = DateTimeOffset.UtcNow };
    [Fact]
    public void CapabilityNeedsMatchingNamespaceObjectSizeExpiryAndSharedScope()
    {
        var offer = CloudAttachmentOffer.Create("self-test", Message);
        var request = new CloudPayloadRequest("request", offer.Message.Id, offer.Capability, 1024, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(offer.Matches(request, "self-test", DateTimeOffset.UtcNow));
        Assert.False(offer.Matches(request, "self-foreign", DateTimeOffset.UtcNow));
        Assert.False(offer.Matches(request with { ItemId = new string('b', 32) }, "self-test", DateTimeOffset.UtcNow));
        Assert.False(offer.Matches(request with { Capability = new string('0', 64) }, "self-test", DateTimeOffset.UtcNow));
        Assert.False(offer.Matches(request with { Size = 2048 }, "self-test", DateTimeOffset.UtcNow));
        Assert.False(offer.Matches(request, "self-test", DateTimeOffset.UtcNow.AddDays(8)));
        Assert.False((offer with { Message = Message with { TargetDeviceId = "private-recipient" } }).Matches(request, "self-test", DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task MappingPersistsBeforeUploadAndKeepsNamespacesDistinct()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-cloud-map-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CloudPayloadStore(Path.Combine(root, "owned.json"));
            var mapping = new CloudPayloadMapping(CloudAttachmentOffer.Create("self-first", Message), "account", "/mount", "/mount/MPT/object", false);
            await store.SaveAsync(mapping, default);
            var original = Assert.Single(await new CloudPayloadStore(Path.Combine(root, "owned.json")).ReadAsync(default));
            Assert.False(original.Uploaded);
            await store.SaveAsync(mapping with { Uploaded = true }, default);
            await store.SaveAsync(mapping with { Offer = CloudAttachmentOffer.Create("self-second", Message) }, default);
            var all = await store.ReadAsync(default);
            Assert.Equal(2, all.Length);
            Assert.True(all.Single(m => m.Offer.ConversationId == "self-first").Uploaded);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
