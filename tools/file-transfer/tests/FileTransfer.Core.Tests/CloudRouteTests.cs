using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FileTransfer.Core;
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
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
        var message = new AssistantManifest { Id = Guid.NewGuid().ToString("N"), Kind = AssistantItemKind.File, Name = "a.bin", Size = 1, CreatedAt = DateTimeOffset.UtcNow, SenderDeviceId = "sender", SenderName = "sender" };
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = status;
            var bytes = Encoding.UTF8.GetBytes(valid ? JsonSerializer.Serialize(CloudAttachmentOffer.Create("route-test", message), AssistantJson.Options) : "invalid-json");
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            if (valid)
            {
                context = await listener.GetContextAsync();
                Assert.Equal("PROPFIND", context.Request.HttpMethod);
                context.Response.StatusCode = 404; context.Response.Close();
            }
        });

        try
        {
            using var client = new CloudRelayClient("route-test", new string('a', 64), new Uri($"http://127.0.0.1:{port}"));
            Assert.Equal(expected, await client.ReadPayloadRouteAsync(message, default));
            await server;
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadPayloadRouteAsync(message, cancelled.Token));
        }
        finally { listener.Stop(); }
    }
}
