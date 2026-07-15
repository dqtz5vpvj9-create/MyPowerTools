namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed record ShellCommandFaultContext(
    string ControllerId,
    string WorkspaceId,
    long NavigationGeneration,
    string InvocationId)
{
    public static ShellCommandFaultContext Unscoped { get; } = new("", "", 0, "");

    public ShellCommandFaultContext BeginInvocation()
    {
        return this with { InvocationId = Guid.NewGuid().ToString("N") };
    }
}

public sealed class ShellCommandFaultEventArgs : EventArgs
{
    public ShellCommandFaultEventArgs(string operation, Exception exception)
        : this(operation, exception, ShellCommandFaultContext.Unscoped)
    {
    }

    public ShellCommandFaultEventArgs(
        string operation,
        Exception exception,
        ShellCommandFaultContext context)
    {
        Operation = operation;
        Exception = exception;
        Context = context;
    }

    public string Operation { get; }
    public Exception Exception { get; }
    public ShellCommandFaultContext Context { get; }
}

/// <summary>
/// Creates monotonic workspace identities. Route text can repeat; workspace IDs
/// and generations cannot, so an old callback cannot pass an ABA route check.
/// </summary>
public sealed class ShellWorkspaceIdentity
{
    private readonly object _gate = new();
    private string _workspaceId = Guid.NewGuid().ToString("N");
    private long _generation;

    public ShellWorkspaceIdentity(string? controllerId = null)
    {
        ControllerId = string.IsNullOrWhiteSpace(controllerId)
            ? Guid.NewGuid().ToString("N")
            : controllerId;
    }

    public string ControllerId { get; }

    public ShellCommandFaultContext BeginNavigation()
    {
        lock (_gate)
        {
            _generation = checked(_generation + 1);
            _workspaceId = Guid.NewGuid().ToString("N");
            return CaptureCore("");
        }
    }

    public ShellCommandFaultContext Capture(string? invocationId = null)
    {
        lock (_gate)
        {
            return CaptureCore(invocationId ?? "");
        }
    }

    public bool IsCurrent(ShellCommandFaultContext context)
    {
        lock (_gate)
        {
            return string.Equals(context.ControllerId, ControllerId, StringComparison.Ordinal) &&
                   string.Equals(context.WorkspaceId, _workspaceId, StringComparison.Ordinal) &&
                   context.NavigationGeneration == _generation;
        }
    }

    private ShellCommandFaultContext CaptureCore(string invocationId)
    {
        return new ShellCommandFaultContext(
            ControllerId,
            _workspaceId,
            _generation,
            invocationId);
    }
}

/// <summary>
/// A fault sink belongs to exactly one Shell workspace controller. Its
/// subscribers are isolated individually and can never fault the UI dispatcher.
/// </summary>
public sealed class ShellCommandFaultSink : IDisposable
{
    private int _disposed;

    public ShellCommandFaultSink(string controllerId)
    {
        ControllerId = controllerId;
    }

    public string ControllerId { get; }

    public event EventHandler<ShellCommandFaultEventArgs>? Faulted;

    public void Report(
        object? sender,
        string operation,
        Exception exception,
        ShellCommandFaultContext context)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !string.Equals(context.ControllerId, ControllerId, StringComparison.Ordinal))
        {
            ShellCommandFaultLog.Write(operation, exception, "rejected-scope");
            return;
        }

        var fault = new ShellCommandFaultEventArgs(operation, exception, context);
        var handlers = Faulted;
        if (handlers is null)
        {
            ShellCommandFaultLog.Write(operation, exception, "unobserved");
            return;
        }

        foreach (EventHandler<ShellCommandFaultEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(sender, fault);
            }
            catch (Exception observerException)
            {
                ShellCommandFaultLog.Write(
                    $"{operation}.FaultSubscriber",
                    observerException,
                    "subscriber");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Faulted = null;
        }
    }
}

/// <summary>
/// Last-resort boundary for UI callbacks without a workspace owner. The static
/// layer only writes bounded diagnostics; it never navigates or mutates a page.
/// </summary>
public static class ShellCommandFaultBoundary
{
    internal static void Report(
        object? sender,
        ShellCommandFaultEventArgs fault,
        ShellCommandFaultSink? sink = null)
    {
        if (sink is not null)
        {
            sink.Report(sender, fault.Operation, fault.Exception, fault.Context);
            return;
        }

        ShellCommandFaultLog.Write(fault.Operation, fault.Exception, "unscoped");
    }

    internal static void Run(
        object? sender,
        string operation,
        Func<Task> action,
        ShellCommandFaultSink? sink = null,
        ShellCommandFaultContext? context = null)
    {
        _ = RunCoreAsync(sender, operation, action, sink, context);
    }

    private static async Task RunCoreAsync(
        object? sender,
        string operation,
        Func<Task> action,
        ShellCommandFaultSink? sink,
        ShellCommandFaultContext? context)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var captured = (context ?? ShellCommandFaultContext.Unscoped).BeginInvocation();
            Report(sender, new ShellCommandFaultEventArgs(operation, ex, captured), sink);
        }
    }
}
