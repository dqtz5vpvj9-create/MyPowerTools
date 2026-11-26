using System.Net.Http.Json;
using System.Text.Json;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;

namespace FileTransfer.Tests;

// Explicit opt-in to an official binary; no network download or daemon during ordinary unit tests.
public sealed class OpenListIntegrationTests
{
    [OfficialOpenListFact]
    public async Task OfficialServerInitializesUploadsListsDownloadsAndStops()
    {
        var root = TestRoot("mpt-openlist-test-");
        try
        {
            await using var relay = await OfficialOpenListRelay.StartAsync(root, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var token = timeout.Token;
            using var http = await OfficialOpenListRelay.CreateAdminClientAsync(relay.ServerUrl, relay.AdminPassword, token);

            // A dedicated account owned by another MPT device must survive this device's setup untouched.
            using (var other = await http.PostAsJsonAsync("/api/admin/user/create", new
            {
                username = "mpt-other-device", password = "other-device-password", base_path = "other", role = 0,
                permission = (1 << 3) | (1 << 8) | (1 << 9), disabled = false
            }, token))
            {
                Assert.Equal(200, (await other.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("code").GetInt32());
            }
            // The saved identity is reused, and a request without one creates a fresh random account.
            Assert.Equal(relay.Account, await OpenListSetup.ConnectAsync(relay.ServerUrl, relay.AdminPassword,
                new Uri(relay.ServerUrl, "/dav/mpt"), relay.AdminPassword, relay.Account, token));
            var fresh = await OpenListSetup.ConnectAsync(relay.ServerUrl, relay.AdminPassword,
                new Uri(relay.ServerUrl, "/dav/mpt"), relay.AdminPassword, null, token);
            Assert.NotEqual(relay.Account.Username, fresh.Username);
            Assert.NotEqual("mpt-other-device", fresh.Username);
            using (var otherLogin = await http.PostAsJsonAsync("/api/auth/login", new { username = "mpt-other-device", password = "other-device-password" }, token))
            {
                Assert.Equal(200, (await otherLogin.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("code").GetInt32());
            }

            using var cloud = new OpenListClient(relay.DavUrl, relay.Account.Username, relay.AdminPassword);
            var path = Path.Combine(root, "网盘 round trip.txt");
            await File.WriteAllTextAsync(path, "文件互传 integration\n", token);
            var item = await cloud.UploadAsync(path, "phone", "desktop", null, token);
            // An incomplete transfer folder must not become a downloadable inbox entry.
            Directory.CreateDirectory(Path.Combine(relay.StoragePath, "phone", Guid.NewGuid().ToString("N")));
            var items = await cloud.ListAsync("phone", token);
            Assert.Single(items);
            Assert.Equal(item, items[0]);
            var saved = await cloud.DownloadAsync("phone", items[0], Path.Combine(root, "received"), null, token);
            Assert.Equal(await File.ReadAllBytesAsync(path, token), await File.ReadAllBytesAsync(saved, token));
            Assert.Null(await relay.Runtime.InitializeAdminAsync(token));
            await relay.Runtime.StopAsync();
            Assert.False(relay.Runtime.Running);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// The whole assistant chain against the real official service and its Local storage driver: one compose
    /// action (text + Chinese-named attachment) plus a file addressed to the phone, published while the
    /// receiver is offline, then pulled, downloaded, acknowledged by a real receipt, and reopened after a
    /// receiver restart.
    /// </summary>
    [OfficialOpenListFact]
    public async Task OfficialServerCarriesTheAssistantConversationBetweenTwoDevices()
    {
        var root = TestRoot("mpt-openlist-assistant-");
        try
        {
            await using var relay = await OfficialOpenListRelay.StartAsync(root, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var token = timeout.Token;

            var conversation = "conv" + Guid.NewGuid().ToString("N");
            var senderIdentity = new AssistantIdentity("desktop-a", "台式机 A", conversation);
            var receiverIdentity = new AssistantIdentity("phone-b", "手机 B", conversation);
            using var senderClient = new OpenListClient(relay.DavUrl, relay.Account.Username, relay.AdminPassword);
            var senderStore = new AssistantStore(Path.Combine(root, "device-sender", "assistant"));
            var senderSync = new AssistantSync(senderStore, senderClient);

            const string message = "这是一条发到所有设备的文字";
            const string content = "真实官方 OpenList 助手往返\n中文内容 ✔\n";
            var fileName = "会议纪要 2026-09-28.txt";
            var source = Path.Combine(root, "outgoing", fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            await File.WriteAllTextAsync(source, content, token);
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var targetedSource = Path.Combine(root, "outgoing", "只给手机.bin");
            await File.WriteAllBytesAsync(targetedSource, bytes, token);

            // One compose action carrying both text and the attachment, plus a file addressed to the phone.
            var composed = await senderStore.EnqueueAsync(senderIdentity, AssistantDraft.ForContent(message, [source]), token);
            Assert.Equal(2, composed.Count);
            var targeted = Assert.Single(await senderStore.EnqueueAsync(senderIdentity,
                AssistantDraft.ForPaths([targetedSource], receiverIdentity.DeviceId), token));

            var publish = await senderSync.SyncAsync(senderIdentity, token);
            Assert.Equal(3, publish.Published);
            Assert.Equal(0, publish.Failed);
            Assert.False(publish.HasMore);
            Assert.Null(publish.RetryAfter);

            // The receiver is offline: everything reached the relay, nothing is confirmed yet.
            var senderState = await senderStore.LoadAsync(token);
            Assert.Equal(3, senderState.Items.Count);
            Assert.All(senderState.Items, item => Assert.Equal(AssistantItemState.Stored, item.State));
            Assert.All(senderState.Items, item => Assert.Empty(item.Receipts));
            Assert.True(File.Exists(relay.DiskPath($"assistant/{conversation}/{composed[1].Id}/manifest.json")));
            Assert.True(File.Exists(relay.DiskPath($"assistant/{conversation}/{composed[1].Id}/payload")));
            Assert.False(File.Exists(relay.DiskPath($"assistant/{conversation}/{composed[0].Id}/payload")));
            var fileManifest = JsonSerializer.Deserialize<AssistantManifest>(
                await File.ReadAllBytesAsync(relay.DiskPath($"assistant/{conversation}/{composed[1].Id}/manifest.json"), token),
                AssistantJson.Options)!;
            Assert.Equal(fileName, fileManifest.Name);
            Assert.Equal(AssistantItemKind.File, fileManifest.Kind);
            var textManifest = JsonSerializer.Deserialize<AssistantManifest>(
                await File.ReadAllBytesAsync(relay.DiskPath($"assistant/{conversation}/{composed[0].Id}/manifest.json"), token),
                AssistantJson.Options)!;
            Assert.Equal(message, textManifest.Text);

            // The receiver comes online with its own independent data root.
            using var receiverClient = new OpenListClient(relay.DavUrl, relay.Account.Username, relay.AdminPassword);
            var receiverStore = new AssistantStore(Path.Combine(root, "device-receiver", "assistant"));
            var receiverSync = new AssistantSync(receiverStore, receiverClient);
            var pull = await receiverSync.SyncAsync(receiverIdentity, token);
            Assert.Equal(3, pull.Received);
            Assert.Equal(0, pull.Failed);
            Assert.Equal(2, pull.Downloaded);   // both files; text needs no payload
            Assert.Equal(3, pull.ReceiptsWritten);

            var received = await receiverStore.LoadAsync(token);
            Assert.Equal(3, received.Items.Count);   // no duplicates: one entry per immutable id
            var text = received.Find(composed[0].Id)!;
            Assert.Equal(AssistantItemKind.Text, text.Kind);
            Assert.Equal(AssistantItemState.Available, text.State);
            Assert.Equal(message, text.Text);
            Assert.Null(text.LocalPath);
            var file = received.Find(composed[1].Id)!;
            Assert.Equal(AssistantItemState.Available, file.State);
            Assert.Equal(fileName, Path.GetFileName(file.LocalPath!));
            Assert.Equal(content, await File.ReadAllTextAsync(file.LocalPath!, token));
            var targetedIncoming = received.Find(targeted.Id)!;
            Assert.Equal(AssistantItemState.Available, targetedIncoming.State);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(targetedIncoming.LocalPath!, token));
            Assert.All(received.Items, item => Assert.NotNull(item.ReceiptAt));
            // Per-device receipt files really exist in the relay namespace.
            Assert.True(File.Exists(relay.DiskPath($"assistant/{conversation}/{composed[1].Id}/receipts/phone-b.json")));

            // The real receipt confirms the sender: the targeted send becomes "delivered", while a self send
            // stays "stored" (synced to the relay) and shows the concrete device that saved it.
            var confirm = await senderSync.SyncAsync(senderIdentity, token);
            Assert.Equal(3, confirm.ReceiptsChecked);
            senderState = await senderStore.LoadAsync(token);
            Assert.All(senderState.Items, item =>
                Assert.Contains(item.Receipts, receipt => receipt.DeviceId == receiverIdentity.DeviceId));
            Assert.Equal(AssistantItemState.Delivered, senderState.Find(targeted.Id)!.State);
            Assert.Equal(AssistantItemState.Stored, senderState.Find(composed[1].Id)!.State);

            // The receiver restarts: a brand-new store over the same root still opens the content locally,
            // and a fresh sync neither re-pulls nor re-downloads anything.
            var restartedStore = new AssistantStore(Path.Combine(root, "device-receiver", "assistant"));
            var restarted = await restartedStore.LoadAsync(token);
            Assert.Equal(3, restarted.Items.Count);
            var reopened = restarted.Find(composed[1].Id)!;
            Assert.Equal(AssistantItemState.Available, reopened.State);
            var open = AssistantContent.OpenTarget(reopened);
            Assert.False(open.NeedsDownload);
            Assert.Equal(content, await File.ReadAllTextAsync(open.Path!, token));
            using var restartedClient = new OpenListClient(relay.DavUrl, relay.Account.Username, relay.AdminPassword);
            var restartedSync = new AssistantSync(restartedStore, restartedClient);
            var second = await restartedSync.SyncAsync(receiverIdentity, token);
            Assert.Equal(0, second.Received);
            Assert.Equal(0, second.Downloaded);
            Assert.Equal(3, (await restartedStore.LoadAsync(token)).Items.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string TestRoot(string prefix)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
            prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}

internal sealed class OfficialOpenListFactAttribute : FactAttribute
{
    public OfficialOpenListFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MPT_OPENLIST_TEST_BINARY")))
            Skip = "Set MPT_OPENLIST_TEST_BINARY to an official OpenList executable for the integration test.";
    }
}
