using System.Net;
using System.Net.Sockets;
using System.Text;
using FileTransfer.Core;

namespace FileTransfer.Tests;

/// <summary>
/// The relay may redirect a download to its own address or to a storage provider. The account must
/// never follow a redirect to a foreign plain-HTTP host, and must never carry credentials there.
/// </summary>
public sealed class CloudRedirectTests : IAsyncDisposable
{
    private const string Payload = "redirected payload\n";
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-redirect-test-" + Guid.NewGuid().ToString("N"));
    private readonly List<StubServer> _servers = [];

    public CloudRedirectTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers) await server.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private StubServer Server(Func<string, string> respond)
    {
        var server = new StubServer(respond);
        _servers.Add(server);
        return server;
    }

    private static string Ok(string body) =>
        $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    private static string Redirect(string location) =>
        $"HTTP/1.1 302 Found\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

    private static bool HasAuthorization(string request) =>
        request.Contains("Authorization:", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task SameAuthorityHttpRedirectIsFollowedWithoutCredentials()
    {
        var id = Guid.NewGuid().ToString("N");
        StubServer? relay = null;
        relay = Server(request => request.StartsWith("GET /dav/phone/" + id + "/payload", StringComparison.Ordinal)
            ? Redirect($"http://127.0.0.1:{relay!.Port}/dav/phone/{id}/direct")
            : Ok(Payload));
        var file = new CloudFile(1, id, "payload.txt", Encoding.UTF8.GetByteCount(Payload), "desktop", DateTimeOffset.UtcNow);
        using var cloud = new OpenListClient($"http://127.0.0.1:{relay.Port}/dav", "mpt-relay", "relay-password");
        var saved = await cloud.DownloadAsync("phone", file, Path.Combine(_root, "downloads"), null, CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllTextAsync(saved));
        // The WebDAV request is authenticated; the followed redirect is not.
        Assert.Equal(new[] { true, false }, relay.Requests.Select(HasAuthorization));
    }

    [Fact]
    public async Task ForeignPlainHttpRedirectIsRefusedBeforeAnyRequest()
    {
        var id = Guid.NewGuid().ToString("N");
        var foreign = Server(_ => Ok("stolen"));
        StubServer? relay = null;
        relay = Server(_ => Redirect($"http://127.0.0.1:{foreign.Port}/stolen"));
        var file = new CloudFile(1, id, "payload.txt", Encoding.UTF8.GetByteCount(Payload), "desktop", DateTimeOffset.UtcNow);
        using var cloud = new OpenListClient($"http://127.0.0.1:{relay.Port}/dav", "mpt-relay", "relay-password");
        var error = await Assert.ThrowsAsync<IOException>(() =>
            cloud.DownloadAsync("phone", file, Path.Combine(_root, "downloads"), null, CancellationToken.None));
        Assert.Contains("HTTPS", error.Message);
        Assert.Empty(foreign.Requests);
        Assert.False(Directory.Exists(Path.Combine(_root, "downloads")) && Directory.GetFiles(Path.Combine(_root, "downloads")).Length > 0);
    }

    private sealed class StubServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Func<string, string> _respond;
        private readonly List<string> _requests = [];
        private readonly Task _loop;

        public StubServer(Func<string, string> respond)
        {
            _respond = respond;
            _listener.Start(8);
            _loop = RunAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public IReadOnlyList<string> Requests { get { lock (_requests) return _requests.ToArray(); } }

        private async Task RunAsync()
        {
            while (!_lifetime.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_lifetime.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { return; }
                _ = HandleAsync(client);
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var header = new StringBuilder();
                    var buffer = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(buffer, _lifetime.Token) == 0) return;
                        header.Append((char)buffer[0]);
                    }
                    var request = header.ToString();
                    lock (_requests) _requests.Add(request);
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(_respond(request)), _lifetime.Token);
                    await stream.FlushAsync(_lifetime.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            _listener.Stop();
            try { await _loop; } catch (Exception) { }
            _lifetime.Dispose();
        }
    }
}
