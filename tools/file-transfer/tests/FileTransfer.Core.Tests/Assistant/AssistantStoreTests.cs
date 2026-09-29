using System.Text.Json;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;

namespace FileTransfer.Tests;

/// <summary>
/// Store-level behaviour with no network: durability, restart recovery, duplicate names, cancel rules and
/// the JSON shape the Surface and the contract expect.
/// </summary>
public sealed class AssistantStoreTests : IDisposable
{
    private static readonly AssistantIdentity Me = new("desktop-1", "台式机", "conv-1");

    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-assistant-store-" + Guid.NewGuid().ToString("N"));

    public AssistantStoreTests() => System.IO.Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_root)) System.IO.Directory.Delete(_root, true);
    }

    private string StoreDirectory => Path.Combine(_root, "assistant");

    private AssistantStore NewStore() => new(StoreDirectory);

    private string SourceFile(string name, string content, string? folder = null)
    {
        var path = Path.Combine(_root, folder ?? "source", name);
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static AssistantItem Item(string sender, AssistantItemState state, string? name = "a.bin") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = name is null ? AssistantItemKind.Text : AssistantItemKind.File,
        Name = name,
        Text = name is null ? "文字" : null,
        Size = name is null ? 6 : 1,
        CreatedAt = DateTimeOffset.UtcNow,
        SenderDeviceId = sender,
        SenderName = sender,
        State = state
    };

    [Fact]
    public async Task ConversationOriginsSurviveRestartAndAmbiguousHistoryIsNotGuessed()
    {
        var store = NewStore();
        var shared = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForText("shared"), CancellationToken.None));
        var direct = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForText("private", "phone-1"), CancellationToken.None));
        Assert.Equal("shared:conv-1", AssistantConversations.Key(shared, Me.DeviceId));
        Assert.Equal("device:phone-1", AssistantConversations.Key(direct, Me.DeviceId));
        var sharedIncoming = await store.AdoptAsync(Me, Item("phone-1", AssistantItemState.Available, null) with
        { ConversationId = "original-conversation", Provenance = AssistantConversations.DirectShared }, CancellationToken.None);
        var privateIncoming = await store.AdoptAsync(Me, Item("phone-1", AssistantItemState.Available, null) with
        { TargetDeviceId = Me.DeviceId, Provenance = AssistantConversations.PairedInbox }, CancellationToken.None);
        var legacy = Item("phone-1", AssistantItemState.Available, null) with { TargetDeviceId = Me.DeviceId };
        await store.MutateAsync(state => state.Add(legacy), CancellationToken.None);
        var after = await NewStore().ConfigureAsync(Me with { ConversationId = "new-conversation" }, CancellationToken.None);
        Assert.Equal("shared:conv-1", AssistantConversations.Key(after.Find(shared.Id)!, Me.DeviceId));
        Assert.Equal("shared:original-conversation", AssistantConversations.Key(after.Find(sharedIncoming.Id)!, Me.DeviceId));
        Assert.Equal("device:phone-1", AssistantConversations.Key(after.Find(privateIncoming.Id)!, Me.DeviceId));
        Assert.Equal("history", AssistantConversations.Key(after.Find(legacy.Id)!, Me.DeviceId));
        Assert.Equal(5, after.Items.Count);
    }

    [Fact]
    public async Task LegacySingleDraftMigratesUsingItsOriginalConversationBeforeJoiningAnother()
    {
        var store = NewStore();
        await store.ConfigureAsync(Me, CancellationToken.None);
        var state = new AssistantState { Identity = Me, Preferences = new AssistantPreferences { DraftText = "keep" } };
        await store.SaveAsync(state, CancellationToken.None);
        var restored = await NewStore().ConfigureAsync(Me with { ConversationId = "changed" }, CancellationToken.None);
        Assert.Equal("keep", restored.Drafts["shared:conv-1"].DraftText);
        Assert.False(restored.Drafts.ContainsKey("shared:changed"));
    }

    [Fact]
    public async Task EnqueueIsDurableBeforeItReturnsAndSurvivesRestart()
    {
        var source = SourceFile("报表 final.xlsx", "payload-1");
        var store = NewStore();
        var text = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForText("你好，这是一条消息"), CancellationToken.None));
        var file = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForPaths([source]), CancellationToken.None));

        Assert.Equal(AssistantItemKind.Text, text.Kind);
        Assert.Equal(AssistantItemState.Queued, text.State);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("你好，这是一条消息"), text.Size);
        Assert.Null(store.GetPayloadPath(text));
        Assert.Equal(AssistantItemKind.File, file.Kind);
        Assert.Equal(new FileInfo(source).Length, file.Size);
        Assert.True(File.Exists(source));   // an attachment is copied, never moved away from the user

        // A new instance is exactly what a process restart sees.
        var restarted = NewStore();
        var state = await restarted.LoadAsync(CancellationToken.None);
        Assert.Equal(2, state.Items.Count);
        Assert.Equal(Me, state.Identity);
        var restored = state.Find(file.Id)!;
        Assert.Equal(AssistantItemKind.File, restored.Kind);
        Assert.Equal(AssistantItemState.Queued, restored.State);
        Assert.Equal("payload-1", await File.ReadAllTextAsync(restarted.GetPayloadPath(restored)!));
        Assert.Equal(restored.LocalPath, restarted.GetPayloadPath(restored));
        Assert.Equal("你好，这是一条消息", state.Find(text.Id)!.Text);
    }

    [Fact]
    public async Task DuplicateFileNamesNeverOverwriteEachOther()
    {
        var first = SourceFile("照片.png", "first-image", "one");
        var second = SourceFile("照片.png", "second-image-longer", "two");
        var store = NewStore();
        var items = await store.EnqueueAsync(Me, AssistantDraft.ForPaths([first, second]), CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(2, items.Select(item => item.Id).Distinct().Count());
        Assert.All(items, item => Assert.Equal(AssistantItemKind.Image, item.Kind));
        Assert.All(items, item => Assert.Equal("照片.png", item.Name));
        Assert.Equal("first-image", await File.ReadAllTextAsync(store.GetPayloadPath(items[0])!));
        Assert.Equal("second-image-longer", await File.ReadAllTextAsync(store.GetPayloadPath(items[1])!));
        Assert.NotEqual(store.GetInboxPath(items[0]), store.GetInboxPath(items[1]));
    }

    [Fact]
    public async Task RestartReturnsInFlightEntriesToRetryableStates()
    {
        var store = NewStore();
        var state = await store.LoadAsync(CancellationToken.None);
        state.Items.Add(Item(Me.DeviceId, AssistantItemState.Sending));
        state.Items.Add(Item("phone-1", AssistantItemState.Downloading, "b.png"));
        await store.SaveAsync(state, CancellationToken.None);

        var restarted = await NewStore().LoadAsync(CancellationToken.None);
        Assert.Equal(AssistantItemState.Queued, restarted.Items[0].State);
        Assert.Equal(AssistantItemState.Stored, restarted.Items[1].State);
    }

    [Fact]
    public async Task CancelIsRefusedOnceADeviceConfirmedAndNeverDeletesThePayload()
    {
        var store = NewStore();
        var queued = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForPaths([SourceFile("report.pdf", "pdf")]), CancellationToken.None));
        var state = await store.LoadAsync(CancellationToken.None);

        Assert.True(state.TryCancel(queued.Id));
        Assert.Equal(AssistantItemState.Cancelled, queued.State);
        Assert.True(File.Exists(store.GetPayloadPath(queued)!));

        var confirmed = Item("phone-1", AssistantItemState.Stored, "b.png");
        confirmed.Receipts.Add(new AssistantReceipt
        {
            ItemId = confirmed.Id,
            DeviceId = "desktop-1",
            DeviceName = "台式机",
            SavedAt = DateTimeOffset.UtcNow,
            Bytes = 1
        });
        state.Items.Add(confirmed);
        Assert.False(state.TryCancel(confirmed.Id));
        Assert.Equal(AssistantItemState.Stored, confirmed.State);
    }

    [Fact]
    public async Task ConversationIsNeverTrimmedToTheLegacyFiftyRecordCap()
    {
        var store = NewStore();
        for (var i = 0; i < 60; i++)
            await store.EnqueueAsync(Me, AssistantDraft.ForText($"消息 {i}"), CancellationToken.None);
        var restarted = await NewStore().LoadAsync(CancellationToken.None);
        Assert.Equal(60, restarted.Items.Count);
        Assert.Equal("消息 0", restarted.Items[0].Text);
    }

    [Fact]
    public void ItemJsonMatchesTheContractAndTheManifestHidesLocalMetadata()
    {
        var item = new AssistantItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = AssistantItemKind.Image,
            Name = "照片.png",
            Size = 12,
            CreatedAt = DateTimeOffset.UtcNow,
            SenderDeviceId = "phone-1",
            SenderName = "手机",
            TargetDeviceId = "desktop-1",
            State = AssistantItemState.Stored,
            BytesDone = 4,
            LocalPath = "/tmp/not-published",
            Error = "hidden",
            Receipts = [new AssistantReceipt { ItemId = "x", DeviceId = "desktop-1", DeviceName = "台式机", SavedAt = DateTimeOffset.UtcNow, Bytes = 12 }]
        };

        var json = JsonSerializer.Serialize(item, DirectTransfer.Json);
        var node = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("image", node["kind"]!.GetValue<string>());
        Assert.Equal("stored", node["state"]!.GetValue<string>());
        Assert.Equal("desktop-1", node["targetDeviceId"]!.GetValue<string>());
        Assert.Equal(4, node["bytesDone"]!.GetValue<long>());

        var round = JsonSerializer.Deserialize<AssistantItem>(json, DirectTransfer.Json)!;
        Assert.Equal(item.Id, round.Id);
        Assert.Equal(item.Kind, round.Kind);
        Assert.Equal(item.State, round.State);
        Assert.Equal(item.TargetDeviceId, round.TargetDeviceId);
        Assert.Single(round.Receipts);

        var manifest = JsonSerializer.Serialize(item.ToManifest(), AssistantJson.Options);
        Assert.DoesNotContain("localPath", manifest);
        Assert.DoesNotContain("receipts", manifest);
        Assert.DoesNotContain("bytesDone", manifest);
        Assert.DoesNotContain("\"state\"", manifest);
        Assert.DoesNotContain("\"error\"", manifest);
        Assert.Contains("\"targetDeviceId\":\"desktop-1\"", manifest);

        var text = new AssistantItem { Id = Guid.NewGuid().ToString("N"), Kind = AssistantItemKind.Text, Text = "你好", CreatedAt = DateTimeOffset.UtcNow, SenderDeviceId = "phone-1", SenderName = "手机" };
        Assert.Contains("\"kind\":\"text\"", JsonSerializer.Serialize(text, DirectTransfer.Json));
    }

    [Fact]
    public async Task CorruptConversationFileIsReportedAndNeverRewritten()
    {
        System.IO.Directory.CreateDirectory(StoreDirectory);
        var path = Path.Combine(StoreDirectory, "assistant.json");
        await File.WriteAllTextAsync(path, "{not json");
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadAsync(CancellationToken.None));
        Assert.Equal("{not json", await File.ReadAllTextAsync(path));

        await File.WriteAllTextAsync(path, """{"version":1,"items":[{"id":"not-a-guid","kind":"file","name":"a.bin"}],"knownRemoteIds":[]}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => NewStore().LoadAsync(CancellationToken.None));
        Assert.Contains("not-a-guid", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task RestartSweepsOnlyTheToolsOwnPartialFiles()
    {
        var store = NewStore();
        var item = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForPaths([SourceFile("a.bin", "data")]), CancellationToken.None));
        var payload = Path.GetDirectoryName(store.GetPayloadPath(item)!)!;
        var partial = Path.Combine(payload, TransferFiles.PartialPrefix + Guid.NewGuid().ToString("N") + TransferFiles.PartialSuffix);
        await File.WriteAllTextAsync(partial, "half");
        var userFile = Path.Combine(payload, "keep.txt");
        await File.WriteAllTextAsync(userFile, "user");

        Assert.Equal(1, NewStore().SweepPartials());
        Assert.False(File.Exists(partial));
        Assert.True(File.Exists(userFile));

        var second = Path.Combine(payload, TransferFiles.PartialPrefix + Guid.NewGuid().ToString("N") + TransferFiles.PartialSuffix);
        await File.WriteAllTextAsync(second, "half");
        await NewStore().LoadAsync(CancellationToken.None);
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task OpenTargetReturnsInlineTextAndAsksToDownloadMissingFiles()
    {
        var store = NewStore();
        var text = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForText("hello"), CancellationToken.None));
        var inline = AssistantContent.OpenTarget(text);
        Assert.Equal("hello", inline.Text);
        Assert.Null(inline.Path);
        Assert.False(inline.NeedsDownload);

        var file = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForPaths([SourceFile("b.bin", "bytes")]), CancellationToken.None));
        var local = AssistantContent.OpenTarget(file);
        Assert.Equal(file.LocalPath, local.Path);
        Assert.False(local.NeedsDownload);

        Assert.True(AssistantContent.OpenTarget(file with { LocalPath = null }).NeedsDownload);
    }

    [Fact]
    public async Task StoreNeverTouchesUnrelatedFilesInItsDirectory()
    {
        System.IO.Directory.CreateDirectory(StoreDirectory);
        var history = Path.Combine(StoreDirectory, "history.json");
        await File.WriteAllTextAsync(history, """{"records":[]}""");
        var secret = Path.Combine(StoreDirectory, "secret.json");
        await File.WriteAllTextAsync(secret, "keep-me");

        var store = NewStore();
        await store.EnqueueAsync(Me, AssistantDraft.ForText("hello"), CancellationToken.None);
        await store.LoadAsync(CancellationToken.None);
        Assert.Equal("""{"records":[]}""", await File.ReadAllTextAsync(history));
        Assert.Equal("keep-me", await File.ReadAllTextAsync(secret));
    }

    [Fact]
    public async Task FailedLiveSaveRestoresTheLastCommittedGeneration()
    {
        var store = NewStore();
        var item = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForText("已提交"), CancellationToken.None));
        var blocker = Path.Combine(StoreDirectory, "assistant.json.tmp");
        System.IO.Directory.CreateDirectory(blocker);

        // What a module edit looks like: the caller mutates the live snapshot, then the save fails.
        var state = await store.LoadAsync(CancellationToken.None);
        state.Find(item.Id)!.State = AssistantItemState.Failed;
        state.Find(item.Id)!.Error = "只在内存里";
        await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(state, CancellationToken.None));

        // Same instance and a restarted instance both show the last committed generation: no ghosts.
        Assert.Equal(AssistantItemState.Queued, item.State);
        Assert.Null(item.Error);
        var restarted = await NewStore().LoadAsync(CancellationToken.None);
        Assert.Equal(AssistantItemState.Queued, restarted.Find(item.Id)!.State);
        Assert.Null(restarted.Find(item.Id)!.Error);

        System.IO.Directory.Delete(blocker);
        state.Find(item.Id)!.State = AssistantItemState.Failed;
        await store.SaveAsync(state, CancellationToken.None);
        Assert.Equal(AssistantItemState.Failed, (await NewStore().LoadAsync(CancellationToken.None)).Find(item.Id)!.State);
    }

    [Fact]
    public async Task AdoptKeepsDirectlyReceivedEntriesAndDedupesById()
    {
        var store = NewStore();
        var saved = SourceFile("direct.bin", "direct", "direct");
        var received = new AssistantItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = AssistantItemKind.File,
            Name = "direct.bin",
            Size = new FileInfo(saved).Length,
            CreatedAt = DateTimeOffset.UtcNow,
            SenderDeviceId = "phone-1",
            SenderName = "手机",
            TargetDeviceId = Me.DeviceId,
            LocalPath = saved
        };
        var adopted = await store.AdoptAsync(Me, received, CancellationToken.None);
        Assert.Equal(AssistantItemState.Available, adopted.State);
        Assert.Equal(saved, adopted.LocalPath);
        Assert.Null(adopted.ReceiptAt);   // the receipt is written by the next sync, never by the adoption itself

        // A retried direct transfer reuses the id: it must not create a second message.
        var again = await store.AdoptAsync(Me, received with { LocalPath = null }, CancellationToken.None);
        Assert.Equal(adopted.Id, again.Id);
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Items);

        await Assert.ThrowsAsync<ArgumentException>(() => store.AdoptAsync(Me,
            received with { Id = Guid.NewGuid().ToString("N"), LocalPath = null }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.AdoptAsync(Me,
            received with { Id = Guid.NewGuid().ToString("N"), SenderDeviceId = Me.DeviceId }, CancellationToken.None));
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Items);
    }

    [Fact]
    public async Task AdoptUpgradesAnEntryThatWasOnlyKnownFromTheRelay()
    {
        var store = NewStore();
        // What a manifest-only pull leaves behind: metadata, no local content.
        var known = Item("phone-1", AssistantItemState.Stored, "later.bin");
        var state = await store.LoadAsync(CancellationToken.None);
        state.Add(known);
        await store.SaveAsync(state, CancellationToken.None);
        Assert.False(AssistantContent.HasLocalContent(known));   // metadata is not "saved"
        await Assert.ThrowsAsync<ArgumentException>(() => store.AdoptAsync(Me,
            known with { LocalPath = null }, CancellationToken.None));

        var saved = SourceFile("later.bin", "direct-payload", "inbox");
        var adopted = await store.AdoptAsync(Me, known with { LocalPath = saved, Size = new FileInfo(saved).Length }, CancellationToken.None);

        Assert.Equal(known.Id, adopted.Id);
        Assert.Equal(AssistantItemState.Available, adopted.State);
        Assert.Equal(saved, adopted.LocalPath);
        Assert.True(AssistantContent.HasLocalContent(adopted));
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Items);   // upgraded, not duplicated
        Assert.Equal(AssistantItemState.Available, (await NewStore().LoadAsync(CancellationToken.None)).Find(known.Id)!.State);

        // A cancelled entry is never revived by a late direct delivery.
        var cancelled = Item("phone-1", AssistantItemState.Cancelled, "cancelled.bin");
        var other = SourceFile("cancelled.bin", "unwanted", "inbox");
        state = await store.LoadAsync(CancellationToken.None);
        state.Add(cancelled);
        await store.SaveAsync(state, CancellationToken.None);
        await store.AdoptAsync(Me, cancelled with { LocalPath = other, Size = new FileInfo(other).Length }, CancellationToken.None);
        Assert.Equal(AssistantItemState.Cancelled, (await store.LoadAsync(CancellationToken.None)).Find(cancelled.Id)!.State);
    }

    [Fact]
    public async Task EnqueueRejectsEmptyTextAndMissingFilesWithoutLeavingAnythingBehind()
    {
        var store = NewStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueAsync(Me, AssistantDraft.ForText("   "), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.EnqueueAsync(Me, new AssistantDraft(), CancellationToken.None));
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.EnqueueAsync(Me,
            AssistantDraft.ForPaths([Path.Combine(_root, "missing.bin")]), CancellationToken.None));
        // A mixed send whose second attachment is missing must not keep the text or the first attachment.
        var present = SourceFile("present.bin", "present");
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.EnqueueAsync(Me,
            AssistantDraft.ForContent("说明", [present, Path.Combine(_root, "missing.bin")]), CancellationToken.None));

        var state = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(state.Items);
        Assert.Null(state.Identity);
        Assert.Empty((await NewStore().LoadAsync(CancellationToken.None)).Items);
        Assert.True(File.Exists(present));
    }

    [Fact]
    public async Task MixedDraftSendsOneTextAndEveryAttachmentInOneAtomicBatch()
    {
        var first = SourceFile("照片.png", "image-bytes", "mix-a");
        var second = SourceFile("说明.pdf", "pdf-bytes", "mix-b");
        var store = NewStore();
        var items = await store.EnqueueAsync(Me, AssistantDraft.ForContent("一次发送：文字 + 附件", [first, second]), CancellationToken.None);

        Assert.Equal(3, items.Count);
        Assert.Equal(AssistantItemKind.Text, items[0].Kind);
        Assert.Equal("一次发送：文字 + 附件", items[0].Text);
        Assert.Equal(AssistantItemKind.Image, items[1].Kind);
        Assert.Equal(AssistantItemKind.File, items[2].Kind);
        Assert.Null(store.GetPayloadPath(items[0]));
        Assert.Equal("image-bytes", await File.ReadAllTextAsync(store.GetPayloadPath(items[1])!));
        Assert.Equal("pdf-bytes", await File.ReadAllTextAsync(store.GetPayloadPath(items[2])!));
        Assert.Equal(3, items.Select(item => item.Id).Distinct().Count());

        // Every id is durable before the call returned.
        var restarted = await NewStore().LoadAsync(CancellationToken.None);
        Assert.Equal(3, restarted.Items.Count);
        Assert.Equal(items.Select(item => item.Id).OrderBy(id => id), restarted.Items.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task FailedStateWriteRollsBackMemoryDiskAndRetriesExactlyOnce()
    {
        var source = SourceFile("atomic.bin", "atomic-bytes");
        var store = NewStore();
        // A directory where the atomic temporary file must be created makes the commit fail deterministically.
        var blocker = Path.Combine(StoreDirectory, "assistant.json.tmp");
        System.IO.Directory.CreateDirectory(blocker);

        await Assert.ThrowsAnyAsync<Exception>(() => store.EnqueueAsync(Me, AssistantDraft.ForContent("幽灵?", [source]), CancellationToken.None));

        // Same instance: no ghost entries, no committed identity.
        var state = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(state.Items);
        Assert.Null(state.Identity);
        // Restart instance: nothing was left on disk either.
        Assert.Empty((await NewStore().LoadAsync(CancellationToken.None)).Items);
        // The user's original file is untouched and no staging directory survived the rollback.
        Assert.Equal("atomic-bytes", await File.ReadAllTextAsync(source));
        Assert.Empty(System.IO.Directory.GetDirectories(store.PayloadRoot));

        System.IO.Directory.Delete(blocker);
        var retried = await store.EnqueueAsync(Me, AssistantDraft.ForContent("幽灵?", [source]), CancellationToken.None);
        Assert.Equal(2, retried.Count);
        Assert.Equal(2, (await NewStore().LoadAsync(CancellationToken.None)).Items.Count);
    }

    [Fact]
    public async Task FailedWriteRollsBackACancelToo()
    {
        var store = NewStore();
        var item = Assert.Single(await store.EnqueueAsync(Me, AssistantDraft.ForText("保持不变"), CancellationToken.None));
        var blocker = Path.Combine(StoreDirectory, "assistant.json.tmp");
        System.IO.Directory.CreateDirectory(blocker);

        await Assert.ThrowsAnyAsync<Exception>(() => store.MutateAsync(state => state.TryCancel(item.Id), CancellationToken.None));

        Assert.Equal(AssistantItemState.Queued, item.State);   // the live instance was rolled back
        Assert.Equal(AssistantItemState.Queued, (await store.LoadAsync(CancellationToken.None)).Find(item.Id)!.State);
        System.IO.Directory.Delete(blocker);
        Assert.Equal(AssistantItemState.Queued, (await NewStore().LoadAsync(CancellationToken.None)).Find(item.Id)!.State);
    }
}
