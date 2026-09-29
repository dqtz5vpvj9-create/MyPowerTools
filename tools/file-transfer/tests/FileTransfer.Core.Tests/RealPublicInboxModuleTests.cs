using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FileTransfer.Core;
using FileTransfer.Core.Assistant;
using FileTransfer.MyPowerTools;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace FileTransfer.Tests;

/// <summary>
/// The ordinary device pairing against a <b>real</b> relay process (<c>tools/file-transfer/relay</c>), with
/// two real modules that have no Tailscale address and no manual sync: the receiving side must pick the
/// deposit up on its own, save the real file, write a receipt, and only then may the sender show delivered.
/// The second test pins the permission boundary the pairing code must never cross.
/// </summary>
[CollectionDefinition(RealInboxCollection.Name, DisableParallelization = true)]
public sealed class RealInboxCollection
{
    public const string Name = "real-public-inbox";
}

[Collection(RealInboxCollection.Name)]
public sealed class RealPublicInboxModuleTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("MPT_REAL_INBOX_TEST_ROOT") ?? Path.Combine(
            Environment.GetEnvironmentVariable("MPT_TEST_TEMP") ?? Path.GetTempPath(), "mpt-real-inbox"),
        Guid.NewGuid().ToString("N"));
    private readonly List<FileTransferModule> _modules = [];
    private RelayProcess? _relay;
    private readonly List<RelayProcess> _additionalRelays = [];

    public RealPublicInboxModuleTests()
    {
        Directory.CreateDirectory(_root);
        // A phone without Tailscale: the local address list is empty, so only the public relay can carry this.
        TransferFiles.LocalAddressesOverride = () => [];
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var module in _modules) await module.DisposeAsync(CancellationToken.None);
        PublicRelayClient.BaseAddressOverride = null;
        InboxRelays.EndpointsOverride = null;
        TransferFiles.LocalAddressesOverride = null;
        if (_relay is not null) await _relay.DisposeAsync();
        foreach (var relay in _additionalRelays) await relay.DisposeAsync();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }

    private async Task<RelayProcess> RelayAsync()
    {
        if (_relay is not null) return _relay;
        _relay = new RelayProcess();
        await _relay.StartAsync();
        PublicRelayClient.BaseAddressOverride = () => _relay.BaseAddress;
        return _relay;
    }

    private async Task<FileTransferModule> StartAsync(string name, string deviceId)
    {
        var root = Path.Combine(_root, name);
        Directory.CreateDirectory(root);
        var preferences = new JsonObject
        {
            ["deviceId"] = deviceId,
            ["listenAddress"] = "",
            ["receiveDirectory"] = Path.Combine(root, "inbox"),
            ["peers"] = new JsonArray()
        };
        await File.WriteAllTextAsync(Path.Combine(root, "preferences.json"), preferences.ToJsonString());
        var context = new ModuleContext("test", "1.0", "file-transfer", "file-transfer", root, root, root, "linux",
            ["secret.store"], new Dictionary<string, object> { ["secret.store"] = new InMemorySecretStore() });
        var module = new FileTransferModule();
        _modules.Add(module);
        Assert.True((await module.InitializeAsync(context, CancellationToken.None)).Ok);
        return module;
    }

    private static async Task<JsonObject> CallAsync(FileTransferModule module, string command, JsonObject? args = null)
    {
        var result = await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString("N"), command, args ?? new JsonObject()), CancellationToken.None);
        Assert.True(result.Success, result.Output);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    private static JsonArray Items(JsonObject inspect) => inspect["items"]!.AsArray();

    /// <summary>Waits for real state only; the test never calls a sync command.</summary>
    private static async Task<JsonObject> WaitForItemAsync(FileTransferModule module, string itemId, Func<JsonObject, bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        JsonObject? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = Items(await CallAsync(module, "file-transfer.assistant.inspect"))
                .FirstOrDefault(item => item!["id"]!.GetValue<string>() == itemId) as JsonObject;
            if (last is not null && ready(last)) return last;
            await Task.Delay(200);
        }
        throw new InvalidOperationException("条目状态没有稳定：" + (last?.ToJsonString() ?? "(条目尚未出现)"));
    }

    [Fact]
    public async Task AnOrdinaryPairDeliversAFileOverTheRealRelayWithoutTailscaleOrManualSync()
    {
        await RelayAsync();
        var sender = await StartAsync("laptop", "laptop-a");
        var receiver = await StartAsync("phone", "phone-b");

        var pairing = await CallAsync(receiver, "file-transfer.pairing");
        var code = pairing["code"]!.GetValue<string>();
        Assert.StartsWith(Pairing.Prefix, code);
        var decoded = Pairing.Decode(code);
        Assert.NotNull(decoded.Inbox);
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = code });

        var payload = "real relay payload " + new string('y', 4096);
        var file = Path.Combine(_root, "real.txt");
        await File.WriteAllTextAsync(file, payload);
        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["paths"] = new JsonArray(file), ["targetDeviceId"] = "phone-b" });
        var itemId = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();

        // The receiver is never told to sync: its own inbox loop must pick the deposit up and save it.
        var received = await WaitForItemAsync(receiver, itemId, item => item["state"]!.GetValue<string>() == "available");
        Assert.Equal("real.txt", received["name"]!.GetValue<string>());
        var saved = received["localPath"]!.GetValue<string>();
        Assert.True(File.Exists(saved), saved);
        Assert.Equal(payload, await File.ReadAllTextAsync(saved));

        // Delivered is only ever the real receipt written after that save.
        var delivered = await WaitForItemAsync(sender, itemId, item => item["state"]!.GetValue<string>() == "delivered");
        var receipt = delivered["receipts"]!.AsArray().Single()!.AsObject();
        Assert.Equal("phone-b", receipt["deviceId"]!.GetValue<string>());
        Assert.True(receipt["bytes"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task APairedDeviceGainsNoConversationPermissionFromTheCode()
    {
        await RelayAsync();
        var sender = await StartAsync("laptop", "laptop-a");
        var receiver = await StartAsync("phone", "phone-b");
        var pairing = await CallAsync(receiver, "file-transfer.pairing");
        var code = pairing["code"]!.GetValue<string>();
        var inbox = Pairing.Decode(code).Inbox!;
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = code });

        // The code carries file-delivery permission only: no conversation membership in either direction.
        var senderIdentity = (await CallAsync(sender, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
        Assert.Equal(0, senderIdentity["ownDevices"]!.GetValue<int>());
        Assert.False(senderIdentity["linked"]!.GetValue<bool>());
        var receiverIdentity = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["identity"]!.AsObject();
        Assert.Equal(0, receiverIdentity["ownDevices"]!.GetValue<int>());
        Assert.DoesNotContain("laptop-a", receiverIdentity.ToJsonString());

        // The real relay enforces the same boundary, so the check goes to the server itself: the deposit
        // key may deliver, but it may not register an inbox and may not list one.
        // The receiver must have registered before the server can tell "not your role" (403) apart from
        // "unknown inbox" (401); both prove a deposit key cannot list, and the registered case is the
        // deterministic one to assert.
        var registered = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < registered)
        {
            var state = (await CallAsync(receiver, "file-transfer.assistant.inspect"))["inbox"]!.AsObject();
            if (state["state"]!.GetValue<string>() == "available") break;
            await Task.Delay(50);
        }
        var relay = await RelayAsync();
        using var depositOnly = new HttpClient { BaseAddress = relay.BaseAddress };
        depositOnly.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{inbox.InboxId}:{inbox.DepositKey}")));
        var registration = new JsonObject { ["depositKey"] = inbox.DepositKey }.ToJsonString();
        using (var register = await depositOnly.PostAsync(PublicInboxClient.InboxesPath,
            new StringContent(registration, System.Text.Encoding.UTF8, "application/json")))
        {
            // The deployed server refuses it (401, or 400 when it rejects the key pair first); what matters
            // is that deposit credentials can never create or re-key an inbox.
            Assert.True((int)register.StatusCode >= 400, $"deposit 凭据不得注册收件箱：{(int)register.StatusCode}");
        }
        using (var list = await depositOnly.GetAsync(PublicInboxClient.ItemsPath + "?since=0"))
        {
            // 403 for a registered inbox whose caller is not the owner; 401 if the key pair is not known
            // at all. Neither may ever be a successful list.
            Assert.True(list.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized,
                $"deposit 凭据不得列出收件箱：{(int)list.StatusCode}");
        }
    }

    [Fact]
    public async Task AReceiverNeverRegisteredOnTailStillReceivesThroughTheRealPublicRelay()
    {
        var publicRelay = await RelayAsync();
        var tail = new RelayProcess();
        _additionalRelays.Add(tail);
        await tail.StartAsync();
        InboxRelays.EndpointsOverride = () => [new(InboxRelays.Tail, tail.BaseAddress), new(InboxRelays.Public, publicRelay.BaseAddress)];
        var identity = PublicInboxIdentity.CreateNew("inbox-" + Guid.NewGuid().ToString("N"));
        using var owner = PublicInboxClient.Owner(identity, PublicInboxRetryPolicy.None, publicRelay.BaseAddress);
        await owner.RegisterAsync();
        using (var tailProbe = PublicInboxClient.Deposit(identity.Pairing, PublicInboxRetryPolicy.None, tail.BaseAddress))
        {
            var absent = await Assert.ThrowsAsync<PublicInboxUnavailableException>(
                () => tailProbe.GetReceiptAsync(PublicInboxIds.NewItemId()));
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, absent.Status);
            Assert.Equal("inbox_not_ready", absent.Code);
            var putAbsent = await Assert.ThrowsAsync<PublicInboxUnavailableException>(
                () => tailProbe.DepositTextAsync(PublicInboxIds.NewItemId(), "not registered"));
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, putAbsent.Status);
            Assert.Equal("inbox_not_ready", putAbsent.Code);
        }
        var sender = await StartAsync("laptop", "laptop-a");
        var code = new Pairing("phone-b", "Public only phone", "", new string('a', 64), identity.Pairing).Encode();
        await CallAsync(sender, "file-transfer.pair.import", new JsonObject { ["code"] = code });
        var sent = await CallAsync(sender, "file-transfer.assistant.send",
            new JsonObject { ["text"] = "only public is reachable", ["targetDeviceId"] = "phone-b" });
        var id = sent["itemIds"]!.AsArray()[0]!.GetValue<string>();
        await WaitForItemAsync(sender, id, item => item["state"]!.GetValue<string>() == "stored");
        var page = await owner.PollAsync();
        Assert.Equal(id, Assert.Single(page.Items).ItemId);
        using var buffer = new MemoryStream();
        await owner.DownloadAsync(id, buffer);
        Assert.Equal("only public is reachable", System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
        await owner.AcknowledgeAsync(id, buffer.Length, deviceId: "phone-b", deviceName: "Public only phone");
        var delivered = await WaitForItemAsync(sender, id, item => item["state"]!.GetValue<string>() == "delivered");
        Assert.Equal("public", Assert.Single(delivered["depositRoutes"]!.AsArray())!["relayId"]!.GetValue<string>());
    }

    /// <summary>Starts the repository's real relay as a child process, mirroring the client-test fixture.</summary>
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

        private readonly string _serverDirectory;
        private readonly string _stdoutLog;
        private readonly string _stderrLog;
        private Process? _process;
        private Task _stdoutPump = Task.CompletedTask;
        private Task _stderrPump = Task.CompletedTask;

        public RelayProcess()
        {
            var name = Guid.NewGuid().ToString("N")[..8];
            _serverDirectory = Path.Combine(RootDirectory, "servers", name);
            DataDirectory = Path.Combine(_serverDirectory, "data");
            _stdoutLog = Path.Combine(RootDirectory, "logs", $"relay-{name}.stdout.log");
            _stderrLog = Path.Combine(RootDirectory, "logs", $"relay-{name}.stderr.log");
        }

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
                UseShellExecute = false
            };
            info.ArgumentList.Add(launcher);
            info.ArgumentList.Add(Path.Combine(RepositoryRoot, "tools", "file-transfer", "relay"));
            info.ArgumentList.Add(DataDirectory);
            foreach (var inherited in info.Environment.Keys.Where(key => key.StartsWith("MPT_RELAY_", StringComparison.OrdinalIgnoreCase)).ToList())
                info.Environment.Remove(inherited);
            // Test-only abuse-control relaxation: two inboxes and a few deliberate wrong-key calls.
            info.Environment["MPT_RELAY_REGISTER_PER_IP_PER_HOUR"] = "1000";
            info.Environment["MPT_RELAY_REGISTER_GLOBAL_PER_HOUR"] = "5000";
            info.Environment["MPT_RELAY_AUTH_FAILURES_PER_IP"] = "0";
            info.Environment["MPT_RELAY_NOT_READY_PER_IP_PER_MINUTE"] = "600";

            _process = Process.Start(info) ?? throw new InvalidOperationException("无法启动真实的中转服务进程。");
            string? ready;
            try { ready = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            catch (TimeoutException) { ready = null; }
            if (ready is null || !ready.StartsWith("MPT_RELAY_READY port=", StringComparison.Ordinal))
            {
                var diagnostics = await _process.StandardError.ReadToEndAsync();
                await DisposeAsync();
                throw new InvalidOperationException(
                    $"真实中转服务没有启动（{PythonExecutable} {launcher}）：{ready ?? "<no output>"}\n{diagnostics}\nstdout 日志：{_stdoutLog}");
            }
            BaseAddress = new Uri($"http://127.0.0.1:{ready["MPT_RELAY_READY port=".Length..].Trim()}");
            _stdoutPump = PumpAsync(_process.StandardOutput, _stdoutLog);
            _stderrPump = PumpAsync(_process.StandardError, _stderrLog);
        }

        public async ValueTask DisposeAsync()
        {
            if (_process is { } process)
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
                process.Dispose();
                _process = null;
            }
            foreach (var pump in new[] { _stdoutPump, _stderrPump })
            {
                try { await pump.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            }
        }

        private static async Task PumpAsync(StreamReader reader, string path)
        {
            await using var writer = new StreamWriter(path, append: true) { AutoFlush = true };
            while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line);
        }

        private static string ResolveRootDirectory()
        {
            if (Environment.GetEnvironmentVariable("MPT_INBOX_TEST_ROOT") is { Length: > 0 } configured)
            {
                var path = Path.Combine(configured, "module-tests");
                Directory.CreateDirectory(path);
                return path;
            }
            var local = Path.Combine(ResolveRepositoryRoot(), "artifacts", ".tmp-android-verify", "public-inbox-module");
            Directory.CreateDirectory(local);
            return local;
        }

        private static string ResolveRepositoryRoot()
        {
            if (Environment.GetEnvironmentVariable("MPT_INBOX_TEST_REPO_ROOT") is { Length: > 0 } configured) return configured;
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
    }
}
