using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace RemoteToolGateway.Surface;

/// <summary>Outcome of a command executed through the existing runtime entry point on this computer.</summary>
public sealed record SurfaceCommandOutcome(string State, bool Success, string Output, string ErrorCode, string ErrorMessage, bool Retryable);

/// <summary>
/// The two execution paths the page needs. <see cref="ExecuteModuleCommandAsync"/> talks to the
/// gateway module; <see cref="ExecuteRuntimeCommandAsync"/> is the existing
/// <c>MptAvaloniaSurfaceContext</c> entry point, so a confirmed remote request follows exactly the
/// same policy, Broker and UAC chain as a local action.
///
/// <see cref="ExecuteRuntimeCommandAsync"/> takes the phone's invocation id on purpose: the runtime
/// then knows the same invocation the phone is polling, which is what makes a phone-initiated cancel
/// act on the real call. A host without that capability reports
/// <see cref="SupportsInvocationScopedExecution"/> = false and the page refuses the request instead
/// of silently executing under a different, uncancellable id.
/// </summary>
public interface IGatewaySurfaceHost
{
    /// <summary>True when the host can execute with a caller-owned invocation id.</summary>
    bool SupportsInvocationScopedExecution { get; }

    Task<JsonObject> ExecuteModuleCommandAsync(string commandId, JsonObject args, CancellationToken cancellationToken);

    Task<SurfaceCommandOutcome> ExecuteRuntimeCommandAsync(
        string invocationId,
        string commandId,
        JsonObject args,
        CancellationToken cancellationToken);
}

public sealed class MptAvaloniaSurfaceHost(MptAvaloniaSurfaceContext context) : IGatewaySurfaceHost
{
    public bool SupportsInvocationScopedExecution => context.ExecuteCommandWithInvocationAsync is not null;

    public async Task<JsonObject> ExecuteModuleCommandAsync(string commandId, JsonObject args, CancellationToken cancellationToken)
    {
        var result = await context.ExecuteCommandAsync(commandId, args, cancellationToken);
        if (result is null) throw new InvalidOperationException("命令没有返回结果。");
        if (result.Error is not null || string.Equals(result.State, "failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(result.Error?.Message ?? result.Output ?? "命令执行失败。");
        return JsonNode.Parse(string.IsNullOrWhiteSpace(result.Output) ? "{}" : result.Output) as JsonObject ?? new JsonObject();
    }

    public async Task<SurfaceCommandOutcome> ExecuteRuntimeCommandAsync(
        string invocationId,
        string commandId,
        JsonObject args,
        CancellationToken cancellationToken)
    {
        var execute = context.ExecuteCommandWithInvocationAsync
            ?? throw new NotSupportedException(
                "当前 Shell 不支持保留手机调用 ID 的执行入口；为避免产生无法取消的远程执行，已拒绝执行。");
        var result = await execute(invocationId, commandId, args, cancellationToken);
        return new SurfaceCommandOutcome(
            result?.State ?? "failed",
            result?.Success ?? false,
            result?.Output ?? "",
            result?.Error?.Code ?? "",
            result?.Error?.Message ?? "",
            result?.Error?.Retryable ?? false);
    }
}

public sealed class GrantRow
{
    public required string GrantId { get; init; }
    public required string DeviceName { get; init; }
    public required bool AllowElevated { get; init; }
    public required IReadOnlyList<string> CommandIds { get; init; }
    public int CommandCount => CommandIds.Count;
    public required int ActiveInvocations { get; init; }
    public required string LastUsedAt { get; init; }
}

public sealed class PendingRow
{
    public required string InvocationId { get; init; }
    public required string DeviceName { get; init; }
    public required string CommandId { get; init; }
    public required string CommandTitle { get; init; }
    public required string ArgsSummary { get; init; }
    public required string Kind { get; init; }
    public required bool RequiresElevation { get; init; }
    public required bool Claimed { get; init; }
    public string Summary => $"来自 {DeviceName} · {CommandTitle}";
    public string Detail => RequiresElevation ? ArgsSummary + " · 需要管理员权限" : ArgsSummary;
}

public sealed class CatalogCommandRow : MptObservableViewModel
{
    private bool _selected;
    public required string CommandId { get; init; }
    public required string ModuleId { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required bool RequiresElevation { get; init; }
    public required bool ManagementOnly { get; init; }
    public string Display => $"{Title} · {ModuleId}";
    public bool Selectable => !ManagementOnly;

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}

public sealed class ActivityRow
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string State { get; init; }
}

/// <summary>
/// Desktop page state for the remote tool gateway: listener switch, per-phone grants, the
/// pending-confirmation list and recent audit. Confirming a pending item is always an explicit
/// click on that item; nothing on this page approves a remote request automatically.
///
/// The page follows module events instead of polling: <see cref="Attach"/> subscribes to the
/// Shell's existing event stream, a burst of events coalesces into one reload, and
/// <see cref="Detach"/> releases the subscription when the surface leaves the visual tree. Reloads
/// never reset the user's in-progress grant edits or command selection.
/// </summary>
public sealed class ControlSurfaceViewModel : MptObservableViewModel
{
    public const string ModuleId = "remote-tool-gateway";

    private readonly IGatewaySurfaceHost _host;
    private string _status = "正在读取状态…";
    private string _listenerEndpoint = "";
    private bool _listenerRunning;
    private bool _listenerEnabled;
    private string _address = "";
    private string _port = "";
    private string _newDeviceName = "";
    private bool _newAllowElevated;
    private string _createdCode = "";
    private string _createdCodeGrantId = "";
    private string _createdCodeContext = "";
    private int _codeRevealGeneration;
    private string _commandFilter = "";
    private bool _busy;
    private bool _loaded;
    private IDisposable? _subscription;
    private bool _refreshing;
    private bool _refreshPending;
    private int _refreshes;

    public ControlSurfaceViewModel(IGatewaySurfaceHost host)
    {
        _host = host;
    }

    public ObservableCollection<GrantRow> Grants { get; } = [];
    public ObservableCollection<PendingRow> Pending { get; } = [];
    public ObservableCollection<CatalogCommandRow> Catalog { get; } = [];
    public ObservableCollection<ActivityRow> Activity { get; } = [];
    public ObservableCollection<string> AvailableAddresses { get; } = [];

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ListenerEndpoint { get => _listenerEndpoint; private set => SetProperty(ref _listenerEndpoint, value); }
    public bool ListenerRunning { get => _listenerRunning; private set => SetProperty(ref _listenerRunning, value); }
    public bool ListenerEnabled { get => _listenerEnabled; private set => SetProperty(ref _listenerEnabled, value); }
    public bool HasPending => Pending.Count > 0;
    public bool HasGrants => Grants.Count > 0;
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    /// <summary>The user's Tailscale address; only values from the machine's own tailnet are accepted.</summary>
    public string Address { get => _address; set => SetProperty(ref _address, value); }
    public string Port { get => _port; set => SetProperty(ref _port, value); }
    public string NewDeviceName { get => _newDeviceName; set => SetProperty(ref _newDeviceName, value); }
    public bool NewAllowElevated { get => _newAllowElevated; set => SetProperty(ref _newAllowElevated, value); }
    /// <summary>
    /// The connection code the user explicitly asked to see. It is a credential: the page shows it
    /// (and its QR symbol) only while this is non-empty, and clears it on hide, detach, revocation
    /// or when the grant it belongs to disappears.
    /// </summary>
    public string CreatedCode { get => _createdCode; private set => SetProperty(ref _createdCode, value); }

    /// <summary>Short, non-secret context for the displayed code: which phone it authorizes.</summary>
    public string CreatedCodeContext { get => _createdCodeContext; private set => SetProperty(ref _createdCodeContext, value); }

    /// <summary>True while a code is revealed; the view uses it instead of inspecting the string.</summary>
    public bool HasCreatedCode => CreatedCode.Length > 0;
    public string CommandFilter { get => _commandFilter; set { if (SetProperty(ref _commandFilter, value)) ApplyFilter(); } }

    /// <summary>True once a page load completed; used by tests to assert nothing auto-confirms.</summary>
    public bool Loaded => _loaded;

    /// <summary>True while the module event stream is attached.</summary>
    public bool IsAttached => _subscription is not null;

    /// <summary>Completed reloads; exposed so tests can assert coalescing and detach behaviour.</summary>
    public int RefreshCount => Volatile.Read(ref _refreshes);

    public IReadOnlyList<CatalogCommandRow> VisibleCatalog { get; private set; } = [];

    /// <summary>
    /// Follows the module's event stream through the Shell's existing subscription. Passing null (a
    /// host without event support) leaves the manual refresh button as the only path.
    /// </summary>
    public void Attach(Func<Action<MptSurfaceEvent>, IDisposable>? subscribe)
    {
        Detach();
        if (subscribe is null) return;
        _subscription = subscribe(HandleSurfaceEvent);
    }

    public void Detach()
    {
        _subscription?.Dispose();
        _subscription = null;
        _refreshPending = false;
        // Leaving the page drops the revealed credential too: nothing stays on screen (or in a
        // cached view) after the surface is gone.
        ClearCreatedCode();
    }

    /// <summary>
    /// Module events that can change this page schedule exactly one coalesced reload. Detach is
    /// authoritative: a late callback from a released subscription cannot reload anything.
    /// </summary>
    public void HandleSurfaceEvent(MptSurfaceEvent surfaceEvent)
    {
        if (surfaceEvent is null || _subscription is null) return;
        if (!surfaceEvent.Type.StartsWith(ModuleId + ".", StringComparison.Ordinal)) return;
        _refreshPending = true;
        if (_refreshing) return;
        _ = RefreshLoopAsync();
    }

    /// <summary>One reload per burst of events; never a timer and never a poll.</summary>
    private async Task RefreshLoopAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            // Let a synchronous burst of events accumulate before the first reload, so N events in
            // one dispatch produce one reload. The loop is event-driven: with no events pending it
            // ends and nothing keeps running.
            await Task.Yield();
            while (_refreshPending)
            {
                _refreshPending = false;
                await LoadAsync();
                await Task.Yield();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = MyPowerTools.Abstractions.MptLogRedactor.Redact(ex.Message);
        }
        finally { _refreshing = false; }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var payload = await _host.ExecuteModuleCommandAsync($"{ModuleId}.inspect", new JsonObject { ["includeCatalog"] = true }, cancellationToken);
        Apply(payload);
        _loaded = true;
        Interlocked.Increment(ref _refreshes);
    }

    private void Apply(JsonObject payload)
    {
        var listener = payload["listener"] as JsonObject ?? new JsonObject();
        ListenerRunning = ReadBool(listener, "running");
        ListenerEnabled = ReadBool(listener, "enabled");
        ListenerEndpoint = ReadString(listener, "endpoint");
        Address = ReadString(listener, "address");
        var port = ReadInt(listener, "port");
        Port = port > 0 ? port.ToString() : ReadInt(payload, "defaultPort").ToString();
        Status = ReadString(listener, "message");

        AvailableAddresses.Clear();
        if (listener["availableAddresses"] is JsonArray addresses)
            foreach (var item in addresses)
                if (item?.GetValue<string>() is { Length: > 0 } address) AvailableAddresses.Add(address);

        Grants.Clear();
        if (payload["grants"] is JsonArray grants)
        {
            foreach (var item in grants.OfType<JsonObject>())
            {
                Grants.Add(new GrantRow
                {
                    GrantId = ReadString(item, "grantId"),
                    DeviceName = ReadString(item, "deviceName"),
                    AllowElevated = ReadBool(item, "allowElevated"),
                    CommandIds = (item["commandIds"] as JsonArray)?
                        .Select(node => node?.GetValue<string>() ?? "")
                        .Where(id => id.Length > 0)
                        .ToArray() ?? [],
                    ActiveInvocations = ReadInt(item, "activeInvocations"),
                    LastUsedAt = ReadString(item, "lastUsedAt")
                });
            }
        }

        Pending.Clear();
        if (payload["pending"] is JsonArray pending)
        {
            foreach (var item in pending.OfType<JsonObject>())
            {
                Pending.Add(new PendingRow
                {
                    InvocationId = ReadString(item, "invocationId"),
                    DeviceName = ReadString(item, "deviceName"),
                    CommandId = ReadString(item, "commandId"),
                    CommandTitle = ReadString(item, "commandTitle"),
                    ArgsSummary = ReadString(item, "argsSummary"),
                    Kind = ReadString(item, "kind"),
                    RequiresElevation = ReadBool(item, "requiresElevation"),
                    Claimed = ReadBool(item, "claimed")
                });
            }
        }

        // The picker keeps whatever the user has selected so far: a background refresh must never
        // silently drop or add commands to an authorization the user is editing.
        var selected = CurrentSelection();
        Catalog.Clear();
        if (payload["catalog"] is JsonObject catalog && catalog["commands"] is JsonArray commands)
        {
            foreach (var item in commands.OfType<JsonObject>())
            {
                var commandId = ReadString(item, "commandId");
                Catalog.Add(new CatalogCommandRow
                {
                    CommandId = commandId,
                    ModuleId = ReadString(item, "moduleId"),
                    Title = ReadString(item, "title"),
                    Subtitle = ReadString(item, "subtitle"),
                    RequiresElevation = ReadBool(item, "requiresElevation"),
                    ManagementOnly = ReadBool(item, "manageOnly"),
                    Selected = selected.Contains(commandId)
                });
            }
        }

        Activity.Clear();
        if (payload["invocations"] is JsonArray invocations)
        {
            foreach (var item in invocations.OfType<JsonObject>().Take(12))
            {
                Activity.Add(new ActivityRow
                {
                    Title = $"{ReadString(item, "commandId")} · {ReadString(item, "deviceName")}",
                    Detail = ReadString(item, "message"),
                    State = ReadString(item, "state")
                });
            }
        }

        if (payload["audit"] is JsonArray audit)
        {
            foreach (var item in audit.OfType<JsonObject>().Take(8))
            {
                Activity.Add(new ActivityRow
                {
                    Title = $"{ReadString(item, "kind")} · {ReadString(item, "commandId")}",
                    Detail = ReadString(item, "message"),
                    State = ReadString(item, "state")
                });
            }
        }

        // The grant may have been revoked from another surface or by a direct module command: the
        // displayed code must not outlive its authorization.
        if (_createdCodeGrantId.Length > 0 && !Grants.Any(row => string.Equals(row.GrantId, _createdCodeGrantId, StringComparison.Ordinal)))
            ClearCreatedCode();

        ApplyFilter();
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(HasGrants));
    }

    public HashSet<string> CurrentSelection() =>
        Catalog.Where(row => row.Selected && row.Selectable).Select(row => row.CommandId).ToHashSet(StringComparer.Ordinal);

    public void SelectCommands(IEnumerable<string> commandIds)
    {
        var set = commandIds.ToHashSet(StringComparer.Ordinal);
        foreach (var row in Catalog) row.Selected = row.Selectable && set.Contains(row.CommandId);
    }

    private void ApplyFilter()
    {
        var filter = CommandFilter.Trim();
        VisibleCatalog = filter.Length == 0
            ? Catalog.ToArray()
            : Catalog.Where(row => row.Display.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        OnPropertyChanged(nameof(VisibleCatalog));
    }

    public async Task StartListenerAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            await _host.ExecuteModuleCommandAsync($"{ModuleId}.listener.start", new JsonObject
            {
                ["address"] = Address,
                ["port"] = ParsePort()
            }, cancellationToken);
            await LoadAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task StopListenerAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            await _host.ExecuteModuleCommandAsync($"{ModuleId}.listener.stop", new JsonObject(), cancellationToken);
            await LoadAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task CreateGrantAsync(CancellationToken cancellationToken = default)
    {
        var revealGeneration = _codeRevealGeneration;
        await RunAsync(async () =>
        {
            var payload = await _host.ExecuteModuleCommandAsync($"{ModuleId}.grant.create", new JsonObject
            {
                ["deviceName"] = NewDeviceName,
                ["allowElevated"] = NewAllowElevated,
                ["commandIds"] = new JsonArray(CurrentSelection().Select(id => (JsonNode)JsonValue.Create(id)!).ToArray())
            }, cancellationToken);
            if (revealGeneration == _codeRevealGeneration) ShowCode(payload);
            NewDeviceName = "";
            NewAllowElevated = false;
            await LoadAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task ShowCodeAsync(string grantId, CancellationToken cancellationToken = default)
    {
        var revealGeneration = _codeRevealGeneration;
        await RunAsync(async () =>
        {
            var payload = await _host.ExecuteModuleCommandAsync($"{ModuleId}.grant.code", new JsonObject { ["grantId"] = grantId }, cancellationToken);
            if (revealGeneration == _codeRevealGeneration) ShowCode(payload);
        }, cancellationToken);
    }

    public async Task RevokeAsync(string grantId, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            await _host.ExecuteModuleCommandAsync($"{ModuleId}.grant.revoke", new JsonObject { ["grantId"] = grantId }, cancellationToken);
            // A revoked code is dead: it must not stay on screen (or in a scannable QR) after this.
            if (string.Equals(_createdCodeGrantId, grantId, StringComparison.Ordinal)) ClearCreatedCode();
            await LoadAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Hides the code and its QR symbol; the string is dropped, not just hidden.</summary>
    public void ClearCreatedCode()
    {
        _codeRevealGeneration++;
        _createdCodeGrantId = "";
        CreatedCodeContext = "";
        CreatedCode = "";
        OnPropertyChanged(nameof(HasCreatedCode));
    }

    private void ShowCode(JsonObject payload)
    {
        var code = ReadString(payload, "code");
        _createdCodeGrantId = ReadString(payload, "grantId");
        var name = ReadString(payload, "deviceName");
        var endpoint = ReadString(payload, "endpoint");
        CreatedCode = code;
        CreatedCodeContext = code.Length == 0
            ? ""
            : (name.Length > 0 ? name : "设备") + (endpoint.Length > 0 ? " · " + endpoint : "");
        OnPropertyChanged(nameof(HasCreatedCode));
    }

    public async Task SaveGrantAsync(string grantId, bool allowElevated, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var existing = Grants.FirstOrDefault(row => row.GrantId == grantId);
            await _host.ExecuteModuleCommandAsync($"{ModuleId}.grant.update", new JsonObject
            {
                ["grantId"] = grantId,
                ["deviceName"] = existing?.DeviceName ?? "",
                ["allowElevated"] = allowElevated,
                ["commandIds"] = new JsonArray(CurrentSelection().Select(id => (JsonNode)JsonValue.Create(id)!).ToArray())
            }, cancellationToken);
            await LoadAsync(cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Confirms one pending remote request. The runtime command runs through the surface's existing
    /// execution entry point with the phone's own invocation id, so the phone can still cancel the
    /// real call; only the runtime's real outcome is reported back to the gateway.
    /// </summary>
    public async Task ConfirmAsync(PendingRow row, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            if (!_host.SupportsInvocationScopedExecution)
            {
                // Never fall back to a fresh id: that execution could not be cancelled from the
                // phone, so the request is refused with an explicit reason instead.
                await ResolveAsync(row.InvocationId, accepted: false,
                    errorMessage: "当前 Shell 不支持保留手机调用 ID 的执行入口；为避免产生无法取消的远程执行，已在电脑上拒绝该请求。",
                    cancellationToken: cancellationToken);
                await LoadAsync(cancellationToken);
                return;
            }

            var claim = await _host.ExecuteModuleCommandAsync($"{ModuleId}.confirmation.claim",
                new JsonObject { ["invocationId"] = row.InvocationId }, cancellationToken);
            var commandId = ReadString(claim, "commandId");
            var args = claim["args"] as JsonObject ?? new JsonObject();
            var outcome = await _host.ExecuteRuntimeCommandAsync(row.InvocationId, commandId, args, cancellationToken);
            await ResolveAsync(row.InvocationId, accepted: true,
                state: outcome.State,
                summary: outcome.Output,
                errorCode: outcome.ErrorCode,
                errorMessage: outcome.ErrorMessage,
                retryable: outcome.Retryable,
                cancellationToken: cancellationToken);
            await LoadAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task RejectAsync(PendingRow row, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            await ResolveAsync(row.InvocationId, accepted: false, errorMessage: "电脑端用户拒绝了此操作。", cancellationToken: cancellationToken);
            await LoadAsync(cancellationToken);
        }, cancellationToken);
    }

    private Task<JsonObject> ResolveAsync(
        string invocationId,
        bool accepted,
        string state = "",
        string summary = "",
        string errorCode = "",
        string errorMessage = "",
        bool retryable = false,
        CancellationToken cancellationToken = default) =>
        _host.ExecuteModuleCommandAsync($"{ModuleId}.confirmation.resolve", new JsonObject
        {
            ["invocationId"] = invocationId,
            ["accepted"] = accepted,
            ["state"] = state,
            ["summary"] = summary,
            ["errorCode"] = errorCode,
            ["errorMessage"] = errorMessage,
            ["retryable"] = retryable
        }, cancellationToken);

    private int ParsePort() => int.TryParse(Port.Trim(), out var port) ? port : 0;

    private async Task RunAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await action();
            Status = "已完成。";
        }
        catch (OperationCanceledException) { Status = "操作已取消。"; }
        catch (Exception ex)
        {
            // Failures are shown on the page; the surface never faults the Shell's UI loop.
            Status = MyPowerTools.Abstractions.MptLogRedactor.Redact(ex.Message);
        }
        finally { IsBusy = false; }
    }

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

    private static int ReadInt(JsonObject json, string key)
    {
        try { return json[key]?.GetValue<int>() ?? 0; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return 0; }
    }
}
