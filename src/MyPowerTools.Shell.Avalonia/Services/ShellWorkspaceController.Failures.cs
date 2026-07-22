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
}
