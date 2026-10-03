using System.Net;
using System.Text;
using Xbrd.Surface.Services;

namespace Xbrd.Workflows.Tests;

public sealed class PublisherWorkflows
{
    private const string Endpoint = "http://publisher.test";

    [Fact]
    public async Task ReadHealthSourcesAndPanel()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/health" => Reply(200, """{"ok":true,"panel_ok":true,"source_count":2}"""),
            "/api/v1/sources" => Reply(200, """{"ok":true,"sources":[{"source_id":"z","status":"ok","effective_status":"ok","capabilities":["refresh"]},{"source_id":"a","expired":true,"status":"error","effective_status":"error"}]}"""),
            "/panel.json" => Reply(200, """{"schema":"xbrd.panel.v1","status":{"label":"running"},"weather":{"temperature":25},"plan":{"title":"fixture"}}"""),
            _ => throw new InvalidOperationException("Unexpected endpoint")
        });
        using var client = new XbrdPublisherClient(TimeSpan.FromSeconds(2), handler);
        var health = await client.GetHealthAsync(Endpoint, default);
        Assert.True(health.Ok);
        Assert.Equal(2, health.SourceCount);
        var sources = await client.GetSourcesAsync(Endpoint, default);
        Assert.True(sources.Ok);
        Assert.Equal(new[] { "a", "z" }, sources.Sources.Select(source => source.SourceId));
        Assert.Equal(1, sources.ExpiredCount);
        Assert.Equal(XbrdSeverity.Error, sources.Severity);
        var panel = await client.GetPanelAsync(Endpoint, default);
        Assert.True(panel.Ok);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ControlRefreshEnableDisableDeleteAndEvidence()
    {
        var handler = new StubHandler(_ => Reply(200, """{"ok":true,"exit_code":0,"snapshot_updated":true,"duration_ms":12,"stdout_tail":"updated"}"""));
        using var client = new XbrdPublisherClient(TimeSpan.FromSeconds(2), handler);
        const string id = "a/b ?";
        var refresh = await client.RefreshSourceAsync(Endpoint, id, default);
        Assert.True(refresh.Ok);
        Assert.Equal(0, refresh.ExitCode);
        Assert.True(refresh.SnapshotUpdated);
        Assert.Contains("updated", refresh.EvidenceText);
        Assert.True((await client.SetSourceEnabledAsync(Endpoint, id, false, default)).Ok);
        Assert.True((await client.SetSourceEnabledAsync(Endpoint, id, true, default)).Ok);
        Assert.True((await client.DeleteSourceAsync(Endpoint, id, default)).Ok);
        Assert.Equal(new[] { "POST", "POST", "POST", "DELETE" }, handler.Requests.Select(r => r.Method));
        Assert.Equal(new[] { "/refresh", "/disable", "/enable", "" }, handler.Requests.Select(r => r.Url[(r.Url.IndexOf("a%2Fb%20%3F", StringComparison.Ordinal) + "a%2Fb%20%3F".Length)..]));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(405)]
    public async Task OldPublisherControlIsUnsupported(int status)
    {
        using var client = new XbrdPublisherClient(TimeSpan.FromSeconds(2), new StubHandler(_ => Reply(status, "missing")));
        var result = await client.RefreshSourceAsync(Endpoint, "quota", default);
        Assert.Equal(XbrdControlStatus.Unsupported, result.Status);
        Assert.Equal(status, result.HttpStatus);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(500)]
    public async Task HttpErrorCannotReportSuccessfulControlOrLog(int status)
    {
        using var client = new XbrdPublisherClient(TimeSpan.FromSeconds(2), new StubHandler(_ => Reply(status, """{"ok":true,"lines":["stale"]}""")));
        Assert.False((await client.RefreshSourceAsync(Endpoint, "quota", default)).Ok);
        Assert.False((await client.GetSourceLogAsync(Endpoint, "quota", 20, default)).Ok);
    }

    [Fact]
    public async Task LogReadClampsLimitAndPreservesLines()
    {
        var handler = new StubHandler(_ => Reply(200, """{"ok":true,"log_path":"fixture.log","lines":["first","",{"event":"second"}],"truncated":true}"""));
        using var client = new XbrdPublisherClient(TimeSpan.FromSeconds(2), handler);
        var result = await client.GetSourceLogAsync(Endpoint, "quota", 2000, default);
        Assert.True(result.Ok);
        Assert.True(result.Truncated);
        Assert.Equal(new[] { "first", "{\"event\":\"second\"}" }, result.Lines);
        Assert.EndsWith("?lines=1000", handler.Requests.Single().Url);
        await client.GetSourceLogAsync(Endpoint, "quota", -10, default);
        Assert.EndsWith("?lines=1", handler.Requests.Last().Url);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("[]")]
    [InlineData("{\"ok\":false,\"error\":\"fixture rejected\"}")]
    public async Task MalformedOrRejectedResponsesShowFailure(string body)
    {
        using var client = new XbrdPublisherClient(TimeSpan.FromSeconds(2), new StubHandler(_ => Reply(200, body)));
        Assert.False((await client.GetHealthAsync(Endpoint, default)).Ok);
        Assert.False((await client.GetSourcesAsync(Endpoint, default)).Ok);
        Assert.False((await client.RefreshSourceAsync(Endpoint, "quota", default)).Ok);
        Assert.False((await client.GetSourceLogAsync(Endpoint, "quota", 5, default)).Ok);
    }

    [Fact]
    public async Task UnreachablePublisherRecoversOnNextRead()
    {
        var attempt = 0;
        using var client = new XbrdPublisherClient(TimeSpan.FromSeconds(2), new StubHandler(_ => ++attempt == 1
            ? throw new HttpRequestException("fixture offline") : Reply(200, "{\"ok\":true}")));
        Assert.False((await client.GetHealthAsync(Endpoint, default)).Ok);
        Assert.True((await client.GetHealthAsync(Endpoint, default)).Ok);
    }

    private static HttpResponseMessage Reply(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    [Theory]
    [InlineData("quota.mem")]
    [InlineData("quota.codex")]
    public async Task OwnedUnitPublishReturnsEvidence(string sourceId)
    {
        var handler = new StubHandler(_ => Reply(200, """{"ok":true,"revision":"fixture-r1","duration_ms":17,"status":"published","skipped":true}"""));
        using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(2), handler);
        var result = await client.PublishNowAsync(sourceId, default);
        Assert.True(result.Ok);
        Assert.Equal("fixture-r1", result.Revision);
        Assert.Equal(17, result.DurationMs);
        Assert.True(result.Skipped);
        Assert.Equal("POST", handler.Requests.Single().Method);
        Assert.EndsWith("/publish-now", handler.Requests.Single().Url);
        Assert.False((await client.PublishNowAsync("other-source", default)).Ok);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(500)]
    public async Task HttpErrorCannotReportSuccessfulUnitPublish(int status)
    {
        using var client = new XbrdUnitControlClient(TimeSpan.FromSeconds(2), new StubHandler(_ => Reply(status, "{\"ok\":true}")));
        Assert.False((await client.PublishNowAsync("quota.mem", default)).Ok);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Url)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(respond(request));
        }
    }
}
