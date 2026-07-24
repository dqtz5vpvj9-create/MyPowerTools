namespace MyPowerTools.AvaloniaSdk;

/// <summary>
/// Fire-and-forget fault boundary for UI command callbacks in dotnet-surface tools.
/// Runs an action without blocking the calling (UI) thread; async actions are scheduled by
/// their awaiter, so UI-thread-affine operations like clipboard writes cannot deadlock the UI.
/// Faults are written to <see cref="System.Diagnostics.Debug"/> listeners and optionally
/// surfaced through <see cref="FaultObserved"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the single shared implementation for surface tools. It replaces the per-surface
/// <c>ShellCommandFaultBoundary</c> stubs that diverged (one blocked the UI thread with
/// <c>.GetAwaiter().GetResult()</c>, another used <c>async void</c>).
/// </para>
/// <para>
/// The async overload never blocks the calling thread — the returned <see cref="Task"/> is
/// intentionally discarded so the caller (typically an Avalonia <c>Click</c> handler on the UI
/// thread) returns immediately. The inner action is still awaited with
/// <see cref="Task.ConfigureAwait"/>(true) so post-await UI work runs on the UI thread; this is
/// safe because the UI thread was never blocked in the first place.
/// </para>
/// </remarks>
public static class MptCommandFaultBoundary
{
    /// <summary>
    /// Runs a synchronous action on the calling thread and swallows any fault into the trace sink.
    /// </summary>
    /// <param name="source">Originator of the call (passed for symmetry with the async overload; not currently used).</param>
    /// <param name="operationName">Short label used in fault traces and the <see cref="FaultObserved"/> event.</param>
    /// <param name="action">The synchronous action to run.</param>
    public static void Run(object? source, string operationName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            TraceFault(operationName, ex);
        }
    }

    /// <summary>
    /// Runs an async action fire-and-forget without blocking the calling thread. Any fault is
    /// captured after the action completes and routed to the trace sink.
    /// </summary>
    /// <param name="source">Originator of the call (passed for symmetry; not currently used).</param>
    /// <param name="operationName">Short label used in fault traces and the <see cref="FaultObserved"/> event.</param>
    /// <param name="action">The async action to run.</param>
    public static void Run(object? source, string operationName, Func<Task> action)
    {
        // Fire-and-forget: never block the calling (UI) thread. The Task's continuation is
        // scheduled by its awaiter; because we did not capture-and-block a sync context here,
        // an inner await that needs the UI thread can actually get it.
        _ = RunCoreAsync(operationName, action);
    }

    /// <summary>
    /// Optional observer hook. Surfaces that want to surface faults in-product subscribe here;
    /// the default behavior (Debug listener) is always applied regardless of subscribers.
    /// </summary>
    public static event Action<string, Exception>? FaultObserved;

    private static async Task RunCoreAsync(string operationName, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            TraceFault(operationName, ex);
        }
    }

    private static void TraceFault(string operationName, Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"[{operationName}] {ex.Message}");
        FaultObserved?.Invoke(operationName, ex);
    }
}
