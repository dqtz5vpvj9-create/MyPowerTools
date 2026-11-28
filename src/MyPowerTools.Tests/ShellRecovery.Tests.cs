using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.ViewModels;

namespace MyPowerTools.Tests;

/// <summary>Pins docs/desktop/ui-recovery-contract.md.</summary>
public sealed class ShellRecoveryTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Theory]
    [InlineData("runner-unavailable", "ServiceUnavailable")]
    [InlineData("service-manager-timeout", "ServiceTimeout")]
    [InlineData("runner-credential-mismatch", "CredentialMismatch")]
    [InlineData("runner-access-denied", "AccessDenied")]
    [InlineData("runner-permission-denied", "PermissionDenied")]
    [InlineData("local-file-missing", "FileMissing")]
    [InlineData("local-data-invalid", "DataInvalid")]
    [InlineData("unexpected-page-failure", "Unexpected")]
    [InlineData("ui-fault", "Unexpected")]
    public void Failure_codes_map_to_recovery_kinds(string code, string expected)
    {
        Assert.Equal(Enum.Parse<ShellFailureKind>(expected), ShellRecoveryPlan.KindOf(code));
    }

    [Theory]
    [InlineData("ServiceUnavailable")]
    [InlineData("ServiceTimeout")]
    [InlineData("CredentialMismatch")]
    [InlineData("AccessDenied")]
    [InlineData("PermissionDenied")]
    [InlineData("FileMissing")]
    [InlineData("DataInvalid")]
    [InlineData("Unexpected")]
    [InlineData("SurfaceUnavailable")]
    public void Every_ladder_escalates_without_repeating_and_ends_with_restart(string kindName)
    {
        var kind = Enum.Parse<ShellFailureKind>(kindName);
        var ladder = ShellRecoveryPlan.Ladder(kind);

        Assert.True(ladder.Count >= 2, "a ladder needs at least one escalation");
        Assert.Equal(ladder.Count, ladder.Distinct().Count());
        Assert.Equal(ShellRecoveryStep.RestartApp, ladder[^1]);
        var labels = ladder.Select(step => ShellRecoveryPlan.ActionLabel(step, ShellFailureSource.Runner)).ToArray();
        Assert.Equal(labels.Length, labels.Distinct().Count());
        // Past the end of the ladder the terminal rung keeps being offered.
        Assert.Equal(ShellRecoveryStep.RestartApp, ShellRecoveryPlan.StepFor(kind, 99));
    }

    [Fact]
    public void Service_outage_ladder_starts_then_restarts_the_service_then_the_app()
    {
        Assert.Equal(
            [ShellRecoveryStep.StartService, ShellRecoveryStep.RestartService, ShellRecoveryStep.RestartApp],
            ShellRecoveryPlan.Ladder(ShellFailureKind.ServiceUnavailable));
        Assert.Equal("Restart service manager", ShellRecoveryPlan.ActionLabel(ShellRecoveryStep.RestartService, ShellFailureSource.ServiceManager));
        Assert.Equal("Restart background service", ShellRecoveryPlan.ActionLabel(ShellRecoveryStep.RestartService, ShellFailureSource.Runner));
    }

    [Fact]
    public void Failure_after_a_recovery_attempt_advances_and_reports_what_was_tried()
    {
        var now = new DateTimeOffset(2026, 11, 28, 1, 13, 5, TimeSpan.Zero);
        var tracker = new ShellRecoveryTracker(() => now);
        const string key = "page:Home";

        var first = tracker.RecordFailure(key);
        Assert.Equal(1, first.Attempt);
        var firstStep = ShellRecoveryPlan.StepFor(ShellFailureKind.ServiceUnavailable, first.Attempt);
        Assert.Equal("", ShellRecoveryPlan.Present(firstStep, ShellFailureSource.Runner, first).AttemptText);

        tracker.BeginAttempt(key, firstStep);
        var second = tracker.RecordFailure(key);
        Assert.Equal(2, second.Attempt);
        var presentation = ShellRecoveryPlan.Present(
            ShellRecoveryPlan.StepFor(ShellFailureKind.ServiceUnavailable, second.Attempt),
            ShellFailureSource.Runner,
            second);

        Assert.Equal("Restart background service", presentation.ActionLabel);
        Assert.Contains(now.ToLocalTime().ToString("HH:mm:ss"), presentation.AttemptText, StringComparison.Ordinal);
        Assert.Contains("checked the background service", presentation.AttemptText, StringComparison.Ordinal);
    }

    [Fact]
    public void Background_failures_without_a_user_attempt_do_not_burn_rungs()
    {
        var tracker = new ShellRecoveryTracker();
        tracker.RecordFailure("page:Tools");
        var again = tracker.RecordFailure("page:Tools");

        Assert.Equal(1, again.Attempt);
    }

    [Fact]
    public void Success_and_expiry_reset_the_ladder()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new ShellRecoveryTracker(() => now);
        tracker.RecordFailure("a");
        tracker.BeginAttempt("a", ShellRecoveryStep.StartService);
        tracker.RecordFailure("a");
        tracker.Clear("a");
        Assert.Null(tracker.Find("a"));

        tracker.RecordFailure("b");
        now = now.AddMinutes(11);
        Assert.Null(tracker.Find("b"));
    }

    [Fact]
    public async Task Recovery_page_shows_progress_while_the_rung_runs()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        UnavailablePageViewModel? viewModel = null;
        viewModel = new UnavailablePageViewModel(
            "This page couldn't load",
            "The MyPowerTools background service isn't running.",
            new ShellRecoveryPresentation("Restart background service", "hint", "Tried at 09:13:05: reloaded."),
            async () =>
            {
                viewModel!.ReportProgress("Restarting the background service…");
                await release.Task;
            },
            () => Task.CompletedTask,
            "details");

        Assert.Equal("Restart background service", viewModel.ActionLabel);
        Assert.True(viewModel.HasAttemptText);
        var running = ((AsyncRelayCommand)viewModel.RetryCommand).ExecuteAsync(null);
        Assert.True(viewModel.IsRecovering);
        Assert.False(viewModel.IsIdle);
        Assert.Equal("Restarting the background service…", viewModel.ProgressText);

        release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(viewModel.IsRecovering);
    }

    [Fact]
    public void Shell_views_have_no_literal_retry_buttons_or_unreachable_failure_states()
    {
        var views = Directory.GetFiles(Path.Combine(Root, "src", "MyPowerTools.Shell.Avalonia", "Views"), "*.axaml");
        Assert.NotEmpty(views);
        foreach (var view in views)
        {
            var text = File.ReadAllText(view);
            Assert.False(
                text.Contains("Content=\"Try again\"", StringComparison.Ordinal),
                $"{Path.GetFileName(view)} has a literal Try again button; bind the label to the recovery plan.");
            Assert.False(
                text.Contains("IsVisible=\"{Binding IsFailed}\"", StringComparison.Ordinal),
                $"{Path.GetFileName(view)} renders its own failure state; failures must go through the recovery page.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("MyPowerTools repository root was not found.");
    }
}
