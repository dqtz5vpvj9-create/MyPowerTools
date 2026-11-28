using Avalonia.Controls;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.WebSurface.Avalonia;
using MyPowerTools.Shell.Avalonia.ViewModels;
using MyPowerTools.Shell.Avalonia.Views;
using MyPowerTools.UI.Controls;

namespace MyPowerTools.Shell.Avalonia.Services;

/// <summary>
/// Implements docs/desktop/ui-recovery-contract.md: every failure renders through one recovery
/// page whose button runs the next, stronger rung of the ladder and shows progress while it runs.
/// </summary>
public sealed partial class ShellWorkspaceController
{
    private static readonly TimeSpan SurfaceSettleTimeout = TimeSpan.FromSeconds(15);
    private readonly ShellRecoveryTracker _recovery = new();

    private string CurrentRecoveryKey => string.IsNullOrWhiteSpace(_currentToolId)
        ? $"page:{_currentPage}"
        : $"tool:{_currentToolId}:{_currentToolRouteId}";

    private Control BuildRecoveryPage(
        string title,
        ShellUserFacingFailure failure,
        Func<Task> reload,
        ShellFailureSource source = ShellFailureSource.Runner)
    {
        var key = CurrentRecoveryKey;
        var kind = ShellRecoveryPlan.KindOf(failure.Code);
        var entry = _recovery.RecordFailure(key);
        var step = ShellRecoveryPlan.StepFor(kind, entry.Attempt);
        UnavailablePageViewModel? viewModel = null;
        viewModel = new UnavailablePageViewModel(
            RecoveryTitle(title),
            failure.Message,
            ShellRecoveryPlan.Present(step, source, entry),
            () => RunRecoveryAsync(key, step, source, text => viewModel?.ReportProgress(text), reload),
            () => ShowPageAsync(HomePage),
            failure.Details);
        return new UnavailablePageView { DataContext = viewModel };
    }

    private async Task RunRecoveryAsync(
        string key,
        ShellRecoveryStep step,
        ShellFailureSource source,
        Action<string> report,
        Func<Task> reload,
        bool clearOnSuccess = true)
    {
        var progress = new Progress<string>(report);
        _recovery.BeginAttempt(key, step);
        try
        {
            switch (step)
            {
                case ShellRecoveryStep.StartService:
                    await ShellRecoveryActions.StartServiceAsync(source, progress);
                    break;
                case ShellRecoveryStep.RestartService:
                    await ShellRecoveryActions.RestartServiceAsync(source, progress);
                    break;
                case ShellRecoveryStep.Repair:
                    _recovery.Clear(key);
                    await ShowPageAsync(PackagesPage);
                    return;
                case ShellRecoveryStep.ReviewPermissions:
                    _recovery.Clear(key);
                    await ShowPageAsync(ModulesPage);
                    return;
                case ShellRecoveryStep.RestartApp:
                    report("Restarting MyPowerTools…");
                    _recovery.Clear(key);
                    ShellRecoveryActions.RestartApplication();
                    return;
            }
        }
        catch (Exception ex)
        {
            // A remediation that cannot complete (for example a service owned by another account)
            // still falls through to the reload; the next rung covers what this one could not.
            ShellCommandFaultLog.Write($"Recovery step {step}", ex, "recovery");
        }

        report("Reloading…");
        await reload();
        if (clearOnSuccess && !IsRecoveryContent(_contentHost.Content))
        {
            _recovery.Clear(key);
        }
    }

    /// <summary>
    /// Hosted tool surfaces keep their own view model across reloads, so the ladder is driven here:
    /// a plain refresh when the surface is healthy, the next recovery rung when it is not.
    /// </summary>
    private async Task RecoverToolSurfaceAsync(ExternalSdkToolViewModel? viewModel, Func<Task> reload)
    {
        if (viewModel is null || !viewModel.IsSurfaceFailed)
        {
            await reload();
            return;
        }

        var key = CurrentRecoveryKey;
        var entry = _recovery.RecordFailure(key);
        var step = ShellRecoveryPlan.StepFor(ShellFailureKind.SurfaceUnavailable, entry.Attempt);
        viewModel.BeginRecovery();
        try
        {
            await RunRecoveryAsync(key, step, ShellFailureSource.Runner, viewModel.ReportRecoveryProgress, reload, clearOnSuccess: false);
            var current = CurrentSurfaceViewModel() ?? viewModel;
            var deadline = DateTimeOffset.UtcNow + SurfaceSettleTimeout;
            while (current.IsSurfaceLoading && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250);
            }

            if (current.IsSurfaceFailed)
            {
                ApplyToolRecoveryPresentation(current);
            }
            else
            {
                _recovery.Clear(key);
            }
        }
        finally
        {
            viewModel.EndRecovery();
        }
    }

    private Task ReloadToolSurfaceAsync(IMptWebSurfaceSession? session, string toolId, string routeId)
    {
        if (session is not null)
        {
            session.Reload();
            return Task.CompletedTask;
        }

        return ShowToolPageAsync(toolId, routeId);
    }

    private void ApplyToolRecoveryPresentation(ExternalSdkToolViewModel viewModel)
    {
        var key = CurrentRecoveryKey;
        if (_recovery.Find(key) is null)
        {
            return;
        }

        var entry = _recovery.RecordFailure(key);
        var step = ShellRecoveryPlan.StepFor(ShellFailureKind.SurfaceUnavailable, entry.Attempt);
        viewModel.ApplyRecoveryPresentation(ShellRecoveryPlan.Present(step, ShellFailureSource.Runner, entry));
    }

    private ExternalSdkToolViewModel? CurrentSurfaceViewModel() =>
        (_contentHost.Content as Control)?.DataContext as ExternalSdkToolViewModel
        ?? _chromeViewModel.HeaderContent;

    private static bool IsRecoveryContent(object? content) =>
        content is UnavailablePageView or MptErrorState;

    private static string RecoveryTitle(string title) =>
        string.Equals(title, "Tool", StringComparison.OrdinalIgnoreCase)
            ? "This tool couldn't open"
            : "This page couldn't load";

    /// <summary>Last-resort content when even the recovery page cannot be rendered.</summary>
    private Control BuildTerminalRecoveryContent(string message) =>
        ShellRecoveryPlan.CanRestartApp
            ? new MptErrorState(message, "Restart MyPowerTools", ShellRecoveryActions.RestartApplication)
            : new MptErrorState(message, "Go to Home", () => RunUiEvent(() => ShowPageAsync(HomePage), "Recover to Home"));
}
