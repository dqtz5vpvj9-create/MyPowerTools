using System.Text;
using System.Text.Json;
using FileTransfer.Core.Assistant;
using FileTransfer.Core.Cloud;

namespace FileTransfer.Tests;

public sealed class CloudRouteTests
{
    [Theory]
    [InlineData(404, false, "public-relay")]
    [InlineData(503, false, null)]
    [InlineData(200, false, null)]
    [InlineData(200, true, "cloud")]
    public async Task ObservationUsesValidatedOfferAndUnknownDoesNotInventPublic(int status, bool valid, string? expected)
    {
        var directory = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-cloud-route-" + Guid.NewGuid().ToString("N"));
        var server = new AssistantWebDavServer(directory);
        var message = new AssistantManifest { Id = Guid.NewGuid().ToString("N"), Kind = AssistantItemKind.File, Name = "a.bin", Size = 1, CreatedAt = DateTimeOffset.UtcNow, SenderDeviceId = "sender", SenderName = "sender" };
        server.Intercept = (context, request) =>
        {
            context.Response.StatusCode = request.Method == "PROPFIND" ? 404 : status;
            var bytes = request.Method == "PROPFIND" ? [] : Encoding.UTF8.GetBytes(valid
                ? JsonSerializer.Serialize(CloudAttachmentOffer.Create("route-test", message), AssistantJson.Options) : "invalid-json");
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes);
            context.Response.Close();
            return true;
        };
        try
        {
            using var client = new CloudRelayClient("route-test", new string('a', 64), new Uri(server.Url));
            Assert.Equal(expected, await client.ReadPayloadRouteAsync(message, default));
            if (valid) Assert.Equal(1, server.Count("PROPFIND", "/payload"));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadPayloadRouteAsync(message, cancelled.Token));
        }
        finally { await server.DisposeAsync(); Directory.Delete(directory, true); }
    }
}
