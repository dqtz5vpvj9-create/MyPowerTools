using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MyPowerTools.Abstractions;
using MyPowerTools.Platform.Abstractions;

namespace MobileToolControl.Android;

/// <summary>
/// Phone-side module for controlling tools on a computer over the Tailnet gateway
/// (<c>docs/mobile-ux/REMOTE_CONTROL_CONTRACT.md</c>).
///
/// The module is the only component on the phone that talks to the computer. It owns the imported
/// device records, the grant token in the platform secret store, the <c>/mpt-control/v1</c> HTTP
/// client, the short-lived catalog cache and the known invocations. The page drives it exclusively
/// through the commands below, so there is exactly one HTTP implementation and one credential owner.
///
/// Lifecycle rules:
/// <list type="bullet">
/// <item>initialize, enable, start and settings changes never open a socket: a connection happens
/// only inside an explicit command (import preview/confirm, catalog, check, invoke, status poll,
/// cancel), and every request ends with the command;</item>
/// <item>there is no timer, no polling loop and no background worker: the catalog cache is lazy
/// (used only by a later command) and expires on its own;</item>
/// <item>disable/stop/dispose cancel in-flight calls and drop cached documents and tokens; the next
/// command re-reads the secret store.</item>
/// </list>
/// </summary>
public sealed class MobileToolControlModule : IMptModule, IMptModuleLifecycle
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private const string StoppedMessage = "电脑工具模块已停用，请重新启用后再试。";

    private readonly Channel<MptModuleEvent> _events = Channel.CreateBounded<MptModuleEvent>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly ConcurrentDictionary<string, CachedCatalog> _catalogs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MobileToolInvocation> _invocations = new(StringComparer.Ordinal);

    private readonly object _lifecycleLock = new();
    // Cancelled sources are kept (not disposed) until the module itself is disposed: a request may
    // still be unwinding on the token when Stop/Disable returns, and disposing under it would turn a
    // cancellation into an ObjectDisposedException.
    private readonly List<CancellationTokenSource> _retiredLifetimes = [];

    private ModuleContext? _context;
    private ISecretStore? _secrets;
    private MobileToolControlSecrets? _secretAccess;
    private MobileToolControlDeviceStore? _devices;
    private CancellationTokenSource? _lifecycle;
    private bool _stopped = true;
    private string _dataRoot = "";
    private long _eventSeq;
    private string _activeInvocationId = "";
    private string _activeDeviceId = "";
    private MobileToolInvocation? _lastInvocation;
    private string _lastDeviceId = "";
    private bool _disposed;

    public string Id => MobileToolControlOptions.ModuleId;

    public string PackageId => MobileToolControlOptions.PackageId;

    public Version Version => new(0, 1, 0);

    /// <summary>
    /// Test seam: the address policy used for imports and every request. Production keeps
    /// <see cref="TailnetEndpointPolicy"/>; a test substitutes a loopback-only policy so the real HTTP
    /// client can be driven against a local gateway double. There is no settings, preferences or
    /// command path that can set this.
    /// </summary>
    internal IMobileToolEndpointPolicy EndpointPolicy { get; set; } = TailnetEndpointPolicy.Instance;

    private ModuleContext Context =>
        _context ?? throw new InvalidOperationException("电脑工具模块尚未初始化。");

    private MobileToolControlDeviceStore Devices =>
        _devices ?? throw new InvalidOperationException("电脑工具模块尚未初始化。");

    private MobileToolControlSecrets SecretAccess =>
        _secretAccess ?? throw new InvalidOperationException("电脑工具模块尚未初始化。");

    // ---------------------------------------------------------------- lifecycle

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _context = context;
        _disposed = false;

        _dataRoot = context.DataDirectory;
        Directory.CreateDirectory(_dataRoot);
        Directory.CreateDirectory(context.CacheDirectory);
        Directory.CreateDirectory(context.LogDirectory);

        _secrets = context.GetCapability<ISecretStore>("secret.store");
        _secretAccess = new MobileToolControlSecrets(_secrets);
        _devices = new MobileToolControlDeviceStore(Path.Combine(_dataRoot, MobileToolControlOptions.DevicesFileName));
        _devices.Load();
        EnsureLifetime();

        // Nothing else happens here on purpose: no socket, no token read, no directory scan.
        return ValueTask.FromResult(new InitializeResult(
            true,
            context.ProtocolVersion,
            ["status", "commands", "settings", "logs"]));
    }

    // Enable and Start are both real (re)activations: the Android host may call either after a
    // Stop/Disable, and the module must be usable again instead of staying permanently stopped.
    public ValueTask EnableAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        EnsureLifetime();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisableAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        StopLifetime();
        return ValueTask.CompletedTask;
    }

    public ValueTask StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        EnsureLifetime();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        StopLifetime();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        _disposed = true;
        StopLifetime();
        lock (_lifecycleLock)
        {
            foreach (var source in _retiredLifetimes)
            {
                source.Dispose();
            }

            _retiredLifetimes.Clear();
        }

        _catalogs.Clear();
        _invocations.Clear();
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Creates a fresh lifetime when the module is enabled/started (or initialized). A cancelled
    /// lifetime is never reused, so a request admitted after a restart is not already cancelled.
    /// </summary>
    private void EnsureLifetime()
    {
        lock (_lifecycleLock)
        {
            if (_lifecycle is null || _lifecycle.IsCancellationRequested)
            {
                _lifecycle = new CancellationTokenSource();
            }

            _stopped = false;
        }
    }

    /// <summary>
    /// Cancels every request that is still in flight and blocks new ones until the module is
    /// enabled/started again. Stop/disable/dispose use it; an individual command never cancels
    /// another command's call.
    /// </summary>
    private void StopLifetime()
    {
        CancellationTokenSource? source;
        lock (_lifecycleLock)
        {
            source = _lifecycle;
            _lifecycle = null;
            _stopped = true;
            if (source is not null)
            {
                _retiredLifetimes.Add(source);
            }
        }

        source?.Cancel();
        _activeInvocationId = "";
        _activeDeviceId = "";
    }

    private bool IsStopped
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _stopped;
            }
        }
    }

    /// <summary>
    /// One command's cancellation scope, linked to the module lifetime that is current when the
    /// command starts. Returns <see langword="null"/> when the module is stopped, so a command can
    /// refuse instead of opening a request nothing will cancel.
    /// </summary>
    private CancellationTokenSource? TryCreateCall(CancellationToken cancellationToken)
    {
        lock (_lifecycleLock)
        {
            if (_stopped || _lifecycle is null || _lifecycle.IsCancellationRequested)
            {
                return null;
            }

            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifecycle.Token);
        }
    }

    // ---------------------------------------------------------------- status

    public async ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var payload = await BuildStatusJsonAsync(cancellationToken).ConfigureAwait(false);
        var devices = Devices.Devices.Count;
        return new ModuleStatusSnapshot(
            Id,
            "ready",
            devices == 0 ? "尚未导入电脑" : $"已导入 {devices} 台电脑",
            DateTimeOffset.UtcNow,
            [
                new HealthCheckSnapshot("secret.store", "凭据存储", _secrets is not null, _secrets is null ? "平台凭据库不可用。" : "可用"),
                new HealthCheckSnapshot("endpoint.policy", "地址边界", true, EndpointPolicy.Rule)
            ],
            (ulong)Interlocked.Read(ref _eventSeq));
    }

    private async Task<JsonObject> BuildStatusJsonAsync(CancellationToken cancellationToken)
    {
        var devices = new JsonArray();
        foreach (var device in Devices.Devices)
        {
            devices.Add(await DeviceJsonAsync(device, cancellationToken).ConfigureAwait(false));
        }

        var payload = new JsonObject
        {
            ["devices"] = devices,
            ["deviceCount"] = Devices.Devices.Count,
            ["activeInvocationId"] = _activeInvocationId,
            ["activeDeviceId"] = _activeDeviceId,
            ["transport"] = "tailnet-http",
            ["endpointRule"] = EndpointPolicy.Rule,
            ["loadError"] = Devices.LoadError,
            ["dataDirectory"] = _dataRoot
        };

        if (_lastInvocation is not null)
        {
            payload["lastResult"] = InvocationJson(_lastInvocation, _lastDeviceId);
        }

        return payload;
    }

    private async Task<JsonObject> DeviceJsonAsync(MobileToolDevice device, CancellationToken cancellationToken)
    {
        var configured = await SecretAccess.HasTokenAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["deviceId"] = device.DeviceId,
            ["deviceName"] = device.DeviceName,
            ["endpoint"] = device.Endpoint,
            ["platform"] = device.Platform,
            ["importedAt"] = device.ImportedAt.ToString("O"),
            ["lastState"] = device.LastState,
            ["lastDetail"] = device.LastDetail,
            ["lastCheckedAt"] = device.LastCheckedAt,
            ["credentialConfigured"] = configured
        };
    }

    private static JsonObject InvocationJson(MobileToolInvocation invocation, string deviceId) => new()
    {
        ["deviceId"] = deviceId,
        ["invocationId"] = invocation.InvocationId,
        ["commandId"] = invocation.CommandId,
        ["state"] = invocation.State,
        ["message"] = invocation.Message,
        ["terminal"] = invocation.Terminal,
        // The last value the computer reported; false when nothing has reported one yet.
        ["cancelAccepted"] = invocation.CancelAccepted ?? false,
        ["result"] = invocation.Result?.DeepClone()
    };

    // ---------------------------------------------------------------- commands

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command(MobileToolControlOptions.CommandStatus, "查看电脑工具状态", "已导入的电脑、调用状态与地址边界"),
            Command(
                MobileToolControlOptions.CommandImportPreview,
                "预览连接码",
                "解析 mpt://control 连接码并显示目标电脑，不会保存任何内容",
                parameters: [Parameter(MobileToolControlOptions.ArgumentCode, "连接码", "string", required: true)]),
            Command(
                MobileToolControlOptions.CommandImportConfirm,
                "确认导入连接码",
                "用户确认后把凭据写入平台凭据库（不会显示或返回凭据）",
                parameters:
                [
                    Parameter(MobileToolControlOptions.ArgumentCode, "连接码", "string", required: true),
                    Parameter(MobileToolControlOptions.ArgumentAccepted, "已确认", "boolean")
                ]),
            Command(MobileToolControlOptions.CommandDevicesList, "列出已导入电脑", "读取本机保存的电脑记录"),
            Command(
                MobileToolControlOptions.CommandDevicesCheck,
                "检查电脑连接",
                "显式检查一次网关是否可达（不会建立常驻连接）",
                parameters: [Parameter(MobileToolControlOptions.ArgumentDeviceId, "电脑", "string", required: true)],
                timeoutMs: 40000),
            Command(
                MobileToolControlOptions.CommandDevicesRemove,
                "移除电脑",
                "删除本机保存的记录与平台凭据库中的授权凭据",
                parameters: [Parameter(MobileToolControlOptions.ArgumentDeviceId, "电脑", "string", required: true)]),
            Command(
                MobileToolControlOptions.CommandCatalog,
                "读取电脑工具目录",
                "读取这台电脑实际提供的工具与命令；refresh=true 强制刷新",
                parameters:
                [
                    Parameter(MobileToolControlOptions.ArgumentDeviceId, "电脑", "string", required: true),
                    Parameter(MobileToolControlOptions.ArgumentRefresh, "强制刷新", "boolean")
                ],
                timeoutMs: 40000),
            Command(
                MobileToolControlOptions.CommandInvoke,
                "调用电脑命令",
                "提交一次调用；是否执行由电脑端授权与确认决定",
                parameters:
                [
                    Parameter(MobileToolControlOptions.ArgumentDeviceId, "电脑", "string", required: true),
                    Parameter(MobileToolControlOptions.ArgumentCommandId, "命令", "string", required: true),
                    Parameter(MobileToolControlOptions.ArgumentArgs, "参数", "object"),
                    Parameter(MobileToolControlOptions.ArgumentInvocationId, "调用 ID", "string", required: true)
                ],
                timeoutMs: 60000,
                supportsProgress: true,
                supportsCancellation: true),
            Command(
                MobileToolControlOptions.CommandInvocationStatus,
                "读取调用状态",
                "按需读取一次调用状态；终态后不再请求",
                parameters:
                [
                    Parameter(MobileToolControlOptions.ArgumentDeviceId, "电脑", "string", required: true),
                    Parameter(MobileToolControlOptions.ArgumentInvocationId, "调用 ID", "string", required: true)
                ],
                timeoutMs: 40000),
            Command(
                MobileToolControlOptions.CommandInvokeCancel,
                "取消调用",
                "请求电脑取消这次调用，并返回电脑给出的真实状态",
                parameters:
                [
                    Parameter(MobileToolControlOptions.ArgumentDeviceId, "电脑", "string", required: true),
                    Parameter(MobileToolControlOptions.ArgumentInvocationId, "调用 ID", "string", required: true)
                ],
                timeoutMs: 40000,
                supportsCancellation: true)
        ];
        return ValueTask.FromResult(commands);
    }

    private static MptCommandDescriptor Command(
        string id,
        string title,
        string subtitle,
        IReadOnlyList<CommandParameterDescriptor>? parameters = null,
        int timeoutMs = 20000,
        bool supportsProgress = false,
        bool supportsCancellation = false) =>
        new(
            id,
            MobileToolControlOptions.ModuleId,
            title,
            subtitle,
            "action",
            Category: MobileToolControlOptions.DisplayName,
            TimeoutMs: timeoutMs,
            Execution: new JsonObject { ["type"] = "module.execute" },
            Parameters: parameters,
            SupportsProgress: supportsProgress,
            SupportsCancellation: supportsCancellation);

    private static CommandParameterDescriptor Parameter(
        string id,
        string label,
        string type,
        bool required = false) => new(id, label, type, required, "");

    public async ValueTask<CommandExecutionResult> ExecuteCommandAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (IsStopped)
        {
            return Failed(request, MobileToolControlErrorCodes.Unavailable, StoppedMessage);
        }

        try
        {
            return request.CommandId switch
            {
                MobileToolControlOptions.CommandStatus => Succeeded(
                    request,
                    (await BuildStatusJsonAsync(cancellationToken).ConfigureAwait(false)).ToJsonString(IndentedJson)),
                MobileToolControlOptions.CommandImportPreview => PreviewImport(request),
                MobileToolControlOptions.CommandImportConfirm => await ConfirmImportAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                MobileToolControlOptions.CommandDevicesList => await ListDevicesAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                MobileToolControlOptions.CommandDevicesCheck => await CheckDeviceAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                MobileToolControlOptions.CommandDevicesRemove => await RemoveDeviceAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                MobileToolControlOptions.CommandCatalog => await CatalogAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                MobileToolControlOptions.CommandInvoke => await InvokeAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                MobileToolControlOptions.CommandInvocationStatus => await InvocationStatusAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                MobileToolControlOptions.CommandInvokeCancel => await CancelAsync(request, cancellationToken)
                    .ConfigureAwait(false),
                _ => Failed(
                    request,
                    MobileToolControlErrorCodes.NotFound,
                    $"电脑工具模块未实现命令 '{request.CommandId}'。")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The module was stopped or disabled while the request was in flight.
            return Failed(request, MobileToolControlErrorCodes.Cancelled, "调用已取消。");
        }
        catch (MobileToolControlException exception)
        {
            return Failed(request, exception);
        }
        catch (Exception exception)
        {
            return Failed(request, MobileToolControlErrorCodes.Protocol, exception.Message);
        }
    }

    // ---------------------------------------------------------------- import

    private CommandExecutionResult PreviewImport(CommandRequest request)
    {
        var code = ReadString(request, MobileToolControlOptions.ArgumentCode);
        var policy = EndpointPolicy;
        if (!MobileToolConnectionCodeParser.TryParse(code, policy, out var parsed, out var error))
        {
            return Succeeded(request, new JsonObject
            {
                ["ok"] = false,
                ["error"] = error
            }.ToJsonString(IndentedJson));
        }

        var existing = Devices.Find(parsed.GrantId);
        return Succeeded(request, new JsonObject
        {
            ["ok"] = true,
            ["error"] = "",
            ["version"] = parsed.Version,
            ["endpoint"] = parsed.Endpoint.Origin,
            ["endpointDisplay"] = parsed.Endpoint.Display,
            ["grantId"] = parsed.GrantId,
            ["deviceName"] = parsed.ResolvedDeviceName,
            ["tokenConfigured"] = true,
            ["alreadyImported"] = existing is not null,
            ["replacesExisting"] = existing is not null,
            ["addressRule"] = policy.Rule
        }.ToJsonString(IndentedJson));
    }

    private async Task<CommandExecutionResult> ConfirmImportAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        if (!ReadFlag(request, MobileToolControlOptions.ArgumentAccepted))
        {
            return Failed(
                request,
                MobileToolControlErrorCodes.ValidationFailed,
                "需要先确认目标电脑，才会保存凭据。");
        }

        var code = ReadString(request, MobileToolControlOptions.ArgumentCode);
        if (!MobileToolConnectionCodeParser.TryParse(code, EndpointPolicy, out var parsed, out var error))
        {
            return Failed(request, MobileToolControlErrorCodes.EndpointRefused, error);
        }

        using var call = TryCreateCall(cancellationToken);
        if (call is null)
        {
            return Failed(request, MobileToolControlErrorCodes.Unavailable, StoppedMessage);
        }
        await SecretAccess.SaveAsync(parsed.GrantId, parsed.Token, call.Token).ConfigureAwait(false);

        var existing = Devices.Find(parsed.GrantId);
        Devices.Upsert(new MobileToolDevice(
            parsed.GrantId,
            parsed.ResolvedDeviceName,
            parsed.Endpoint.Origin,
            existing?.Platform ?? "",
            DateTimeOffset.UtcNow,
            existing?.LastState ?? "imported",
            existing?.LastDetail ?? "尚未检查",
            existing?.LastCheckedAt ?? ""));

        Publish(MobileToolControlOptions.EventDeviceImported, new JsonObject
        {
            ["deviceId"] = parsed.GrantId,
            ["deviceName"] = parsed.ResolvedDeviceName,
            ["endpoint"] = parsed.Endpoint.Origin,
            ["replaced"] = existing is not null
        });

        var payload = await ListDevicesPayloadAsync(call.Token).ConfigureAwait(false);
        payload["imported"] = parsed.GrantId;
        return Succeeded(request, payload.ToJsonString(IndentedJson));
    }

    // ---------------------------------------------------------------- devices

    private async Task<CommandExecutionResult> ListDevicesAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        using var call = TryCreateCall(cancellationToken);
        if (call is null)
        {
            return Failed(request, MobileToolControlErrorCodes.Unavailable, StoppedMessage);
        }
        return Succeeded(
            request,
            (await ListDevicesPayloadAsync(call.Token).ConfigureAwait(false)).ToJsonString(IndentedJson));
    }

    private async Task<JsonObject> ListDevicesPayloadAsync(CancellationToken cancellationToken)
    {
        var devices = new JsonArray();
        foreach (var device in Devices.Devices)
        {
            devices.Add(await DeviceJsonAsync(device, cancellationToken).ConfigureAwait(false));
        }

        return new JsonObject
        {
            ["devices"] = devices,
            ["count"] = Devices.Devices.Count,
            ["loadError"] = Devices.LoadError,
            ["addressRule"] = EndpointPolicy.Rule
        };
    }

    private async Task<CommandExecutionResult> RemoveDeviceAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = ReadString(request, MobileToolControlOptions.ArgumentDeviceId);
        var device = Devices.Find(deviceId);
        if (device is null)
        {
            return Failed(
                request,
                MobileToolControlErrorCodes.UnknownDevice,
                "这台电脑不在本机的已导入列表中。");
        }

        using var call = TryCreateCall(cancellationToken);
        if (call is null)
        {
            return Failed(request, MobileToolControlErrorCodes.Unavailable, StoppedMessage);
        }
        await SecretAccess.DeleteAsync(deviceId, call.Token).ConfigureAwait(false);
        Devices.Remove(deviceId);
        _catalogs.TryRemove(deviceId, out _);
        DropInvocations(deviceId);
        if (string.Equals(_activeDeviceId, deviceId, StringComparison.Ordinal))
        {
            _activeInvocationId = "";
            _activeDeviceId = "";
        }

        Publish(MobileToolControlOptions.EventDeviceRemoved, new JsonObject
        {
            ["deviceId"] = deviceId,
            ["deviceName"] = device.DeviceName
        });

        var payload = await ListDevicesPayloadAsync(call.Token).ConfigureAwait(false);
        payload["removed"] = deviceId;
        return Succeeded(request, payload.ToJsonString(IndentedJson));
    }

    /// <summary>
    /// Explicit reachability check. This is the only way a device leaves "尚未检查": the phone never
    /// shows "在线" because a record exists. It performs one catalog read and records the outcome.
    /// </summary>
    private async Task<CommandExecutionResult> CheckDeviceAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = ReadString(request, MobileToolControlOptions.ArgumentDeviceId);
        var device = Devices.Find(deviceId);
        if (device is null)
        {
            return Failed(
                request,
                MobileToolControlErrorCodes.UnknownDevice,
                "这台电脑不在本机的已导入列表中。");
        }

        using var call = TryCreateCall(cancellationToken);
        if (call is null)
        {
            return Failed(request, MobileToolControlErrorCodes.Unavailable, StoppedMessage);
        }
        try
        {
            var catalog = await FetchCatalogAsync(device, call.Token).ConfigureAwait(false);
            Devices.UpdateProbe(
                deviceId,
                "reachable",
                string.IsNullOrWhiteSpace(catalog.Device.Name) ? "已连接" : $"已连接 {catalog.Device.Name}");
            var payload = new JsonObject
            {
                ["deviceId"] = deviceId,
                ["reachable"] = true,
                ["deviceName"] = catalog.Device.Name,
                ["platform"] = catalog.Device.Platform,
                ["toolCount"] = catalog.Tools.Count,
                ["commandCount"] = catalog.Commands.Count,
                ["detail"] = "电脑已响应，可以读取工具目录。"
            };
            return Succeeded(request, payload.ToJsonString(IndentedJson));
        }
        catch (MobileToolControlException exception)
        {
            Devices.UpdateProbe(deviceId, "unreachable", exception.Message);
            return Failed(request, exception);
        }
    }

    // ---------------------------------------------------------------- catalog

    private async Task<CommandExecutionResult> CatalogAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = ReadString(request, MobileToolControlOptions.ArgumentDeviceId);
        var device = Devices.Find(deviceId);
        if (device is null)
        {
            return Failed(
                request,
                MobileToolControlErrorCodes.UnknownDevice,
                "这台电脑不在本机的已导入列表中。");
        }

        var refresh = ReadFlag(request, MobileToolControlOptions.ArgumentRefresh);
        using var call = TryCreateCall(cancellationToken);
        if (call is null)
        {
            return Failed(request, MobileToolControlErrorCodes.Unavailable, StoppedMessage);
        }
        if (!refresh && _catalogs.TryGetValue(deviceId, out var cached) &&
            DateTimeOffset.UtcNow - cached.Catalog.FetchedAt < MobileToolControlOptions.CatalogCacheLifetime)
        {
            return Succeeded(request, CatalogJson(cached.Catalog with { FromCache = true }).ToJsonString(IndentedJson));
        }

        try
        {
            var catalog = await FetchCatalogAsync(device, call.Token).ConfigureAwait(false);
            _catalogs[deviceId] = new CachedCatalog(catalog);
            Devices.UpdateProbe(deviceId, "reachable", "目录已刷新");
            Publish(MobileToolControlOptions.EventCatalogRefreshed, new JsonObject
            {
                ["deviceId"] = deviceId,
                ["toolCount"] = catalog.Tools.Count,
                ["commandCount"] = catalog.Commands.Count
            });
            return Succeeded(request, CatalogJson(catalog).ToJsonString(IndentedJson));
        }
        catch (MobileToolControlException exception)
        {
            Devices.UpdateProbe(deviceId, "unreachable", exception.Message);
            return Failed(request, exception);
        }
    }

    private static JsonObject CatalogJson(MobileToolCatalog catalog)
    {
        var tools = new JsonArray();
        foreach (var tool in catalog.Tools)
        {
            tools.Add(new JsonObject
            {
                ["toolId"] = tool.ToolId,
                ["moduleId"] = tool.ModuleId,
                ["title"] = tool.Title,
                ["description"] = tool.Description,
                ["category"] = tool.Category,
                ["state"] = tool.State,
                ["availability"] = tool.Availability
            });
        }

        var commands = new JsonArray();
        foreach (var command in catalog.Commands)
        {
            var parameters = new JsonArray();
            foreach (var parameter in command.Parameters)
            {
                parameters.Add(new JsonObject
                {
                    ["id"] = parameter.Id,
                    ["label"] = parameter.Label,
                    ["type"] = parameter.Type,
                    ["required"] = parameter.Required,
                    ["defaultValue"] = parameter.DefaultValue
                });
            }

            commands.Add(new JsonObject
            {
                ["commandId"] = command.CommandId,
                ["moduleId"] = command.ModuleId,
                ["title"] = command.Title,
                ["subtitle"] = command.Subtitle,
                ["dangerLevel"] = command.DangerLevel,
                ["requiresElevation"] = command.RequiresElevation,
                ["supportsProgress"] = command.SupportsProgress,
                ["supportsCancellation"] = command.SupportsCancellation,
                ["allowed"] = command.Allowed,
                ["notAllowedReason"] = command.NotAllowedReason,
                ["parameters"] = parameters
            });
        }

        return new JsonObject
        {
            ["device"] = new JsonObject
            {
                ["name"] = catalog.Device.Name,
                ["platform"] = catalog.Device.Platform
            },
            ["tools"] = tools,
            ["commands"] = commands,
            ["toolCount"] = catalog.Tools.Count,
            ["commandCount"] = catalog.Commands.Count,
            ["fetchedAt"] = catalog.FetchedAt.ToString("O"),
            ["fromCache"] = catalog.FromCache
        };
    }

    // ---------------------------------------------------------------- invocations

    private async Task<CommandExecutionResult> InvokeAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = ReadString(request, MobileToolControlOptions.ArgumentDeviceId);
        var commandId = ReadString(request, MobileToolControlOptions.ArgumentCommandId);
        var invocationId = ReadString(request, MobileToolControlOptions.ArgumentInvocationId);
        var args = request.Args?[MobileToolControlOptions.ArgumentArgs] as JsonObject ?? new JsonObject();

        var device = Devices.Find(deviceId);
        if (device is null)
        {
            return Failed(request, MobileToolControlErrorCodes.UnknownDevice, "这台电脑不在本机的已导入列表中。");
        }

        if (invocationId.Length == 0)
        {
            return Failed(request, MobileToolControlErrorCodes.ValidationFailed, "调用缺少调用 ID。");
        }

        if (commandId.Length == 0)
        {
            return Failed(request, MobileToolControlErrorCodes.ValidationFailed, "调用缺少命令 ID。");
        }

        var key = InvocationKey(deviceId, invocationId);
        if (_invocations.TryGetValue(key, out var known))
        {
            if (!string.Equals(known.CommandId, commandId, StringComparison.Ordinal))
            {
                return Failed(
                    request,
                    MobileToolControlErrorCodes.InvocationConflict,
                    "同一个调用 ID 不能用于不同的命令。");
            }

            if (known.Terminal)
            {
                // Repeat of a finished invocation: return the recorded wire result and send nothing.
                return Succeeded(request, InvocationJson(known, deviceId).ToJsonString(IndentedJson));
            }
        }

        using var call = TryCreateCall(cancellationToken);
        if (call is null)
        {
            return Failed(request, MobileToolControlErrorCodes.Unavailable, StoppedMessage);
        }

        if (known is not null)
        {
            // Already submitted: read the current state instead of submitting the same id twice.
            return await RefreshInvocationAsync(request, device, known, call, isCancel: false).ConfigureAwait(false);
        }

        // The catalog is the authority on whether this command exists and is authorized. A command the
        // computer did not mark `allowed` is refused here and never submitted.
        MobileToolCatalog catalog;
        try
        {
            catalog = await EnsureCatalogAsync(device, call.Token).ConfigureAwait(false);
        }
        catch (MobileToolControlException exception)
        {
            return Failed(request, exception);
        }

        var command = catalog.Find(commandId);
        if (command is null)
        {
            return Failed(
                request,
                MobileToolControlErrorCodes.UnknownCommand,
                "这台电脑的工具目录里没有这个命令。");
        }

        if (!command.Allowed)
        {
            return Failed(
                request,
                MobileToolControlErrorCodes.CommandNotAllowed,
                command.NotAllowedReason.Length > 0
                    ? command.NotAllowedReason
                    : "这台电脑没有授权这个命令。");
        }

        MobileToolInvocation invocation;
        try
        {
            var client = await CreateClientAsync(device, call.Token).ConfigureAwait(false);
            invocation = await client.PostInvocationAsync(invocationId, commandId, args, call.Token)
                .ConfigureAwait(false);
        }
        catch (MobileToolControlException exception)
        {
            return Failed(request, exception);
        }

        invocation = Store(deviceId, invocation);
        _lastInvocation = invocation;
        _lastDeviceId = deviceId;
        if (!invocation.Terminal)
        {
            _activeInvocationId = invocation.InvocationId;
            _activeDeviceId = deviceId;
        }
        else
        {
            _activeInvocationId = "";
            _activeDeviceId = "";
        }

        Publish(MobileToolControlOptions.EventInvocationStarted, new JsonObject
        {
            ["deviceId"] = deviceId,
            ["commandId"] = commandId,
            ["invocationId"] = invocationId,
            ["state"] = invocation.State,
            ["terminal"] = invocation.Terminal
        });

        if (invocation.Terminal)
        {
            PublishFinished(deviceId, invocation);
        }

        return Succeeded(request, InvocationJson(invocation, deviceId).ToJsonString(IndentedJson));
    }

    private async Task<CommandExecutionResult> InvocationStatusAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = ReadString(request, MobileToolControlOptions.ArgumentDeviceId);
        var invocationId = ReadString(request, MobileToolControlOptions.ArgumentInvocationId);
        var device = Devices.Find(deviceId);
        if (device is null)
        {
            return Failed(request, MobileToolControlErrorCodes.UnknownDevice, "这台电脑不在本机的已导入列表中。");
        }

        if (!_invocations.TryGetValue(InvocationKey(deviceId, invocationId), out var known))
        {
            // A调用 that this phone did not start for this computer is not readable: the answer is the
            // same whether or not it exists on the computer, so nothing is probed.
            return Failed(request, MobileToolControlErrorCodes.NotFound, "这台电脑上没有这个调用。");
        }

        if (known.Terminal)
        {
            return Succeeded(request, InvocationJson(known, deviceId).ToJsonString(IndentedJson));
        }

        using var call = TryCreateCall(cancellationToken);
        if (call is null)
        {
            return Failed(request, MobileToolControlErrorCodes.Unavailable, StoppedMessage);
        }
        return await RefreshInvocationAsync(request, device, known, call, isCancel: false).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> CancelAsync(
        CommandRequest request,
        CancellationToken cancellationToken)
    {
        var deviceId = ReadString(request, MobileToolControlOptions.ArgumentDeviceId);
        var invocationId = ReadString(request, MobileToolControlOptions.ArgumentInvocationId);
        var device = Devices.Find(deviceId);
        if (device is null)
        {
            return Failed(request, MobileToolControlErrorCodes.UnknownDevice, "这台电脑不在本机的已导入列表中。");
        }

        if (!_invocations.TryGetValue(InvocationKey(deviceId, invocationId), out var known))
        {
            return Failed(request, MobileToolControlErrorCodes.NotFound, "这台电脑上没有这个调用。");
        }

        using var call = TryCreateCall(cancellationToken);
        if (call is null)
        {
            return Failed(request, MobileToolControlErrorCodes.Unavailable, StoppedMessage);
        }

        // A cancel always asks the computer, even when the last known state was terminal: the answer
        // ("accepted", "refused", "already finished") is the computer's, never the phone's assumption.
        return await RefreshInvocationAsync(request, device, known, call, isCancel: true).ConfigureAwait(false);
    }

    private async Task<CommandExecutionResult> RefreshInvocationAsync(
        CommandRequest request,
        MobileToolDevice device,
        MobileToolInvocation known,
        CancellationTokenSource call,
        bool isCancel)
    {
        MobileToolInvocation invocation;
        try
        {
            var client = await CreateClientAsync(device, call.Token).ConfigureAwait(false);
            invocation = isCancel
                ? await client.CancelInvocationAsync(known.InvocationId, call.Token).ConfigureAwait(false)
                : await client.GetInvocationAsync(known.InvocationId, call.Token).ConfigureAwait(false);
        }
        catch (MobileToolControlException exception)
        {
            return Failed(request, exception);
        }

        invocation = Store(device.DeviceId, invocation);
        _lastInvocation = invocation;
        _lastDeviceId = device.DeviceId;
        if (invocation.Terminal)
        {
            _activeInvocationId = "";
            _activeDeviceId = "";
            PublishFinished(device.DeviceId, invocation);
        }

        return Succeeded(request, InvocationJson(invocation, device.DeviceId).ToJsonString(IndentedJson));
    }

    /// <summary>
    /// Records the newest document for an invocation. A status read does not carry
    /// <c>cancelAccepted</c>, so the last reported answer is kept instead of being reset to false.
    /// </summary>
    private MobileToolInvocation Store(string deviceId, MobileToolInvocation invocation)
    {
        var key = InvocationKey(deviceId, invocation.InvocationId);
        if (invocation.CancelAccepted is null &&
            _invocations.TryGetValue(key, out var known) &&
            known.CancelAccepted is not null)
        {
            invocation = invocation with { CancelAccepted = known.CancelAccepted };
        }

        _invocations[key] = invocation;
        return invocation;
    }

    private void DropInvocations(string deviceId)
    {
        foreach (var key in _invocations.Keys.Where(key => key.StartsWith(deviceId + "\u001f", StringComparison.Ordinal)).ToArray())
        {
            _invocations.TryRemove(key, out _);
        }
    }

    private static string InvocationKey(string deviceId, string invocationId) => deviceId + "\u001f" + invocationId;

    private void PublishFinished(string deviceId, MobileToolInvocation invocation)
    {
        Publish(MobileToolControlOptions.EventInvocationFinished, new JsonObject
        {
            ["deviceId"] = deviceId,
            ["commandId"] = invocation.CommandId,
            ["invocationId"] = invocation.InvocationId,
            ["state"] = invocation.State,
            ["terminal"] = true
        });
    }

    // ---------------------------------------------------------------- transport helpers

    private async Task<MobileToolCatalog> EnsureCatalogAsync(MobileToolDevice device, CancellationToken cancellationToken)
    {
        if (_catalogs.TryGetValue(device.DeviceId, out var cached) &&
            DateTimeOffset.UtcNow - cached.Catalog.FetchedAt < MobileToolControlOptions.CatalogCacheLifetime)
        {
            return cached.Catalog;
        }

        var catalog = await FetchCatalogAsync(device, cancellationToken).ConfigureAwait(false);
        _catalogs[device.DeviceId] = new CachedCatalog(catalog);
        return catalog;
    }

    private async Task<MobileToolCatalog> FetchCatalogAsync(MobileToolDevice device, CancellationToken cancellationToken)
    {
        var client = await CreateClientAsync(device, cancellationToken).ConfigureAwait(false);
        return await client.GetCatalogAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the HTTP client for one device. The stored endpoint is re-validated here on every call:
    /// a device record edited by hand (or restored from a backup) can never widen the address
    /// boundary, and a missing token is reported instead of attempting an anonymous request.
    /// </summary>
    private async Task<MobileToolControlHttpClient> CreateClientAsync(
        MobileToolDevice device,
        CancellationToken cancellationToken)
    {
        if (!MobileToolEndpointParser.TryParse(device.Endpoint, EndpointPolicy, out var endpoint, out var parseError))
        {
            throw new MobileToolControlException(MobileToolControlErrorCodes.EndpointRefused, parseError);
        }

        var token = await SecretAccess.ReadAsync(device.DeviceId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            throw new MobileToolControlException(
                MobileToolControlErrorCodes.Unauthorized,
                "这台电脑的授权凭据已丢失，请重新导入连接码。");
        }

        return new MobileToolControlHttpClient(endpoint, token);
    }

    // ---------------------------------------------------------------- settings and surfaces

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsSchemaDocument(Id, "{\"type\":\"object\",\"properties\":{}}"));

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, new JsonObject(), DateTimeOffset.UtcNow));

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(
        SettingsPatch patch,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new SettingsValidationResult(true, []));

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<UiSurfaceDescriptor>>([]);

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    // ---------------------------------------------------------------- plumbing

    private void Publish(string type, JsonObject payload) =>
        _events.Writer.TryWrite(new MptModuleEvent(
            Id,
            (ulong)Interlocked.Increment(ref _eventSeq),
            type,
            DateTimeOffset.UtcNow,
            payload));

    private static string ReadString(CommandRequest request, string name)
    {
        if (request.Args is null || !request.Args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return "";
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }

            return value.ToJsonString().Trim('"');
        }

        return "";
    }

    /// <summary>
    /// Boolean arguments arrive over HostControl as protobuf doubles, so an integral 1/1.0 is read as
    /// true and a fractional value is rejected rather than rounded (same wire lesson as
    /// <c>RemoteCommandsAndroidModule.ReadInt</c>).
    /// </summary>
    private static bool ReadFlag(CommandRequest request, string name)
    {
        if (request.Args is null || !request.Args.TryGetPropertyValue(name, out var node) || node is null)
        {
            return false;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag))
            {
                return flag;
            }

            if (value.TryGetValue<string>(out var text))
            {
                return bool.TryParse(text, out var parsed) && parsed;
            }

            if (value.TryGetValue<double>(out var number))
            {
                return number == 1d;
            }

            if (value.TryGetValue<int>(out var integer))
            {
                return integer == 1;
            }
        }

        return false;
    }

    private CommandExecutionResult Succeeded(CommandRequest request, string output) =>
        new(request.InvocationId, request.CommandId, "succeeded", true, output);

    private static CommandExecutionResult Failed(CommandRequest request, MobileToolControlException exception) =>
        new(
            request.InvocationId,
            request.CommandId,
            "failed",
            false,
            "",
            exception.ToRuntimeError());

    private static CommandExecutionResult Failed(
        CommandRequest request,
        string code,
        string message,
        bool retryable = false) =>
        new(
            request.InvocationId,
            request.CommandId,
            "failed",
            false,
            "",
            new MptRuntimeError(code, message, retryable));

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record CachedCatalog(MobileToolCatalog Catalog);
}
