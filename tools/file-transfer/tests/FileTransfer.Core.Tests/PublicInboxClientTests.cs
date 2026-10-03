using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FileTransfer.Core;
using Xunit;

namespace FileTransfer.Core.Tests;

/// <summary>
/// End-to-end tests of <see cref="PublicInboxClient"/> against the repository's real relay service.
///
/// Every test in this class talks HTTP over a loopback socket to a real
/// <c>python3 -m mpt_relay</c>-equivalent child process (the same <c>Config</c> → <c>Store</c> →
/// <c>RelayApp</c> → <c>RelayServer</c> wiring as production, per <c>relay/tests/relay_testkit.py</c>),
/// started on a random port with an isolated temporary data directory. Nothing is stubbed except the
/// two transport-guard cases, which need a server the fixed protocol would never run.
///
/// The service is exercised through its own <c>inbox.py</c>/<c>app.py</c> routing, so the permission
/// matrix, the percent-encoded headers, the long poll, the real on-disk receipt and the quota
/// responses are the production code paths.
/// </summary>
public sealed class PublicInboxClientTests : IAsyncLifetime
{
    private readonly List<RelayProcess> _relays = [];
    private RelayProcess _relay = null!;
    private RelayProcess _smallRelay = null!;

    public async Task InitializeAsync()
    {
        // The default relay mirrors production limits; the second one has a 2 KiB single-file limit
        // and a 3 KiB namespace quota so 413/507 are reachable without writing megabytes.
        _relay = Track(new RelayProcess());
        _smallRelay = Track(new RelayProcess(new RelayLimits
        {
            MaxFileBytes = 2 * 1024,
            PerConversationBytes = 3 * 1024,
            GlobalBytes = 8L * 1024 * 1024
        }));
        await _relay.StartAsync();
        await _smallRelay.StartAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var relay in _relays) await relay.DisposeAsync();
    }

    private RelayProcess Track(RelayProcess relay)
    {
        _relays.Add(relay);
        return relay;
    }

    // -- offline identity and pairing code ---------------------------------------------------

    [Fact]
    public void AnOfflinePairingCodeCarriesTheInboxIdAndDepositKeyButNeverTheOwnerKey()
    {
        var identity = PublicInboxIdentity.CreateNew();
        Assert.Matches("^inbox-[0-9a-f]{32}$", identity.InboxId);
        Assert.Matches("^[0-9a-f]{64}$", identity.OwnerKey);
        Assert.Matches("^[0-9a-f]{64}$", identity.DepositKey);
        Assert.NotEqual(identity.OwnerKey, identity.DepositKey);

        var code = identity.Pairing.Encode();
        Assert.StartsWith(PublicInboxPairing.CodeScheme, code);
        Assert.DoesNotContain(identity.OwnerKey, code, StringComparison.OrdinalIgnoreCase);
        var decoded = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(code.AsSpan(PublicInboxPairing.CodeScheme.Length)));
        Assert.DoesNotContain("ownerKey", decoded, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(identity.OwnerKey, decoded, StringComparison.OrdinalIgnoreCase);

        Assert.True(PublicInboxPairing.TryParse(code, out var parsed));
        Assert.Equal(identity.InboxId, parsed.InboxId);
        Assert.Equal(identity.DepositKey, parsed.DepositKey);
        Assert.Equal(identity.Pairing, parsed);

        // 身份（含 ownerKey）的本地往返由模块负责，格式必须稳定且不会把密钥打印到日志里。
        Assert.True(PublicInboxIdentity.TryParseJson(identity.ToJson(), out var restored));
        Assert.Equal(identity, restored);
        Assert.DoesNotContain(identity.OwnerKey, identity.ToString());
        Assert.DoesNotContain(identity.DepositKey, parsed.ToString());

        Assert.False(PublicInboxPairing.TryParse("mpt://pair/AAAA", out _));
        Assert.False(PublicInboxPairing.TryParse(code + "x", out _));
        Assert.False(PublicInboxIdentity.TryParseJson("{\"inboxId\":\"inbox-00000000\"}", out _));
    }

    [Fact]
    public void PlainHttpCredentialsAreLimitedToTheFixedTailRelayAndLoopbackTests()
    {
        var identity = PublicInboxIdentity.CreateNew();
        using var trusted = PublicInboxClient.Owner(identity, baseAddress: InboxRelays.TailAddress);
        FileTransfer.Core.Assistant.OpenListClient.ValidateUrl(new Uri(InboxRelays.TailAddress, "/mpt/relay/dav/"));
        foreach (var url in new[] { "http://example.com", "http://mpt-relay.tail.lixinrui000.cn.evil.test", "http://mpt-relay.tail.lixinrui000.cn:8080" })
        {
            Assert.Throws<ArgumentException>(() => PublicInboxClient.Owner(identity, baseAddress: new Uri(url)));
            Assert.Throws<ArgumentException>(() => FileTransfer.Core.Assistant.OpenListClient.ValidateUrl(new Uri(url)));
        }
    }

    [Fact]
    public void TheDefaultTransportIsTheFixedPublicRelayAndNoRoleIsChosenByAccident()
    {
        Assert.Equal(new Uri("https://proxy.lixinrui000.cn"), PublicRelayClient.ProductionBaseAddress);
        var expected = PublicRelayClient.BaseAddress;
        using var client = PublicInboxClient.Owner(PublicInboxIdentity.CreateNew());
        Assert.Equal(expected, client.BaseAddress);
        Assert.Equal(expected, PublicInboxClient.ResolveBaseAddress());

        // 独立构造器 = 明确的角色；不存在“默认按 owner 处理”的重载。
        var identity = PublicInboxIdentity.CreateNew();
        using var owner = PublicInboxClient.Owner(identity, baseAddress: _relay.BaseAddress);
        using var deposit = PublicInboxClient.Deposit(identity.Pairing, baseAddress: _relay.BaseAddress);
        Assert.True(owner.IsOwner);
        Assert.False(owner.IsDeposit);
        Assert.True(deposit.IsDeposit);
        Assert.False(deposit.IsOwner);
        Assert.Equal(identity.InboxId, owner.InboxId);

        Assert.Throws<ArgumentException>(() => PublicInboxClient.Owner(new PublicInboxIdentity("bad id!", identity.OwnerKey, identity.DepositKey)));
        Assert.Throws<ArgumentException>(() => PublicInboxClient.Owner(new PublicInboxIdentity(identity.InboxId, "not-hex", identity.DepositKey)));
        Assert.Throws<ArgumentException>(() => PublicInboxClient.Owner(new PublicInboxIdentity(identity.InboxId, identity.OwnerKey, identity.OwnerKey)));
        Assert.Throws<ArgumentException>(() => PublicInboxClient.Deposit(new PublicInboxPairing("bad id!", identity.DepositKey)));
        Assert.Throws<ArgumentException>(() => PublicInboxClient.Deposit(new PublicInboxPairing(identity.InboxId, "short")));
    }

    // -- registration -------------------------------------------------------------------------

    [Fact]
    public async Task AnOwnerRegistersItsInboxAndARepeatedRegistrationIsIdempotent()
    {
        var identity = PublicInboxIdentity.CreateNew();
        using var owner = AsOwner(_relay, identity);
        var first = await owner.RegisterAsync();
        Assert.True(first.Created);
        Assert.Equal(identity.InboxId, first.InboxId);
        Assert.Equal(0, first.Revision);

        using var again = AsOwner(_relay, identity);
        var second = await again.RegisterAsync();
        Assert.False(second.Created);
        Assert.Equal(first.Revision, second.Revision);

        // 用 depositKey 冒充 owner 注册：401 永久失败，一次尝试，绝不重试。
        var impostorIdentity = new PublicInboxIdentity(identity.InboxId, identity.DepositKey, PublicInboxIds.NewKey());
        using var impostor = AsOwner(_relay, impostorIdentity);
        var rejected = await Assert.ThrowsAsync<PublicInboxAuthException>(() => impostor.RegisterAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.Status);
        Assert.Equal(1, rejected.Attempts);

        // 正确的 owner + 新的 depositKey：旧配对码仍然有效，服务端 409。
        var relock = new PublicInboxIdentity(identity.InboxId, identity.OwnerKey, PublicInboxIds.NewKey());
        using var relocker = AsOwner(_relay, relock);
        var conflict = await Assert.ThrowsAsync<PublicInboxConflictException>(() => relocker.RegisterAsync());
        Assert.Equal(HttpStatusCode.Conflict, conflict.Status);
        Assert.Equal("deposit_key_locked", conflict.Code);
        Assert.Equal(1, conflict.Attempts);
    }

    [Fact]
    public async Task ADepositBeforeTheOwnerRegistersIs503AndThenSucceedsOnceTheOwnerRegisters()
    {
        var identity = PublicInboxIdentity.CreateNew();
        var itemId = PublicInboxIds.NewItemId();
        const string text = "先导码时的问候 ✅";

        using (var impatient = PublicInboxClient.Deposit(identity.Pairing, PublicInboxRetryPolicy.None, _relay.BaseAddress))
        {
            var notReady = await Assert.ThrowsAsync<PublicInboxUnavailableException>(() => impatient.DepositTextAsync(itemId, text));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.Status);
            Assert.Equal("inbox_not_ready", notReady.Code);
            Assert.Equal(1, notReady.Attempts);
            Assert.NotNull(notReady.RetryAfter);
            Assert.InRange(notReady.RetryAfter!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        }

        // 同一个 itemId 的自动退避重试：收件端注册后，重试必须成功且复用同一个 id。
        using var sender = PublicInboxClient.Deposit(
            identity.Pairing,
            new PublicInboxRetryPolicy
            {
                MaxAttempts = 12,
                InitialDelay = TimeSpan.FromMilliseconds(150),
                MaxDelay = TimeSpan.FromMilliseconds(400)
            },
            _relay.BaseAddress);
        var deposit = sender.DepositTextAsync(itemId, text);
        await Task.Delay(500);
        using var owner = AsOwner(_relay, identity);
        Assert.True((await owner.RegisterAsync()).Created);
        var result = await deposit;
        Assert.True(result.Attempts >= 2, $"期望 503 之后至少重试一次，实际只有 {result.Attempts} 次尝试。");
        Assert.Equal(itemId, result.ItemId);
        Assert.False(result.Duplicate);

        var page = await owner.PollAsync();
        Assert.Equal(itemId, Assert.Single(page.Items).ItemId);
        Assert.Equal(text, Encoding.UTF8.GetString(await DownloadBytesAsync(owner, itemId)));
    }

    // -- two devices, both directions ----------------------------------------------------------

    [Fact]
    public async Task FilesAndTextTravelBothWaysBetweenTwoPairedDevices()
    {
        var deviceA = PublicInboxIdentity.CreateNew();
        var deviceB = PublicInboxIdentity.CreateNew();
        using var ownerA = AsOwner(_relay, deviceA);
        using var ownerB = AsOwner(_relay, deviceB);
        Assert.True((await ownerA.RegisterAsync()).Created);
        Assert.True((await ownerB.RegisterAsync()).Created);

        const string unicodeName = "报告-δ✅ v2(最终).pdf";
        const string senderName = "张三的手机 📱";
        var fileBytes = Encoding.UTF8.GetBytes("A→B 的文件内容 with Unicode ✅");
        var payloadDirectory = Path.Combine(RelayProcess.RootDirectory, "payloads", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(payloadDirectory);
        var filePath = Path.Combine(payloadDirectory, "source.bin");
        await File.WriteAllBytesAsync(filePath, fileBytes);

        var fileItem = PublicInboxIds.NewItemId();
        var textItem = PublicInboxIds.NewItemId();
        using (var aToB = PublicInboxClient.Deposit(deviceB.Pairing, baseAddress: _relay.BaseAddress))
        {
            var file = await aToB.DepositFileAsync(fileItem, filePath, displayName: unicodeName, senderDeviceId: "device-a", senderName: senderName);
            Assert.False(file.Duplicate);
            Assert.Equal(fileBytes.Length, file.Size);
            Assert.Equal(1, file.Attempts);

            var text = await aToB.DepositTextAsync(textItem, "A→B 的文本 ✅", senderDeviceId: "device-a", senderName: senderName);
            Assert.False(text.Duplicate);
        }

        var page = await ownerB.PollAsync(since: 0);
        Assert.Equal(2, page.Items.Count);
        var receivedFile = page.Items.Single(item => item.ItemId == fileItem);
        Assert.Equal(PublicInboxItemKind.File, receivedFile.Kind);
        Assert.Equal(unicodeName, receivedFile.Name);
        Assert.Equal(senderName, receivedFile.SenderName);
        Assert.Equal("device-a", receivedFile.SenderDeviceId);
        Assert.Equal(fileBytes.Length, receivedFile.Size);
        var receivedText = page.Items.Single(item => item.ItemId == textItem);
        Assert.Equal(PublicInboxItemKind.Text, receivedText.Kind);
        Assert.Null(receivedText.Name);

        // HEAD 也给元信息，且百分号编码的 UTF-8 文件名被严格解码。
        var head = await ownerB.HeadAsync(fileItem);
        Assert.Equal(unicodeName, head.Name);
        Assert.Equal(senderName, head.SenderName);
        Assert.Equal(fileBytes.Length, head.Size);

        var downloaded = await DownloadBytesAsync(ownerB, fileItem);
        Assert.Equal(fileBytes, downloaded);
        Assert.Equal("A→B 的文本 ✅", Encoding.UTF8.GetString(await DownloadBytesAsync(ownerB, textItem)));

        // 服务端 item.json 里存的是解码后的真实文件名，证明头部编码正确。
        var metadata = Path.Combine(_relay.DataDirectory, "inboxes", deviceB.InboxId, "items", fileItem, "item.json");
        Assert.True(File.Exists(metadata), $"缺少 {metadata}");
        using (var stored = JsonDocument.Parse(await File.ReadAllTextAsync(metadata)))
        {
            Assert.Equal(unicodeName, stored.RootElement.GetProperty("name").GetString());
            Assert.Equal(senderName, stored.RootElement.GetProperty("senderName").GetString());
            Assert.Equal("device-a", stored.RootElement.GetProperty("senderDeviceId").GetString());
        }

        // 反方向：B 用 A 的配对码投递，文本和文件都要能到达 A。
        var replyItem = PublicInboxIds.NewItemId();
        var replyFileItem = PublicInboxIds.NewItemId();
        using (var bToA = PublicInboxClient.Deposit(deviceA.Pairing, baseAddress: _relay.BaseAddress))
        {
            await bToA.DepositTextAsync(replyItem, "B→A 的回复 ✅", senderDeviceId: "device-b", senderName: "李四的电脑");
            await bToA.DepositFileAsync(replyFileItem, filePath, displayName: "reply-✅.bin", senderDeviceId: "device-b", senderName: "李四的电脑");
        }
        var pageA = await ownerA.PollAsync(since: 0);
        Assert.Equal(2, pageA.Items.Count);
        Assert.Equal("B→A 的回复 ✅", Encoding.UTF8.GetString(await DownloadBytesAsync(ownerA, replyItem)));
        Assert.Equal(fileBytes, await DownloadBytesAsync(ownerA, replyFileItem));
        Assert.Equal("reply-✅.bin", pageA.Items.Single(item => item.ItemId == replyFileItem).Name);

        // 两条通道互不可见：B 的列表里没有 A 的条目。
        Assert.DoesNotContain(pageA.Items, item => item.ItemId == fileItem || item.ItemId == textItem);
    }

    // -- long poll ----------------------------------------------------------------------------

    [Fact]
    public async Task TheOwnerLongPollAnswersImmediatelyWithoutSinceAndWakesOnDelivery()
    {
        var identity = PublicInboxIdentity.CreateNew();
        using var owner = AsOwner(_relay, identity);
        await owner.RegisterAsync();

        var immediate = Stopwatch.StartNew();
        var empty = await owner.PollAsync();
        immediate.Stop();
        Assert.Empty(empty.Items);
        Assert.Equal(0, empty.Revision);
        Assert.True(immediate.Elapsed < TimeSpan.FromSeconds(10), $"不带 since 应立即返回，实际 {immediate.Elapsed}。");

        using var sender = PublicInboxClient.Deposit(identity.Pairing, baseAddress: _relay.BaseAddress);
        using var pollCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var parked = owner.PollAsync(since: 0, limit: 50, token: pollCancellation.Token);
        await Task.Delay(500); // 让长轮询真正挂起在 25 秒的等待里
        var wake = Stopwatch.StartNew();
        var itemId = PublicInboxIds.NewItemId();
        await sender.DepositTextAsync(itemId, "唤醒长轮询 ✅");
        var page = await parked;
        wake.Stop();

        Assert.True(wake.Elapsed < TimeSpan.FromSeconds(20), $"投递应立即唤醒长轮询，实际等待 {wake.Elapsed}（服务端上限 25 秒）。");
        Assert.Equal(1, page.Revision);
        Assert.Equal(itemId, Assert.Single(page.Items).ItemId);

        // since 超前于服务端（回滚/换库）立即返回当前 revision。
        var future = Stopwatch.StartNew();
        var ahead = await owner.PollAsync(since: 999);
        future.Stop();
        Assert.Equal(1, ahead.Revision);
        Assert.True(future.Elapsed < TimeSpan.FromSeconds(10));

        // 回执不唤醒长轮询、不增加 revision：挂起的 since=1 会一直等到超时（这里用 5 秒预算的取消证明它没被唤醒）。
        using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var quietPoll = owner.PollAsync(since: 1, token: quiet.Token);
        await owner.AcknowledgeAsync(itemId, Encoding.UTF8.GetByteCount("唤醒长轮询 ✅"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quietPoll);
    }

    // -- download + real receipt ---------------------------------------------------------------

    [Fact]
    public async Task TheOwnerWritesAReceiptThatIsStoredOnDiskAndReadBackByTheSender()
    {
        var identity = PublicInboxIdentity.CreateNew();
        using var owner = AsOwner(_relay, identity);
        await owner.RegisterAsync();
        using var sender = PublicInboxClient.Deposit(identity.Pairing, baseAddress: _relay.BaseAddress);

        var itemId = PublicInboxIds.NewItemId();
        const string text = "真正落盘的内容 ✅";
        var bytes = Encoding.UTF8.GetBytes(text);
        await sender.DepositTextAsync(itemId, text, senderDeviceId: "device-a");

        var before = await sender.GetReceiptAsync(itemId);
        Assert.False(before.Saved);
        Assert.Equal(bytes.Length, before.Size);
        Assert.Null(before.SavedAt);

        var pending = Assert.Single((await owner.PollAsync()).Items);
        Assert.Equal(itemId, pending.ItemId);
        Assert.Equal(bytes, await DownloadBytesAsync(owner, itemId));

        var savedAt = DateTimeOffset.UtcNow;
        var receipt = await owner.AcknowledgeAsync(itemId, bytes.Length, savedAt, deviceId: "device-b", deviceName: "收件电脑 ✅");
        Assert.True(receipt.Saved);
        Assert.False(receipt.Duplicate);
        Assert.Equal(bytes.Length, receipt.Bytes);

        // 回执是服务器文件系统里的真实文件，不是内存里的一个标记。
        var receiptPath = Path.Combine(_relay.DataDirectory, "inboxes", identity.InboxId, "items", itemId, "receipt.json");
        Assert.True(File.Exists(receiptPath), $"缺少 {receiptPath}");
        using (var stored = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath)))
        {
            Assert.Equal("device-b", stored.RootElement.GetProperty("deviceId").GetString());
            Assert.Equal("收件电脑 ✅", stored.RootElement.GetProperty("deviceName").GetString());
            Assert.Equal(bytes.Length, stored.RootElement.GetProperty("bytes").GetInt64());
        }

        // 有回执的条目离开 owner 的待处理列表。
        Assert.Empty((await owner.PollAsync()).Items);

        // deposit 凭据用随机 itemId 读回同一条回执。
        var confirmed = await sender.GetReceiptAsync(itemId);
        Assert.True(confirmed.Saved);
        Assert.Equal(bytes.Length, confirmed.Bytes);
        Assert.Equal("device-b", confirmed.DeviceId);
        Assert.Equal("收件电脑 ✅", confirmed.DeviceName);
        Assert.NotNull(confirmed.SavedAt);

        // 重复回执只报 duplicate。
        var again = await owner.AcknowledgeAsync(itemId, bytes.Length, savedAt, deviceId: "device-b", deviceName: "收件电脑 ✅");
        Assert.True(again.Saved);
        Assert.True(again.Duplicate);

        // 删除后回执也不可读，itemId 变成未知条目。
        await owner.DeleteAsync(itemId);
        await Assert.ThrowsAsync<PublicInboxNotFoundException>(() => owner.HeadAsync(itemId));
        await Assert.ThrowsAsync<PublicInboxNotFoundException>(() => sender.GetReceiptAsync(itemId));
        Assert.Empty((await owner.PollAsync()).Items);
    }

    // -- permission matrix ---------------------------------------------------------------------

    [Fact]
    public async Task ADepositCredentialCannotListReadPayloadsOrWriteReceipts()
    {
        var identity = PublicInboxIdentity.CreateNew();
        var other = PublicInboxIdentity.CreateNew();
        using var owner = AsOwner(_relay, identity);
        using var otherOwner = AsOwner(_relay, other);
        await owner.RegisterAsync();
        await otherOwner.RegisterAsync();

        using var sender = PublicInboxClient.Deposit(identity.Pairing, baseAddress: _relay.BaseAddress);
        var mine = PublicInboxIds.NewItemId();
        var foreign = PublicInboxIds.NewItemId();
        await sender.DepositTextAsync(mine, "只有 owner 能读我");
        await sender.DepositTextAsync(foreign, "另一条");

        // 客户端层：投递实例根本没有 owner 操作的入口。
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.PollAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.DownloadAsync(mine, Stream.Null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.AcknowledgeAsync(mine, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.DeleteAsync(mine));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.RegisterAsync());

        // 服务端层：直接发送同一份 Basic 凭据也被拒（403），权限不是客户端装出来的。
        using var raw = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        var items = PublicInboxClient.ItemsPath;
        await AssertRawStatusAsync(raw, _relay, HttpMethod.Get, items, identity.InboxId, identity.DepositKey, HttpStatusCode.Forbidden);
        await AssertRawStatusAsync(raw, _relay, HttpMethod.Get, $"{items}/{mine}", identity.InboxId, identity.DepositKey, HttpStatusCode.Forbidden);
        await AssertRawStatusAsync(raw, _relay, HttpMethod.Head, $"{items}/{mine}", identity.InboxId, identity.DepositKey, HttpStatusCode.Forbidden);
        await AssertRawStatusAsync(raw, _relay, HttpMethod.Delete, $"{items}/{mine}", identity.InboxId, identity.DepositKey, HttpStatusCode.Forbidden);
        await AssertRawStatusAsync(
            raw, _relay, HttpMethod.Post, $"{items}/{mine}/receipt", identity.InboxId, identity.DepositKey,
            HttpStatusCode.Forbidden, JsonContent($$"""{"itemId":"{{mine}}","savedAt":"{{DateTimeOffset.UtcNow:O}}","bytes":3}"""));

        // 投递凭据只能按随机 itemId 读那一条回执（没有列表、没有枚举）。
        await AssertRawStatusAsync(raw, _relay, HttpMethod.Get, $"{items}/{mine}/receipt", identity.InboxId, identity.DepositKey, HttpStatusCode.OK);
        await AssertRawStatusAsync(raw, _relay, HttpMethod.Get, $"{items}/{PublicInboxIds.NewItemId()}/receipt", identity.InboxId, identity.DepositKey, HttpStatusCode.NotFound);
        await AssertRawStatusAsync(raw, _relay, HttpMethod.Get, $"{items}/not-hex/receipt", identity.InboxId, identity.DepositKey, HttpStatusCode.BadRequest);

        // 投递凭据不能伪造回执：owner 读回来仍然是 saved=false。
        Assert.False((await sender.GetReceiptAsync(mine)).Saved);

        // owner 自己列表/读写正常，而 owner 凭据不能走投递口。
        Assert.Equal(2, (await owner.PollAsync()).Items.Count);
        Assert.Equal("只有 owner 能读我", Encoding.UTF8.GetString(await DownloadBytesAsync(owner, mine)));
        await AssertRawStatusAsync(
            raw, _relay, HttpMethod.Put, $"{items}/{PublicInboxIds.NewItemId()}", identity.InboxId, identity.OwnerKey,
            HttpStatusCode.Forbidden, JsonContent("{}"));

        // 跨收件箱：把 A 的收件箱 id 与 B 的投递密钥拼在一起是纯鉴权失败（401），不是授权（403）。
        using var crossPaired = PublicInboxClient.Deposit(new PublicInboxPairing(other.InboxId, identity.DepositKey), baseAddress: _relay.BaseAddress);
        var crossError = await Assert.ThrowsAsync<PublicInboxAuthException>(() => crossPaired.DepositTextAsync(PublicInboxIds.NewItemId(), "cross"));
        Assert.Equal(HttpStatusCode.Unauthorized, crossError.Status);
        Assert.Equal(1, crossError.Attempts);

        // 另一个收件箱里没有任何越界条目。
        Assert.Empty((await otherOwner.PollAsync()).Items);
    }

    // -- cancellation --------------------------------------------------------------------------

    [Fact]
    public async Task ACancelledDepositPublishesNothingAndLeavesNoTemporaryFile()
    {
        var identity = PublicInboxIdentity.CreateNew();
        using var owner = AsOwner(_relay, identity);
        await owner.RegisterAsync();
        using var sender = PublicInboxClient.Deposit(identity.Pairing, baseAddress: _relay.BaseAddress);

        var itemId = PublicInboxIds.NewItemId();
        using var slow = new SlowStream(totalBytes: 8L * 1024 * 1024, chunkBytes: 64 * 1024, delay: TimeSpan.FromMilliseconds(10));
        using var cancellation = new CancellationTokenSource();
        var deposit = sender.DepositAsync(
            new PublicInboxDeposit { ItemId = itemId, Kind = PublicInboxItemKind.File, Name = "big.bin", SenderDeviceId = "device-a" },
            slow,
            cancellation.Token);
        await slow.Started.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(250);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deposit);

        Assert.Empty((await owner.PollAsync()).Items);
        await Assert.ThrowsAsync<PublicInboxNotFoundException>(() => owner.HeadAsync(itemId));
        var itemDirectory = Path.Combine(_relay.DataDirectory, "inboxes", identity.InboxId, "items", itemId);
        Assert.False(File.Exists(Path.Combine(itemDirectory, "item.json")), "被取消的投递不能发布条目");
        await WaitUntilAsync(
            () => !Directory.EnumerateFileSystemEntries(_relay.DataDirectory, ".mpt-relay-upload-*", SearchOption.AllDirectories).Any(),
            TimeSpan.FromSeconds(10),
            "服务端上传临时文件没有被清理");
        Assert.Empty(Directory.EnumerateFileSystemEntries(_relay.DataDirectory, ".mpt-relay-upload-*", SearchOption.AllDirectories));
    }

    // -- quota text ----------------------------------------------------------------------------

    [Fact]
    public async Task TooLargeAndOverQuotaFailuresCarryClearUserTextAndAreNeverRetried()
    {
        var identity = PublicInboxIdentity.CreateNew();
        using var owner = AsOwner(_smallRelay, identity);
        await owner.RegisterAsync();
        using var sender = PublicInboxClient.Deposit(identity.Pairing, baseAddress: _smallRelay.BaseAddress);

        var oversized = new byte[4 * 1024];
        using (var stream = new MemoryStream(oversized))
        {
            var tooLarge = await Assert.ThrowsAsync<PublicInboxQuotaException>(() => sender.DepositAsync(
                new PublicInboxDeposit { ItemId = PublicInboxIds.NewItemId(), Kind = PublicInboxItemKind.File, Name = "big.bin" }, stream));
            Assert.True(tooLarge.TooLarge);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.Status);
            Assert.Equal(1, tooLarge.Attempts);
            Assert.Contains("单文件上限", tooLarge.UserMessage);
        }

        var chunk = new byte[2 * 1024];
        chunk[0] = 7;
        using (var first = new MemoryStream(chunk))
        {
            var stored = await sender.DepositAsync(
                new PublicInboxDeposit { ItemId = PublicInboxIds.NewItemId(), Kind = PublicInboxItemKind.File, Name = "a.bin", Length = chunk.Length }, first);
            Assert.False(stored.Duplicate);
            Assert.Equal(chunk.Length, stored.Size);
        }
        using (var second = new MemoryStream(chunk))
        {
            var overQuota = await Assert.ThrowsAsync<PublicInboxQuotaException>(() => sender.DepositAsync(
                new PublicInboxDeposit { ItemId = PublicInboxIds.NewItemId(), Kind = PublicInboxItemKind.File, Name = "b.bin", Length = chunk.Length }, second));
            Assert.False(overQuota.TooLarge);
            Assert.Equal(HttpStatusCode.InsufficientStorage, overQuota.Status);
            Assert.Equal(1, overQuota.Attempts);
            Assert.Contains("空间不足", overQuota.UserMessage);
        }

        // 失败没有留下残条目：待处理列表里只有成功的那一条。
        Assert.Single((await owner.PollAsync()).Items);
    }

    // -- auth ----------------------------------------------------------------------------------

    [Fact]
    public async Task AWrongKeyIsAPermanentAuthFailureAndIsNeverRetried()
    {
        var identity = PublicInboxIdentity.CreateNew();
        using var owner = AsOwner(_relay, identity);
        await owner.RegisterAsync();

        var wrongOwner = new PublicInboxIdentity(identity.InboxId, PublicInboxIds.NewKey(), identity.DepositKey);
        using var intruder = AsOwner(_relay, wrongOwner);
        var rejected = await Assert.ThrowsAsync<PublicInboxAuthException>(() => intruder.PollAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.Status);
        Assert.Equal(1, rejected.Attempts);
        Assert.Contains("owner 密钥", rejected.UserMessage);

        // 未注册收件箱的 owner 列表是 401（不是可重试的 503）。
        using var unknown = AsOwner(_relay, PublicInboxIdentity.CreateNew());
        var notRegistered = await Assert.ThrowsAsync<PublicInboxAuthException>(() => unknown.PollAsync());
        Assert.Equal(1, notRegistered.Attempts);

        // 投递密钥不对：永久失败，且不会因为 503 之外的错误消耗重试次数。
        using var badSender = PublicInboxClient.Deposit(new PublicInboxPairing(identity.InboxId, PublicInboxIds.NewKey()), baseAddress: _relay.BaseAddress);
        var badDeposit = await Assert.ThrowsAsync<PublicInboxAuthException>(() => badSender.DepositTextAsync(PublicInboxIds.NewItemId(), "nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, badDeposit.Status);
        Assert.Equal(1, badDeposit.Attempts);
        Assert.Contains("投递密钥", badDeposit.UserMessage);
    }

    // -- transport guards (the fixed relay never redirects or over-answers, so these use a stub) --

    [Fact]
    public async Task ARedirectIsRefusedSoTheBasicCredentialIsNeverForwarded()
    {
        using var stub = new SingleShotHttpServer(_ =>
            "HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:9/steal\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = PublicInboxClient.Owner(PublicInboxIdentity.CreateNew(), baseAddress: stub.BaseAddress);
        var error = await Assert.ThrowsAsync<PublicInboxProtocolException>(() => client.RegisterAsync());
        Assert.Contains("重定向", error.UserMessage);
        Assert.Equal(1, stub.RequestCount);
        Assert.Equal(1, stub.ConnectionCount);
    }

    [Fact]
    public async Task AnOversizedOrMalformedResponseIsRejectedInsteadOfBuffered()
    {
        using (var oversized = new SingleShotHttpServer(_ => JsonResponse($$"""{"itemId":"{{new string('a', 100_000)}}"}""")))
        {
            using var client = DepositClient(oversized);
            var error = await Assert.ThrowsAsync<PublicInboxProtocolException>(() => client.GetReceiptAsync(PublicInboxIds.NewItemId()));
            Assert.Contains("上限", error.UserMessage);
        }

        using (var malformed = new SingleShotHttpServer(_ =>
            "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 8\r\nConnection: close\r\n\r\nnot-json"))
        {
            using var client = DepositClient(malformed);
            var error = await Assert.ThrowsAsync<PublicInboxProtocolException>(() => client.GetReceiptAsync(PublicInboxIds.NewItemId()));
            Assert.Contains("JSON", error.UserMessage);
        }

        // 接收端会拿文件名去落盘：服务端若返回路径型名字，客户端必须拒绝而不是交给模块写文件。
        var hostileName = JsonResponse(
            $$"""{"revision":1,"items":[{"itemId":"{{PublicInboxIds.NewItemId()}}","kind":"file","name":"../escape.exe","size":1,"createdAt":"2026-01-01T00:00:00Z"}],"hasMore":false}""");
        using (var hostile = new SingleShotHttpServer(_ => hostileName))
        {
            using var client = PublicInboxClient.Owner(PublicInboxIdentity.CreateNew(), baseAddress: hostile.BaseAddress);
            var error = await Assert.ThrowsAsync<PublicInboxProtocolException>(() => client.PollAsync());
            Assert.Contains("文件名", error.UserMessage);
        }

        static PublicInboxClient DepositClient(SingleShotHttpServer stub) =>
            PublicInboxClient.Deposit(new PublicInboxPairing("inbox-00000000", PublicInboxIds.NewKey()), baseAddress: stub.BaseAddress);
    }

    // -- helpers -------------------------------------------------------------------------------

    private static PublicInboxClient AsOwner(RelayProcess relay, PublicInboxIdentity identity, PublicInboxRetryPolicy? retry = null) =>
        PublicInboxClient.Owner(identity, retry, relay.BaseAddress);

    private static async Task<byte[]> DownloadBytesAsync(PublicInboxClient owner, string itemId)
    {
        using var buffer = new MemoryStream();
        var payload = await owner.DownloadAsync(itemId, buffer);
        Assert.Equal(payload.Bytes, buffer.Length);
        return buffer.ToArray();
    }

    private static HttpContent JsonContent(string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return content;
    }

    private static async Task AssertRawStatusAsync(
        HttpClient client,
        RelayProcess relay,
        HttpMethod method,
        string path,
        string inboxId,
        string key,
        HttpStatusCode expected,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(relay.BaseAddress, path)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{inboxId}:{key}")));
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            expected == response.StatusCode,
            $"{method} {path} 期望 {(int)expected} {expected}，实际 {(int)response.StatusCode} {response.StatusCode}：{body}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string message)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        Assert.True(condition(), message);
    }

    private static string JsonResponse(string body) =>
        $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    // -- real relay process --------------------------------------------------------------------

    private sealed record RelayLimits
    {
        public long MaxFileBytes { get; init; } = 512L * 1024 * 1024;
        public long PerConversationBytes { get; init; } = 1024L * 1024 * 1024;
        public long GlobalBytes { get; init; } = 2L * 1024 * 1024 * 1024;
        public int LongPollSeconds { get; init; } = 25;
    }

    /// <summary>
    /// The repository's own relay as a real child process: production wiring, random loopback port,
    /// isolated data directory, killed and removed on dispose. The launcher only prints the bound port
    /// (the config validates port 0 for exactly this purpose) before serving.
    /// </summary>
    private sealed class RelayProcess : IAsyncDisposable
    {
        private const string LauncherScript = """
            import sys

            sys.path.insert(0, sys.argv[1])

            from mpt_relay.config import Config
            from mpt_relay.service import build, setup_logging

            config = Config.load([
                '--host', '127.0.0.1', '--port', '0', '--data-dir', sys.argv[2],
                '--base-path', '/mpt/relay', '--log-level', 'info',
            ])
            logger = setup_logging(config)
            store, app, server = build(config, logger)
            print('MPT_RELAY_READY port=%d' % server.port, flush=True)
            server.start_maintenance()
            server.serve_forever()
            """;

        private readonly RelayLimits _limits;
        private readonly string _serverDirectory;
        private readonly string _stdoutLog;
        private readonly string _stderrLog;
        private Process? _process;
        private Task _stdoutPump = Task.CompletedTask;
        private Task _stderrPump = Task.CompletedTask;

        public RelayProcess(RelayLimits? limits = null)
        {
            _limits = limits ?? new RelayLimits();
            var name = Guid.NewGuid().ToString("N")[..8];
            _serverDirectory = Path.Combine(RootDirectory, "servers", name);
            DataDirectory = Path.Combine(_serverDirectory, "data");
            _stdoutLog = Path.Combine(RootDirectory, "logs", $"relay-{name}.stdout.log");
            _stderrLog = Path.Combine(RootDirectory, "logs", $"relay-{name}.stderr.log");
        }

        /// <summary><c>artifacts/.tmp-android-verify/public-inbox-client</c>, declared as scratch in the artifacts policy.</summary>
        public static string RootDirectory { get; } = ResolveRootDirectory();

        public static string RepositoryRoot { get; } = ResolveRepositoryRoot();

        public static string PythonExecutable { get; } =
            Environment.GetEnvironmentVariable("MPT_INBOX_TEST_PYTHON") is { Length: > 0 } configured ? configured : "python3";

        public string DataDirectory { get; }

        public Uri BaseAddress { get; private set; } = null!;

        public async Task StartAsync()
        {
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(_stdoutLog)!);
            var launcher = Path.Combine(_serverDirectory, "launch_relay.py");
            await File.WriteAllTextAsync(launcher, LauncherScript);

            var info = new ProcessStartInfo(PythonExecutable)
            {
                WorkingDirectory = _serverDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add(launcher);
            info.ArgumentList.Add(Path.Combine(RepositoryRoot, "tools", "file-transfer", "relay"));
            info.ArgumentList.Add(DataDirectory);
            foreach (var inherited in info.Environment.Keys.Where(key => key.StartsWith("MPT_RELAY_", StringComparison.OrdinalIgnoreCase)).ToList())
                info.Environment.Remove(inherited);
            // Test-only abuse-control relaxation: the suite registers several inboxes and deliberately
            // presents wrong keys from one address. Everything else stays at its production default.
            info.Environment["MPT_RELAY_REGISTER_PER_IP_PER_HOUR"] = "1000";
            info.Environment["MPT_RELAY_REGISTER_GLOBAL_PER_HOUR"] = "5000";
            info.Environment["MPT_RELAY_AUTH_FAILURES_PER_IP"] = "0";
            info.Environment["MPT_RELAY_NOT_READY_PER_IP_PER_MINUTE"] = "600";
            info.Environment["MPT_RELAY_MAX_FILE_BYTES"] = _limits.MaxFileBytes.ToString(CultureInfo.InvariantCulture);
            info.Environment["MPT_RELAY_PER_CONVERSATION_BYTES"] = _limits.PerConversationBytes.ToString(CultureInfo.InvariantCulture);
            info.Environment["MPT_RELAY_GLOBAL_BYTES"] = _limits.GlobalBytes.ToString(CultureInfo.InvariantCulture);
            info.Environment["MPT_RELAY_LONGPOLL_MAX_SECONDS"] = _limits.LongPollSeconds.ToString(CultureInfo.InvariantCulture);

            _process = Process.Start(info) ?? throw new InvalidOperationException("无法启动真实的中转服务进程。");
            string? ready;
            try
            {
                ready = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45));
            }
            catch (TimeoutException)
            {
                ready = null;
            }
            if (ready is null || !ready.StartsWith("MPT_RELAY_READY port=", StringComparison.Ordinal))
            {
                var diagnostics = await ReadAllAsync(_process.StandardError);
                await DisposeAsync();
                throw new InvalidOperationException(
                    $"真实中转服务没有启动（{PythonExecutable} {launcher}）：{ready ?? "<no output>"}\n{diagnostics}\nstdout 日志：{_stdoutLog}");
            }
            BaseAddress = new Uri($"http://127.0.0.1:{ready["MPT_RELAY_READY port=".Length..].Trim()}");

            var stdout = _process.StandardOutput;
            var stderr = _process.StandardError;
            _stdoutPump = PumpAsync(stdout, _stdoutLog);
            _stderrPump = PumpAsync(stderr, _stderrLog);
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is { } process)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // already gone
                    }
                }
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    // the temp directory is still removed below
                }
                process.Dispose();
                _process = null;
            }
            foreach (var pump in new[] { _stdoutPump, _stderrPump })
            {
                try
                {
                    await pump.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // the pipes close with the process
                }
            }
            try
            {
                if (Directory.Exists(_serverDirectory)) Directory.Delete(_serverDirectory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // a leftover scratch directory is never a test failure
            }
        }

        private static async Task PumpAsync(StreamReader reader, string path)
        {
            await using var log = new StreamWriter(path, append: true) { AutoFlush = true };
            while (await reader.ReadLineAsync() is { } line) await log.WriteLineAsync(line);
        }

        private static async Task<string> ReadAllAsync(StreamReader reader)
        {
            try
            {
                return await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                return "<stderr unavailable>";
            }
        }

        private static string ResolveRepositoryRoot()
        {
            if (Environment.GetEnvironmentVariable("MPT_INBOX_TEST_REPO_ROOT") is { Length: > 0 } configured)
                return configured;
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "tools", "file-transfer", "relay", "mpt_relay", "inbox.py")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new InvalidOperationException(
                "找不到仓库根目录（tools/file-transfer/relay/mpt_relay/inbox.py）；可用 MPT_INBOX_TEST_REPO_ROOT 指定。");
        }

        private static string ResolveRootDirectory()
        {
            if (Environment.GetEnvironmentVariable("MPT_INBOX_TEST_ROOT") is { Length: > 0 } configured)
            {
                Directory.CreateDirectory(configured);
                return configured;
            }
            var path = Path.Combine(ResolveRepositoryRoot(), "artifacts", ".tmp-android-verify", "public-inbox-client");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>A caller-owned stream that yields slowly, so a cancellation lands mid-upload.</summary>
    private sealed class SlowStream : Stream
    {
        private readonly long _totalBytes;
        private readonly int _chunkBytes;
        private readonly TimeSpan _delay;
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _produced;

        public SlowStream(long totalBytes, int chunkBytes, TimeSpan delay)
        {
            _totalBytes = totalBytes;
            _chunkBytes = chunkBytes;
            _delay = delay;
        }

        public Task Started => _started.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            if (_produced >= _totalBytes) return 0;
            await Task.Delay(_delay, cancellationToken);
            var count = (int)Math.Min(Math.Min(buffer.Length, _chunkBytes), _totalBytes - _produced);
            buffer.Span[..count].Fill((byte)'x');
            _produced += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A one-response TCP server, used only for the two transport guards.</summary>
    private sealed class SingleShotHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string, string> _respond;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _loop;
        private int _requests;
        private int _connections;

        public SingleShotHttpServer(Func<string, string> respond)
        {
            _respond = respond;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            BaseAddress = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
            _loop = Task.Run(AcceptAsync);
        }

        public Uri BaseAddress { get; }

        public int RequestCount => Volatile.Read(ref _requests);

        public int ConnectionCount => Volatile.Read(ref _connections);

        private async Task AcceptAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
                }
                catch (Exception)
                {
                    return;
                }
                Interlocked.Increment(ref _connections);
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);
                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(_respond(request)));
                await stream.FlushAsync();
            }
        }

        private static async Task<string> ReadRequestAsync(NetworkStream stream)
        {
            var buffer = new byte[8192];
            using var collected = new MemoryStream();
            var headerEnd = -1;
            while (headerEnd < 0 && collected.Length < 64 * 1024)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                collected.Write(buffer, 0, read);
                headerEnd = IndexOfHeaderEnd(collected.GetBuffer().AsSpan(0, (int)collected.Length));
            }
            if (headerEnd < 0) return "";
            var headers = Encoding.ASCII.GetString(collected.GetBuffer(), 0, headerEnd);
            var contentLength = 0;
            foreach (var line in headers.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line["Content-Length:".Length..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
            }
            var received = (int)collected.Length - (headerEnd + 4);
            while (received < contentLength)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) break;
                received += read;
            }
            return headers;
        }

        private static int IndexOfHeaderEnd(ReadOnlySpan<byte> data)
        {
            for (var index = 0; index + 3 < data.Length; index++)
            {
                if (data[index] == (byte)'\r' && data[index + 1] == (byte)'\n' && data[index + 2] == (byte)'\r' && data[index + 3] == (byte)'\n')
                    return index;
            }
            return -1;
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // the accept loop is aborted by the listener stop
            }
            _cancellation.Dispose();
        }
    }
}
