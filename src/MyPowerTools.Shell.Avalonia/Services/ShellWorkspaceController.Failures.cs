using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Shell.Avalonia.Services;

public sealed partial class ShellWorkspaceController
{
    private ShellUserFacingFailure ReportPageFailure(
        string operation,
        Exception exception,
        ShellFailureSource source = ShellFailureSource.Runner)
    {
        var failure = ShellFailurePresenter.Present(exception, source);
        ShellCommandFaultLog.Write($"{operation} [{failure.Code}]", exception, "page-load");
        SetStatus(failure.StatusMessage);
        return failure;
    }

    /// <summary>
    /// A page load that fails after the user has navigated elsewhere must not repaint the
    /// workspace: with per-call gRPC deadlines the failure can surface many seconds later and
    /// would otherwise replace whatever the user is now looking at. Stale failures are still
    /// written to the fault log, they just stop short of touching the status bar and content host.
    /// </summary>
    private bool IsStalePageFailure(
        string operation,
        Exception exception,
        ShellCommandFaultContext identity)
    {
        if (_workspaceIdentity.IsCurrent(identity))
        {
            return false;
        }

        ShellCommandFaultLog.Write(operation, exception, "page-load-stale");
        return true;
    }
}
