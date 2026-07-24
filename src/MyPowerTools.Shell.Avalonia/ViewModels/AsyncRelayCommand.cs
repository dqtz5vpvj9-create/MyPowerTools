using MyPowerTools.Shell.Avalonia.Services;
using System.Windows.Input;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private readonly string _operationName;
    private ShellCommandFaultOwner? _faultOwner;
    private int _canExecuteFaultReported;
    private int _isRunning;

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null,
        string? operationName = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
        _operationName = string.IsNullOrWhiteSpace(operationName)
            ? execute.Method.Name
            : operationName;
    }

    public event EventHandler? CanExecuteChanged;
    public event EventHandler<ShellCommandFaultEventArgs>? ExecutionFailed;

    public void NotifyCanExecuteChanged()
    {
        RaiseCanExecuteChanged();
    }

    public bool CanExecute(object? parameter)
    {
        if (Volatile.Read(ref _isRunning) != 0)
        {
            return false;
        }

        var owner = Volatile.Read(ref _faultOwner);
        try
        {
            var allowed = _canExecute?.Invoke() ?? true;
            Interlocked.Exchange(ref _canExecuteFaultReported, 0);
            return allowed;
        }
        catch (Exception ex)
        {
            if (Interlocked.CompareExchange(ref _canExecuteFaultReported, 1, 0) == 0)
            {
                ReportFault(
                    $"{_operationName}.CanExecute",
                    ex,
                    CaptureInvocation(owner),
                    owner);
            }
            return false;
        }
    }

    public void Execute(object? parameter)
    {
        _ = ExecuteAsync(parameter);
    }

    internal async Task ExecuteAsync(object? parameter)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            return;
        }

        var owner = Volatile.Read(ref _faultOwner);
        var context = CaptureInvocation(owner);
        try
        {
            bool allowed;
            try
            {
                allowed = _canExecute?.Invoke() ?? true;
                Interlocked.Exchange(ref _canExecuteFaultReported, 0);
            }
            catch (Exception ex)
            {
                if (Interlocked.CompareExchange(ref _canExecuteFaultReported, 1, 0) == 0)
                {
                    ReportFault($"{_operationName}.CanExecute", ex, context, owner);
                }
                return;
            }

            if (!allowed)
            {
                return;
            }

            RaiseCanExecuteChanged();
            try
            {
                await _execute();
            }
            catch (Exception ex)
            {
                var fault = new ShellCommandFaultEventArgs(_operationName, ex, context);
                RaiseExecutionFailed(fault);
                try
                {
                    _onError?.Invoke(ex);
                }
                catch (Exception observerException)
                {
                    ShellCommandFaultLog.Write(
                        $"{_operationName}.OnError",
                        observerException,
                        "subscriber");
                }

                ReportFault(_operationName, ex, context, owner);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isRunning, 0);
            RaiseCanExecuteChanged();
        }
    }

    internal void SetFaultOwner(
        ShellCommandFaultSink sink,
        ShellCommandFaultContext workspaceContext)
    {
        Volatile.Write(
            ref _faultOwner,
            new ShellCommandFaultOwner(
                sink,
                workspaceContext with { InvocationId = "" }));
        Interlocked.Exchange(ref _canExecuteFaultReported, 0);
    }

    private static ShellCommandFaultContext CaptureInvocation(ShellCommandFaultOwner? owner)
    {
        return (owner?.WorkspaceContext ?? ShellCommandFaultContext.Unscoped).BeginInvocation();
    }

    private void ReportFault(
        string operation,
        Exception exception,
        ShellCommandFaultContext context,
        ShellCommandFaultOwner? owner)
    {
        var fault = new ShellCommandFaultEventArgs(operation, exception, context);
        ShellCommandFaultBoundary.Report(this, fault, owner?.Sink);
    }

    private void RaiseCanExecuteChanged()
    {
        var handlers = CanExecuteChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                ShellCommandFaultLog.Write(
                    $"{_operationName}.CanExecuteChanged",
                    ex,
                    "subscriber");
            }
        }
    }

    private void RaiseExecutionFailed(ShellCommandFaultEventArgs fault)
    {
        var handlers = ExecutionFailed;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<ShellCommandFaultEventArgs> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, fault);
            }
            catch (Exception ex)
            {
                ShellCommandFaultLog.Write(
                    $"{_operationName}.ExecutionFailed",
                    ex,
                    "subscriber");
            }
        }
    }
}
