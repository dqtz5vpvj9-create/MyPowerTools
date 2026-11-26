using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FileTransfer.Core.WebDrop;
using FileTransfer.MyPowerTools;

namespace FileTransfer.Core.Tests;

public sealed class WebDropServerTests
{
    private const string ChineseSlideFileName = "智能终端操作系统的跨层研究实践-李新锐-高校论坛.pptx";

    [Fact]
    public async Task ServesLandingPageExactBytesContentDispositionHeadAndByteRangeForChineseFile()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var filePath = Path.Combine(tempDir, ChineseSlideFileName);
            var payload = RandomNumberGenerator.GetBytes(200_000);
            await File.WriteAllBytesAsync(filePath, payload);

            await using var server = new WebDropServer(
                addressProvider: () => [IPAddress.Loopback]);

            var drop = server.CreateDrop(filePath, TimeSpan.FromMinutes(10), maxDownloads: 0);

            Assert.StartsWith("drop-", drop.Id, StringComparison.Ordinal);
            Assert.Equal(13, drop.Id.Length);
            Assert.True(drop.Active);
            Assert.True(server.IsListening);
            Assert.True(server.Port > 0);
            Assert.Contains($"http://127.0.0.1:{server.Port}/d/{drop.Token}", drop.Urls);

            using var http = new HttpClient();

            // 1. Landing page HTML
            using var pageResponse = await http.GetAsync(drop.PrimaryUrl);
            Assert.Equal(HttpStatusCode.OK, pageResponse.StatusCode);
            Assert.Equal("text/html", pageResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", pageResponse.Content.Headers.ContentType?.CharSet);
            var html = await pageResponse.Content.ReadAsStringAsync();
            Assert.Contains(ChineseSlideFileName, html);
            Assert.Contains($"/d/{drop.Token}/download", html);

            // 2. HEAD download metadata (must not consume a download)
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, drop.DownloadUrl);
            using var headResponse = await http.SendAsync(headRequest);
            Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            Assert.Equal(payload.Length, headResponse.Content.Headers.ContentLength);
            Assert.Equal("application/octet-stream", headResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains("bytes", headResponse.Headers.AcceptRanges);

            var rawDisposition = string.Join(", ", headResponse.Content.Headers.GetValues("Content-Disposition"));
            Assert.Contains("attachment;", rawDisposition);
            Assert.Contains("filename=\"download.pptx\"", rawDisposition);
            Assert.Contains("filename*=UTF-8''" + Uri.EscapeDataString(ChineseSlideFileName), rawDisposition);
            Assert.Equal(ChineseSlideFileName, headResponse.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());

            // 3. Full GET download with exact byte match
            using var fullResponse = await http.GetAsync(drop.DownloadUrl);
            Assert.Equal(HttpStatusCode.OK, fullResponse.StatusCode);
            Assert.Equal(payload.Length, fullResponse.Content.Headers.ContentLength);
            var downloaded = await fullResponse.Content.ReadAsByteArrayAsync();
            Assert.Equal(payload, downloaded);

            // 4. Range: bytes=0-99 -> 206 Partial Content
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, drop.DownloadUrl);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 99);
            using var rangeResponse = await http.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal(100, rangeResponse.Content.Headers.ContentLength);
            Assert.NotNull(rangeResponse.Content.Headers.ContentRange);
            Assert.Equal(0, rangeResponse.Content.Headers.ContentRange!.From);
            Assert.Equal(99, rangeResponse.Content.Headers.ContentRange!.To);
            Assert.Equal(payload.Length, rangeResponse.Content.Headers.ContentRange!.Length);
            var slice = await rangeResponse.Content.ReadAsByteArrayAsync();
            Assert.Equal(payload[..100], slice);

            // 5. Method Not Allowed for POST
            using var postResponse = await http.PostAsync(drop.DownloadUrl, new StringContent(""));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, postResponse.StatusCode);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RangeVariantsAndHtmlEscapingAndCandidateAddressDiscoveryWork()
    {
        Assert.True(WebDropServer.IsTailscaleIpv4(IPAddress.Parse("100.64.0.1")));
        Assert.True(WebDropServer.IsTailscaleIpv4(IPAddress.Parse("100.127.255.254")));
        Assert.False(WebDropServer.IsTailscaleIpv4(IPAddress.Parse("100.128.0.1")));
        Assert.True(WebDropServer.IsPrivateLanIpv4(IPAddress.Parse("10.0.0.5")));
        Assert.True(WebDropServer.IsPrivateLanIpv4(IPAddress.Parse("172.16.4.1")));
        Assert.True(WebDropServer.IsPrivateLanIpv4(IPAddress.Parse("192.168.1.42")));
        Assert.False(WebDropServer.IsPrivateLanIpv4(IPAddress.Parse("8.8.8.8")));

        var discovered = WebDropServer.DiscoverCandidateAddresses();
        Assert.Contains(IPAddress.Loopback, discovered);

        var tempDir = CreateTempDirectory();
        try
        {
            var trickyName = "report_<script>&1.txt";
            var filePath = Path.Combine(tempDir, trickyName);
            var payload = Encoding.UTF8.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ");
            await File.WriteAllBytesAsync(filePath, payload);

            await using var server = new WebDropServer(addressProvider: () => [IPAddress.Loopback]);
            var drop = server.CreateDrop(filePath, TimeSpan.FromMinutes(5), maxDownloads: 0);

            using var http = new HttpClient();

            // Landing page escapes HTML special characters.
            var html = await http.GetStringAsync(drop.PrimaryUrl);
            Assert.DoesNotContain("<script>", html);
            Assert.Contains("report_&lt;script&gt;&amp;1.txt", html);

            // Suffix range: bytes=-10
            using var suffixReq = new HttpRequestMessage(HttpMethod.Get, drop.DownloadUrl);
            suffixReq.Headers.Range = new RangeHeaderValue(null, 10);
            using var suffixResp = await http.SendAsync(suffixReq);
            Assert.Equal(HttpStatusCode.PartialContent, suffixResp.StatusCode);
            Assert.Equal(payload[^10..], await suffixResp.Content.ReadAsByteArrayAsync());

            // Unsatisfiable range: bytes=99999- -> 416 Range Not Satisfiable
            using var invalidReq = new HttpRequestMessage(HttpMethod.Get, drop.DownloadUrl);
            invalidReq.Headers.Range = new RangeHeaderValue(99999, null);
            using var invalidResp = await http.SendAsync(invalidReq);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalidResp.StatusCode);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task MaxDownloadsOneReturnsGoneOnSecondDownload()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var filePath = Path.Combine(tempDir, "one-shot.bin");
            var payload = Encoding.UTF8.GetBytes("hello web drop");
            await File.WriteAllBytesAsync(filePath, payload);

            await using var server = new WebDropServer(
                addressProvider: () => [IPAddress.Loopback]);

            var drop = server.CreateDrop(filePath, TimeSpan.FromMinutes(5), maxDownloads: 1);
            using var http = new HttpClient();

            // HEAD must not consume the single download quota.
            using (var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, drop.DownloadUrl)))
            {
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            }

            // First GET succeeds.
            using (var first = await http.GetAsync(drop.DownloadUrl))
            {
                Assert.Equal(HttpStatusCode.OK, first.StatusCode);
                Assert.Equal(payload, await first.Content.ReadAsByteArrayAsync());
            }

            // Second GET returns 410 Gone.
            using (var second = await http.GetAsync(drop.DownloadUrl))
            {
                Assert.Equal(HttpStatusCode.Gone, second.StatusCode);
            }

            // Landing page also reports 410 Gone.
            using (var page = await http.GetAsync(drop.PrimaryUrl))
            {
                Assert.Equal(HttpStatusCode.Gone, page.StatusCode);
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task TtlExpiryAndRevokeReturnGoneAndUnknownTokenReturnsNotFound()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var filePath = Path.Combine(tempDir, "sample.txt");
            await File.WriteAllTextAsync(filePath, "temporary content");

            var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero));
            await using var server = new WebDropServer(
                timeProvider: clock,
                idleStopDelay: TimeSpan.FromSeconds(10),
                addressProvider: () => [IPAddress.Loopback]);

            var expiringDrop = server.CreateDrop(filePath, TimeSpan.FromSeconds(60), maxDownloads: 0);
            var revokingDrop = server.CreateDrop(filePath, TimeSpan.FromMinutes(30), maxDownloads: 0);

            using var http = new HttpClient();

            // Unknown token -> 404 Not Found.
            var unknownUrl = $"http://127.0.0.1:{server.Port}/d/aaaaaaaaaaaaaaaaaaaaaa";
            using (var notFound = await http.GetAsync(unknownUrl))
            {
                Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
            }

            // Revoke returns 410 Gone immediately.
            Assert.True(server.RevokeDrop(revokingDrop.Id));
            Assert.False(server.RevokeDrop(revokingDrop.Id));
            using (var revokedResponse = await http.GetAsync(revokingDrop.DownloadUrl))
            {
                Assert.Equal(HttpStatusCode.Gone, revokedResponse.StatusCode);
            }

            // Advance clock past 60s TTL -> expiringDrop returns 410 Gone.
            clock.Advance(TimeSpan.FromSeconds(61));
            using (var expiredResponse = await http.GetAsync(expiringDrop.DownloadUrl))
            {
                Assert.Equal(HttpStatusCode.Gone, expiredResponse.StatusCode);
            }

            // After idle grace delay with zero active drops, server auto-stops listening.
            clock.Advance(TimeSpan.FromSeconds(15));
            var stopped = await WaitUntilAsync(() => !server.IsListening, TimeSpan.FromSeconds(3));
            Assert.True(stopped);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task DisposeAsyncStopsListenerCleanlyAndModulePartialHandlesDropCommands()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var filePath = Path.Combine(tempDir, ChineseSlideFileName);
            await File.WriteAllTextAsync(filePath, "slides");

            var module = new FileTransferModule();
            try
            {
                var (createHandled, createResult) = await module.TryExecuteWebDropCommandAsync(
                    "file-transfer.drop.create",
                    new JsonObject
                    {
                        ["path"] = filePath,
                        ["ttlSeconds"] = 300,
                        ["maxDownloads"] = 2
                    },
                    CancellationToken.None);

                Assert.True(createHandled);
                var drop = Assert.IsType<WebDropInfo>(createResult);
                Assert.Equal(ChineseSlideFileName, drop.FileName);

                var (listHandled, listResult) = await module.TryExecuteWebDropCommandAsync(
                    "file-transfer.drop.list",
                    new JsonObject(),
                    CancellationToken.None);
                Assert.True(listHandled);
                Assert.NotNull(listResult);

                var (revokeHandled, _) = await module.TryExecuteWebDropCommandAsync(
                    "file-transfer.drop.revoke",
                    new JsonObject { ["id"] = drop.Id },
                    CancellationToken.None);
                Assert.True(revokeHandled);

                var (unknownHandled, unknownResult) = await module.TryExecuteWebDropCommandAsync(
                    "file-transfer.other",
                    new JsonObject(),
                    CancellationToken.None);
                Assert.False(unknownHandled);
                Assert.Null(unknownResult);
            }
            finally
            {
                await module.DisposeWebDropAsync();
                await module.DisposeAsync(CancellationToken.None);
            }

            // Direct server DisposeAsync verification.
            var server = new WebDropServer(addressProvider: () => [IPAddress.Loopback]);
            var info = server.CreateDrop(filePath);
            Assert.True(server.IsListening);
            await server.DisposeAsync();
            Assert.False(server.IsListening);
            await server.DisposeAsync(); // Idempotent.
            Assert.Throws<ObjectDisposedException>(() => server.CreateDrop(filePath));
            Assert.NotEmpty(info.Urls);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-webdrop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return predicate();
    }

    private sealed class FakeTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private readonly object _lock = new();
        private readonly List<FakeTimer> _timers = [];
        private DateTimeOffset _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        public void Advance(TimeSpan delta)
        {
            List<FakeTimer> due = [];
            lock (_lock)
            {
                _utcNow += delta;
                foreach (var timer in _timers)
                {
                    if (timer.IsDue(_utcNow))
                    {
                        due.Add(timer);
                    }
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(this, callback, state);
            lock (_lock)
            {
                _timers.Add(timer);
                timer.SetSchedule(_utcNow, dueTime, period);
            }

            return timer;
        }

        private void RemoveTimer(FakeTimer timer)
        {
            lock (_lock)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _nextDue;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public void SetSchedule(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                _nextDue = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            }

            public bool IsDue(DateTimeOffset now)
            {
                return !_disposed && _nextDue is { } due && now >= due;
            }

            public void Fire()
            {
                lock (owner._lock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _nextDue = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero
                        ? null
                        : owner._utcNow + _period;
                }

                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._lock)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    SetSchedule(owner._utcNow, dueTime, period);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._lock)
                {
                    _disposed = true;
                    _nextDue = null;
                }

                owner.RemoveTimer(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
