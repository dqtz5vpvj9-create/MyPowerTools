using System.Runtime.InteropServices;
using System.Text.Json;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Platform.Mac;

public sealed class MacStatusItemTrayService : ITrayService
{
    private static readonly MacNative.TrayActionCallback NativeCallback = HandleNativeAction;
    private readonly object _gate = new();
    private Func<TrayActionInvocation, CancellationToken, Task>? _actionHandler;
    private GCHandle _selfHandle;
    private nint _nativeHandle;
    private CancellationTokenSource? _quotaCancellation;
    private Task? _quotaMonitorTask;
    private string _state = "stopped";
    private bool _disposed;

    public string State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public Task<TrayStartResult> StartAsync(
        TrayOptions options,
        Func<TrayActionInvocation, CancellationToken, Task> actionHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(actionHandler);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsMacOS())
        {
            return Task.FromResult(new TrayStartResult(false, "unsupported", "NSStatusItem requires macOS."));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return Task.FromResult(new TrayStartResult(false, "disposed", "NSStatusItem has been disposed."));
            }
            if (_nativeHandle != 0)
            {
                return Task.FromResult(new TrayStartResult(true, _state, "NSStatusItem is already running."));
            }
            _state = "starting";
            _actionHandler = actionHandler;
            _selfHandle = GCHandle.Alloc(this, GCHandleType.Normal);
            try
            {
                var menuJson = JsonSerializer.Serialize(options.MenuItems.Select(item => new
                {
                    id = item.Id,
                    label = item.Label,
                    isDefault = item.IsDefault,
                    separatorBefore = item.SeparatorBefore
                }));
                _nativeHandle = MacNative.CreateStatusItem(
                    options.ToolTip,
                    options.IconPath ?? "",
                    menuJson,
                    NativeCallback,
                    GCHandle.ToIntPtr(_selfHandle));
                if (_nativeHandle == 0)
                {
                    throw new InvalidOperationException("NSStatusItem returned an empty handle.");
                }
                _quotaCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var statusItemHandle = _nativeHandle;
                var quotaToken = _quotaCancellation.Token;
                _quotaMonitorTask = Task.Run(
                    () => MonitorCodexQuotaAsync(statusItemHandle, options.ToolTip, quotaToken),
                    CancellationToken.None);
                _state = "running";
                return Task.FromResult(
                    new TrayStartResult(true, _state, "Native NSStatusItem with Codex quota monitoring started."));
            }
            catch (Exception ex)
            {
                _quotaCancellation?.Cancel();
                _quotaCancellation?.Dispose();
                _quotaCancellation = null;
                _quotaMonitorTask = null;
                var statusItemHandle = Interlocked.Exchange(ref _nativeHandle, 0);
                if (statusItemHandle != 0)
                {
                    MacNative.DestroyStatusItem(statusItemHandle);
                }
                if (_selfHandle.IsAllocated)
                {
                    _selfHandle.Free();
                }
                _actionHandler = null;
                _state = "failed";
                return Task.FromResult(new TrayStartResult(false, _state, ex.GetBaseException().Message));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? quotaCancellation;
        Task? quotaMonitorTask;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _state = "stopping";
            quotaCancellation = _quotaCancellation;
            quotaMonitorTask = _quotaMonitorTask;
        }

        quotaCancellation?.Cancel();
        if (quotaMonitorTask is not null)
        {
            try
            {
                await quotaMonitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        nint statusItemHandle;
        lock (_gate)
        {
            statusItemHandle = Interlocked.Exchange(ref _nativeHandle, 0);
            _actionHandler = null;
        }
        if (statusItemHandle != 0)
        {
            MacNative.DestroyStatusItem(statusItemHandle);
        }

        quotaCancellation?.Dispose();
        lock (_gate)
        {
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }
            _quotaCancellation = null;
            _quotaMonitorTask = null;
            _state = "stopped";
        }
    }

    private async Task MonitorCodexQuotaAsync(
        nint statusItemHandle,
        string baseToolTip,
        CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromMinutes(1);
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(5);
            try
            {
                var snapshot = await CodexQuotaReader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var displayWindow = snapshot.DisplayWindow;
                if (displayWindow is null ||
                    MacNative.UpdateStatusItemQuota(
                        statusItemHandle,
                        displayWindow.RemainingPercent,
                        BuildQuotaToolTip(baseToolTip, snapshot)) == 0)
                {
                    delay = retryDelay;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                delay = retryDelay;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string BuildQuotaToolTip(
        string baseToolTip,
        CodexQuotaSnapshot snapshot,
        DateTimeOffset? utcNow = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseToolTip))
        {
            parts.Add(baseToolTip.Trim());
        }
        var now = utcNow ?? DateTimeOffset.UtcNow;
        if (snapshot.WeeklyWindow is { } weekly)
        {
            parts.Add($"Codex 7d {weekly.RemainingPercent}% left{FormatReset(weekly.ResetsAt, now)}");
        }
        if (snapshot.ShortWindow is { } shortWindow)
        {
            parts.Add($"Codex 5h {shortWindow.RemainingPercent}% left{FormatReset(shortWindow.ResetsAt, now)}");
        }
        return string.Join(" | ", parts);
    }

    private static string FormatReset(DateTimeOffset? resetsAt, DateTimeOffset utcNow)
    {
        if (resetsAt is null)
        {
            return "";
        }

        var remaining = resetsAt.Value - utcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return " · resetting";
        }
        if (remaining.TotalDays >= 1)
        {
            return $" · resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
        }
        if (remaining.TotalHours >= 1)
        {
            return $" · resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        }
        return $" · resets in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    private static void HandleNativeAction(nint context, nint actionIdPointer)
    {
        if (context == 0 || actionIdPointer == 0)
        {
            return;
        }
        MacStatusItemTrayService? service;
        try
        {
            service = GCHandle.FromIntPtr(context).Target as MacStatusItemTrayService;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        var actionId = Marshal.PtrToStringUTF8(actionIdPointer);
        if (service is null || string.IsNullOrWhiteSpace(actionId))
        {
            return;
        }
        _ = service.InvokeActionAsync(actionId);
    }

    private async Task InvokeActionAsync(string actionId)
    {
        Func<TrayActionInvocation, CancellationToken, Task>? handler;
        lock (_gate)
        {
            handler = _actionHandler;
        }
        if (handler is null)
        {
            return;
        }
        try
        {
            await handler(
                new TrayActionInvocation(actionId, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
