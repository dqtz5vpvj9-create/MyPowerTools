using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.MyPowerTools;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace FileTransfer.Tests;

public sealed class FileTransferModuleTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-module-test-" + Guid.NewGuid().ToString("N"));
    private readonly InMemorySecretStore _secrets = new();
    private readonly RecordingBackground _background = new();
    private FileTransferModule? _module;

    public FileTransferModuleTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync(CancellationToken.None);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task<FileTransferModule> StartAsync()
    {
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", _root, _root, _root, "linux",
            ["secret.store", "background.activity"],
            new Dictionary<string, object> { ["secret.store"] = _secrets, ["background.activity"] = _background });
        _module = new FileTransferModule();
        var initialized = await _module.InitializeAsync(context, CancellationToken.None);
        Assert.True(initialized.Ok);
        return _module;
    }

    private static JsonObject Read(CommandExecutionResult result)
    {
        Assert.True(result.Success, result.Output);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    [Fact]
    public async Task ConfigureRejectsUnknownKeysAndNeverWritesTheSecretToDisk()
    {
        var module = await StartAsync();
        var rejected = await module.ExecuteCommandAsync(new CommandRequest("1", "file-transfer.configure", new JsonObject
        { ["deviceId"] = "pc-test", ["adminPassword"] = "leak-me" }), CancellationToken.None);
        Assert.False(rejected.Success);
        Assert.Contains("adminPassword", rejected.Output);
        var saved = await module.ExecuteCommandAsync(new CommandRequest("2", "file-transfer.configure", new JsonObject
        {
            ["deviceId"] = "pc-test", ["receiveDirectory"] = Path.Combine(_root, "inbox"), ["password"] = "openlist-secret"
        }), CancellationToken.None);
        Assert.True(saved.Success, saved.Output);
        var preferences = await File.ReadAllTextAsync(Path.Combine(_root, "preferences.json"));
        Assert.DoesNotContain("openlist-secret", preferences);
        Assert.DoesNotContain("password", preferences);
        Assert.Contains("pc-test", preferences);
        Assert.Equal("openlist-secret", await _secrets.ReadAsync(SecretReference.Create("file-transfer", "password"), CancellationToken.None));
    }

    [Fact]
    public async Task ReceiveStartTakesOneLeaseAndStopReleasesIt()
    {
        // A loopback address keeps the test off the local tailnet; the module still enforces the key length.
        await File.WriteAllTextAsync(Path.Combine(_root, "preferences.json"), new JsonObject
        {
            ["deviceId"] = "pc-test", ["listenAddress"] = "127.0.0.1", ["receiveDirectory"] = Path.Combine(_root, "inbox")
        }.ToJsonString());
        var module = await StartAsync();
        Read(await module.ExecuteCommandAsync(new CommandRequest("1", "file-transfer.receive.start", new JsonObject()), CancellationToken.None));
        Assert.Equal(1, _background.Begins);
        Assert.Equal(0, _background.Disposes);
        Assert.True(Read(await module.ExecuteCommandAsync(new CommandRequest("2", "file-transfer.inspect", new JsonObject()), CancellationToken.None))["receiving"]!.GetValue<bool>());
        // Starting an already running receiver must not take a second background lease.
        Read(await module.ExecuteCommandAsync(new CommandRequest("3", "file-transfer.receive.start", new JsonObject()), CancellationToken.None));
        Assert.Equal(1, _background.Begins);
        Read(await module.ExecuteCommandAsync(new CommandRequest("4", "file-transfer.receive.stop", new JsonObject()), CancellationToken.None));
        Assert.Equal(1, _background.Disposes);
        Assert.False(Read(await module.ExecuteCommandAsync(new CommandRequest("5", "file-transfer.inspect", new JsonObject()), CancellationToken.None))["receiving"]!.GetValue<bool>());
    }

    [Fact]
    public async Task InterruptedTransferIsReportedAndTheEventSequenceKeepsAdvancing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "history.json"), new JsonObject
        {
            ["sequence"] = 7,
            ["active"] = new JsonObject { ["name"] = "big.iso", ["state"] = "started" },
            ["records"] = new JsonArray(new JsonObject { ["name"] = "old.txt", ["state"] = "completed" })
        }.ToJsonString());
        var module = await StartAsync();
        Assert.True((await module.GetStatusAsync(CancellationToken.None)).EventSeq >= 7);
        var history = Read(await module.ExecuteCommandAsync(new CommandRequest("1", "file-transfer.inspect", new JsonObject()), CancellationToken.None))["history"]!.AsArray();
        Assert.Equal(2, history.Count);
        Assert.Equal("big.iso", history[1]!["name"]!.GetValue<string>());
        Assert.Equal("failed", history[1]!["state"]!.GetValue<string>());
        Assert.Contains("上次传输未完成", history[1]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task CorruptPreferencesFailLoudlyAndAreNeverOverwritten()
    {
        var path = Path.Combine(_root, "preferences.json");
        await File.WriteAllTextAsync(path, "{ not json");
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", _root, _root, _root, "linux",
            ["secret.store", "background.activity"],
            new Dictionary<string, object> { ["secret.store"] = _secrets, ["background.activity"] = _background });
        _module = new FileTransferModule();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => _module.InitializeAsync(context, CancellationToken.None).AsTask());
        Assert.Contains("preferences.json", error.Message);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(Path.Combine(_root, "preferences.invalid.json")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("mpt-unknown-device")]
    public void MultipleManagedAccountsAreNeverClaimedWithoutASavedIdentity(string? savedUsername)
    {
        OpenListSetup.RelayAccount[] users = [new(3, "mpt-other-device"), new(9, "mpt-second-device")];
        var saved = savedUsername is null ? null : new OpenListSetup.RelayAccount(0, savedUsername);
        Assert.Null(OpenListSetup.MatchSavedAccount(users, saved));
        // A mismatched id must not be treated as proof either.
        Assert.Null(OpenListSetup.MatchSavedAccount(users, new OpenListSetup.RelayAccount(7, "mpt-third-device")));
    }

    [Fact]
    public void SavedIdentityIsReusedByIdOrByItsExactUsername()
    {
        OpenListSetup.RelayAccount[] users = [new(3, "mpt-other-device"), new(9, "mpt-second-device")];
        Assert.Equal(users[1], OpenListSetup.MatchSavedAccount(users, new OpenListSetup.RelayAccount(9, "mpt-second-device")));
        // The server may renumber users; the saved exact username is still explicit proof.
        Assert.Equal(users[1], OpenListSetup.MatchSavedAccount(users, new OpenListSetup.RelayAccount(4, "mpt-second-device")));
        Assert.Null(OpenListSetup.MatchSavedAccount(users, new OpenListSetup.RelayAccount(3, "mpt-deleted-device")));
    }

    private sealed class RecordingBackground : IBackgroundActivityService
    {
        private int _begins;
        private int _disposes;
        public int Begins => _begins;
        public int Disposes => _disposes;

        public Task<IDisposable> BeginAsync(string moduleId, string title, bool waitingForPeers, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _begins);
            return Task.FromResult<IDisposable>(new Lease(this));
        }

        private sealed class Lease(RecordingBackground owner) : IDisposable
        {
            public void Dispose() => Interlocked.Increment(ref owner._disposes);
        }
    }
}
