using System.Net;
using System.Net.Sockets;
using System.Text;
using FileTransfer.Core;
using FileTransfer.Core.Discovery;
using FileTransfer.MyPowerTools;

namespace FileTransfer.Tests;

/// <summary>
/// Protocol v3 over a real loopback socket: the identity hello answers without a credential, a
/// first contact waits for the user and leaves no trust behind when it is refused or expires, an
/// accepted contact delivers once, and a repeated item id is answered as delivered without a copy.
/// </summary>
public sealed class AssistantWireTests : IDisposable
{
    private const string Key = "assistant-wire-key-0123456789abcdef";
    private const string ConversationKey = "conversation-key-0123456789abcdef";
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(),
        "mpt-assistant-wire-" + Guid.NewGuid().ToString("N"));
    private readonly List<DirectReceiver> _receivers = [];

    public AssistantWireTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var receiver in _receivers) receiver.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private DirectReceiver Receiver(ReceiveAuthorization? authorization = null, Func<string, string?, string?, string?, CancellationToken, Task<bool>>? trusted = null,
        Func<string, CancellationToken, Task<bool>>? duplicate = null, Func<ReceivedItem, CancellationToken, Task>? onItem = null,
        string deviceId = "pc-receiver", string name = "Receiver PC")
    {
        var receiver = new DirectReceiver("127.0.0.1", 0, Key, Path.Combine(_root, "inbox"), 4L * 1024 * 1024,
            (_, _, _, _) => { }, deviceId: deviceId, deviceName: name, platform: "linux",
            authorization: authorization, isTrusted: trusted, isDuplicate: duplicate, onItem: onItem);
        _receivers.Add(receiver);
        return receiver;
    }

    private static AssistantWire.Frame Item(string itemId, string text, string? token = null, string deviceId = "phone-sender") =>
        new(AssistantWire.Version, AssistantWire.ItemKind, token, null, 0, deviceId, itemId, "conv-1",
            AssistantWire.TextItem, text, "Sender Phone", null);

    [Fact]
    public async Task TrustedLargeFileDoesNotReadPayloadUntilReceiverConfirms()
    {
        var auth = new ReceiveAuthorization();
        var receiver = new DirectReceiver("127.0.0.1", 0, Key, Path.Combine(_root, "large-inbox"), 32L * 1024 * 1024,
            (_, _, _, _) => { }, authorization: auth);
        _receivers.Add(receiver);
        var path = Path.Combine(_root, "large.bin");
        const long size = 15L * 1024 * 1024 + 1;
        using (var file = File.Create(path)) file.SetLength(size);
        var frame = new AssistantWire.Frame(AssistantWire.Version, AssistantWire.ItemKind, Key, "large.bin",
            size, "phone-other", Guid.NewGuid().ToString("N"), "conv-1", AssistantWire.FileItem, null, "Phone", null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reading = false;
        var sending = AssistantWire.SendItemAsync("127.0.0.1", receiver.Port, frame, path, null, TimeSpan.FromSeconds(15), timeout.Token,
            payloadSelected: () => { reading = true; return Task.CompletedTask; });
        while (auth.Pending().Count == 0) await Task.Delay(10, timeout.Token);
        Assert.False(reading);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "large-inbox")));
        auth.Respond(auth.Pending()[0].RequestId, true, false, out _);
        Assert.True((await sending).Ok);
        Assert.True(reading);
        Assert.Equal(size, new FileInfo(Assert.Single(Directory.GetFiles(Path.Combine(_root, "large-inbox")))).Length);
    }

    [Fact]
    public async Task TheIdentityHelloAnswersWithoutACredential()
    {
        var receiver = Receiver();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // The identity query is the discovery side's real client against this receiver.
        var candidate = new DiscoveryCandidate(
            new DiscoveredDevice("", "", "127.0.0.1", receiver.Port, ""), false, DiscoverySource.Lan);
        var probe = await new HelloIdentityProbe().ProbeAsync(candidate, timeout.Token);

        Assert.True(probe.Available, probe.Message);
        Assert.Equal("pc-receiver", probe.Device.DeviceId);
        Assert.Equal("Receiver PC", probe.Device.Name);
        Assert.Equal("linux", probe.Device.Platform);
        Assert.Equal(receiver.Port, probe.Device.Port);
        // The hello answer is public identity only; it must not leak the receiver credential.
        Assert.DoesNotContain(Key, probe.Message);
    }

    [Fact]
    public async Task AFirstContactHoldsTheConnectionAndRefusalLeavesNoTrust()
    {
        var authorization = new ReceiveAuthorization(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
        var receiver = Receiver(authorization);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string itemId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

        var sending = AssistantWire.SendItemAsync("127.0.0.1", receiver.Port,
            Item(itemId, "hello", "unknown-token-0123456789abcdef"), null, null, TimeSpan.FromSeconds(15), timeout.Token);
        var request = await WaitForRequestAsync(authorization);
        Assert.Equal("phone-sender", request.DeviceId);

        // Refusing answers the held connection and stores nothing.
        Assert.True(authorization.Respond(request.RequestId, accept: false, remember: false, out _));
        var refused = await sending;
        Assert.False(refused.Ok);
        Assert.Empty(authorization.Pending());
        Assert.False(authorization.IsApproved("phone-sender", "127.0.0.1", itemId));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "inbox")));

        // A retry of the same request is refused again without a new prompt.
        var again = await AssistantWire.SendItemAsync("127.0.0.1", receiver.Port,
            Item(itemId, "hello", "unknown-token-0123456789abcdef"), null, null, TimeSpan.FromSeconds(10), timeout.Token);
        Assert.False(again.Ok);
        Assert.Empty(authorization.Pending());
    }

    [Fact]
    public async Task AnAcceptedFirstContactDeliversImmediatelyAndThenDeduplicates()
    {
        var authorization = new ReceiveAuthorization(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
        var saved = new List<ReceivedItem>();
        var receiver = Receiver(authorization,
            duplicate: (id, _) => Task.FromResult(saved.Any(item => item.ItemId == id)),
            onItem: (item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string itemId = "b1b2c3d4e5f60718293a4b5c6d7e8f91";

        var sending = AssistantWire.SendItemAsync("127.0.0.1", receiver.Port,
            Item(itemId, "文本内容", "first-contact-token-0123456789"), null, null, TimeSpan.FromSeconds(15), timeout.Token);
        var request = await WaitForRequestAsync(authorization);
        Assert.True(authorization.Respond(request.RequestId, accept: true, remember: false, out _));
        // The accepted first contact transfers on the same connection: no second attempt, no sync.
        var delivered = await sending;
        Assert.True(delivered.Ok, delivered.Message);
        Assert.Equal(AssistantWire.DeliveredState, delivered.State);
        Assert.Single(saved);
        Assert.Equal("文本内容", saved[0].Text);

        // The same id from the same endpoint is answered as delivered without persisting a second copy.
        var again = await AssistantWire.SendItemAsync("127.0.0.1", receiver.Port,
            Item(itemId, "文本内容", "first-contact-token-0123456789"), null, null, TimeSpan.FromSeconds(10), timeout.Token);
        Assert.True(again.Ok);
        Assert.Equal(AssistantWire.DeliveredState, again.State);
        Assert.Single(saved);
    }

    [Fact]
    public async Task ASecondClientCannotSpendAnApprovalWithAForgedDeviceId()
    {
        var authorization = new ReceiveAuthorization(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
        var saved = new List<ReceivedItem>();
        var receiver = Receiver(authorization,
            duplicate: (id, _) => Task.FromResult(saved.Any(item => item.ItemId == id)),
            onItem: (item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        // Client A asks for its item and the user accepts it.
        var first = AssistantWire.SendItemAsync("127.0.0.1", receiver.Port,
            Item("c1b2c3d4e5f60718293a4b5c6d7e8f92", "from A", "token-a-0123456789abcdef"), null, null,
            TimeSpan.FromSeconds(20), timeout.Token);
        var request = await WaitForRequestAsync(authorization);
        Assert.True(authorization.Respond(request.RequestId, accept: true, remember: false, out _));
        Assert.True((await first).Ok);
        Assert.Single(saved);

        // Client B connects from another endpoint and claims A's device id with a different item.
        var forgedId = "d1b2c3d4e5f60718293a4b5c6d7e8f93";
        var forged = AssistantWire.SendItemAsync("127.0.0.1", receiver.Port,
            Item(forgedId, "from B pretending to be A", "token-a-0123456789abcdef"), null, null,
            TimeSpan.FromSeconds(20), timeout.Token);
        var second = await WaitForRequestAsync(authorization);
        Assert.NotEqual(request.RequestId, second.RequestId);
        Assert.False(authorization.IsApproved("phone-sender", "127.0.0.1", forgedId));
        // The user refuses that second request, and B's payload never lands.
        Assert.True(authorization.Respond(second.RequestId, accept: false, remember: false, out _));
        var refused = await forged;
        Assert.False(refused.Ok);
        Assert.Single(saved);

        // Even the same item id is not transferable to another endpoint.
        Assert.False(authorization.IsApproved("phone-sender", "127.0.0.2", "c1b2c3d4e5f60718293a4b5c6d7e8f92"));
    }

    [Fact]
    public async Task ARepeatedRequestKeepsItsRequestIdInsteadOfStackingPrompts()
    {
        var authorization = new ReceiveAuthorization(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5));
        var receiver = Receiver(authorization);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        const string itemId = "e1b2c3d4e5f60718293a4b5c6d7e8f94";

        var first = AssistantWire.SendItemAsync("127.0.0.1", receiver.Port,
            Item(itemId, "retry me", "token-r-0123456789abcdef"), null, null, TimeSpan.FromSeconds(15), timeout.Token);
        var request = await WaitForRequestAsync(authorization);
        var second = AssistantWire.SendItemAsync("127.0.0.1", receiver.Port,
            Item(itemId, "retry me", "token-r-0123456789abcdef"), null, null, TimeSpan.FromSeconds(15), timeout.Token);
        await Task.Delay(150);
        // One prompt for both connections, with the same id the user is looking at.
        Assert.Single(authorization.Pending());
        Assert.Equal(request.RequestId, authorization.Pending()[0].RequestId);

        Assert.True(authorization.Respond(request.RequestId, accept: true, remember: false, out _));
        Assert.True((await first).Ok);
        Assert.True((await second).Ok);
    }

    private static async Task<ReceiveRequest> WaitForRequestAsync(ReceiveAuthorization authorization)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var pending = authorization.Pending();
            if (pending.Count > 0) return pending[0];
            await Task.Delay(25, timeout.Token);
        }
    }

    [Fact]
    public async Task ATrustedDeviceCarriesAFrameWithItsOwnTokenAndFilePayload()
    {
        var saved = new List<ReceivedItem>();
        var trusted = new List<string>();
        var receiver = Receiver(
            trusted: (token, deviceId, _, _, _) => { trusted.Add(deviceId ?? ""); return Task.FromResult(token == ConversationKey); },
            onItem: (item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var payload = Path.Combine(_root, "photo.png");
        var bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        await File.WriteAllBytesAsync(payload, bytes);
        var frame = new AssistantWire.Frame(AssistantWire.Version, AssistantWire.ItemKind, ConversationKey, "photo.png",
            bytes.Length, "phone-other", "c1b2c3d4e5f60718293a4b5c6d7e8f92", "conv-1", AssistantWire.ImageItem, null,
            "Other Phone", null);
        var payloadSelected = false;
        var reply = await AssistantWire.SendItemAsync("127.0.0.1", receiver.Port, frame, payload,
            (_, _) => Assert.True(payloadSelected), TimeSpan.FromSeconds(15), timeout.Token,
            payloadSelected: () => { payloadSelected = true; return Task.CompletedTask; });
        Assert.True(payloadSelected);

        Assert.True(reply.Ok, reply.Message);
        Assert.Equal(AssistantWire.DeliveredState, reply.State);
        var item = Assert.Single(saved);
        Assert.Equal(AssistantWire.ImageItem, item.ItemKind);
        Assert.Equal("photo.png", item.Name);
        Assert.NotNull(item.Path);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(item.Path!));
        Assert.Equal("phone-other", item.DeviceId);
        Assert.Contains("phone-other", trusted);
    }

    [Fact]
    public async Task AnUnknownDeviceIsRefusedWhenNoUserCanBeAsked()
    {
        // Without an authorization queue the receiver cannot ask anyone, so it must refuse instead
        // of silently trusting the caller.
        var receiver = Receiver();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var reply = await AssistantWire.SendItemAsync("127.0.0.1", receiver.Port,
            Item("d1b2c3d4e5f60718293a4b5c6d7e8f93", "hello", "stranger-token-0123456789abc"), null, null,
            TimeSpan.FromSeconds(10), timeout.Token);
        Assert.False(reply.Ok);
        Assert.False(reply.Pending);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "inbox")));
    }

    [Fact]
    public async Task AZeroByteFileIsSavedAndADuplicateAnswerEndsTheSenderSide()
    {
        var saved = new List<ReceivedItem>();
        var receiver = Receiver(
            duplicate: (id, _) => Task.FromResult(saved.Any(item => item.ItemId == id)),
            onItem: (item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var empty = Path.Combine(_root, "empty.bin");
        await File.WriteAllBytesAsync(empty, []);
        const string itemId = "f1b2c3d4e5f60718293a4b5c6d7e8f95";
        var frame = new AssistantWire.Frame(AssistantWire.Version, AssistantWire.ItemKind, Key, "empty.bin",
            0, "phone-sender", itemId, "conv-1", AssistantWire.FileItem, null, "Sender Phone", null);

        var reply = await AssistantWire.SendItemAsync("127.0.0.1", receiver.Port, frame, empty, null, TimeSpan.FromSeconds(15), timeout.Token);
        Assert.True(reply.Ok, reply.Message);
        Assert.Equal(AssistantWire.DeliveredState, reply.State);
        var item = Assert.Single(saved);
        Assert.Equal(0, item.Size);
        Assert.True(File.Exists(item.Path));
        Assert.Empty(await File.ReadAllBytesAsync(item.Path!));

        // A resend of the same id is answered from the stored copy: the sender must not wait for a
        // second acknowledgement that will never come.
        var resent = await AssistantWire.SendItemAsync("127.0.0.1", receiver.Port, frame, empty, null, TimeSpan.FromSeconds(15), timeout.Token);
        Assert.True(resent.Ok, resent.Message);
        Assert.Equal(AssistantWire.DeliveredState, resent.State);
        Assert.Single(saved);
    }

    [Fact]
    public async Task AFrameAddressedToAnotherDeviceIsRefused()
    {
        var saved = new List<ReceivedItem>();
        var receiver = Receiver(onItem: (item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var frame = new AssistantWire.Frame(AssistantWire.Version, AssistantWire.ItemKind, Key, null,
            0, "phone-sender", "a2b2c3d4e5f60718293a4b5c6d7e8f96", "conv-1", AssistantWire.TextItem, "for someone else",
            "Sender Phone", "another-device");
        var reply = await AssistantWire.SendItemAsync("127.0.0.1", receiver.Port, frame, null, null, TimeSpan.FromSeconds(10), timeout.Token);
        Assert.False(reply.Ok);
        Assert.Empty(saved);
    }

    [Fact]
    public async Task AMaximumLengthChineseTextSurvivesTheFrameBound()
    {
        var saved = new List<ReceivedItem>();
        var receiver = Receiver(onItem: (item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // 8192 characters is the documented text limit, and escaping makes the frame much larger than
        // the old 16 KiB bound: the protocol bound has to follow the text limit, not cut it off.
        var text = string.Concat(Enumerable.Repeat("中", 8192));
        var frame = new AssistantWire.Frame(AssistantWire.Version, AssistantWire.ItemKind, Key, null,
            0, "phone-sender", "b3b2c3d4e5f60718293a4b5c6d7e8f97", "conv-1", AssistantWire.TextItem, text,
            "Sender Phone", null);
        var encoded = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(frame, DirectTransfer.Json);
        Assert.True(encoded.Length > 16 * 1024, $"frame was only {encoded.Length} bytes");
        Assert.True(encoded.Length < DirectTransfer.MaxFrameBytes);

        var reply = await AssistantWire.SendItemAsync("127.0.0.1", receiver.Port, frame, null, null,
            TimeSpan.FromSeconds(15), timeout.Token);
        Assert.True(reply.Ok, reply.Message);
        Assert.Equal(AssistantWire.DeliveredState, reply.State);
        var stored = Assert.Single(saved);
        Assert.Equal(8192, stored.Text!.Length);
        Assert.Equal(text, stored.Text);
    }

    [Fact]
    public async Task TheLegacyFileOfferAndProbeStillWorkOnTheSamePort()
    {
        var receiver = Receiver();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var source = Path.Combine(_root, "legacy.txt");
        await File.WriteAllTextAsync(source, "legacy v1");
        var name = await DirectTransfer.SendAsync("127.0.0.1", receiver.Port, Key, source, null, timeout.Token, "old-phone");
        Assert.Equal("legacy.txt", name);
        var probe = await DirectTransfer.ProbeAsync("127.0.0.1", receiver.Port, Key, "pc-receiver", "phone", timeout.Token);
        Assert.True(probe.Verified);
    }

    [Fact]
    public void TheLinkCodeUsesTheFrozenPrefixAndStillDecodesTheLegacyOne()
    {
        var code = LinkCode.Encode(new LinkCode.Payload(1, "self-abcd1234",
            new string('a', 64), "phone-local", "My Phone", "100.64.0.9", 47165, "android"));
        // The frozen activation entry is mpt://assistant/; a shortcut prefix is never produced.
        Assert.StartsWith("mpt://assistant/", code);
        var payload = LinkCode.Decode(code);
        Assert.Equal("self-abcd1234", payload.ConversationId);
        Assert.Equal("100.64.0.9", payload.Address);
        // A code from the earlier build is still readable, but only as input.
        var legacy = "mpt://link/" + code["mpt://assistant/".Length..];
        Assert.Equal("self-abcd1234", LinkCode.Decode(legacy).ConversationId);
        Assert.Throws<ArgumentException>(() => LinkCode.Decode("123456"));
        Assert.Throws<ArgumentException>(() => LinkCode.Decode(code.Replace("mpt://assistant/", "mpt://pair/")));
    }
}
