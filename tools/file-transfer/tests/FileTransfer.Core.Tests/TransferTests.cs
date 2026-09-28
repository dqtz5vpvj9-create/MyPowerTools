using System.Net;
using System.Net.Sockets;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using MyPowerTools.Abstractions;

namespace FileTransfer.Tests;

public sealed class TransferTests : IDisposable
{
    private const string Key = "integration-pairing-key-0123456789";
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-transfer-test-" + Guid.NewGuid().ToString("N"));

    public TransferTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task DirectTransferPublishesBeforeAcknowledgingAndPreservesExistingFiles()
    {
        var source = Path.Combine(_root, "中文 hello.txt");
        var bytes = new byte[524_301];
        new Random(24).NextBytes(bytes);
        await File.WriteAllBytesAsync(source, bytes);
        var target = Path.Combine(_root, "inbox");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, Path.GetFileName(source)), "keep");
        var published = false;
        await using var receiver = new DirectReceiver("127.0.0.1", 0, Key, target, 1_000_000, (_, _, _, _) => { },
            async (path, _) => { Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); published = true; });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var name = await DirectTransfer.SendAsync("127.0.0.1", receiver.Port, Key, source, null, timeout.Token);
        Assert.True(published);
        Assert.Equal("中文 hello (1).txt", name);
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, Path.GetFileName(source))));
    }

    [Theory]
    [InlineData("wrong-key", "fine.txt", 1)]
    [InlineData(Key, "../escape", 1)]
    [InlineData(Key, "fine.txt", 1025)]
    [InlineData(Key, "fine.txt", -1)]
    public async Task RejectedOfferDoesNotCreateFilesOrStopReceiver(string key, string name, long size)
    {
        var target = Path.Combine(_root, "inbox");
        await using var receiver = new DirectReceiver("127.0.0.1", 0, Key, target, 1024, (_, _, _, _) => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", receiver.Port, timeout.Token);
            await DirectTransfer.WriteJsonAsync(client.GetStream(), new DirectTransfer.Offer(1, key, name, size), timeout.Token);
            Assert.False((await DirectTransfer.ReadJsonAsync<DirectTransfer.Reply>(client.GetStream(), timeout.Token)).Ok);
        }
        Assert.Empty(Directory.GetFiles(target));
        var source = Path.Combine(_root, "empty.txt");
        await File.WriteAllTextAsync(source, "");
        await DirectTransfer.SendAsync("127.0.0.1", receiver.Port, Key, source, null, timeout.Token);
        Assert.True(File.Exists(Path.Combine(target, "empty.txt")));
    }

    [Fact]
    public async Task DisconnectRemovesPartialAndNextTransferStillWorks()
    {
        var target = Path.Combine(_root, "inbox");
        await using var receiver = new DirectReceiver("127.0.0.1", 0, Key, target, 1024, (_, _, _, _) => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", receiver.Port, timeout.Token);
            await DirectTransfer.WriteJsonAsync(client.GetStream(), new DirectTransfer.Offer(1, Key, "interrupted.txt", 100), timeout.Token);
            Assert.True((await DirectTransfer.ReadJsonAsync<DirectTransfer.Reply>(client.GetStream(), timeout.Token)).Ok);
            await client.GetStream().WriteAsync(new byte[7], timeout.Token);
        }
        var source = Path.Combine(_root, "next.txt");
        await File.WriteAllTextAsync(source, "next");
        await DirectTransfer.SendAsync("127.0.0.1", receiver.Port, Key, source, null, timeout.Token);
        Assert.Equal(new[] { "next.txt" }, Directory.GetFiles(target).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task StoppingReceiverCancelsActiveReadAndRemovesPartial()
    {
        var target = Path.Combine(_root, "inbox");
        var receiver = new DirectReceiver("127.0.0.1", 0, Key, target, 1024, (_, _, _, _) => { });
        using var client = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.ConnectAsync("127.0.0.1", receiver.Port, timeout.Token);
        await DirectTransfer.WriteJsonAsync(client.GetStream(), new DirectTransfer.Offer(1, Key, "stalled.txt", 100), timeout.Token);
        Assert.True((await DirectTransfer.ReadJsonAsync<DirectTransfer.Reply>(client.GetStream(), timeout.Token)).Ok);
        await receiver.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        Assert.Empty(Directory.GetFiles(target));
    }

    [Theory]
    [InlineData("http://example.org/dav")]
    [InlineData("http://192.168.0.1/dav")]
    [InlineData("https://user:password@example.org/dav")]
    [InlineData("https://example.org/dav?password=secret")]
    public void RejectsCloudUrlsThatLeakCredentials(string url) => Assert.Throws<ArgumentException>(() => new OpenListClient(url, "user", "secret"));

    [Fact]
    public async Task PublishFailureIsRejectedAndTheReceiverKeepsWorking()
    {
        var source = Path.Combine(_root, "publish-fails.txt");
        await File.WriteAllTextAsync(source, "publish-fails");
        var target = Path.Combine(_root, "inbox");
        var published = new List<string>();
        var states = new List<string>();
        var failing = true;
        Func<string, CancellationToken, Task> publish = (path, _) =>
        {
            if (failing) return Task.FromException(new IOException("系统下载目录不可用"));
            published.Add(path);
            return Task.CompletedTask;
        };
        await using var receiver = new DirectReceiver("127.0.0.1", 0, Key, target, 1024,
            (_, _, _, state) => states.Add(state), publish);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<IOException>(() =>
            DirectTransfer.SendAsync("127.0.0.1", receiver.Port, Key, source, null, timeout.Token));
        Assert.Contains("系统下载目录不可用", error.Message);
        Assert.Contains("failed", states);
        Assert.DoesNotContain("received", states);
        // The complete private copy is kept so the user can still take the file out.
        Assert.Equal("publish-fails", await File.ReadAllTextAsync(Path.Combine(target, "publish-fails.txt")));
        // A failed publish must not break the accept loop for the next file.
        failing = false;
        using var retry = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await DirectTransfer.SendAsync("127.0.0.1", receiver.Port, Key, source, null, retry.Token);
        Assert.Single(published);
        Assert.Contains("received", states);
        Assert.True(File.Exists(Path.Combine(target, "publish-fails (1).txt")));
    }

    [Fact]
    public async Task StalePartialsAreSweptWithoutTouchingUserFiles()
    {
        var target = Path.Combine(_root, "inbox");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, ".mpt-deadbeef.part"), "left over");
        await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "keep");
        await using var receiver = new DirectReceiver("127.0.0.1", 0, Key, target, 1024, (_, _, _, _) => { });
        Assert.Equal(new[] { "keep.txt" }, Directory.GetFiles(target).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void SweepPartialsReportsWhatItRemoved()
    {
        var target = Path.Combine(_root, "sweep");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, ".mpt-1.part"), "a");
        File.WriteAllText(Path.Combine(target, ".mpt-2.part"), "b");
        File.WriteAllText(Path.Combine(target, ".mpt-keep.txt"), "c");
        Assert.Equal(2, TransferFiles.SweepPartials(target));
        Assert.Equal(new[] { ".mpt-keep.txt" }, Directory.GetFiles(target).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task MalformedHandshakeDoesNotStopTheReceiver()
    {
        var target = Path.Combine(_root, "inbox");
        await using var receiver = new DirectReceiver("127.0.0.1", 0, Key, target, 1024, (_, _, _, _) => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", receiver.Port, timeout.Token);
            // A header claiming a huge frame must be rejected without killing the accept loop.
            await client.GetStream().WriteAsync(new byte[] { 0x7F, 0xFF, 0xFF, 0xFF }, timeout.Token);
            Assert.False((await DirectTransfer.ReadJsonAsync<DirectTransfer.Reply>(client.GetStream(), timeout.Token)).Ok);
        }
        var source = Path.Combine(_root, "after-garbage.txt");
        await File.WriteAllTextAsync(source, "after");
        await DirectTransfer.SendAsync("127.0.0.1", receiver.Port, Key, source, null, timeout.Token);
        Assert.True(File.Exists(Path.Combine(target, "after-garbage.txt")));
    }

    [Fact]
    public async Task LostListenerIsReportedAsFaultInsteadOfHangingTheWatcher()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(8);
        var target = Path.Combine(_root, "inbox");
        await using var receiver = new DirectReceiver("127.0.0.1", 0, Key, target, 1024, (_, _, _, _) => { },
            listener: listener);
        Assert.Null(receiver.Fault);
        listener.Stop();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await receiver.Completion.WaitAsync(timeout.Token);
        Assert.NotNull(receiver.Fault);
    }

    [Theory]
    [InlineData("{\"adminPassword\":\"hunter2\"}", "hunter2")]
    [InlineData("{\"relayPassword\":\"hunter2\"}", "hunter2")]
    [InlineData("{\"peerToken\":\"hunter2\"}", "hunter2")]
    [InlineData("{\"password\":123456}", "123456")]
    [InlineData("adminPassword=hunter2", "hunter2")]
    [InlineData("relayPassword: hunter2", "hunter2")]
    [InlineData("openlistAdminPassword = hunter2", "hunter2")]
    [InlineData("X-Auth-Token: hunter2", "hunter2")]
    [InlineData("X-Api-Key=hunter2", "hunter2")]
    [InlineData("refresh_token=hunter2", "hunter2")]
    [InlineData("Authorization: Bearer hunter2", "hunter2")]
    [InlineData("password: hunter2", "hunter2")]
    public void RedactorHidesPrefixedAndColonCredentialShapes(string text, string secret)
    {
        var result = MptLogRedactor.Redact(text);
        Assert.DoesNotContain(secret, result);
    }

    [Theory]
    [InlineData("发送给 pc-1234 完成", "pc-1234")]
    [InlineData("Content-Length: 4096", "4096")]
    [InlineData("path=/home/user/Downloads/MPT", "/home/user/Downloads/MPT")]
    public void RedactorKeepsOrdinaryLogText(string text, string kept)
    {
        Assert.Contains(kept, MptLogRedactor.Redact(text));
    }

    [Fact]
    public void CommandLogsRedactPairingAndJsonPasswords()
    {
        var text = "{\"code\":\"mpt://pair/abc_123-ABC\",\"password\":\"secret\\\"value\",\"name\":\"My phone\"}";
        var result = MptLogRedactor.Redact(text);
        Assert.DoesNotContain("abc_123-ABC", result);
        Assert.DoesNotContain("secret", result);
        Assert.Contains("My phone", result);
    }
}
