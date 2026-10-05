using System.Text;
using FileTransfer.Core.Assistant;

namespace FileTransfer.Tests;

public sealed class AssistantDownloadIsolationTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-download-isolation-" + Guid.NewGuid().ToString("N"));
    private AssistantWebDavServer? _server;

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(503)] // A virtual payload is waiting for its sender; the relay itself is healthy.
    [InlineData(0)] // Connected HTTP server never sends response headers.
    public async Task Unavailable_old_payload_does_not_block_later_download_or_its_receipt(int status)
    {
        var (sender, receiver, client, old, good) = await SetupAsync();
        using (client)
        {
            _server!.Intercept = (context, request) =>
            {
                if (request.Method != "GET" || !request.Path.Contains(old.Id) || !request.Path.EndsWith("/payload")) return false;
                if (status != 0) AssistantWebDavServer.Write(context, status);
                return true;
            };
            var result = await new AssistantSync(receiver, client).SyncAsync(new("receiver", "Receiver", "isolation"), default);
            Assert.Equal(1, result.Failed);
            Assert.Equal(1, result.Downloaded);
            Assert.Equal(1, result.ReceiptsWritten);
            Assert.Null(result.RetryAfter);
            var state = await receiver.LoadAsync(default);
            Assert.Equal(AssistantItemState.Failed, state.Find(old.Id)!.State);
            var saved = state.Find(good.Id)!;
            Assert.Equal(AssistantItemState.Available, saved.State);
            Assert.Equal("second attachment", await File.ReadAllTextAsync(saved.LocalPath!));
            Assert.NotNull(saved.ReceiptAt);
        }
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Shared_authentication_or_service_failure_still_stops_downloads_and_backs_off(int status)
    {
        var (sender, receiver, client, old, good) = await SetupAsync();
        using (client)
        {
            _server!.Intercept = (context, request) =>
            {
                if (request.Method != "GET" || !request.Path.EndsWith("/payload")) return false;
                AssistantWebDavServer.Write(context, status);
                return true;
            };
            var result = await new AssistantSync(receiver, client).SyncAsync(new("receiver", "Receiver", "isolation"), default);
            Assert.Equal(1, result.Failed);
            Assert.Equal(0, result.Downloaded);
            Assert.NotNull(result.RetryAfter);
            Assert.Equal(1, _server.Count("GET", "/payload"));
            Assert.Equal(AssistantItemState.Stored, (await receiver.LoadAsync(default)).Find(good.Id)!.State);
        }
    }

    private async Task<(AssistantStore Sender, AssistantStore Receiver, OpenListClient Client, AssistantItem Old, AssistantItem Good)> SetupAsync()
    {
        _server = new AssistantWebDavServer(Path.Combine(_root, "relay"));
        var sender = new AssistantStore(Path.Combine(_root, "sender"));
        var receiver = new AssistantStore(Path.Combine(_root, "receiver"));
        var identity = new AssistantIdentity("sender", "Sender", "isolation");
        var oldPath = Path.Combine(_root, "old.txt");
        var goodPath = Path.Combine(_root, "new.txt");
        await File.WriteAllTextAsync(oldPath, "old unavailable attachment");
        await File.WriteAllTextAsync(goodPath, "second attachment");
        var old = (await sender.EnqueueAsync(identity, AssistantDraft.ForPaths([oldPath]), default)).Single();
        await Task.Delay(5);
        var good = (await sender.EnqueueAsync(identity, AssistantDraft.ForPaths([goodPath]), default)).Single();
        using (var publisher = new OpenListClient(_server.Url, AssistantWebDavServer.UserName, AssistantWebDavServer.Password))
            Assert.Equal(2, (await new AssistantSync(sender, publisher).SyncAsync(identity, default)).Published);
        var client = new OpenListClient(_server.Url, AssistantWebDavServer.UserName, AssistantWebDavServer.Password)
            { ResponseHeadersTimeout = TimeSpan.FromMilliseconds(200) };
        return (sender, receiver, client, old, good);
    }

    [Fact]
    public async Task Actual_connection_loss_still_stops_the_download_pass()
    {
        var (sender, receiver, client, old, good) = await SetupAsync();
        using (client)
        {
            var sync = new AssistantSync(receiver, client)
            {
                CloudDownloader = (_, _, _, _) =>
                {
                    // Close the real listener after discovery, before the first payload connects.
                    // Stopping inside an accepted HttpListener request emits HTTP 503 instead.
                    _server!.Stop();
                    return Task.FromResult<string?>(null);
                }
            };
            var result = await sync.SyncAsync(new("receiver", "Receiver", "isolation"), default);
            Assert.Equal(1, result.Failed);
            Assert.Equal(0, result.Downloaded);
            Assert.NotNull(result.RetryAfter);
            Assert.Equal(AssistantItemState.Stored, (await receiver.LoadAsync(default)).Find(good.Id)!.State);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
