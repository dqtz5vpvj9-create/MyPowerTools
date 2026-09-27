using System.Text.Json.Nodes;
using FileTransfer.Core;

namespace FileTransfer.Tests;

public sealed class BatchSendTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-batch-test-" + Guid.NewGuid().ToString("N"));

    public BatchSendTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private string[] Paths() =>
        [Path.Combine(_root, "a.txt"), Path.Combine(_root, "b.txt"), Path.Combine(_root, "c.txt")];

    [Fact]
    public async Task FailureIsIsolatedAndEveryFileIsStillAttempted()
    {
        var attempts = new List<string>();
        var results = await BatchSend.RunAsync(Paths(), (path, _) =>
        {
            attempts.Add(Path.GetFileName(path));
            return Path.GetFileName(path) == "b.txt" ? Task.FromException(new IOException("磁盘已满")) : Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, attempts);
        Assert.Equal(new[] { true, false, true }, results.Select(item => item.Ok));
        Assert.Equal("磁盘已满", results[1].Message);
        var summary = BatchSend.Summary(results);
        Assert.Contains("成功 2 个", summary);
        Assert.Contains("失败 1 个", summary);
        Assert.Contains("b.txt", summary);
    }

    [Fact]
    public async Task CancellationStopsRemainingFilesButKeepsTheCount()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var results = await BatchSend.RunAsync(Paths(), (_, token) =>
        {
            calls++;
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cancellation.Token);
        Assert.Equal(1, calls);
        Assert.True(results[0].Cancelled);
        Assert.All(results, item => Assert.True(item.Cancelled));
        Assert.Equal("共 3 个文件，成功 0 个，已取消 3 个。", BatchSend.Summary(results));
    }

    [Fact]
    public async Task UnexpectedFailuresSurfaceInsteadOfBeingSwallowed()
    {
        await Assert.ThrowsAsync<NullReferenceException>(() => BatchSend.RunAsync(Paths(),
            (_, _) => throw new NullReferenceException("bug"), CancellationToken.None));
    }

    [Fact]
    public void PathsAcceptsAnArrayOrASinglePathAndRejectsUnusableInput()
    {
        Assert.Equal(new[] { "/a/one.txt", "/a/two.txt" }, BatchSend.Paths(new JsonObject { ["paths"] = new JsonArray("/a/one.txt", "/a/two.txt") }));
        Assert.Equal(new[] { "/a/one.txt" }, BatchSend.Paths(new JsonObject { ["path"] = "/a/one.txt" }));
        Assert.Empty(BatchSend.Paths(new JsonObject { ["paths"] = new JsonArray("", " ") }));
        Assert.Empty(BatchSend.Paths(new JsonObject()));
        var relative = Assert.Throws<ArgumentException>(() => BatchSend.Paths(new JsonObject { ["paths"] = new JsonArray("one.txt") }));
        Assert.Contains("绝对路径", relative.Message);
        var tooMany = new JsonArray(Enumerable.Range(0, BatchSend.MaxFiles + 1).Select(index => (JsonNode?)JsonValue.Create($"/tmp/{index}.txt")).ToArray());
        Assert.Throws<ArgumentException>(() => BatchSend.Paths(new JsonObject { ["paths"] = tooMany }));
    }
}
