using System.Net;
using System.Text;
using RemoteNotifications.Surface.Services;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.Utilities.IO.Pem;

namespace MyPowerTools.Tests;

public sealed class RemoteNotificationHttpPollerTests
{
    [Theory]
    [InlineData("{\"notifications\":[]}", "idle")]
    [InlineData("{\"notifications\":[{\"message\":\"   \"}]}", "idle")]
    [InlineData("null", "error")]
    [InlineData("not-json", "error")]
    public async Task RemoteNotificationWorkflow_handles_empty_and_invalid_server_responses(string body, string state)
    {
        using var fixture = OpenSshKeyFixture.Create();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        using var client = new HttpClient(handler);
        var poller = new RemoteNotificationHttpPoller(client, fixture.Path, "https://notifications.example.test", "e2e-isolated");

        var result = await poller.PullAsync("");

        Assert.Equal(state, result.State);
        Assert.Empty(result.Notifications);
        Assert.Single(handler.Requests);
        if (state == "error") Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task RemoteNotificationWorkflow_recovers_from_transport_failure_and_preserves_metadata()
    {
        using var fixture = OpenSshKeyFixture.Create();
        var attempts = 0;
        var handler = new RecordingHandler(_ =>
        {
            if (++attempts < 3) throw new HttpRequestException("transport interrupted sig=private-handshake");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"notifications":[{"message_id":"fallback","message":"[build] ready",
                    "session_id":"session-1","session_name":"Build","source_client":"codex",
                    "source_event_id":"event-1","source_message_id":"source-1","content_kind":"assistant_final"}]}
                    """, Encoding.UTF8, "application/json")
            };
        });
        using var client = new HttpClient(handler);
        var poller = new RemoteNotificationHttpPoller(client, fixture.Path, "https://notifications.example.test", "e2e-isolated");

        var result = await poller.PullAsync("2026-10-03T00:00:00Z", CancellationToken.None, 2500);

        Assert.Equal("ok", result.State);
        Assert.Equal(3, attempts);
        var record = Assert.Single(result.Notifications);
        Assert.Equal("fallback", record.Id);
        Assert.Equal("e2e-isolated", record.Channel);
        Assert.Equal("info", record.Icon);
        Assert.Equal("session-1", record.SessionId);
        Assert.Equal("Build", record.SessionName);
        Assert.Equal("codex", record.SourceClient);
        Assert.Equal("event-1", record.SourceEventId);
        Assert.Equal("source-1", record.SourceMessageId);
        Assert.Equal("assistant_final", record.ContentKind);
        Assert.All(handler.Requests, request => Assert.Equal("2000", ParseQuery(request.RequestUri!)["limit"]));
    }

    [Fact]
    public async Task RemoteNotificationWorkflow_exhausted_transport_retries_redact_the_signature()
    {
        using var fixture = OpenSshKeyFixture.Create();
        var handler = new RecordingHandler(_ => throw new HttpRequestException("transport interrupted sig=private-handshake"));
        using var client = new HttpClient(handler);
        var poller = new RemoteNotificationHttpPoller(client, fixture.Path, "https://notifications.example.test", "e2e-isolated");

        var result = await poller.PullAsync("");

        Assert.Equal("error", result.State);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("sig=<redacted>", result.Error);
        Assert.DoesNotContain("private-handshake", result.Error);
    }

    [Fact]
    public async Task RemoteNotificationWorkflow_cancellation_stops_before_any_request()
    {
        using var fixture = OpenSshKeyFixture.Create();
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("No request should occur"));
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var poller = new RemoteNotificationHttpPoller(client, fixture.Path, "https://notifications.example.test", "e2e-isolated");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poller.PullAsync("", cancellation.Token));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Dotnet_signer_preserves_the_original_ed25519_hello_protocol()
    {
        using var fixture = OpenSshKeyFixture.Create();

        var encodedSignature = RemoteNotificationSshSigner.SignHandshake(fixture.Path);
        var signature = DecodeUrlSafeBase64(encodedSignature);
        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, fixture.PrivateKey.GeneratePublicKey());
        var handshake = Encoding.ASCII.GetBytes("hello");
        verifier.BlockUpdate(handshake, 0, handshake.Length);

        Assert.Equal(64, signature.Length);
        Assert.True(verifier.VerifySignature(signature));
        Assert.EndsWith("==", encodedSignature, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dotnet_poller_sends_the_signed_pull_contract_and_maps_notifications()
    {
        using var fixture = OpenSshKeyFixture.Create();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {
                  "notifications": [
                    {
                      "id": "message-1",
                      "channel": "default",
                      "message": "[build] complete",
                      "icon": "success",
                      "timestamp": "2026-07-11T00:00:00Z",
                      "server_timestamp": "2026-07-11T00:00:01Z"
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var poller = new RemoteNotificationHttpPoller(
            httpClient,
            fixture.Path,
            "https://notifications.example.test:8888",
            "default");

        var result = await poller.PullAsync("2026-07-10T23:59:00Z");

        Assert.Equal("ok", result.State);
        var notification = Assert.Single(result.Notifications);
        Assert.Equal("message-1", notification.Id);
        Assert.Equal("[build] complete", notification.Message);
        Assert.Equal("2026-07-11T00:00:01Z", notification.ServerTimestamp);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/pull", request.RequestUri!.AbsolutePath);
        Assert.Contains("MyPowerTools/RemoteNotifications", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        var query = ParseQuery(request.RequestUri);
        Assert.Equal("default", query["channel"]);
        Assert.Equal("20", query["limit"]);
        Assert.Equal("2026-07-10T23:59:00Z", query["since"]);

        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, fixture.PrivateKey.GeneratePublicKey());
        var handshake = Encoding.ASCII.GetBytes("hello");
        verifier.BlockUpdate(handshake, 0, handshake.Length);
        Assert.True(verifier.VerifySignature(DecodeUrlSafeBase64(query["sig"])));
    }

    [Fact]
    public async Task Dotnet_poller_reports_auth_and_missing_key_failures_without_external_processes()
    {
        using var fixture = OpenSshKeyFixture.Create();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("denied sig=should-not-leak")
        });
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var poller = new RemoteNotificationHttpPoller(
            httpClient,
            fixture.Path,
            "https://notifications.example.test:8888",
            "default");

        var unauthorized = await poller.PullAsync("");
        var missingKey = await new RemoteNotificationHttpPoller(
                httpClient,
                fixture.Path + ".missing",
                "https://notifications.example.test:8888",
                "default")
            .PullAsync("");

        Assert.Equal("auth", unauthorized.State);
        Assert.Contains("HTTP 401", unauthorized.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("should-not-leak", unauthorized.Error, StringComparison.Ordinal);
        Assert.Equal("error", missingKey.State);
        Assert.Contains("SSH signing key was not found", missingKey.Error, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Shell_runtime_declares_the_managed_crypto_dependency_and_has_no_python_poller()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "MyPowerTools.Shell.Avalonia.csproj"));
        var legacyStore = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "remote-notifications",
            "current-integration",
            "src",
            "RemoteNotifications.Surface",
            "Services",
            "RemoteNotificationsLegacyStore.cs"));
        var poller = File.ReadAllText(Path.Combine(
            root,
            "tools",
            "remote-notifications",
            "current-integration",
            "src",
            "RemoteNotifications.Surface",
            "Services",
            "RemoteNotificationHttpPoller.cs"));

        Assert.Contains("BouncyCastle.Cryptography", project, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoteNotificationPythonPoller", legacyStore, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessStartInfo", legacyStore, StringComparison.Ordinal);
        Assert.DoesNotContain("from cryptography", legacyStore, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("import cryptography", legacyStore, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("python", poller, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ParseQuery(Uri uri)
    {
        return uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => Uri.UnescapeDataString(part.Length == 2 ? part[1] : ""),
                StringComparer.Ordinal);
    }

    private static byte[] DecodeUrlSafeBase64(string value)
    {
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/'));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MyPowerTools.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("MyPowerTools repository root was not found.");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class OpenSshKeyFixture : IDisposable
    {
        private OpenSshKeyFixture(string path, Ed25519PrivateKeyParameters privateKey)
        {
            Path = path;
            PrivateKey = privateKey;
        }

        public string Path { get; }
        public Ed25519PrivateKeyParameters PrivateKey { get; }

        public static OpenSshKeyFixture Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "mpt-remote-notification-key",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "id_ed25519");
            var seed = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            var privateKey = new Ed25519PrivateKeyParameters(seed);
            var keyBlob = OpenSshPrivateKeyUtilities.EncodePrivateKey(privateKey);
            using (var writer = File.CreateText(path))
            using (var pemWriter = new PemWriter(writer))
            {
                pemWriter.WriteObject(new PemObject("OPENSSH PRIVATE KEY", keyBlob));
            }

            return new OpenSshKeyFixture(path, privateKey);
        }

        public void Dispose()
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
