using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;
using RemoteToolGateway.Core;

namespace RemoteToolGateway.MyPowerTools;

/// <summary>
/// Desktop side of the remote tool contract. The module owns the grant store, the Tailnet-only
/// listener, the authorization/confirmation state and the audit; command execution, cancellation
/// and elevation policy stay in the Runner through the existing HostControl client.
///
/// The listener is off by default. Nothing here polls: the listener waits for connections, and
/// state only changes when the desktop user, the phone or the runtime acts.
/// </summary>
public sealed class RemoteToolGatewayModule : IMptModule
{
    public const string ModuleId = "remote-tool-gateway";
    public const int DefaultPort = 49541;

    private readonly Channel<MptModuleEvent> _events = Channel.CreateUnbounded<MptModuleEvent>();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly object _stateLock = new();
    private CancellationTokenSource? _lifetime = new();
    private ISecretStore _secrets = null!;
    private RemoteToolGatewayService _service = null!;
    private string _data = "";
    private string _lastListenerError = "";
    private long _seq;
    private ulong _revision = 1;
    private JsonObject _settings = Defaults();

    public string Id => ModuleId;
    public string PackageId => ModuleId;
    public Version Version => new(0, 1, 0);

    // Test seams. They are internal (InternalsVisibleTo) so no manifest, preference or HTTP
    // field can swap the executor or relax the Tailnet boundary in a production module.
    internal IHostControlBridge? BridgeOverride { get; init; }
    internal bool AllowLoopbackTransportForTests { get; init; }
    internal RemoteToolGatewayService Service => _service;

    private static readonly string[] SettingKeys = ["listenAddress", "port", "listenerEnabled"];

    private static JsonObject Defaults() => new()
    {
        ["listenAddress"] = "",
        ["port"] = DefaultPort,
        ["listenerEnabled"] = false
    };

    public async ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _data = context.DataDirectory;
        Directory.CreateDirectory(_data);
        _secrets = context.GetCapability<ISecretStore>("secret.store");
        var path = Path.Combine(_data, "preferences.json");
        var loadedSettings = File.Exists(path)
            ? JsonArgs.Merge(Defaults(), await ReadSettingsAsync(path, cancellationToken))
            : Defaults();
        if (JsonArgs.String(loadedSettings, "listenAddress").Length == 0)
        {
            loadedSettings["listenAddress"] = TailnetBinding.LocalAddresses().FirstOrDefault()?.ToString() ?? "";
        }

        string initialJson;
        lock (_stateLock)
        {
            _settings = loadedSettings;
            initialJson = _settings.ToJsonString();
        }

        _service = new RemoteToolGatewayService(_data, _secrets, BridgeOverride ?? new HostControlClientBridge(), new RemoteToolGatewayOptions
        {
            ModuleId = ModuleId,
            DeviceName = Environment.MachineName,
            Platform = PlatformName(),
            DefaultPort = DefaultPort,
            AllowLoopbackTransport = AllowLoopbackTransportForTests
        });
        _service.Changed += OnServiceChanged;
        await _service.InitializeAsync(cancellationToken);
        await WriteSettingsAtomicallyAsync(path, initialJson, cancellationToken);

        if (SettingBool("listenerEnabled"))
        {
            // The user enabled the listener earlier; resume it, but never fail the whole module
            // when Tailscale is not connected yet.
            try
            {
                await StartListenerAsync(Setting("listenAddress"), SettingInt("port"), cancellationToken);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                SetLastListenerError(MptLogRedactor.Redact(ex.Message));
            }
        }

        return new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings", "logs", "detailPage"]);
    }

    private static string PlatformName() =>
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        OperatingSystem.IsAndroid() ? "android" : "linux";

    /// <summary>A damaged settings file is reported with its path; it is never overwritten with defaults.</summary>
    private static async Task<JsonObject> ReadSettingsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))?.AsObject()
                ?? throw new InvalidDataException("设置文件的根节点必须是对象。");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"设置文件已损坏，未做任何修改：{path}。请修复或重命名该文件后重新加载工具。", ex);
        }
    }

    private JsonObject CloneSettings()
    {
        lock (_stateLock)
        {
            return (JsonObject)_settings.DeepClone();
        }
    }

    private string GetLastListenerError()
    {
        lock (_stateLock)
        {
            return _lastListenerError;
        }
    }

    private void SetLastListenerError(string message)
    {
        lock (_stateLock)
        {
            _lastListenerError = message;
        }
    }

    private string Setting(string key)
    {
        lock (_stateLock)
        {
            try { return _settings[key]?.GetValue<string>() ?? ""; }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
        }
    }

    private int SettingInt(string key)
    {
        lock (_stateLock)
        {
            try { return _settings[key]?.GetValue<int>() ?? 0; }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return 0; }
        }
    }

    private bool SettingBool(string key)
    {
        lock (_stateLock)
        {
            try { return _settings[key]?.GetValue<bool>() ?? false; }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return false; }
        }
    }

    // ---- status and commands ------------------------------------------------------------------

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var running = _service.ListenerRunning;
        var grants = _service.Grants.Grants.Count;
        var pending = _service.Invocations.PendingConfirmations().Count;
        var lastError = GetLastListenerError();
        var text = running
            ? $"正在 {TailnetBinding.FormatEndpoint(System.Net.IPAddress.Parse(_service.ListenerAddress), _service.ListenerPort)} 等待已授权的手机；{grants} 个设备授权，{pending} 个待确认。"
            : lastError.Length > 0
                ? "监听未开启：" + lastError
                : $"监听未开启；{grants} 个设备授权，{pending} 个待确认。";
        return ValueTask.FromResult(new ModuleStatusSnapshot(Id, "running", text, DateTimeOffset.UtcNow, [],
            (ulong)Interlocked.Read(ref _seq)));
    }

    private static readonly string[] CommandNames =
    [
        "inspect", "listener.start", "listener.stop", "grant.create", "grant.update", "grant.revoke", "grant.code",
        "confirmation.claim", "confirmation.resolve"
    ];

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<MptCommandDescriptor>>(CommandNames.Select(name => new MptCommandDescriptor(
            $"{ModuleId}.{name}", ModuleId, name, "远程工具访问", "action",
            Category: "Remote", SupportsCancellation: false)).ToArray());

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = request.CommandId switch
            {
                "remote-tool-gateway.inspect" => await InspectAsync(request.Args, cancellationToken),
                "remote-tool-gateway.listener.start" => await StartCommandAsync(request.Args, cancellationToken),
                "remote-tool-gateway.listener.stop" => await StopCommandAsync(cancellationToken),
                "remote-tool-gateway.grant.create" => await _service.CreateGrantAsync(
                    JsonArgs.String(request.Args, "deviceName"),
                    JsonArgs.StringList(request.Args, "commandIds"),
                    JsonArgs.Bool(request.Args, "allowElevated"),
                    cancellationToken),
                "remote-tool-gateway.grant.update" => await _service.UpdateGrantAsync(
                    JsonArgs.String(request.Args, "grantId"),
                    JsonArgs.String(request.Args, "deviceName"),
                    JsonArgs.StringList(request.Args, "commandIds"),
                    JsonArgs.Bool(request.Args, "allowElevated"),
                    cancellationToken),
                "remote-tool-gateway.grant.revoke" => await _service.RevokeGrantAsync(
                    JsonArgs.String(request.Args, "grantId"), cancellationToken),
                "remote-tool-gateway.grant.code" => await _service.GetGrantCodeAsync(
                    JsonArgs.String(request.Args, "grantId"), cancellationToken),
                "remote-tool-gateway.confirmation.claim" => await _service.ClaimConfirmationAsync(
                    JsonArgs.String(request.Args, "invocationId"), cancellationToken),
                "remote-tool-gateway.confirmation.resolve" => _service.ResolveConfirmation(request.Args.DeepClone().AsObject()),
                _ => throw new ArgumentException("未知的远程工具访问操作。")
            };
            return new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true,
                JsonSerializer.Serialize(result, ControlWire.Json));
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "操作已取消。" : MptLogRedactor.Redact(ex.Message);
            return new CommandExecutionResult(request.InvocationId, request.CommandId, "failed", false, message,
                new MptRuntimeError("remote-tool-gateway.failed", message));
        }
    }

    private async Task<JsonObject> InspectAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var includeCatalog = JsonArgs.Bool(args, "includeCatalog", true);
        var payload = await _service.DescribeAsync(SettingBool("listenerEnabled"), includeCatalog, cancellationToken);
        payload["hostReachable"] = await _service.PingHostAsync(cancellationToken);
        lock (_stateLock)
        {
            payload["settings"] = _settings.DeepClone();
            payload["listenerError"] = _lastListenerError;
        }

        return payload;
    }

    private async Task<JsonObject> StartCommandAsync(JsonObject args, CancellationToken cancellationToken)
    {
        var address = JsonArgs.String(args, "address");
        if (address.Length == 0) address = Setting("listenAddress");
        var port = JsonArgs.Int(args, "port", SettingInt("port"));
        var endpoint = await StartListenerAsync(address, port, cancellationToken);
        return new JsonObject { ["running"] = true, ["endpoint"] = endpoint };
    }

    private async Task<JsonObject> StopCommandAsync(CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            await _service.StopListenerAsync();
            lock (_stateLock)
            {
                _settings["listenerEnabled"] = false;
                _lastListenerError = "";
            }

            await PersistSettingsAsync(cancellationToken);
        }
        finally
        {
            _operations.Release();
        }

        return new JsonObject { ["running"] = false };
    }

    private async Task<string> StartListenerAsync(string address, int port, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var resolved = address;
            if (resolved.Length == 0) resolved = TailnetBinding.LocalAddresses().FirstOrDefault()?.ToString() ?? "";
            var endpoint = await _service.StartListenerAsync(resolved, port, cancellationToken);
            lock (_stateLock)
            {
                _settings["listenAddress"] = resolved;
                _settings["port"] = _service.ListenerPort;
                _settings["listenerEnabled"] = true;
                _lastListenerError = "";
            }

            await PersistSettingsAsync(cancellationToken);
            return endpoint;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            SetLastListenerError(MptLogRedactor.Redact(ex.Message));
            throw;
        }
        finally
        {
            _operations.Release();
        }
    }

    // ---- events -------------------------------------------------------------------------------

    private void OnServiceChanged(string type, JsonObject payload)
    {
        var node = (JsonObject)payload.DeepClone();
        node["type"] = type;
        _events.Writer.TryWrite(new MptModuleEvent(Id, (ulong)Interlocked.Increment(ref _seq), "remote-tool-gateway." + type, DateTimeOffset.UtcNow, node));
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(EventCursor cursor, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken))
        {
            if (item.Seq > cursor.LastEventSeq) yield return item;
        }
    }

    // ---- settings -----------------------------------------------------------------------------

    private const string SchemaJson = """
    {
      "type": "object",
      "properties": {
        "listenAddress": { "type": "string", "title": "本机 Tailscale IP（仅监听该地址）" },
        "port": { "type": "integer", "title": "监听端口", "minimum": 1024, "maximum": 65535, "default": 49541 },
        "listenerEnabled": { "type": "boolean", "title": "允许手机访问（默认关闭）", "default": false }
      }
    }
    """;

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsSchemaDocument(Id, SchemaJson));

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        ulong revision;
        JsonObject clone;
        lock (_stateLock)
        {
            revision = _revision;
            clone = (JsonObject)_settings.DeepClone();
        }

        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, revision, clone, DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        try
        {
            var values = patch.Patch.DeepClone().AsObject();
            foreach (var key in values.Select(pair => pair.Key))
            {
                if (!SettingKeys.Contains(key))
                {
                    throw new ArgumentException($"不支持设置项：{key}。");
                }
            }

            ValidateSettings(JsonArgs.Merge(CloneSettings(), values));
            return ValueTask.FromResult(new SettingsValidationResult(true, []));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return ValueTask.FromResult(new SettingsValidationResult(false, [ex.Message],
                new MptRuntimeError("remote-tool-gateway.settings", ex.Message)));
        }
    }

    public async ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var values = snapshot.Values.DeepClone().AsObject();
            foreach (var key in values.Select(pair => pair.Key))
            {
                if (!SettingKeys.Contains(key))
                {
                    throw new ArgumentException($"不支持设置项：{key}。");
                }
            }

            var merged = JsonArgs.Merge(CloneSettings(), values);
            ValidateSettings(merged);

            var enabled = JsonArgs.Bool(merged, "listenerEnabled");
            var address = JsonArgs.String(merged, "listenAddress");
            var port = JsonArgs.Int(merged, "port", DefaultPort);

            try
            {
                if (enabled)
                {
                    // Restarting on a changed address/port keeps the listener on the configured endpoint.
                    await _service.StopListenerAsync();
                    if (address.Length == 0)
                    {
                        address = TailnetBinding.LocalAddresses().FirstOrDefault()?.ToString() ?? "";
                        merged["listenAddress"] = address;
                    }

                    await _service.StartListenerAsync(address, port, cancellationToken);
                }
                else
                {
                    await _service.StopListenerAsync();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                SetLastListenerError(MptLogRedactor.Redact(ex.Message));
                throw;
            }

            lock (_stateLock)
            {
                _settings = merged;
                _lastListenerError = "";
            }

            await PersistSettingsAsync(cancellationToken);
        }
        finally
        {
            _operations.Release();
        }

        return await GetSettingsAsync(cancellationToken);
    }

    private void ValidateSettings(JsonObject settings)
    {
        var address = JsonArgs.String(settings, "listenAddress");
        if (address.Length > 0 && !TailnetBinding.TryParseTailnet(address, out _))
        {
            // Loopback is accepted only by the injected test transport, never by a production module.
            var loopbackInTests = AllowLoopbackTransportForTests
                && System.Net.IPAddress.TryParse(address, out var candidate)
                && System.Net.IPAddress.IsLoopback(candidate);
            if (!loopbackInTests)
            {
                throw new ArgumentException("监听地址必须是本机当前的 Tailscale IP（100.64.0.0/10 或 fd7a:115c:a1e0::/48）。");
            }
        }

        var port = JsonArgs.Int(settings, "port", 0);
        if (port is < 1024 or > 65535)
        {
            throw new ArgumentException("监听端口应在 1024–65535 之间。");
        }

        if (port == RemoteToolGatewayService.FileTransferPort)
        {
            throw new ArgumentException($"端口 {RemoteToolGatewayService.FileTransferPort} 已被文件互传使用，请换一个端口。");
        }

        _ = JsonArgs.Bool(settings, "listenerEnabled");
    }

    private async Task PersistSettingsAsync(CancellationToken cancellationToken)
    {
        string json;
        lock (_stateLock)
        {
            json = _settings.ToJsonString();
        }

        await WriteSettingsAtomicallyAsync(Path.Combine(_data, "preferences.json"), json, cancellationToken);
        lock (_stateLock)
        {
            _revision++;
        }
    }

    private static async Task WriteSettingsAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Settings path '{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try { File.Delete(temporary); } catch (IOException) { }
            }
        }
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>(
            [new("remote-tool-gateway.control", "detail-page", "远程工具访问", new JsonObject())]);

    public async ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        try { await (_lifetime?.CancelAsync() ?? Task.CompletedTask); } catch (ObjectDisposedException) { }
        if (_service is not null)
        {
            _service.Changed -= OnServiceChanged;
            await _service.DisposeAsync();
        }

        _events.Writer.TryComplete();
        _lifetime?.Dispose();
        _lifetime = null;
        _operations.Dispose();
    }
}
