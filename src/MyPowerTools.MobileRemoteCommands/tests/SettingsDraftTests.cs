using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Regression tests for the settings draft path.
///
/// Real-device report: retention accepted “30q”, save showed the validation error, then the field was
/// corrected on screen but the next save repeated the old error. The root cause was the draft sync:
/// <c>SetSetting</c> let <c>[CallerMemberName]</c> report “SetSetting”, so a typed edit never notified the
/// binding for the property that changed, and the click path trusted whatever the binding had pushed.
/// These tests drive the real TextBox and the real save click, and assert the module payload.
/// </summary>
public sealed class SettingsDraftTests
{
    [AvaloniaFact]
    public void Invalid_edit_is_rejected_and_the_corrected_edit_is_what_the_module_receives()
    {
        using var harness = SurfaceHarness.Create();
        var view = harness.View;
        var retention = view.SettingsRetentionFieldForTests;

        // The status read populated the draft from the module.
        WaitFor(() => retention.Text == "500", harness, "模块状态没有填充草稿。");

        // User types an invalid value and clicks save.
        retention.Text = "30q";
        harness.Pump();
        Assert.Equal("30q", view.ViewModel.SettingsRetention);

        Complete(view.SaveSettingsForTestsAsync(), harness);

        Assert.Empty(harness.Module.SettingsUpdates);
        Assert.Contains("10-5000", view.ViewModel.SettingsMessage);
        Assert.True(view.ViewModel.SettingsDirty);

        // The invariant the bug broke: the visible draft and the view model stay identical after a failure.
        Assert.Equal(retention.Text, view.ViewModel.SettingsRetention);

        // User corrects the field; this is the edit that used to be ignored by the next save.
        retention.Text = "300";
        harness.Pump();
        Assert.Equal("300", view.ViewModel.SettingsRetention);

        Complete(view.SaveSettingsForTestsAsync(), harness);

        var payload = Assert.Single(harness.Module.SettingsUpdates);
        Assert.Equal(300, payload["historyRetention"]!.GetValue<int>());
        Assert.Equal("r743", payload["defaultHost"]!.GetValue<string>());
        Assert.False(view.ViewModel.SettingsDirty);
        Assert.Contains("已保存", view.ViewModel.SettingsMessage);
    }

    [AvaloniaFact]
    public void Rejected_save_keeps_the_visible_draft_and_the_view_model_identical()
    {
        using var harness = SurfaceHarness.Create();
        var view = harness.View;
        harness.Pump();

        harness.Module.SettingsFailure = "历史保留必须是 10-5000 之间的整数。";
        view.SettingsRetentionFieldForTests.Text = "300";
        harness.Pump();

        Complete(view.SaveSettingsForTestsAsync(), harness);

        var payload = Assert.Single(harness.Module.SettingsUpdates);
        Assert.Equal(300, payload["historyRetention"]!.GetValue<int>());
        Assert.True(view.ViewModel.SettingsDirty);
        Assert.Contains("草稿已保留", view.ViewModel.SettingsMessage);
        Assert.Equal(view.SettingsRetentionFieldForTests.Text, view.ViewModel.SettingsRetention);

        // Retrying after a rejection must send the same (still visible) draft again.
        Complete(view.SaveSettingsForTestsAsync(), harness);
        Assert.Equal(2, harness.Module.SettingsUpdates.Count);
        Assert.Equal(300, harness.Module.SettingsUpdates[1]["historyRetention"]!.GetValue<int>());
    }

    [AvaloniaFact]
    public void View_model_writes_reach_the_typed_controls()
    {
        using var harness = SurfaceHarness.Create();
        var view = harness.View;
        harness.Pump();

        // Pins the notification defect directly: a programmatic draft write must update the control.
        view.ViewModel.SettingsRetention = "123";
        view.ViewModel.SettingsKnownHosts = "r743\ndesktop";
        harness.Pump();

        Assert.Equal("123", view.SettingsRetentionFieldForTests.Text);
        Assert.Equal("r743\ndesktop", view.SettingsKnownHostsFieldForTests.Text);
    }

    [AvaloniaFact]
    public void Save_uses_the_visible_text_even_before_the_binding_round_trips()
    {
        using var harness = SurfaceHarness.Create();
        var view = harness.View;
        harness.Pump();

        // No Pump() between the edit and the click: the click path must commit what the user sees.
        view.SettingsRetentionFieldForTests.Text = "640";
        Complete(view.SaveSettingsForTestsAsync(), harness);

        var payload = Assert.Single(harness.Module.SettingsUpdates);
        Assert.Equal(640, payload["historyRetention"]!.GetValue<int>());
    }

    /// <summary>Pumps until <paramref name="condition"/> holds, so async status reads are not raced.</summary>
    private static void WaitFor(Func<bool> condition, SurfaceHarness harness, string because)
    {
        for (var pass = 0; pass < 64 && !condition(); pass++)
        {
            Dispatcher.UIThread.RunJobs();
            harness.Pump();
        }

        Assert.True(condition(), because);
    }

    /// <summary>
    /// Drives the dispatcher until the click's async work finished. Pumping instead of blocking keeps the
    /// test on the headless UI thread without deadlocking on its own continuations.
    /// </summary>
    private static void Complete(Task task, SurfaceHarness harness)
    {
        for (var pass = 0; pass < 32 && !task.IsCompleted; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            harness.Pump();
        }

        Assert.True(task.IsCompleted, "保存流程没有在预期内完成。");
        task.GetAwaiter().GetResult();
        harness.Pump();
    }
}
