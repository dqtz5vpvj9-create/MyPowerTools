using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using FileTransfer.Core;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace FileTransfer.MyPowerTools;

public sealed class FileTransferModule : IMptModule
{
    private sealed record ReceiveSession(DirectReceiver Receiver, IDisposable? Activity);

    private readonly Channel<MptModuleEvent> _events = Channel.CreateUnbounded<MptModuleEvent>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateLock = new();
    private readonly List<JsonObject> _history = [];
    private readonly List<Task> _watches = [];
    private CancellationTokenSource? _transfer;
    private Task? _work;
    private Task _save = Task.CompletedTask;
    private ReceiveSession? _session;
    private TransferStore _store = null!;
    private PublishQueue _publishes = null!;
    private readonly SemaphoreSlim _publishing = new(1, 1);
    private OpenListRuntime _openList = null!;
    private ISecretStore _secrets = null!;
    private string _data = "";
    private long _seq;
    private ulong _revision = 1;
    private JsonObject _settings = Defaults();
    private JsonObject? _progress;
    private JsonObject? _active;
    private IBackgroundActivityService? _background;
    private IDownloadsService? _downloads;
    public string Id => "file-transfer";
    public string PackageId => Id;
    public Version Version => new(0, 1, 0);

    private static readonly string[] SettingKeys =
        ["deviceId", "receiveDirectory", "listenAddress", "peerAddress", "webDavUrl", "username", "recipient", "maxReceiveGiB"];
    private static readonly string[] SecretKeys = ["password", "peerToken"];

    private static JsonObject Defaults() => new()
    {
        ["deviceId"] = (OperatingSystem.IsAndroid() ? "phone-" : "pc-") + Guid.NewGuid().ToString("N")[..8], ["receiveDirectory"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MPT"),
        ["listenAddress"] = "", ["peerAddress"] = "", ["webDavUrl"] = "", ["username"] = "", ["recipient"] = "android",
        ["maxReceiveGiB"] = 20, ["peers"] = new JsonArray()
    };

    public async ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken token)
    {
        _data = context.DataDirectory;
        Directory.CreateDirectory(_data);
        _openList = new OpenListRuntime(Path.Combine(_data, "openlist"));
        _secrets = context.GetCapability<ISecretStore>("secret.store");
        if (context.TryGetCapability<IBackgroundActivityService>("background.activity", out var background)) _background = background;
        if (context.TryGetCapability<IDownloadsService>("files.downloads", out var downloads)) _downloads = downloads;
        if (OperatingSystem.IsAndroid()) _settings["receiveDirectory"] = Path.Combine(_data, "incoming");
        var path = Path.Combine(_data, "preferences.json");
        if (File.Exists(path)) _settings = SettingsJson.Merge(Defaults(), await ReadSettingsAsync(path, token));
        if (Setting("listenAddress").Length == 0) _settings["listenAddress"] = TransferFiles.LocalAddresses().FirstOrDefault() ?? "";
        // Load every state file before writing anything, so a damaged file aborts without any rewrite.
        _store = new TransferStore(_data);
        _publishes = new PublishQueue(_data);
        var snapshot = await _store.LoadAsync(token);
        _seq = snapshot.Sequence;
        lock (_stateLock) _history.AddRange(snapshot.Records);
        await File.WriteAllTextAsync(path, _settings.ToJsonString(), token);
        if (snapshot.Active is not null)
        {
            // A transfer that was in flight when the process stopped must not vanish silently.
            Record(RecordJson(snapshot.Active["name"]?.GetValue<string>() ?? "传输", "failed", "上次传输未完成（应用或模块已退出）。"));
            QueuePersist();
        }
        if (await SecretAsync("receiver-token", token) is null)
            await _secrets.SaveAsync(Id, "receiver-token", Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(), token);
        TransferFiles.SweepPartials(Setting("receiveDirectory"));
        SweepOutbox();
        await RetryPublishesAsync(token);
        return new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings", "logs"]);
    }

    /// <summary>Publishes a completed file into the user's Downloads collection and remembers failures.</summary>
    private async Task PublishAsync(string path, CancellationToken token)
    {
        await _publishing.WaitAsync(token);
        try
        {
            try
            {
                await _downloads!.PublishAsync(path, token);
                await _publishes.RemoveAsync(path, CancellationToken.None);
            }
            catch
            {
                // Keep the file and remember it, so the next receive start or app launch retries.
                await _publishes.AddAsync(path, Path.GetFileName(path), CancellationToken.None);
                throw;
            }
        }
        finally { _publishing.Release(); }
    }

    /// <summary>Retries files that a previous publish failure left in the private inbox.</summary>
    private async Task RetryPublishesAsync(CancellationToken token)
    {
        if (_downloads is null) return;
        // A damaged queue is reported by LoadAsync rather than silently reset.
        var pending = await _publishes.LoadAsync(token);
        await _publishing.WaitAsync(token);
        try
        {
            foreach (var entry in pending)
            {
                if (!File.Exists(entry.Path)) { await _publishes.RemoveAsync(entry.Path, token); continue; }
                try
                {
                    await _downloads.PublishAsync(entry.Path, token);
                    await _publishes.RemoveAsync(entry.Path, token);
                    Changed(entry.Name, 0, 0, "received", "已重新发布到系统下载目录。");
                }
                catch (OperationCanceledException) { return; }
                // The platform is still unavailable; the file stays queued for the next attempt.
                catch (Exception) { return; }
            }
        }
        finally { _publishing.Release(); }
    }

    /// <summary>A damaged settings file is reported with its path; it is never overwritten with defaults.</summary>
    private static async Task<JsonObject> ReadSettingsAsync(string path, CancellationToken token)
    {
        JsonObject settings;
        try
        {
            settings = JsonNode.Parse(await File.ReadAllTextAsync(path, token))?.AsObject()
                ?? throw new InvalidDataException("设置文件的根节点必须是对象。");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"设置文件已损坏，未做任何修改：{path}。请修复或重命名该文件后重新加载工具。", ex);
        }
        if (settings["peers"] is not null and not JsonArray)
            throw new InvalidDataException($"设置文件中的设备列表格式无效：{path}。请修复或重命名该文件后重新加载工具。");
        return settings;
    }

    /// <summary>The picker stages content-URI files in the outbox; only stale leftovers are removed.</summary>
    private void SweepOutbox()
    {
        var outbox = Path.Combine(_data, "outbox");
        if (!Directory.Exists(outbox)) return;
        var cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var directory in Directory.EnumerateDirectories(outbox))
        {
            try { if (Directory.GetLastWriteTimeUtc(directory) < cutoff) Directory.Delete(directory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private Task<string?> SecretAsync(string key, CancellationToken token) => _secrets.ReadAsync(SecretReference.Create(Id, key), token);
    private string Setting(string key) => _settings[key]?.GetValue<string>() ?? "";

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken token)
    {
        var session = Volatile.Read(ref _session);
        var text = session is null
            ? "文件互传已就绪；接收未开启。"
            : session.Receiver.Fault is null ? "正在等待 Tailscale 来件。" : "接收已停止；请重新开启接收。";
        return ValueTask.FromResult(new ModuleStatusSnapshot(Id, "running", text, DateTimeOffset.UtcNow, [],
            (ulong)Interlocked.Read(ref _seq)));
    }

    private static readonly string[] Commands = ["inspect", "configure", "pairing", "pair.import", "receive.start", "receive.stop", "send.direct", "send.cloud", "cloud.list", "cloud.download", "cloud.check", "cloud.export", "cloud.import", "cancel", "openlist.start", "openlist.connect", "openlist.stop"];
    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken token) => ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>(
        Commands.Select(c => new MptCommandDescriptor($"{Id}.{c}", Id, c, "文件互传", "action", TimeoutMs: c == "openlist.start" ? 1200000 : 60000,
            SupportsCancellation: true)).ToArray());

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken token)
    {
        try
        {
            object result;
            switch (request.CommandId)
            {
                case "file-transfer.inspect":
                    result = new { settings = _settings.DeepClone(), addresses = TransferFiles.LocalAddresses(),
                        receiving = Volatile.Read(ref _session) is not null, openListRunning = _openList.Running, adminUrl = _openList.AdminUrl,
                        busy = _work is { IsCompleted: false }, progress = _progress, history = History() }; break;
                case "file-transfer.pairing":
                    if (Setting("listenAddress").Length == 0) _settings["listenAddress"] = TransferFiles.LocalAddresses().FirstOrDefault() ?? "";
                    if (Setting("listenAddress").Length == 0) throw new InvalidOperationException("请先连接 Tailscale 网络，然后重试。");
                    result = new { code = new Pairing(Setting("deviceId"), OperatingSystem.IsAndroid() ? "MPT 手机 " + Setting("deviceId") : Environment.MachineName, Setting("listenAddress"), (await SecretAsync("receiver-token", token))!).Encode() }; break;
                case "file-transfer.pair.import":
                    var paired = Pairing.Decode(SettingsJson.ReadString(request.Args, "code") ?? "");
                    await _operations.WaitAsync(token);
                    try
                    {
                        await _secrets.SaveAsync(Id, "peer-" + paired.DeviceId, paired.Token, token);
                        var peers = _settings["peers"]!.AsArray();
                        var old = peers.FirstOrDefault(p => p?["deviceId"]?.GetValue<string>() == paired.DeviceId);
                        if (old is not null) peers.Remove(old);
                        peers.Add(new JsonObject { ["deviceId"] = paired.DeviceId, ["name"] = paired.Name, ["address"] = paired.Address });
                        await PersistSettingsAsync(token);
                    }
                    finally { _operations.Release(); }
                    result = new { paired = paired.Name }; break;
                case "file-transfer.configure":
                    await _operations.WaitAsync(token);
                    try
                    {
                        if (_work is { IsCompleted: false } || Volatile.Read(ref _session) is not null)
                            throw new InvalidOperationException("请先停止接收并等待当前传输结束，再保存设置。");
                        await ApplyValuesAsync(request.Args.DeepClone().AsObject(), true, token);
                    }
                    finally { _operations.Release(); }
                    result = new { saved = true }; break;
                case "file-transfer.receive.start":
                    await _operations.WaitAsync(token);
                    try { await StartReceiveAsync(token); }
                    finally { _operations.Release(); }
                    result = new { receiving = true }; break;
                case "file-transfer.receive.stop":
                    await _operations.WaitAsync(token);
                    try { await StopReceiveAsync(); }
                    finally { _operations.Release(); }
                    result = new { receiving = false }; break;
                case "file-transfer.send.direct":
                case "file-transfer.send.cloud":
                case "file-transfer.cloud.download":
                    await BeginTransferAsync(request, token); result = new { started = true }; break;
                case "file-transfer.cancel":
                    _transfer?.Cancel(); result = new { cancelled = true }; break;
                case "file-transfer.cloud.check":
                    using (var cloud = await CloudAsync(token)) await cloud.CheckAsync(token);
                    result = new { connected = true }; break;
                case "file-transfer.cloud.list":
                    using (var cloud = await CloudAsync(token)) result = await cloud.ListAsync(Setting("deviceId"), token);
                    break;
                case "file-transfer.openlist.start":
                    await _operations.WaitAsync(token);
                    try
                    {
                        var initialPassword = await _openList.InitializeAdminAsync(token);
                        if (initialPassword is not null) await _secrets.SaveAsync(Id, "openlist-admin", initialPassword, token);
                        await _openList.StartAsync(string.IsNullOrWhiteSpace(Setting("listenAddress")) ? "127.0.0.1" : Setting("listenAddress"), token);
                    }
                    finally { _operations.Release(); }
                    result = new { adminUrl = _openList.AdminUrl, password = await SecretAsync("openlist-admin", token) }; break;
                case "file-transfer.openlist.stop":
                    await _operations.WaitAsync(token);
                    try { await _openList.StopAsync(); }
                    finally { _operations.Release(); }
                    result = new { stopped = true }; break;
                case "file-transfer.openlist.connect":
                    await _operations.WaitAsync(token);
                    try
                    {
                        if (!_openList.Running) throw new InvalidOperationException("请先启用本机 OpenList。");
                        var relayPassword = await SecretAsync("managed-relay", token) ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
                        var accountPath = Path.Combine(_data, "relay-account.json");
                        var account = await ReadRelayAccountAsync(accountPath, token);
                        account = await OpenListSetup.ConnectAsync(new Uri(_openList.AdminUrl), (await SecretAsync("openlist-admin", token))!,
                            new Uri(SettingsJson.ReadString(request.Args, "url") ?? ""), relayPassword, account, token);
                        await File.WriteAllTextAsync(accountPath, JsonSerializer.Serialize(account), token);
                        await _secrets.SaveAsync(Id, "managed-relay", relayPassword, token);
                        await _secrets.SaveAsync(Id, "password", relayPassword, token);
                        _settings["webDavUrl"] = new Uri(new Uri(_openList.AdminUrl), "/dav/").ToString();
                        _settings["username"] = account.Username;
                        await PersistSettingsAsync(token);
                    }
                    finally { _operations.Release(); }
                    using (var cloud = await CloudAsync(token)) await cloud.CheckAsync(token);
                    result = new { connected = true }; break;
                case "file-transfer.cloud.export":
                    result = new { code = new CloudConnection(Setting("webDavUrl"), Setting("username"), await SecretAsync("password", token) ?? "").Encode() }; break;
                case "file-transfer.cloud.import":
                    var connection = CloudConnection.Decode(SettingsJson.ReadString(request.Args, "code") ?? "");
                    using (var cloud = new OpenListClient(connection.Url, connection.Username, connection.Password)) await cloud.CheckAsync(token);
                    await _operations.WaitAsync(token);
                    try
                    {
                        if (_work is { IsCompleted: false }) throw new InvalidOperationException("请等待当前传输结束。");
                        await _secrets.SaveAsync(Id, "password", connection.Password, token);
                        _settings["webDavUrl"] = connection.Url;
                        _settings["username"] = connection.Username;
                        await PersistSettingsAsync(token);
                    }
                    finally { _operations.Release(); }
                    result = new { connected = true }; break;
                default: throw new ArgumentException("未知的文件互传操作。");
            }
            return new(request.InvocationId, request.CommandId, "succeeded", true, JsonSerializer.Serialize(result, DirectTransfer.Json));
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "操作已取消。" : MptLogRedactor.Redact(ex.Message);
            return new(request.InvocationId, request.CommandId, "failed", false, message,
                new MptRuntimeError("file-transfer.failed", message));
        }
    }

    /// <summary>A damaged relay identity file is reported instead of silently creating another account.</summary>
    private static async Task<OpenListSetup.RelayAccount?> ReadRelayAccountAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<OpenListSetup.RelayAccount>(await File.ReadAllTextAsync(path, token)); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"OpenList 专用账号记录已损坏，未做任何修改：{path}。请修复或重命名该文件后重试。", ex);
        }
    }

    private async Task<OpenListClient> CloudAsync(CancellationToken token) => new(Setting("webDavUrl"), Setting("username"),
        await SecretAsync("password", token) ?? throw new InvalidOperationException("请先保存 OpenList 密码。"));

    private async Task StartReceiveAsync(CancellationToken token)
    {
        var current = Volatile.Read(ref _session);
        if (current is not null && current.Receiver.Fault is not null)
        {
            // The listener died on its own; its lease must be released before a new session starts.
            if (Interlocked.CompareExchange(ref _session, null, current) == current) await CloseAsync(current);
        }
        if (Volatile.Read(ref _session) is not null) return;
        if (Setting("listenAddress").Length == 0) _settings["listenAddress"] = TransferFiles.LocalAddresses().FirstOrDefault() ?? "";
        if (Setting("listenAddress").Length == 0) throw new InvalidOperationException("请先连接 Tailscale 网络，再开启接收。");
        var activity = _background is null ? null : await _background.BeginAsync(Id, "文件互传正在等待来件", true, token);
        Func<string, CancellationToken, Task>? publish = _downloads is null ? null : PublishAsync;
        ReceiveSession session;
        try
        {
            session = new ReceiveSession(new DirectReceiver(Setting("listenAddress"), TransferFiles.Port,
                (await SecretAsync("receiver-token", token))!, Setting("receiveDirectory"),
                MaximumGiB(_settings) * 1024L * 1024 * 1024, Changed, publish, Changed), activity);
        }
        catch { activity?.Dispose(); throw; }
        _session = session;
        Watch(session);
        await RetryPublishesAsync(token);
    }

    private async Task StopReceiveAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null) await CloseAsync(session);
    }

    private async Task CloseAsync(ReceiveSession session)
    {
        try { await session.Receiver.DisposeAsync(); }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { }
        session.Activity?.Dispose();
    }

    /// <summary>A dead listener must release its background lease instead of holding it forever.</summary>
    private void Watch(ReceiveSession session)
    {
        var task = WatchAsync(session);
        lock (_stateLock) { _watches.RemoveAll(item => item.IsCompleted); _watches.Add(task); }
    }

    private async Task WatchAsync(ReceiveSession session)
    {
        await session.Receiver.Completion;
        if (Interlocked.CompareExchange(ref _session, null, session) != session) return;
        var fault = session.Receiver.Fault;
        await CloseAsync(session);
        if (fault is not null && !_lifetime.IsCancellationRequested)
            Changed("接收", 0, 0, "failed", MptLogRedactor.Redact(fault.Message));
    }

    private async Task BeginTransferAsync(CommandRequest request, CancellationToken token)
    {
        await _operations.WaitAsync(token);
        try
        {
            if (_work is { IsCompleted: false }) throw new InvalidOperationException("已有传输正在进行。");
            _transfer?.Dispose();
            _transfer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var transfer = _transfer;
            var args = request.Args.DeepClone().AsObject();
            var download = request.CommandId == "file-transfer.cloud.download";
            var paths = download ? Array.Empty<string>() : BatchSend.Paths(args);
            if (!download && paths.Count == 0) throw new ArgumentException("请先选择文件。");
            var item = download ? ReadCloudFile(args) : null;
            if (args["peerId"]?.GetValue<string>() is { } lastPeer)
            {
                _settings["lastPeer"] = lastPeer;
                await PersistSettingsAsync(token);
            }
            var activity = _background is null ? null : await _background.BeginAsync(Id, "文件互传进行中", false, token);
            var name = item?.Name ?? (paths.Count > 1 ? $"{paths.Count} 个文件" : BatchSend.Name(paths[0]));
            _work = RunTransferAsync(request.CommandId, args, paths, item, activity, name, transfer.Token);
        }
        finally { _operations.Release(); }
    }

    private async Task RunTransferAsync(string commandId, JsonObject args, IReadOnlyList<string> paths, CloudFile? item,
        IDisposable? activity, string name, CancellationToken token)
    {
        try
        {
            if (item is not null)
            {
                SetActive(name);
                using (var cloud = await CloudAsync(token))
                {
                    var saved = await cloud.DownloadAsync(Setting("deviceId"), item, Setting("receiveDirectory"),
                        (done, total) => Changed(name, done, total, "downloading"), token);
                    if (_downloads is not null)
                    {
                        try { await PublishAsync(saved, token); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or OperationCanceledException)
                        {
                            ClearActive();
                            Changed(name, 0, 0, "failed", "文件已下载到收件文件夹，但发布到系统下载目录失败：" +
                                MptLogRedactor.Redact(ex.Message) + " 重新开启接收或重启应用会自动重试发布。");
                            return;
                        }
                    }
                }
                ClearActive();
                Changed(name, item.Size, item.Size, "completed");
                return;
            }
            var direct = commandId == "file-transfer.send.direct";
            var peerId = args["peerId"]?.GetValue<string>();
            var peer = direct ? _settings["peers"]!.AsArray().FirstOrDefault(p => p?["deviceId"]?.GetValue<string>() == peerId) : null;
            var pairing = direct ? await SecretAsync(peer is null ? "peerToken" : "peer-" + peerId, token) : null;
            if (direct && string.IsNullOrEmpty(pairing)) throw new InvalidOperationException("请先连接对方设备。");
            var address = peer?["address"]?.GetValue<string>() ?? Setting("peerAddress");
            SetActive(name);
            // Every file is attempted; one failure is reported per file instead of dropping the rest.
            var results = await BatchSend.RunAsync(paths, async (path, itemToken) =>
            {
                var itemName = BatchSend.Name(path);
                if (direct)
                    await DirectTransfer.SendAsync(address, TransferFiles.Port, pairing!, path,
                        (done, total) => Changed(itemName, done, total, "sending"), itemToken);
                else
                {
                    using var cloud = await CloudAsync(itemToken);
                    await cloud.UploadAsync(path, peerId ?? Setting("recipient"), Setting("deviceId"),
                        (done, total) => Changed(itemName, done, total, "uploading"), itemToken);
                }
                Changed(itemName, 0, 0, "completed");
            }, token);
            ClearActive();
            var failed = BatchSend.Failed(results);
            if (failed.Count > 0)
                Changed(failed[0].Name, 0, 0, failed.Any(entry => !entry.Cancelled) ? "failed" : "cancelled", BatchSend.Summary(results));
        }
        catch (OperationCanceledException)
        {
            ClearActive();
            Changed(name, 0, 0, "cancelled", "传输已取消。");
        }
        catch (Exception ex)
        {
            ClearActive();
            Changed(name, 0, 0, "failed", MptLogRedactor.Redact(ex.Message));
        }
        finally { activity?.Dispose(); }
    }

    private static CloudFile ReadCloudFile(JsonObject args)
    {
        if (args["file"] is not JsonObject node) throw new ArgumentException("请选择要下载的收件文件。");
        try { return node.Deserialize<CloudFile>(DirectTransfer.Json) ?? throw new InvalidDataException("收件记录无效。"); }
        catch (JsonException) { throw new InvalidDataException("收件记录无效。"); }
    }

    private void Changed(string name, long done, long total, string state) => Changed(name, done, total, state, "");
    private void Changed(string name, long done, long total, string state, string message)
    {
        var item = RecordJson(name, state, message, done, total);
        _progress = item;
        if (state is "completed" or "received" or "failed" or "cancelled")
        {
            Record(item);
            QueuePersist();
        }
        _events.Writer.TryWrite(new(Id, (ulong)Interlocked.Increment(ref _seq), "transfer.changed", DateTimeOffset.UtcNow, item));
    }

    private static JsonObject RecordJson(string name, string state, string message, long done = 0, long total = 0) => new()
    {
        ["name"] = name, ["done"] = done, ["total"] = total, ["state"] = state, ["message"] = message,
        ["time"] = DateTimeOffset.UtcNow.ToString("O")
    };

    private void Record(JsonObject item)
    {
        lock (_stateLock)
        {
            _history.Add(item);
            while (_history.Count > TransferStore.MaxRecords) _history.RemoveAt(0);
        }
    }

    private JsonObject[] History()
    {
        lock (_stateLock) return _history.ToArray();
    }

    private void SetActive(string name)
    {
        lock (_stateLock) _active = RecordJson(name, "started", "");
        QueuePersist();
    }

    private void ClearActive()
    {
        lock (_stateLock) _active = null;
        QueuePersist();
    }

    /// <summary>Persistence is best effort and strictly ordered; a failed write never fails a transfer.</summary>
    private void QueuePersist()
    {
        JsonObject[] records;
        JsonObject? active;
        lock (_stateLock)
        {
            records = _history.Select(item => (JsonObject)item.DeepClone()).ToArray();
            active = _active is null ? null : (JsonObject)_active.DeepClone();
        }
        _save = ChainAsync(_save, records, active);
    }

    private async Task ChainAsync(Task previous, JsonObject[] records, JsonObject? active)
    {
        try { await previous; } catch (Exception) { }
        try { await _store.SaveAsync(records, active, CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private async Task PersistSettingsAsync(CancellationToken token)
    {
        await File.WriteAllTextAsync(Path.Combine(_data, "preferences.json"), _settings.ToJsonString(), token);
        _revision++;
    }

    /// <summary>Applies whitelisted values only, keeps secrets out of preferences.json, then validates.</summary>
    private async Task ApplyValuesAsync(JsonObject values, bool allowSecrets, CancellationToken token)
    {
        foreach (var key in values.Select(pair => pair.Key).ToArray())
        {
            if (allowSecrets && SecretKeys.Contains(key)) continue;
            if (!SettingKeys.Contains(key)) throw new ArgumentException($"不支持设置项：{key}。");
        }
        if (allowSecrets)
        {
            foreach (var key in SecretKeys)
            {
                var secret = SettingsJson.ReadString(values, key);
                values.Remove(key);
                if (!string.IsNullOrEmpty(secret)) await _secrets.SaveAsync(Id, key, secret, token);
            }
        }
        var merged = SettingsJson.Merge(_settings, values);
        Validate(merged);
        _settings = merged;
        await PersistSettingsAsync(token);
    }

    private static int MaximumGiB(JsonObject settings)
    {
        if (SettingsJson.ReadInt(settings, "maxReceiveGiB") is not { } value) throw new ArgumentException("接收大小上限请填写 1–1024 之间的整数。");
        if (value is < 1 or > 1024) throw new ArgumentException("接收限制应在 1–1024 GiB 之间。");
        return value;
    }

    private static void Validate(JsonObject settings)
    {
        TransferFiles.DeviceId(Text(settings, "deviceId"));
        TransferFiles.DeviceId(Text(settings, "recipient"));
        var directory = Text(settings, "receiveDirectory");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("请选择绝对路径作为收件目录。");
        if (directory.Length > 400) throw new ArgumentException("收件目录路径过长。");
        foreach (var key in new[] { "listenAddress", "peerAddress" })
        {
            var address = Text(settings, key);
            if (address.Length > 0 && (!IPAddress.TryParse(address, out var ip) || !TransferFiles.IsTailAddress(ip)))
                throw new ArgumentException("设备地址请填写 Tailscale IP。");
        }
        if (Text(settings, "webDavUrl") is { Length: > 0 } url) OpenListClient.ValidateUrl(new Uri(url));
        var username = Text(settings, "username");
        if (username.Length > 128 || username.Any(char.IsControl)) throw new ArgumentException("OpenList 用户名无效。");
        MaximumGiB(settings);
    }

    private static string Text(JsonObject settings, string key)
    {
        if (!settings.TryGetPropertyValue(key, out var node) || node is null) return "";
        try { return node.GetValue<string>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { throw new ArgumentException($"设置项 {key} 需要文本值。"); }
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken token)
    { await foreach (var item in _events.Reader.ReadAllAsync(token)) if (item.Seq > cursor.LastEventSeq) yield return item; }

    private const string SchemaJson = """
    {
      "type": "object",
      "properties": {
        "deviceId": { "type": "string", "title": "本机收件箱名称" },
        "receiveDirectory": { "type": "string", "title": "收件文件夹（绝对路径）" },
        "listenAddress": { "type": "string", "title": "本机 Tailscale IP" },
        "peerAddress": { "type": "string", "title": "对方 Tailscale IP（未添加设备时使用）" },
        "webDavUrl": { "type": "string", "title": "WebDAV 互传目录" },
        "username": { "type": "string", "title": "OpenList 用户名" },
        "recipient": { "type": "string", "title": "默认收件箱名称" },
        "maxReceiveGiB": { "type": "integer", "title": "接收大小上限（GiB）", "minimum": 1, "maximum": 1024, "default": 20 }
      }
    }
    """;

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken token) => ValueTask.FromResult(new SettingsSchemaDocument(Id, SchemaJson));

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken token) =>
        ValueTask.FromResult(new SettingsSnapshotDocument(Id, _revision, (JsonObject)_settings.DeepClone(), DateTimeOffset.UtcNow));

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken token)
    {
        try
        {
            var values = patch.Patch.DeepClone().AsObject();
            foreach (var key in values.Select(pair => pair.Key)) if (!SettingKeys.Contains(key)) throw new ArgumentException($"不支持设置项：{key}。");
            Validate(SettingsJson.Merge(_settings, values));
            return ValueTask.FromResult(new SettingsValidationResult(true, []));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException)
        {
            return ValueTask.FromResult(new SettingsValidationResult(false, [ex.Message], new MptRuntimeError("file-transfer.settings", ex.Message)));
        }
    }

    public async ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken token)
    {
        await _operations.WaitAsync(token);
        try
        {
            if (_work is { IsCompleted: false } || Volatile.Read(ref _session) is not null) throw new InvalidOperationException("请先停止接收并等待当前传输结束，再保存设置。");
            await ApplyValuesAsync(snapshot.Values.DeepClone().AsObject(), false, token);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException) { }
        finally { _operations.Release(); }
        return await GetSettingsAsync(token);
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken token) => ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([new("file-transfer.detail", "detail-page", "文件互传", new JsonObject())]);

    public async ValueTask DisposeAsync(CancellationToken token)
    {
        await _lifetime.CancelAsync();
        if (_work is { } work)
        {
            try { await work; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
        }
        var session = Interlocked.Exchange(ref _session, null);
        if (session is not null) await CloseAsync(session);
        Task[] watches;
        lock (_stateLock) watches = _watches.ToArray();
        try { await Task.WhenAll(watches); } catch (Exception) { }
        try { await _save; } catch (Exception) { }
        await _openList.DisposeAsync();
        _events.Writer.TryComplete();
        _transfer?.Dispose();
        _lifetime.Dispose();
    }
}
