using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Android.Pairing;

/// <summary>
/// The request bookkeeping behind <see cref="MobileQrScan"/>, with no Android types in it so the
/// concurrency rules are unit tested.
/// <para>
/// Every scan gets a token. A scanner activity carries the token it was started with and can only
/// ever resolve that token's request, which is what stops a rapid second tap from being cancelled by
/// the first scanner's shutdown. Each result resolves at most once, and a token that is no longer
/// current is ignored rather than treated as an error.
/// </para>
/// </summary>
public sealed class ScanRequestRegistry
{
    private readonly object _gate = new();
    private long _nextId;
    private Entry? _pending;

    private sealed class Entry
    {
        internal Entry(long id, TaskCompletionSource<MobileQrScanResult> result)
        {
            Id = id;
            Result = result;
        }

        internal long Id { get; }

        internal TaskCompletionSource<MobileQrScanResult> Result { get; }

        /// <summary>Set while the scanner activity is waiting for the camera permission answer.</summary>
        internal TaskCompletionSource<bool>? Permission { get; set; }
    }

    /// <summary>
    /// Starts a new request and returns its token. The previous request, when there is one, is
    /// completed as superseded so its caller cannot hang waiting for a camera that is going away.
    /// </summary>
    public ScanRequest Begin(out Task<MobileQrScanResult> result)
    {
        Entry entry;
        Entry? superseded;
        lock (_gate)
        {
            superseded = _pending;
            entry = new Entry(
                ++_nextId,
                new TaskCompletionSource<MobileQrScanResult>(TaskCreationOptions.RunContinuationsAsynchronously));
            _pending = entry;
        }

        result = entry.Result.Task;

        // The superseded request is completed after the lock is released for the same reason as in
        // Complete: its continuation is allowed to start the next scan.
        superseded?.Result.TrySetResult(MobileQrScanResult.Cancelled(
            "已开始新的扫描。", error: MobileQrScanError.Interrupted));

        return new ScanRequest(entry.Id, entry.Result.Task);
    }

    /// <summary>
    /// Resolves the request that owns <paramref name="requestId"/>, at most once. A token that is no
    /// longer current resolves nothing: an older scanner finishing its lifecycle cannot complete a
    /// newer scan.
    /// </summary>
    public void Complete(long requestId, MobileQrScanResult result)
    {
        Entry? pending;
        lock (_gate)
        {
            pending = IsCurrent(requestId) ? _pending : null;
            if (pending is not null) _pending = null;
        }

        // Resolved outside the lock: a completion can run a continuation that calls back into this
        // registry (the caller starts the next scan), and doing that while holding the lock would
        // deadlock the thread that is completing the previous request.
        pending?.Result.TrySetResult(result);
    }

    /// <summary>The permission wait for one request, so a late answer cannot resolve another one.</summary>
    public TaskCompletionSource<bool>? BeginPermissionRequest(long requestId)
    {
        lock (_gate)
        {
            if (!IsCurrent(requestId) || _pending is not { } current)
            {
                return null;
            }

            return current.Permission ??=
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void CompletePermissionRequest(long requestId, bool granted)
    {
        TaskCompletionSource<bool>? permission;
        lock (_gate)
        {
            if (!IsCurrent(requestId) || _pending is not { } current)
            {
                return;
            }

            permission = current.Permission;
            current.Permission = null;
        }

        // Same rule as Complete: resolved after the lock is released.
        permission?.TrySetResult(granted);
    }

    /// <summary>True while <paramref name="requestId"/> is the scan whose result is still awaited.</summary>
    public bool IsCurrentRequest(long requestId)
    {
        lock (_gate)
        {
            return IsCurrent(requestId);
        }
    }

    private bool IsCurrent(long requestId) => _pending is { } current && current.Id == requestId;
}

/// <summary>Handle for one in-flight scan.</summary>
public sealed record ScanRequest(long Id, Task<MobileQrScanResult> Result);
