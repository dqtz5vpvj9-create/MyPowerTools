using System.Text.Json;
using FileTransfer.Core.Assistant;
using FileTransfer.Core.Cloud;

namespace FileTransfer.Tests;

public sealed class CloudShareLocatorTests
{
    private static AssistantManifest Message => new() { Id = Guid.NewGuid().ToString("N"),
        Kind = AssistantItemKind.File, Name = "test.bin", Size = 17, SenderDeviceId = "sender",
        SenderName = "Sender", CreatedAt = DateTimeOffset.UtcNow };

    [Fact]
    public async Task ShareIsPublishedBeforeTheVisibleMessageAndCanBeReadWithoutTheSender()
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-share-" + Guid.NewGuid().ToString("N"));
        await using var server = new AssistantWebDavServer(root);
        var writes = new List<string>();
        byte[]? descriptor = null;
        server.Intercept = (context, request) =>
        {
            if (request.Method == "PUT")
            {
                writes.Add(request.Path);
                using var body = new MemoryStream();
                context.Request.InputStream.CopyTo(body);
                if (request.Path.EndsWith("/cloud-share.json")) descriptor = body.ToArray();
            }
            AssistantWebDavServer.Write(context, 200, request.Method == "GET" ? descriptor : null);
            return true;
        };
        try
        {
            var message = Message;
            var offer = CloudAttachmentOffer.Create("share-test", message);
            var share = new CloudShareLocator(1, "share-test", message, offer.ExpiresAt, new("single-share", "single-file"));
            using (var sender = new CloudRelayClient("share-test", new string('a', 64), new Uri(server.Url)))
                await sender.PublishAsync(offer, default, share);
            Assert.EndsWith("/cloud-share.json", writes[0]);
            Assert.EndsWith($"/assistant/share-test/{message.Id}/manifest.json", writes[^1]);
            using var receiver = new CloudRelayClient("share-test", new string('a', 64), new Uri(server.Url));
            Assert.Equal(share, await receiver.ReadShareAsync(message, default));
        }
        finally { await server.DisposeAsync(); Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(404, true)]
    [InlineData(503, false)]
    public async Task OnlyMissingShareFallsBackToLegacyTransport(int status, bool missing)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-share-" + Guid.NewGuid().ToString("N"));
        await using var server = new AssistantWebDavServer(root);
        server.Intercept = (context, _) => { AssistantWebDavServer.Write(context, status); return true; };
        try
        {
            using var receiver = new CloudRelayClient("share-test", new string('a', 64), new Uri(server.Url));
            if (missing) Assert.Null(await receiver.ReadShareAsync(Message, default));
            else await Assert.ThrowsAsync<IOException>(() => receiver.ReadShareAsync(Message, default));
        }
        finally { await server.DisposeAsync(); Directory.Delete(root, true); }
    }

    [Fact]
    public void AShareCannotChangeFileIdentityConversationOrPrivateScope()
    {
        var message = Message;
        var share = new CloudShareLocator(1, "share-test", message, DateTimeOffset.UtcNow.AddDays(1), new("share", "file"));
        share.Validate("share-test", message);
        Assert.Throws<InvalidDataException>(() => share.Validate("another-conversation", message));
        Assert.Throws<InvalidDataException>(() => share.Validate("share-test", message with { Size = 18 }));
        Assert.Throws<InvalidDataException>(() => share.Validate("share-test", message with { TargetDeviceId = "private" }));
        Assert.Throws<InvalidDataException>(() => (share with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) }).Validate("share-test", message));
    }
}
