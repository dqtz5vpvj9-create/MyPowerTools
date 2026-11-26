using FileTransfer.Core;

namespace FileTransfer.Tests;

public sealed class PublishQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-publish-test-" + Guid.NewGuid().ToString("N"));

    public PublishQueueTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task EntriesSurviveAReloadAndAreRemovedOncePublished()
    {
        var queue = new PublishQueue(_root);
        Assert.Empty(await queue.LoadAsync(CancellationToken.None));
        await queue.AddAsync(Path.Combine(_root, "a.txt"), "a.txt", CancellationToken.None);
        await queue.AddAsync(Path.Combine(_root, "b.txt"), "b.txt", CancellationToken.None);
        var entries = await new PublishQueue(_root).LoadAsync(CancellationToken.None);
        Assert.Equal(new[] { "a.txt", "b.txt" }, entries.Select(entry => entry.Name));
        await queue.RemoveAsync(Path.Combine(_root, "a.txt"), CancellationToken.None);
        Assert.Equal("b.txt", (await queue.LoadAsync(CancellationToken.None)).Single().Name);
        await queue.RemoveAsync(Path.Combine(_root, "b.txt"), CancellationToken.None);
        Assert.Empty(await queue.LoadAsync(CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "publish-pending.json")));
    }

    [Fact]
    public async Task TheSameFileIsQueuedOnlyOnce()
    {
        var queue = new PublishQueue(_root);
        var path = Path.Combine(_root, "same.txt");
        await queue.AddAsync(path, "same.txt", CancellationToken.None);
        await queue.AddAsync(path, "same.txt", CancellationToken.None);
        Assert.Single(await queue.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorruptQueueIsReportedAndLeftUntouched()
    {
        var path = Path.Combine(_root, "publish-pending.json");
        await File.WriteAllTextAsync(path, "{ not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => new PublishQueue(_root).LoadAsync(CancellationToken.None));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task QueueKeepsTheNewestEntriesWithinTheCap()
    {
        var queue = new PublishQueue(_root);
        for (var index = 0; index < PublishQueue.MaxEntries + 5; index++)
            await queue.AddAsync(Path.Combine(_root, $"file-{index}.txt"), $"file-{index}.txt", CancellationToken.None);
        var entries = await queue.LoadAsync(CancellationToken.None);
        Assert.Equal(PublishQueue.MaxEntries, entries.Count);
        Assert.Equal("file-5.txt", entries[0].Name);
    }
}
