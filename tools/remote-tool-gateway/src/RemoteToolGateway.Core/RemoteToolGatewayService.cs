using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;

namespace RemoteToolGateway.Core;

public sealed record RemoteToolGatewayOptions
{
    public string ModuleId { get; init; } = "remote-tool-gateway";
    public string DeviceName { get; init; } = "";
    public string Platform { get; init; } = "";
    public int DefaultPort { get; init; } = 49541;

    /// <summary>
    /// Injection point for the test suite's loopback transport. It is a constructor-only switch:
    /// no preference, module argument or HTTP field can turn it on in a production module.
    /// </summary>
    public bool AllowLoopbackTransport { get; init; }
}

public sealed class GatewayHostUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The computer side of the remote tool contract: grant-scoped catalog, authorized invocation,
/// cancellation, desktop confirmation and audit. Execution itself always goes through
/// <see cref="IHostControlBridge"/> (the real HostControl client in production), so the Runner's
/// RuntimeOperationPolicy, module confirmation tokens, Broker and UAC handling stay authoritative.
/// </summary>
public sealed class RemoteToolGatewayService : IAsyncDisposable
{
    /// <summary>The file-transfer receiver port; the control listener must never occupy it.</summary>
    public const int FileTransferPort = 47165;

    private static readonly TimeSpan CatalogCacheDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CompletionWindow = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(3);

    private readonly RemoteToolGatewayOptions _options;
    private readonly IHostControlBridge _bridge;
    private readonly CancellationTokenSource _lifetime = new();
    // Serializes grant revocation with the admission of a new invocation, so an authorization
    // decision and the execution it authorizes never straddle a revocation.
    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly object _catalogLock = new();
    private HostCatalog? _catalog;
    private DateTimeOffset _catalogFetchedAt = DateTimeOffset.MinValue;
    private ControlHttpListener? _listener;
    private string _listenerAddress = "";
    private string _listenerMessage = "";
    private int _listenerPort;

    public RemoteToolGatewayService(
        string dataDirectory,
        ISecretStore secrets,
        IHostControlBridge bridge,
        RemoteToolGatewayOptions? options = null)
    {
        _options = options ?? new RemoteToolGatewayOptions();
        _bridge = bridge;
        _listenerMessage = "监听未开启；默认不绑定任何网络地址。";
        Grants = new GrantStore(dataDirectory, secrets, _options.ModuleId);
        Invocations = new InvocationStore();
        Audit = new AuditStore(dataDirectory);
    }

    public GrantStore Grants { get; }
    public InvocationStore Invocations { get; }
    public AuditStore Audit { get; }

    public bool ListenerRunning => _listener is { Running: true };
    public string ListenerMessage => _listenerMessage;
    public string ListenerAddress => _listenerAddress;
    public int ListenerPort => _listenerPort;

    /// <summary>Raised for every state change a desktop page may want to re-read.</summary>
    public event Action<string, JsonObject>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await Grants.LoadAsync(cancellationToken);
        await Audit.LoadAsync(cancellationToken);
    }

    // ---- listener -----------------------------------------------------------------------------

    /// <summary>
    /// Starts the listener on a literal Tailscale address. Wildcard, LAN, public, host-name and
    /// file-transfer-port endpoints are refused before a socket exists.
    /// </summary>
    public async Task<string> StartListenerAsync(string address, int port, CancellationToken cancellationToken)
    {
        var ip = ResolveBindAddress(address);
        // Port 0 is an ephemeral test transport; production ports stay in the user range.
        var ephemeral = port == 0 && _options.AllowLoopbackTransport;
        if (!ephemeral && port is < 1024 or > 65535) throw new ArgumentException("监听端口应在 1024–65535 之间。");
        if (port == FileTransferPort) throw new ArgumentException($"端口 {FileTransferPort} 已被文件互传使用，请换一个端口。");
        if (ListenerRunning && ip.Equals(ParseCurrentAddress()) && port == _listenerPort)
            return TailnetBinding.FormatEndpoint(ip, port);

        await StopListenerAsync();
        var listener = new ControlHttpListener(ip, port, _options.AllowLoopbackTransport, HandleAsync);
        try
        {
            listener.Start();
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            _listenerMessage = MptRedact(ex.Message);
            throw new InvalidOperationException($"无法在 {TailnetBinding.FormatEndpoint(ip, port)} 上监听：{_listenerMessage}", ex);
        }

        _listener = listener;
        _listenerAddress = ip.ToString();
        _listenerPort = listener.LocalEndpoint?.Port ?? port;
        _listenerMessage = $"正在 {TailnetBinding.FormatEndpoint(ip, port)} 上等待已授权的手机。";
        Audit.Record("listener-start", "", "", "", "", "running", _listenerMessage);
        Publish("listener.changed", new JsonObject { ["running"] = true, ["address"] = _listenerAddress, ["port"] = port });
        await Task.CompletedTask;
        return TailnetBinding.FormatEndpoint(ip, port);
    }

    public async Task StopListenerAsync()
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null) return;
        await listener.DisposeAsync();
        // Nothing is bound any more: no endpoint is reported until the user starts it again.
        _listenerAddress = "";
        _listenerPort = 0;
        _listenerMessage = "监听已停止；不会接受任何网络请求。";
        Audit.Record("listener-stop", "", "", "", "", "stopped", _listenerMessage);
        Publish("listener.changed", new JsonObject { ["running"] = false });
    }

    private IPAddress ParseCurrentAddress() =>
        IPAddress.TryParse(_listenerAddress, out var current) ? current : IPAddress.None;

    private IPAddress ResolveBindAddress(string address)
    {
        if (_options.AllowLoopbackTransport && IPAddress.TryParse(address?.Trim(), out var loopback) && IPAddress.IsLoopback(loopback))
        {
            // Test-only transport: the service still refuses every other non-Tailnet address.
            return loopback;
        }

        if (!TailnetBinding.TryParseTailnet(address, out var ip))
            throw new ArgumentException("监听地址必须是本机当前的 Tailscale IP（100.64.0.0/10 或 fd7a:115c:a1e0::/48）。");
        if (!TailnetBinding.LocalAddresses().Contains(ip))
            throw new ArgumentException("该地址不是本机当前的 Tailscale IP；请先连接 Tailscale 网络。");
        return ip;
    }

    // ---- HTTP surface -------------------------------------------------------------------------

    public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        if (!request.Path.StartsWith(ControlWire.Prefix, StringComparison.Ordinal))
            return ControlResponse.Error(404, ControlErrorCodes.NotFound, "未知的接口。");

        var grant = await AuthenticateAsync(request, cancellationToken);
        if (grant is null)
        {
            Audit.Record("auth-rejected", "", "", "", "", "unauthorized", "访问凭据无效或已撤销。");
            return ControlResponse.Error(401, ControlErrorCodes.Unauthorized, "访问凭据无效或已撤销。");
        }

        try
        {
            // A known path with the wrong method answers 405 instead of pretending to be unknown.
            if (string.Equals(request.Path, ControlWire.CatalogPath, StringComparison.Ordinal))
            {
                return request.Method == "GET"
                    ? await CatalogResponseAsync(grant, cancellationToken)
                    : MethodNotAllowed();
            }

            if (string.Equals(request.Path, ControlWire.InvocationsPath, StringComparison.Ordinal))
            {
                return request.Method == "POST"
                    ? await SubmitAsync(grant, request, cancellationToken)
                    : MethodNotAllowed();
            }

            if (TryMatchInvocation(request.Path, out var invocationId, out var cancel))
            {
                if (cancel)
                    return request.Method == "POST"
                        ? await CancelInvocationAsync(grant, invocationId, cancellationToken)
                        : MethodNotAllowed();
                return request.Method == "GET"
                    ? await ReadInvocationAsync(grant, invocationId)
                    : MethodNotAllowed();
            }

            if (request.Method is not ("GET" or "POST"))
                return MethodNotAllowed();
            return ControlResponse.Error(404, ControlErrorCodes.NotFound, "未知的接口。");
        }
        catch (GatewayHostUnavailableException ex)
        {
            return ControlResponse.Error(502, ControlErrorCodes.HostUnavailable, MptRedact(ex.Message));
        }
    }

    private static ControlResponse MethodNotAllowed() =>
        ControlResponse.Error(405, ControlErrorCodes.MethodNotAllowed, "该接口不支持此请求方法。");

    private async Task<GatewayGrant?> AuthenticateAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        var header = request.Authorization?.Trim() ?? "";
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[prefix.Length..].Trim();
        if (token.Length < ControlConnectionCode.MinimumTokenLength) return null;
        return await Grants.FindByTokenAsync(token, cancellationToken);
    }

    public static bool TryMatchInvocation(string path, out string invocationId, out bool cancel)
    {
        invocationId = "";
        cancel = false;
        if (!path.StartsWith(ControlWire.InvocationsPath + "/", StringComparison.Ordinal)) return false;
        var rest = path[(ControlWire.InvocationsPath.Length + 1)..];
        if (rest.EndsWith("/cancel", StringComparison.Ordinal))
        {
            cancel = true;
            rest = rest[..^"/cancel".Length];
        }

        if (rest.Length is 0 or > ControlWire.MaxInvocationIdLength || rest.Contains('/')) return false;
        if (!rest.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) return false;
        invocationId = rest;
        return true;
    }

    private async Task<ControlResponse> CatalogResponseAsync(GatewayGrant grant, CancellationToken cancellationToken)
    {
        var catalog = await BuildCatalogAsync(cancellationToken);
        var commands = catalog.Commands
            .Select(command =>
            {
                var allowed = IsAllowedForGrant(grant, command);
                return new ControlCommandInfo(
                    command.CommandId,
                    command.ModuleId,
                    command.Title,
                    command.Subtitle,
                    command.DangerLevel,
                    command.RequiresElevation,
                    command.SupportsProgress,
                    command.SupportsCancellation,
                    command.Parameters,
                    allowed,
                    allowed ? "" : NotAllowedReason(grant, command));
            })
            .ToArray();
        var payload = new ControlCatalog(
            new ControlDeviceInfo(_options.DeviceName, _options.Platform),
            catalog.Tools.Select(tool => new ControlToolInfo(
                tool.ToolId, tool.ModuleId, tool.Title, tool.Description, tool.Category, tool.State, tool.Availability)).ToArray(),
            commands);
        await Grants.TouchAsync(grant.GrantId, cancellationToken);
        Audit.Record("catalog", grant.GrantId, grant.DeviceName, "", "", "ok", "手机读取了工具目录。");
        return ControlResponse.Json(200, ControlWire.Serialize(payload));
    }

    private bool IsAllowedForGrant(GatewayGrant grant, HostCommandDescriptor command) =>
        grant.Allows(command.CommandId) &&
        !CommandPolicy.IsGatewayManagementCommand(command.CommandId, command.ModuleId, _options.ModuleId);

    /// <summary>The reason a command is visible but not executable, so the phone never has to guess.</summary>
    private string NotAllowedReason(GatewayGrant grant, HostCommandDescriptor command)
    {
        if (CommandPolicy.IsGatewayManagementCommand(command.CommandId, command.ModuleId, _options.ModuleId))
            return "网关自身的授权与管理命令不能通过网络执行。";
        if (!grant.Allows(command.CommandId)) return "未加入这台设备的授权。";
        if (CommandPolicy.IsElevated(command) && !grant.AllowElevated) return "这台设备的授权未允许提权。";
        return "当前不可执行。";
    }

    private async Task<ControlResponse> SubmitAsync(GatewayGrant grant, ControlRequest request, CancellationToken cancellationToken)
    {
        JsonObject body;
        try
        {
            body = JsonNode.Parse(request.Body) as JsonObject
                ?? throw new JsonException("请求体必须是 JSON 对象。");
        }
        catch (JsonException)
        {
            return ControlResponse.Error(400, ControlErrorCodes.BadRequest, "请求体不是有效的 JSON 对象。");
        }

        var invocationId = ReadString(body, "invocationId");
        var commandId = ReadString(body, "commandId");
        if (!IsSafeInvocationId(invocationId))
            return ControlResponse.Error(400, ControlErrorCodes.BadRequest, "invocationId 必须是 1–64 位的字母、数字、点、横线或下划线。");
        if (commandId.Length is 0 or > ControlWire.MaxCommandIdLength)
            return ControlResponse.Error(400, ControlErrorCodes.BadRequest, "commandId 无效。");
        var args = body["args"] as JsonObject ?? new JsonObject();
        if (args.ToJsonString().Length > ControlWire.MaxRequestBytes)
            return ControlResponse.Error(413, ControlErrorCodes.PayloadTooLarge, "参数超过大小上限。");

        if (Invocations.TryGet(invocationId, out var existing))
        {
            // Same grant: replay the current state. Another grant: refuse without revealing details.
            return existing.GrantId == grant.GrantId
                ? ControlResponse.Json(200, ControlWire.Serialize(existing.ToWire()))
                : ControlResponse.Error(409, ControlErrorCodes.InvocationExists, "该调用编号已被占用，请使用新的编号重试。");
        }

        var catalog = await BuildCatalogAsync(cancellationToken);
        var command = catalog.Commands.FirstOrDefault(item => string.Equals(item.CommandId, commandId, StringComparison.Ordinal));
        if (command is null)
        {
            Audit.Record("invocation-rejected", grant.GrantId, grant.DeviceName, commandId, invocationId, "not-found", "命令不存在。");
            return ControlResponse.Error(404, ControlErrorCodes.CommandNotFound, "电脑上不存在该命令，请刷新目录后重试。");
        }

        if (CommandPolicy.IsGatewayManagementCommand(command.CommandId, command.ModuleId, _options.ModuleId))
        {
            Audit.Record("invocation-rejected", grant.GrantId, grant.DeviceName, commandId, invocationId, "forbidden", "网关管理命令不能远程执行。");
            return ControlResponse.Error(403, ControlErrorCodes.Forbidden, "网关自身的授权与管理命令不能通过网络执行。");
        }

        // The catalog read above awaited. Revocation, a narrowed command list or a changed
        // allowElevated flag must not be papered over by the grant snapshot taken before it, so the
        // authorization decision is repeated inside the admission gate against the live grant.
        InvocationRecord record;
        await _admission.WaitAsync(cancellationToken);
        try
        {
            var live = Grants.Find(grant.GrantId);
            if (live is null)
            {
                Audit.Record("invocation-rejected", grant.GrantId, grant.DeviceName, commandId, invocationId, "revoked", "授权已在请求处理期间被撤销。");
                return ControlResponse.Error(401, ControlErrorCodes.Unauthorized, "该设备的授权已被撤销。");
            }

            if (!live.Allows(commandId))
            {
                Audit.Record("invocation-rejected", live.GrantId, live.DeviceName, commandId, invocationId, "not-authorized", "该命令未加入本设备的授权。");
                return ControlResponse.Error(403, ControlErrorCodes.CommandNotAuthorized, "该命令未授权给这台设备；请在电脑上调整授权。");
            }

            var elevated = CommandPolicy.IsElevated(command);
            if (elevated && !live.AllowElevated)
            {
                Audit.Record("invocation-rejected", live.GrantId, live.DeviceName, commandId, invocationId, "elevation-not-allowed", "授权未允许提权。");
                return ControlResponse.Error(403, ControlErrorCodes.ElevationNotAllowed, "该命令需要管理员权限，而这台设备的授权未允许提权。");
            }

            var decision = CommandPolicy.Evaluate(command);
            if (decision.Requirement == ConfirmationRequirement.Unsupported)
            {
                Audit.Record("invocation-rejected", live.GrantId, live.DeviceName, commandId, invocationId, "unsupported-confirmation", decision.Reason);
                return ControlResponse.Error(409, ControlErrorCodes.UnsupportedConfirmation, decision.Reason);
            }

            record = new InvocationRecord
            {
                InvocationId = invocationId,
                GrantId = live.GrantId,
                DeviceName = live.DeviceName,
                CommandId = commandId,
                CommandTitle = command.Title,
                Args = (JsonObject)args.DeepClone(),
                ArgsSummary = ControlRedaction.SummarizeArgs(args),
                RequiresElevation = elevated,
                ConfirmationKind = decision.Kind,
                ConfirmationReason = decision.Reason
            };
            record.Apply(ControlStates.Accepted, "已接收。", false);
            if (!Invocations.TryAdd(record))
                return ControlResponse.Error(409, ControlErrorCodes.InvocationExists, "该调用编号已被占用，请使用新的编号重试。");

            if (decision.Requirement == ConfirmationRequirement.Desktop)
            {
                RecordPending(record, decision);
            }
            else
            {
                Audit.Record("invocation-accepted", live.GrantId, live.DeviceName, commandId, invocationId, ControlStates.Accepted, record.ArgsSummary);
                StartExecution(record);
            }
        }
        finally { _admission.Release(); }

        await Grants.TouchAsync(grant.GrantId, cancellationToken);
        if (record.Snapshot().State == ControlStates.AwaitingConfirmation)
            return ControlResponse.Json(202, ControlWire.Serialize(record.ToWire()));

        await WaitForCompletionAsync(record, CompletionWindow, cancellationToken);
        return ControlResponse.Json(record.Snapshot().Terminal ? 200 : 202, ControlWire.Serialize(record.ToWire()));
    }

    private void RecordPending(InvocationRecord record, ConfirmationDecision decision)
    {
        record.Apply(ControlStates.AwaitingConfirmation,
            decision.Reason.Length > 0 ? decision.Reason : "等待电脑确认。", false);
        Audit.Record("confirmation-requested", record.GrantId, record.DeviceName, record.CommandId, record.InvocationId,
            ControlStates.AwaitingConfirmation, $"{decision.Kind}: {record.ArgsSummary}");
        Publish("confirmation.changed", new JsonObject
        {
            ["invocationId"] = record.InvocationId,
            ["commandId"] = record.CommandId,
            ["deviceName"] = record.DeviceName,
            ["kind"] = decision.Kind
        });
    }

    private static async Task WaitForCompletionAsync(InvocationRecord record, TimeSpan window, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + window;
        while (!record.Snapshot().Terminal && DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(25, cancellationToken);
        }
    }

    private async Task<ControlResponse> ReadInvocationAsync(GatewayGrant grant, string invocationId)
    {
        var record = Invocations.Find(invocationId);
        if (record is null || record.GrantId != grant.GrantId)
        {
            // The same answer for "unknown" and "belongs to another grant": nothing is leaked.
            return ControlResponse.Error(404, ControlErrorCodes.NotFound, "没有找到该调用。");
        }

        if (Grants.Find(grant.GrantId) is null)
            return ControlResponse.Error(401, ControlErrorCodes.Unauthorized, "该设备的授权已被撤销。");

        await Grants.TouchAsync(grant.GrantId, CancellationToken.None);
        return ControlResponse.Json(200, ControlWire.Serialize(record.ToWire()));
    }

    private async Task<ControlResponse> CancelInvocationAsync(GatewayGrant grant, string invocationId, CancellationToken cancellationToken)
    {
        var record = Invocations.Find(invocationId);
        if (record is null || record.GrantId != grant.GrantId)
            return ControlResponse.Error(404, ControlErrorCodes.NotFound, "没有找到该调用。");

        if (record.Snapshot().Terminal) return CancelResponse(record, false, "调用已经结束。");

        // A pending confirmation nobody has claimed yet is cancelled locally: nothing is executing.
        // The transition is atomic, so a desktop claim that wins the race is never overwritten.
        if (record.TryCancelPending())
        {
            Audit.Record("confirmation-cancelled", record.GrantId, record.DeviceName, record.CommandId, record.InvocationId, "cancelled", "手机取消了待确认调用。");
            Publish("confirmation.changed", new JsonObject { ["invocationId"] = record.InvocationId });
            return CancelResponse(record, true, "已取消等待电脑确认的调用。");
        }

        // A claimed or running invocation is in the runtime's hands (the desktop page uses the same
        // invocation id). Only the runtime's answer decides: accepted means "cancel requested", and
        // the final state still has to come back from the runtime before anything is cancelled.
        HostCancellation cancellation;
        try
        {
            cancellation = await _bridge.CancelAsync(invocationId, cancellationToken);
        }
        catch (Exception ex)
        {
            return ControlResponse.Error(502, ControlErrorCodes.HostUnavailable, MptRedact(ex.Message));
        }

        if (cancellation.Accepted) RequestGracefulStreamStop(record);
        if (cancellation.Accepted && !record.Snapshot().Terminal)
            record.MarkCancelAccepted(cancellation.Message.Length > 0 ? cancellation.Message : "已向运行时请求取消，等待最终结果。");

        var snapshot = record.Snapshot();
        Audit.Record("invocation-cancel", record.GrantId, record.DeviceName, record.CommandId, record.InvocationId,
            cancellation.Accepted ? "accepted" : "rejected", cancellation.Message);
        return CancelResponse(record, cancellation.Accepted,
            cancellation.Message.Length > 0 ? cancellation.Message : record.ToWire().Message,
            cancellation.State.Length > 0 ? cancellation.State : snapshot.State);
    }

    /// <summary>
    /// The cancel answer is the invocation document plus the real accepted flags. The phone half of
    /// the contract parses the invocation shape (<c>state</c>, <c>terminal</c>, <c>result</c>) and
    /// reads <c>cancelAccepted</c>; <c>accepted</c> carries the same truth for other readers.
    /// <c>state</c> always stays the invocation's own state: the runtime's cancel reply is reported
    /// in <c>cancelState</c> and never leaks into the invocation document.
    /// </summary>
    private static ControlResponse CancelResponse(InvocationRecord record, bool accepted, string message, string? runtimeState = null)
    {
        var payload = WireJson(record.ToWire());
        payload["accepted"] = accepted;
        payload["cancelAccepted"] = accepted;
        if (!string.IsNullOrEmpty(message)) payload["message"] = ControlText.Bound(message, ControlWire.MaxMessageLength);
        if (!string.IsNullOrEmpty(runtimeState)) payload["cancelState"] = ControlText.Bound(runtimeState, ControlWire.MaxTextLength);
        return ControlResponse.Json(200, payload.ToJsonString(ControlWire.Json));
    }

    // ---- execution ----------------------------------------------------------------------------

    private void StartExecution(InvocationRecord record)
    {
        record.ExecutionStarted = true;
        record.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = record.Cancellation.Token;
        _ = RunExecutionAsync(record, token);
    }

    private async Task RunExecutionAsync(InvocationRecord record, CancellationToken cancellationToken)
    {
        record.Apply(ControlStates.Running, "正在电脑上执行。", false);
        Publish("invocation.changed", new JsonObject { ["invocationId"] = record.InvocationId, ["state"] = record.State });
        try
        {
            await foreach (var evt in _bridge.ExecuteStreamAsync(record.InvocationId, record.CommandId, record.Args, cancellationToken))
            {
                if (evt.FinalResponse is { } final)
                {
                    CompleteFromHost(record, final);
                    return;
                }

                if (evt.Terminal)
                {
                    CompleteFromHost(record, Result(record, evt.State, evt.Message, DerivedErrorCode(evt.State), retryable: false));
                    return;
                }

                record.Apply(NormalizeState(evt.State), evt.Message, false);
            }

            if (!record.Snapshot().Terminal) CompleteWithoutTerminal(record, "运行时没有返回最终结果。");
        }
        catch (OperationCanceledException)
        {
            if (!record.Snapshot().Terminal)
            {
                // A stopped gateway, a timeout or a released stream is not a cancellation result.
                CompleteWithoutTerminal(record, "调用在运行时返回最终结果前中断（网关停止或连接中断）。");
            }
        }
        catch (Exception ex)
        {
            if (!record.Snapshot().Terminal)
            {
                var message = MptRedact(ex.Message);
                record.Apply(ControlStates.Failed, message, true,
                    Result(record, ControlStates.Failed, message, ControlErrorCodes.HostUnavailable, retryable: true));
            }
        }
        finally
        {
            record.Cancellation?.Dispose();
            record.Cancellation = null;
            var final = record.Snapshot();
            Audit.Record("invocation-finished", record.GrantId, record.DeviceName, record.CommandId, record.InvocationId,
                final.State, record.ToWire().Message);
            Publish("invocation.changed", new JsonObject { ["invocationId"] = record.InvocationId, ["state"] = final.State });
        }
    }

    /// <summary>
    /// The stream ended without a terminal event. The gateway must not turn an accepted cancel
    /// request (or a broken connection) into a "cancelled" result: the honest answer is a failure
    /// whose outcome is unknown, and the caller may retry with a new invocation id.
    /// </summary>
    private void CompleteWithoutTerminal(InvocationRecord record, string context)
    {
        var cancelRequested = record.Snapshot().CancelRequested;
        var errorCode = cancelRequested ? ControlErrorCodes.CancelUnconfirmed : ControlErrorCodes.HostStreamEnded;
        var message = cancelRequested
            ? context + "取消请求已被运行时接受，但没有收到最终结果；这次执行的结果未知。"
            : context;
        record.Apply(ControlStates.Failed, message, true, Result(record, ControlStates.Failed, message, errorCode, retryable: true));
    }

    /// <summary>
    /// Releases the local stream after the cancel grace. The runtime's own cancellation is what
    /// decides the outcome; this only stops waiting for a stream that will never answer.
    /// </summary>
    private static void RequestGracefulStreamStop(InvocationRecord record)
    {
        try { record.Cancellation?.CancelAfter(CancelGrace); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Records the runtime's terminal answer. The write is refused when the invocation already
    /// ended (for example two resolves raced): the first terminal result stands.
    /// </summary>
    private bool CompleteFromHost(InvocationRecord record, ControlInvocationResult result)
    {
        // The service bounds the final payload itself: a bridge (or a runtime) that returns a
        // large summary must not be able to push it through the phone response unchanged.
        var bounded = new ControlInvocationResult(
            ControlText.Bound(result.InvocationId, ControlWire.MaxInvocationIdLength),
            NormalizeState(result.State),
            ControlText.Bound(result.Summary, ControlWire.MaxSummaryLength),
            ControlText.Bound(result.LogCursor, ControlWire.MaxTextLength),
            ControlText.Bound(result.ErrorCode, ControlWire.MaxTextLength),
            ControlText.Bound(result.ErrorMessage, ControlWire.MaxMessageLength),
            result.Retryable,
            ControlText.BoundDetails(result.ErrorDetails));
        return record.Apply(bounded.State, bounded.Summary.Length > 0 ? bounded.Summary : bounded.ErrorMessage, true, bounded);
    }

    private ControlInvocationResult Result(InvocationRecord record, string state, string message, string errorCode, bool retryable) =>
        new(record.InvocationId, NormalizeState(state), ControlText.Bound(message, ControlWire.MaxSummaryLength), "",
            ControlText.Bound(errorCode, ControlWire.MaxTextLength), ControlText.Bound(message, ControlWire.MaxMessageLength), retryable, null);

    private static string NormalizeState(string state) => state.Trim().ToLowerInvariant() switch
    {
        "succeeded" or "success" or "ready" or "completed" or "ok" => ControlStates.Succeeded,
        "cancelled" or "canceled" => ControlStates.Cancelled,
        "permission-required" => ControlStates.PermissionRequired,
        "failed" or "error" or "timeout" => ControlStates.Failed,
        "running" or "executing" or "started" => ControlStates.Running,
        "accepted" or "pending" or "queued" => ControlStates.Accepted,
        { Length: 0 } => ControlStates.Failed,
        var other => other
    };

    private static string DerivedErrorCode(string state) => NormalizeState(state) switch
    {
        ControlStates.Succeeded => "",
        ControlStates.Cancelled => "cancelled",
        ControlStates.PermissionRequired => "permission-required",
        _ => "command.failed"
    };

    // ---- desktop confirmations ----------------------------------------------------------------

    /// <summary>
    /// Hands the original arguments to exactly one desktop page. The claim is atomic, the
    /// authorization is re-checked against the live grant and the live command catalog first, and a
    /// second claim never receives the executable arguments again.
    /// </summary>
    public async Task<JsonObject> ClaimConfirmationAsync(string invocationId, CancellationToken cancellationToken)
    {
        var record = Invocations.Find(invocationId) ?? throw new ArgumentException("没有找到该待确认调用。");
        var snapshot = record.Snapshot();
        if (snapshot.Terminal) throw new ArgumentException("该调用已经结束。");
        if (snapshot.Claimed) throw new ArgumentException("该调用已被电脑端受理，不能重复领取。");
        if (!string.Equals(snapshot.State, ControlStates.AwaitingConfirmation, StringComparison.Ordinal))
            throw new ArgumentException("该调用已不在等待确认。");

        // The catalog read is deliberately outside the admission boundary: it can be slow (or
        // blocked) and must not stall other devices. Everything that depends on the grant is decided
        // afterwards, inside the boundary, against the grant as it exists at that moment.
        HostCatalog catalog;
        try { catalog = await BuildCatalogAsync(cancellationToken, force: true); }
        catch (GatewayHostUnavailableException ex)
        {
            throw new InvalidOperationException("当前无法读取本机工具目录，请稍后重试：" + MptRedact(ex.Message), ex);
        }

        var command = catalog.Commands.FirstOrDefault(item => string.Equals(item.CommandId, record.CommandId, StringComparison.Ordinal));
        if (command is null)
        {
            FailUnclaimable(record, ControlErrorCodes.CommandNotFound, "电脑上已不存在该命令，无法执行这次调用。");
            throw new ArgumentException("电脑上已不存在该命令。");
        }

        if (CommandPolicy.IsGatewayManagementCommand(command.CommandId, command.ModuleId, _options.ModuleId))
        {
            FailUnclaimable(record, ControlErrorCodes.Forbidden, "网关管理命令不能执行。");
            throw new ArgumentException("网关自身的授权与管理命令不能执行。");
        }

        // One decision boundary: the current grant, the current whitelist/elevation decision and the
        // claim itself. Grant updates and revocations publish inside the same boundary, so a grant
        // narrowed while the catalog was being read can no longer be claimed from the old snapshot.
        await _admission.WaitAsync(cancellationToken);
        try
        {
            var live = Grants.Find(record.GrantId);
            if (live is null)
            {
                FailUnclaimable(record, ControlErrorCodes.Unauthorized, "该设备的授权已被撤销，无法执行这次调用。");
                throw new ArgumentException("该设备的授权已被撤销。");
            }

            if (!live.Allows(record.CommandId))
            {
                FailUnclaimable(record, ControlErrorCodes.CommandNotAuthorized, "该命令已被移出这台设备的授权。");
                throw new ArgumentException("该命令已不在授权列表中。");
            }

            if (CommandPolicy.IsElevated(command) && !live.AllowElevated)
            {
                FailUnclaimable(record, ControlErrorCodes.ElevationNotAllowed, "这台设备的授权已不再允许提权。");
                throw new ArgumentException("这台设备的授权不允许提权。");
            }

            if (!record.TryClaim())
            {
                if (record.Snapshot().Terminal)
                    throw new ArgumentException("该调用已经结束（" + record.ToWire().State + "），无法再领取。");
                throw new ArgumentException("该调用已被电脑端受理，不能重复领取。");
            }
        }
        finally { _admission.Release(); }

        Audit.Record("confirmation-claimed", record.GrantId, record.DeviceName, record.CommandId, record.InvocationId,
            ControlStates.Claimed, record.ArgsSummary);
        Publish("confirmation.changed", new JsonObject
        {
            ["invocationId"] = record.InvocationId,
            ["state"] = ControlStates.Claimed
        });
        return new JsonObject
        {
            ["invocationId"] = record.InvocationId,
            ["commandId"] = record.CommandId,
            ["commandTitle"] = record.CommandTitle,
            ["grantId"] = record.GrantId,
            ["deviceName"] = record.DeviceName,
            ["requiresElevation"] = record.RequiresElevation,
            ["kind"] = record.ConfirmationKind,
            ["argsSummary"] = record.ArgsSummary,
            ["args"] = record.Args.DeepClone()
        };
    }

    /// <summary>A pending request that lost its authorization ends with an explicit failure.</summary>
    private void FailUnclaimable(InvocationRecord record, string errorCode, string message)
    {
        record.FailClosed(message, errorCode);
        Audit.Record("confirmation-failed", record.GrantId, record.DeviceName, record.CommandId, record.InvocationId,
            ControlStates.Failed, message);
        Publish("confirmation.changed", new JsonObject
        {
            ["invocationId"] = record.InvocationId,
            ["state"] = ControlStates.Failed
        });
    }

    /// <summary>
    /// Records the outcome of the desktop user's decision. The execution itself already ran through
    /// the existing command entry point on the surface; this method never executes anything.
    /// </summary>
    public JsonObject ResolveConfirmation(JsonObject resolution)
    {
        var invocationId = ReadString(resolution, "invocationId");
        var record = Invocations.Find(invocationId) ?? throw new ArgumentException("没有找到该待确认调用。");
        // Resolving an already-finished confirmation is idempotent: a second click cannot run twice.
        if (record.Snapshot().Terminal) return WireJson(record.ToWire());

        var accepted = ReadBool(resolution, "accepted");
        if (!accepted)
        {
            var rejectMessage = ControlText.Bound(ReadString(resolution, "errorMessage"), ControlWire.MaxMessageLength);
            if (rejectMessage.Length == 0) rejectMessage = "电脑端用户拒绝了此操作。";
            // A claim means the desktop already handed the request to the runtime; an unclaimed
            // pending request is the only one a rejection can end.
            if (!record.TryRejectPending(rejectMessage))
                throw new ArgumentException("该调用已被电脑端受理，只能报告真实执行结果。");

            Audit.Record("confirmation-resolved", record.GrantId, record.DeviceName, record.CommandId, record.InvocationId,
                ControlStates.Rejected, rejectMessage);
            Publish("invocation.changed", new JsonObject { ["invocationId"] = record.InvocationId, ["state"] = ControlStates.Rejected });
            return WireJson(record.ToWire());
        }

        if (!record.Snapshot().Claimed)
            throw new ArgumentException("必须先通过 confirmation.claim 领取待确认调用，才能报告执行结果。");

        var state = ReadString(resolution, "state");
        var summary = ControlText.Bound(ReadString(resolution, "summary"), ControlWire.MaxSummaryLength);
        var errorCode = ControlText.Bound(ReadString(resolution, "errorCode"), ControlWire.MaxTextLength);
        var errorMessage = ControlText.Bound(ReadString(resolution, "errorMessage"), ControlWire.MaxMessageLength);
        var retryable = ReadBool(resolution, "retryable");
        var normalized = NormalizeState(state);
        bool applied;
        if (string.Equals(normalized, ControlStates.Succeeded, StringComparison.Ordinal))
        {
            applied = record.Apply(ControlStates.Succeeded, summary.Length > 0 ? summary : "电脑端执行完成。", true,
                new ControlInvocationResult(record.InvocationId, ControlStates.Succeeded,
                    summary.Length > 0 ? summary : "电脑端执行完成。", "", "", "", false, null));
        }
        else if (string.Equals(normalized, ControlStates.PermissionRequired, StringComparison.Ordinal))
        {
            // The runtime still wants Broker/UAC approval. That is a real failure for this
            // invocation; the gateway never approves it on the user's behalf.
            var message = errorMessage.Length > 0
                ? errorMessage
                : "电脑端仍需要进一步授权（Broker/UAC）；请在电脑上完成授权后从手机重新发起。";
            applied = record.Apply(ControlStates.PermissionRequired, message, true,
                new ControlInvocationResult(record.InvocationId, ControlStates.PermissionRequired, summary, "",
                    errorCode.Length > 0 ? errorCode : "permission-required", message, true, null));
        }
        else if (string.Equals(normalized, ControlStates.Cancelled, StringComparison.Ordinal))
        {
            applied = record.Apply(ControlStates.Cancelled, summary.Length > 0 ? summary : "电脑端已取消。", true,
                new ControlInvocationResult(record.InvocationId, ControlStates.Cancelled, summary, "",
                    errorCode.Length > 0 ? errorCode : "cancelled", errorMessage, retryable, null));
        }
        else
        {
            var message = errorMessage.Length > 0 ? errorMessage : summary.Length > 0 ? summary : "电脑端执行失败。";
            applied = record.Apply(ControlStates.Failed, message, true,
                new ControlInvocationResult(record.InvocationId, ControlStates.Failed, summary, "",
                    errorCode.Length > 0 ? errorCode : "command.failed", message, retryable, null));
        }

        var resolved = record.Snapshot();
        if (!applied)
        {
            // Another resolve won the race and already ended this invocation: its terminal result is
            // the only truth, and this call reports it instead of overwriting it.
            Audit.Record("confirmation-resolve-ignored", record.GrantId, record.DeviceName, record.CommandId, record.InvocationId,
                resolved.State, "该调用已经结束，忽略后到的执行结果。");
            return WireJson(record.ToWire());
        }

        Audit.Record("confirmation-resolved", record.GrantId, record.DeviceName, record.CommandId, record.InvocationId,
            resolved.State, summary);
        Publish("invocation.changed", new JsonObject { ["invocationId"] = record.InvocationId, ["state"] = resolved.State });
        return WireJson(record.ToWire());
    }

    private static JsonObject WireJson(ControlInvocation invocation) =>
        JsonNode.Parse(ControlWire.Serialize(invocation))!.AsObject();

    // ---- grants -------------------------------------------------------------------------------

    public async Task<JsonObject> CreateGrantAsync(string deviceName, IReadOnlyList<string> commandIds, bool allowElevated, CancellationToken cancellationToken)
    {
        EnsureListenerForCode();
        var (grant, token) = await Grants.CreateAsync(deviceName, commandIds, allowElevated, cancellationToken);
        Audit.Record("grant-created", grant.GrantId, grant.DeviceName, "", "", "created",
            $"命令 {grant.CommandIds.Count} 个；提权 {(grant.AllowElevated ? "允许" : "不允许")}。");
        Publish("grants.changed", new JsonObject { ["grantId"] = grant.GrantId });
        return GrantJson(grant, token);
    }

    public async Task<JsonObject> UpdateGrantAsync(string grantId, string deviceName, IReadOnlyList<string> commandIds, bool allowElevated, CancellationToken cancellationToken)
    {
        // The new authorization set becomes live inside the admission boundary, so no claim or
        // submission can decide against the previous list; only the file write stays outside it.
        GatewayGrant grant;
        await _admission.WaitAsync(cancellationToken);
        try
        {
            grant = Grants.PublishUpdate(grantId, deviceName, commandIds, allowElevated);
        }
        finally { _admission.Release(); }

        await Grants.SaveAsync(cancellationToken);
        Audit.Record("grant-updated", grant.GrantId, grant.DeviceName, "", "", "updated",
            $"命令 {grant.CommandIds.Count} 个；提权 {(grant.AllowElevated ? "允许" : "不允许")}。");
        Publish("grants.changed", new JsonObject { ["grantId"] = grant.GrantId });
        return GrantJson(grant, null);
    }

    /// <summary>Revocation rejects new requests immediately and cancels this grant's active calls.</summary>
    public async Task<JsonObject> RevokeGrantAsync(string grantId, CancellationToken cancellationToken)
    {
        // Revocation is authorized first, then acted on: the grant leaves the live authorization
        // set inside the admission boundary, so every later submission or claim is decided without
        // it. The slow work (secret deletion, runtime cancels) runs after the boundary is released,
        // so one device's revocation never blocks the others' requests.
        GatewayGrant grant;
        await _admission.WaitAsync(cancellationToken);
        try
        {
            grant = Grants.Find(grantId) ?? throw new ArgumentException("未找到该设备授权。");
            Grants.MarkRevoked(grantId);
        }
        finally { _admission.Release(); }

        await Grants.CompleteRevocationAsync(grantId, cancellationToken);
        var cancelled = 0;
        foreach (var record in Invocations.ActiveForGrant(grantId))
        {
            // An unclaimed pending confirmation never executed: it can end locally. Everything
            // else is in the runtime's hands and keeps its real outcome.
            var snapshot = record.Snapshot();
            if (string.Equals(snapshot.State, ControlStates.AwaitingConfirmation, StringComparison.Ordinal))
            {
                if (record.TryCancelPending()) cancelled++;
            }
            else
            {
                try
                {
                    var cancellation = await _bridge.CancelAsync(record.InvocationId, cancellationToken);
                    if (cancellation.Accepted)
                    {
                        cancelled++;
                        RequestGracefulStreamStop(record);
                        if (!record.Snapshot().Terminal)
                            record.MarkCancelAccepted("授权已撤销，已向运行时请求取消，等待最终结果。");
                    }
                }
                catch (Exception)
                {
                    // The runtime may already be gone; the record keeps its real state.
                }
            }

            Publish("invocation.changed", new JsonObject { ["invocationId"] = record.InvocationId, ["state"] = record.Snapshot().State });
        }

        Audit.Record("grant-revoked", grant.GrantId, grant.DeviceName, "", "", "revoked",
            $"已撤销；{cancelled} 个活动调用按要求取消（其余保留运行时返回的真实状态）。");
        Publish("grants.changed", new JsonObject { ["grantId"] = grantId });
        return new JsonObject { ["revoked"] = grantId, ["deviceName"] = grant.DeviceName, ["cancelledInvocations"] = cancelled };
    }

    public async Task<JsonObject> GetGrantCodeAsync(string grantId, CancellationToken cancellationToken)
    {
        EnsureListenerForCode();
        var grant = Grants.Find(grantId) ?? throw new ArgumentException("未找到该设备授权。");
        var token = await Grants.ReadTokenAsync(grantId, cancellationToken)
            ?? throw new InvalidOperationException("该授权缺少访问凭据，请撤销后重新创建。");
        return GrantJson(grant, token);
    }

    private void EnsureListenerForCode()
    {
        if (!ListenerRunning)
            throw new InvalidOperationException("请先在设置中开启监听，再生成连接码。");
        if (_listenerAddress.Length == 0 || _listenerPort == 0)
            throw new InvalidOperationException("监听地址尚未就绪。");
    }

    /// <summary>
    /// A grant's desktop-facing description. Editing a grant does not require the listener to be
    /// running, so the endpoint is empty when it is off; a connection code is only ever produced
    /// from a live listener (the callers that pass a token check that first).
    /// </summary>
    private JsonObject GrantJson(GatewayGrant grant, string? token)
    {
        IPAddress? address = null;
        var hasEndpoint = _listenerPort > 0 && IPAddress.TryParse(_listenerAddress, out address);
        var endpoint = hasEndpoint ? TailnetBinding.FormatEndpoint(address!, _listenerPort) : "";
        var json = new JsonObject
        {
            ["grantId"] = grant.GrantId,
            ["deviceName"] = grant.DeviceName,
            ["allowElevated"] = grant.AllowElevated,
            ["commandIds"] = new JsonArray(grant.CommandIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
            ["createdAt"] = grant.CreatedAt,
            ["lastUsedAt"] = grant.LastUsedAt,
            ["endpoint"] = endpoint
        };
        if (token is not null)
        {
            if (!hasEndpoint) throw new InvalidOperationException("监听未开启，无法生成连接码。");
            json["code"] = ControlConnectionCode.Encode(new ControlConnection(
                ControlConnectionCode.CurrentVersion, endpoint, grant.GrantId, grant.DeviceName, token));
        }

        return json;
    }

    // ---- catalog / status ---------------------------------------------------------------------

    /// <summary>
    /// The runtime catalog. A short cache keeps a polling phone from re-reading the runtime on every
    /// request; a confirmation claim passes <paramref name="force"/> so the decision to hand a
    /// remote request to the desktop is always made against the current catalog.
    /// </summary>
    public async Task<HostCatalog> BuildCatalogAsync(CancellationToken cancellationToken, bool force = false)
    {
        lock (_catalogLock)
        {
            if (!force && _catalog is not null && DateTimeOffset.UtcNow - _catalogFetchedAt < CatalogCacheDuration) return _catalog;
        }

        HostCatalog catalog;
        try
        {
            catalog = await _bridge.GetCatalogAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new GatewayHostUnavailableException("无法读取本机运行时的工具目录：" + MptRedact(ex.Message), ex);
        }

        lock (_catalogLock)
        {
            _catalog = catalog;
            _catalogFetchedAt = DateTimeOffset.UtcNow;
        }

        return catalog;
    }

    /// <summary>Everything the desktop page renders. Never contains a token or a raw argument value.</summary>
    public async Task<JsonObject> DescribeAsync(bool listenerEnabled, bool includeCatalog, CancellationToken cancellationToken)
    {
        var grants = new JsonArray();
        foreach (var grant in Grants.Grants)
        {
            grants.Add(new JsonObject
            {
                ["grantId"] = grant.GrantId,
                ["deviceName"] = grant.DeviceName,
                ["allowElevated"] = grant.AllowElevated,
                ["commandIds"] = new JsonArray(grant.CommandIds.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()),
                ["createdAt"] = grant.CreatedAt,
                ["lastUsedAt"] = grant.LastUsedAt,
                ["activeInvocations"] = Invocations.ActiveForGrant(grant.GrantId).Count
            });
        }

        var pending = new JsonArray();
        foreach (var record in Invocations.PendingConfirmations())
        {
            pending.Add(new JsonObject
            {
                ["invocationId"] = record.InvocationId,
                ["grantId"] = record.GrantId,
                ["deviceName"] = record.DeviceName,
                ["commandId"] = record.CommandId,
                ["commandTitle"] = record.CommandTitle,
                ["kind"] = record.ConfirmationKind,
                ["reason"] = record.ConfirmationReason,
                ["requiresElevation"] = record.RequiresElevation,
                ["argsSummary"] = record.ArgsSummary,
                ["claimed"] = record.Snapshot().Claimed,
                ["createdAt"] = record.CreatedAt.ToString("O")
            });
        }

        var invocations = new JsonArray();
        foreach (var record in Invocations.Recent(30))
        {
            var wire = record.ToWire();
            invocations.Add(new JsonObject
            {
                ["invocationId"] = record.InvocationId,
                ["grantId"] = record.GrantId,
                ["deviceName"] = record.DeviceName,
                ["commandId"] = record.CommandId,
                ["state"] = wire.State,
                ["message"] = wire.Message,
                ["terminal"] = wire.Terminal,
                ["requiresElevation"] = record.RequiresElevation,
                ["createdAt"] = record.CreatedAt.ToString("O"),
                ["updatedAt"] = record.UpdatedAt.ToString("O"),
                ["result"] = new JsonObject
                {
                    ["invocationId"] = wire.Result.InvocationId,
                    ["state"] = wire.Result.State,
                    ["summary"] = wire.Result.Summary,
                    ["logCursor"] = wire.Result.LogCursor,
                    ["errorCode"] = wire.Result.ErrorCode,
                    ["errorMessage"] = wire.Result.ErrorMessage,
                    ["retryable"] = wire.Result.Retryable
                }
            });
        }

        var audit = new JsonArray();
        foreach (var entry in Audit.Recent(50))
        {
            audit.Add(new JsonObject
            {
                ["time"] = entry.Time,
                ["kind"] = entry.Kind,
                ["grantId"] = entry.GrantId,
                ["deviceName"] = entry.DeviceName,
                ["commandId"] = entry.CommandId,
                ["invocationId"] = entry.InvocationId,
                ["state"] = entry.State,
                ["message"] = entry.Message
            });
        }

        var payload = new JsonObject
        {
            ["device"] = new JsonObject { ["name"] = _options.DeviceName, ["platform"] = _options.Platform },
            ["listener"] = new JsonObject
            {
                ["enabled"] = listenerEnabled,
                ["running"] = ListenerRunning,
                ["address"] = _listenerAddress,
                ["port"] = _listenerPort,
                ["endpoint"] = ListenerRunning ? TailnetBinding.FormatEndpoint(ParseCurrentAddress(), _listenerPort) : "",
                ["message"] = _listenerMessage,
                ["availableAddresses"] = new JsonArray(TailnetBinding.LocalAddresses()
                    .Select(address => (JsonNode)JsonValue.Create(address.ToString())!).ToArray())
            },
            ["grants"] = grants,
            ["pending"] = pending,
            ["invocations"] = invocations,
            ["audit"] = audit,
            ["defaultPort"] = _options.DefaultPort
        };

        if (includeCatalog)
        {
            try
            {
                var catalog = await BuildCatalogAsync(cancellationToken);
                payload["catalog"] = new JsonObject
                {
                    ["tools"] = new JsonArray(catalog.Tools.Select(tool => (JsonNode)new JsonObject
                    {
                        ["toolId"] = tool.ToolId,
                        ["moduleId"] = tool.ModuleId,
                        ["title"] = tool.Title,
                        ["category"] = tool.Category,
                        ["state"] = tool.State
                    }).ToArray()),
                    ["commands"] = new JsonArray(catalog.Commands.Select(command => (JsonNode)new JsonObject
                    {
                        ["commandId"] = command.CommandId,
                        ["moduleId"] = command.ModuleId,
                        ["title"] = command.Title,
                        ["subtitle"] = command.Subtitle,
                        ["dangerLevel"] = command.DangerLevel,
                        ["requiresElevation"] = command.RequiresElevation,
                        ["manageOnly"] = CommandPolicy.IsGatewayManagementCommand(command.CommandId, command.ModuleId, _options.ModuleId)
                    }).ToArray())
                };
            }
            catch (GatewayHostUnavailableException ex)
            {
                payload["catalogError"] = MptRedact(ex.Message);
            }
        }

        return payload;
    }

    public async Task<bool> PingHostAsync(CancellationToken cancellationToken)
    {
        try { return await _bridge.PingAsync(cancellationToken); }
        catch (Exception) { return false; }
    }

    private void Publish(string type, JsonObject payload)
    {
        try { Changed?.Invoke(type, payload); }
        catch (Exception) { }
    }

    private static string MptRedact(string message) =>
        MyPowerTools.Abstractions.MptLogRedactor.Redact(message ?? "");

    /// <summary>
    /// Client-generated ids are accepted from 1 to 64 characters of an unambiguous alphabet. The
    /// alphabet matters: the phone escapes ids into the request path, and the listener refuses any
    /// percent-encoded path, so a safe id is also an id that never needs escaping.
    /// </summary>
    private static bool IsSafeInvocationId(string value) =>
        value.Length is >= 1 and <= ControlWire.MaxInvocationIdLength && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string ReadString(JsonObject json, string key)
    {
        try { return json[key]?.GetValue<string>() ?? ""; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return ""; }
    }

    private static bool ReadBool(JsonObject json, string key)
    {
        try { return json[key]?.GetValue<bool>() ?? false; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return false; }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _lifetime.CancelAsync(); } catch (ObjectDisposedException) { }
        await StopListenerAsync();
        foreach (var grant in Grants.Grants)
        {
            foreach (var record in Invocations.ActiveForGrant(grant.GrantId))
            {
                // Shutdown is not a cancellation result: release the local stream and let the
                // execution path report the honest "unknown outcome" when it ends.
                record.Cancellation?.Cancel();
            }
        }

        await Audit.FlushAsync();
        lock (_catalogLock) _catalog = null;
    }
}
