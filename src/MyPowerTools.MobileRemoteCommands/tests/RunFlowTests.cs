using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Contract tests for the run flow: the page must send the catalog command with the user's real inputs,
/// follow the module's own progress events, keep a failure retryable, and never report a result the
/// module did not produce.
/// </summary>
public sealed class RunFlowTests
{
    [AvaloniaFact]
    public void Command_list_shows_the_real_catalog_and_a_row_tap_opens_the_run_sheet()
    {
        using var harness = Harness();
        var view = harness.View;

        Assert.Equal(
            ["查看服务器状态", "查看磁盘空间", "移除 C++ 注释", "对比两份输入"],
            view.ViewModel.VisibleCommands.Select(command => command.Label));
        Assert.Equal(4, view.CommandRowButtonsForTests.Count);

        // The row subtitle is the real command text, not a decorative label.
        Assert.Contains("uptime", view.ViewModel.VisibleCommands[0].ListSubtitleText);

        harness.Click(view.CommandRowButtonsForTests[0]);

        Assert.Equal(MobileSheet.Run, view.ViewModel.Sheet);
        Assert.Equal("查看服务器状态", view.ViewModel.RunSheetTitle);
        Assert.Equal("尚未运行", view.ViewModel.RunStateText);
        Assert.Empty(harness.Module.Calls.Where(call => call.CommandId == FakeModule.RunCommand));
    }

    [AvaloniaFact]
    public void Run_sends_the_catalog_command_with_the_typed_input_and_reports_the_module_result()
    {
        using var harness = Harness();
        var view = harness.View;
        harness.Module.RunOutput = "Uploading input files...\n09:18 up 12 days, 4:32\nload average: 0.21, 0.18, 0.15";
        harness.Module.RunExitCode = 0;

        harness.Click(view.CommandRowButtonsForTests[0]);
        view.Input1FieldForTests.Text = "只读检查";
        harness.Pump();

        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        var call = Assert.Single(harness.Module.Calls.Where(candidate => candidate.CommandId == FakeModule.RunCommand));
        Assert.Equal("server_status", RemoteCommandsMobileJson.Text(call.Args, "commandId"));
        Assert.Equal("只读检查", RemoteCommandsMobileJson.Text(call.Args, "input1"));
        Assert.Equal("", RemoteCommandsMobileJson.Text(call.Args, "input2"));
        Assert.False(call.Args["secondInput"]!.GetValue<bool>());

        // The invocation id is a real correlation id, not a placeholder, and it is the id the host put
        // on the envelope: the module keys its streamed events and its cancellation on that one.
        var invocationId = RemoteCommandsMobileJson.Text(call.Args, "invocationId");
        Assert.Equal(32, invocationId.Length);
        var envelope = Assert.Single(harness.Module.Envelopes.Where(entry => entry.CommandId == FakeModule.RunCommand));
        Assert.Equal(invocationId, envelope.EnvelopeInvocationId);

        Assert.Equal("运行完成", view.ViewModel.RunStateText);
        Assert.Contains("load average", view.OutputViewerForTests.Text);
        Assert.Contains("$ uptime", view.ViewModel.OutputHeaderText);
        Assert.True(view.ViewModel.HasLastResult);
        Assert.Equal("查看服务器状态", view.ViewModel.LastResultTitle);
    }

    [AvaloniaFact]
    public void Terminal_payload_replaces_streamed_lines_so_a_dropped_event_cannot_truncate_the_log()
    {
        using var harness = Harness();
        var view = harness.View;
        harness.Module.RunStreamLines.Add("streamed-only line");
        harness.Module.RunOutput = "final line from the terminal payload";

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        Assert.Contains("final line from the terminal payload", view.OutputViewerForTests.Text);
        Assert.DoesNotContain("streamed-only line", view.OutputViewerForTests.Text);
    }

    [AvaloniaFact]
    public void Progress_follows_the_module_stage_events_and_finishes_when_the_run_ends()
    {
        using var harness = Harness();
        var view = harness.View;
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Module.RunHold = hold;

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);

        // The fake published run.started, run.stage(uploading) and run.stage(running) while the run is
        // still in flight, exactly like the runner does.
        harness.WaitFor(() => view.ViewModel.IsRunning, "运行没有进入执行状态。");
        Assert.Equal("连接主机:done|上传输入:done|执行命令:active", view.StageSummaryForTests);
        Assert.True(view.RunButtonForTests.IsEnabled == false);

        hold.SetResult(true);

        // A held run keeps streaming output lines after the terminal payload arrives only if the module
        // sent them; the payload still wins.
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");
        Assert.Equal("连接主机:done|上传输入:done|执行命令:done", view.StageSummaryForTests);
    }

    [AvaloniaFact]
    public void Cancel_is_sent_with_the_invocation_id_of_the_run_in_flight()
    {
        using var harness = Harness();
        var view = harness.View;
        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Module.RunHold = hold;

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => view.ViewModel.IsRunning, "运行没有进入执行状态。");

        var runCall = Assert.Single(harness.Module.Calls.Where(call => call.CommandId == FakeModule.RunCommand));
        var invocationId = RemoteCommandsMobileJson.Text(runCall.Args, "invocationId");
        Assert.Equal(
            invocationId,
            harness.Module.Envelopes.Single(entry => entry.CommandId == FakeModule.RunCommand).EnvelopeInvocationId);

        // The module answers a cancel by finishing the run as cancelled; mirror that ordering.
        harness.Module.RunState = RemoteCommandsMobileContract.StateCancelled;
        harness.Module.RunMessage = "已取消";
        harness.Click(view.CancelRunButtonForTests);

        var cancelCall = Assert.Single(harness.Module.Calls.Where(call => call.CommandId == FakeModule.CancelCommand));
        Assert.Equal(invocationId, RemoteCommandsMobileJson.Text(cancelCall.Args, "invocationId"));
        Assert.True(cancelCall.Args["cancelled"] is null);

        // The cancel runs under the same page-owned invocation id as the run it stops.
        Assert.Equal(
            invocationId,
            harness.Module.Envelopes.Single(entry => entry.CommandId == FakeModule.CancelCommand).EnvelopeInvocationId);

        hold.SetResult(true);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "取消后运行没有结束。");
        Assert.Equal("已取消", view.ViewModel.RunStateText);
    }

    [AvaloniaFact]
    public void A_failed_run_stays_retryable_and_retry_resends_the_same_request()
    {
        using var harness = Harness();
        var view = harness.View;
        harness.Module.RunState = RemoteCommandsMobileContract.StateFailed;
        harness.Module.RunMessage = "远端命令返回退出码 2";
        harness.Module.RunExitCode = 2;

        harness.Click(view.CommandRowButtonsForTests[0]);
        view.Input1FieldForTests.Text = "retry me";
        harness.Pump();
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        Assert.Equal("运行失败", view.ViewModel.RunStateText);
        Assert.Contains("退出码 2", view.ViewModel.RunMessageText);
        Assert.True(view.ViewModel.CanRetry);
        Assert.True(view.RetryButtonForTests.IsVisible);

        // The retry must repeat the user's request, not a fresh empty one.
        harness.Module.RunState = RemoteCommandsMobileContract.StateSucceeded;
        harness.Module.RunMessage = "执行完成";
        harness.Module.RunExitCode = 0;
        harness.Click(view.RetryButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "重试没有结束。");

        var runs = harness.Module.Calls.Where(call => call.CommandId == FakeModule.RunCommand).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.Equal("retry me", RemoteCommandsMobileJson.Text(runs[1].Args, "input1"));
        Assert.Equal("server_status", RemoteCommandsMobileJson.Text(runs[1].Args, "commandId"));
        Assert.NotEqual(
            RemoteCommandsMobileJson.Text(runs[0].Args, "invocationId"),
            RemoteCommandsMobileJson.Text(runs[1].Args, "invocationId"));
        Assert.Equal("运行完成", view.ViewModel.RunStateText);
    }

    [AvaloniaFact]
    public void The_second_input_is_offered_and_sent_only_for_commands_that_define_it()
    {
        using var harness = Harness();
        var view = harness.View;

        // compare_files (index 3) declares show_second_input: true.
        harness.Click(view.CommandRowButtonsForTests[3]);
        Assert.True(view.SecondInputToggleForTests.IsChecked);
        Assert.True(view.Input2FieldForTests.IsVisible);

        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        var withSecond = Assert.Single(harness.Module.Calls.Where(call => call.CommandId == FakeModule.RunCommand));
        Assert.True(withSecond.Args["secondInput"]!.GetValue<bool>());

        // server_status does not: the page hides the second field and never sends it as enabled.
        harness.Click(view.CommandRowButtonsForTests[0]);
        Assert.False(view.SecondInputToggleForTests.IsChecked);
        Assert.False(view.Input2FieldForTests.IsVisible);

        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => view.ViewModel.RunStateText == "运行完成", "第二次运行没有结束。");

        var withoutSecond = harness.Module.Calls.Last(call => call.CommandId == FakeModule.RunCommand);
        Assert.False(withoutSecond.Args["secondInput"]!.GetValue<bool>());
    }

    [AvaloniaFact]
    public void A_host_key_stops_the_run_until_the_exact_fingerprint_is_confirmed()
    {
        using var harness = Harness();
        var view = harness.View;
        harness.Module.RunState = RemoteCommandsMobileContract.StateHostKeyRequired;
        harness.Module.RunMessage = "首次连接，需要确认主机密钥。";
        harness.Module.RunExitCode = null;
        harness.Module.PendingHostKey = new MobilePendingHostKey(
            "192.168.22.24",
            22,
            "ssh-ed25519",
            "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            FirstUse: true,
            "首次连接，需要确认主机密钥。");

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        Assert.Equal("等待确认主机密钥", view.ViewModel.RunStateText);
        Assert.True(view.ViewModel.HasPendingHostKey);
        Assert.Contains("SHA256:AAAA", view.ViewModel.PendingHostKeyDetail);
        Assert.False(view.ViewModel.CanTrustPendingHostKey);

        // The accept button stays disabled until the user ticks the acknowledgement.
        view.TrustAcknowledgedForTests.IsChecked = true;
        harness.Pump();
        Assert.True(view.ViewModel.CanTrustPendingHostKey);

        harness.Module.RunState = RemoteCommandsMobileContract.StateSucceeded;
        harness.Module.RunMessage = "执行完成";
        harness.Module.PendingHostKey = null;
        harness.Click(view.TrustHostKeyButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning && !view.ViewModel.HasPendingHostKey, "确认指纹后没有重新运行。");

        var accept = Assert.Single(harness.Module.Calls.Where(call => call.CommandId == FakeModule.HostKeyAcceptCommand));
        Assert.Equal("192.168.22.24", RemoteCommandsMobileJson.Text(accept.Args, "hostName"));
        Assert.Equal(22, RemoteCommandsMobileJson.Int(accept.Args, "port"));
        Assert.Equal("SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", RemoteCommandsMobileJson.Text(accept.Args, "fingerprint"));
        Assert.Equal(2, harness.Module.Calls.Count(call => call.CommandId == FakeModule.RunCommand));
        Assert.False(view.ViewModel.HasPendingHostKey);
    }

    [AvaloniaFact]
    public void A_permission_required_answer_is_reported_as_authorization_not_as_success()
    {
        using var harness = Harness();
        var view = harness.View;
        harness.Module.ModuleFailureMessage = "Broker approval required for remote-commands-android.run.";
        harness.Module.ModuleFailureCode = RemoteCommandsMobileContract.ErrorPermissionRequired;

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        Assert.True(view.ViewModel.RunPermissionRequired);
        Assert.Equal("等待系统授权", view.ViewModel.RunStateText);
        Assert.Contains("Broker approval", view.ViewModel.RunMessageText);
        Assert.False(view.ViewModel.HasOutput);

        // Retrying after the authorization is granted must still be possible.
        Assert.True(view.ViewModel.CanRetry);
    }

    [AvaloniaFact]
    public void A_shell_command_without_a_mapped_host_opens_the_connection_sheet_instead_of_running()
    {
        using var harness = SurfaceHarness.Create(new FakeModule());
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.HasCommands, "目录没有加载。");
        Assert.True(view.ViewModel.HasNoHosts);

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.Pump();

        Assert.Empty(harness.Module.Calls.Where(call => call.CommandId == FakeModule.RunCommand));
        Assert.Equal(MobileSheet.Connection, view.ViewModel.Sheet);
        Assert.Contains("主机", view.ViewModel.RunMessageText);
    }

    [AvaloniaFact]
    public void Local_transforms_still_run_without_a_managed_ssh_transport()
    {
        var module = new FakeModule { TransportAvailable = false };
        module.AddHost("lab-host");
        using var harness = SurfaceHarness.Create(module);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.HasCommands, "目录没有加载。");

        // The shell command cannot run: the page disables the run button and says why, instead of
        // calling the module and reporting a failure the device cannot produce.
        harness.Click(view.CommandRowButtonsForTests[0]);
        Assert.False(view.RunButtonForTests.IsEnabled);
        harness.Pump();
        Assert.Empty(module.Calls.Where(call => call.CommandId == FakeModule.RunCommand));
        Assert.True(view.ViewModel.HasTransportWarning);
        Assert.Contains("本地转换", view.ViewModel.TransportWarning);

        // The py transform is local and stays available.
        harness.Click(view.CommandRowButtonsForTests[2]);
        Assert.True(view.ViewModel.SelectedCommand?.IsLocalTransform);
        Assert.True(view.RunButtonForTests.IsEnabled);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning && view.ViewModel.HasLastResult, "本地转换没有执行。");

        var run = Assert.Single(module.Calls.Where(call => call.CommandId == FakeModule.RunCommand));
        Assert.Equal("remove_comments", RemoteCommandsMobileJson.Text(run.Args, "commandId"));
    }

    [AvaloniaFact]
    public void Search_filters_the_real_catalog_without_losing_the_row_actions()
    {
        using var harness = Harness();
        var view = harness.View;

        view.ViewModel.SearchText = "磁盘";
        harness.Pump();
        Assert.Single(view.ViewModel.VisibleCommands);
        Assert.Single(view.CommandRowButtonsForTests);

        view.ViewModel.SearchText = "没有这个命令";
        harness.Pump();
        Assert.True(view.ViewModel.HasNoMatches);
        Assert.Empty(view.CommandRowButtonsForTests);
    }

    [AvaloniaFact]
    public void An_older_host_without_a_caller_owned_invocation_id_still_reports_the_terminal_result()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        using var harness = SurfaceHarness.Create(module, callerOwnedInvocationId: false);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.HasCommands, "目录没有加载。");

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        // The host owns the id here: the terminal payload still carries the real result and the page must
        // report it, and the streamed events still belong to the only run in flight.
        Assert.Equal("", harness.Module.Envelopes.Single(entry => entry.CommandId == FakeModule.RunCommand).EnvelopeInvocationId);
        Assert.Equal("运行完成", view.ViewModel.RunStateText);
        Assert.Contains("load average", view.OutputViewerForTests.Text);
    }

    [AvaloniaFact]
    public void Without_a_caller_owned_id_the_cancel_does_not_name_an_id_the_host_never_used()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        using var harness = SurfaceHarness.Create(module, callerOwnedInvocationId: false);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.HasCommands, "目录没有加载。");

        var hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        module.RunHold = hold;
        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => view.ViewModel.IsRunning, "运行没有进入执行状态。");

        harness.Click(view.CancelRunButtonForTests);

        // A mismatched id makes the module refuse the cancel, so the argument must be omitted and the
        // runner cancels its single active run instead.
        var cancel = Assert.Single(module.Calls.Where(call => call.CommandId == FakeModule.CancelCommand));
        Assert.Null(cancel.Args[RemoteCommandsMobileContract.ArgumentInvocationId]);

        hold.SetResult(true);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "取消后运行没有结束。");
    }

    [AvaloniaFact]
    public void The_page_reads_the_module_once_on_activation_and_then_only_on_real_events()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        using var harness = SurfaceHarness.Create(module);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.HasCommands, "目录没有加载。");

        // Activation reads exactly the five slices it needs; it does not start a timer or a poll.
        var afterActivation = module.Calls.Count;
        Assert.Equal(5, afterActivation);

        Thread.Sleep(60);
        harness.Pump();
        Assert.Equal(afterActivation, module.Calls.Count);

        // A real module event refreshes only the slice it changed.
        module.Publish(
            RemoteCommandsMobileContract.EventCatalogSaved,
            new System.Text.Json.Nodes.JsonObject { ["message"] = "命令目录已保存。" });
        harness.WaitFor(() => module.Calls.Count == afterActivation + 1, "目录事件没有触发刷新。");
        Assert.Equal(FakeModule.CatalogCommand, module.Calls[^1].CommandId);
        Assert.Equal("命令目录已保存。", view.ViewModel.FeedbackText);
    }

    private static SurfaceHarness Harness()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        return SurfaceHarness.Create(module);
    }
}
