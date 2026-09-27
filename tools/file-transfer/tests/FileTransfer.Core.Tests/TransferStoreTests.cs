using System.Text.Json.Nodes;
using FileTransfer.Core;

namespace FileTransfer.Tests;

public sealed class TransferStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-store-test-" + Guid.NewGuid().ToString("N"));

    public TransferStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    private static JsonObject Record(string name, string state) => new()
    { ["name"] = name, ["done"] = 0, ["total"] = 0, ["state"] = state, ["message"] = "", ["time"] = DateTimeOffset.UtcNow.ToString("O") };

    [Fact]
    public async Task HistoryAndSequenceSurviveARestart()
    {
        var store = new TransferStore(_root);
        Assert.Equal(0, (await store.LoadAsync(CancellationToken.None)).Sequence);
        Assert.Equal(1, store.NextSequence());
        await store.SaveAsync([Record("done.txt", "completed")], Record("big.iso", "started"), CancellationToken.None);

        var reopened = new TransferStore(_root);
        var snapshot = await reopened.LoadAsync(CancellationToken.None);
        Assert.Equal(1, snapshot.Sequence);
        Assert.Equal(2, reopened.NextSequence());
        Assert.Equal("done.txt", snapshot.Records.Single()["name"]!.GetValue<string>());
        Assert.Equal("big.iso", snapshot.Active!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task HistoryIsCappedAtTheNewestFiftyRecords()
    {
        var store = new TransferStore(_root);
        await store.SaveAsync(Enumerable.Range(0, 60).Select(index => Record($"file-{index}.txt", "completed")).ToArray(),
            null, CancellationToken.None);
        var snapshot = await new TransferStore(_root).LoadAsync(CancellationToken.None);
        Assert.Equal(TransferStore.MaxRecords, snapshot.Records.Count);
        Assert.Equal("file-10.txt", snapshot.Records[0]["name"]!.GetValue<string>());
        Assert.Equal("file-59.txt", snapshot.Records[^1]["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task CorruptHistoryIsReportedAndLeftUntouched()
    {
        var path = Path.Combine(_root, "history.json");
        await File.WriteAllTextAsync(path, "{ not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => new TransferStore(_root).LoadAsync(CancellationToken.None));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(_root, "history.invalid.json")));
    }

    [Fact]
    public async Task UnreadableRecordsAreReportedInsteadOfDropped()
    {
        var path = Path.Combine(_root, "history.json");
        var content = "{\"sequence\":3,\"records\":[{\"name\":\"ok.txt\",\"state\":\"completed\"},{\"name\":\"no-state\"}],\"active\":null}";
        await File.WriteAllTextAsync(path, content);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new TransferStore(_root).LoadAsync(CancellationToken.None));
        Assert.Contains("history.json", error.Message);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }
}
