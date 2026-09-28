using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Phone back and keyboard behaviour: a bottom sheet is the topmost layer, unsaved edits are not dropped
/// by one stray back press, and a single-line field submits with the keyboard's action key.
/// </summary>
public sealed class BackAndKeyboardTests
{
    [AvaloniaFact]
    public void Back_closes_the_open_sheet_before_it_leaves_the_page()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CommandRowButtonsForTests[0]);
        Assert.Equal(MobileSheet.Run, view.ViewModel.Sheet);

        Assert.True(view.PressBackForTests());
        Assert.Equal(MobileSheet.None, view.ViewModel.Sheet);

        // Nothing left to close: the host may leave the page.
        Assert.False(view.PressBackForTests());
    }

    [AvaloniaFact]
    public void The_escape_key_reaches_the_page_handler_through_the_real_input_pipeline()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CommandRowButtonsForTests[0]);
        Assert.Equal(MobileSheet.Run, view.ViewModel.Sheet);

        view.RunButtonForTests.Focus();
        harness.Pump();
        harness.PressKey(Key.Escape);

        Assert.Equal(MobileSheet.None, view.ViewModel.Sheet);
    }

    [AvaloniaFact]
    public void Back_disarms_a_pending_host_deletion_instead_of_closing_the_sheet()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.ConnectionButtonForTests);
        Assert.Equal(MobileSheet.Connection, view.ViewModel.Sheet);

        harness.Click(view.HostDeleteButtonForTests);
        Assert.True(view.ViewModel.HasPendingRemoval);

        Assert.True(view.PressBackForTests());
        Assert.False(view.ViewModel.HasPendingRemoval);
        Assert.Equal(MobileSheet.Connection, view.ViewModel.Sheet);

        // The next back press closes the sheet.
        Assert.True(view.PressBackForTests());
        Assert.Equal(MobileSheet.None, view.ViewModel.Sheet);
    }

    [AvaloniaFact]
    public void Back_disarms_a_pending_fingerprint_revocation()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        module.TrustedKeys.Add(new MobileHostKeyEntry(
            "192.168.22.24",
            22,
            "ssh-ed25519",
            "SHA256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB",
            "2025-09-27 09:10"));
        using var harness = SurfaceHarness.Create(module);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.HasTrustedKeys, "指纹没有加载。");

        harness.Click(view.ConnectionButtonForTests);
        harness.Click(view.HostKeyRevokeButtonForTests);
        Assert.True(view.ViewModel.HasPendingKeyRevocation);
        Assert.Empty(module.Calls.Where(call => call.CommandId == FakeModule.HostKeyRevokeCommand));

        Assert.True(view.PressBackForTests());
        Assert.False(view.ViewModel.HasPendingKeyRevocation);
        Assert.Empty(module.Calls.Where(call => call.CommandId == FakeModule.HostKeyRevokeCommand));
    }

    [AvaloniaFact]
    public void The_fingerprint_revocation_itself_goes_through_the_module()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        module.TrustedKeys.Add(new MobileHostKeyEntry(
            "192.168.22.24",
            2222,
            "ssh-ed25519",
            "SHA256:CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
            "2025-09-27 09:10"));
        using var harness = SurfaceHarness.Create(module);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.HasTrustedKeys, "指纹没有加载。");

        harness.Click(view.ConnectionButtonForTests);
        harness.Click(view.HostKeyRevokeButtonForTests);
        harness.Click(view.HostKeyRevokeButtonForTests);
        harness.WaitFor(() => !view.ViewModel.HasTrustedKeys, "撤销没有生效。");

        var revoke = Assert.Single(module.Calls.Where(call => call.CommandId == FakeModule.HostKeyRevokeCommand));
        Assert.Equal("192.168.22.24", RemoteCommandsMobileJson.Text(revoke.Args, "hostName"));
        Assert.Equal(2222, RemoteCommandsMobileJson.Int(revoke.Args, "port"));
    }

    [AvaloniaFact]
    public void Unsaved_catalog_edits_need_a_second_back_press()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CatalogButtonForTests);
        view.ViewModel.CatalogYaml = "commands:\n  - id: only\n    label: \"唯一\"\n    command: \"uptime\"\n";
        harness.Pump();

        Assert.True(view.PressBackForTests());
        Assert.Equal(MobileSheet.Catalog, view.ViewModel.Sheet);
        Assert.Contains("未保存", view.ViewModel.FeedbackText);

        Assert.True(view.PressBackForTests());
        Assert.Equal(MobileSheet.None, view.ViewModel.Sheet);
    }

    [AvaloniaFact]
    public void The_keyboard_action_key_saves_a_single_line_command_form()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.AddCommandButtonForTests);
        view.CommandLabelFieldForTests.Text = "键盘保存";
        view.CommandCommandFieldForTests.Text = "echo keyboard";
        harness.Pump();

        view.CommandLabelFieldForTests.Focus();
        harness.Pump();
        Assert.True(view.CommandLabelFieldForTests.IsFocused);

        harness.PressKey(Key.Enter);

        Assert.Contains(
            harness.Module.Calls,
            call => call.CommandId == FakeModule.CatalogSaveCommand);
    }

    [AvaloniaFact]
    public void Starting_a_run_moves_focus_off_the_input_so_the_soft_keyboard_closes()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CommandRowButtonsForTests[0]);
        view.Input1FieldForTests.Focus();
        harness.Pump();
        Assert.True(view.Input1FieldForTests.IsFocused);

        harness.Click(view.RunButtonForTests);
        harness.WaitFor(() => !view.ViewModel.IsRunning, "运行没有结束。");

        Assert.False(view.Input1FieldForTests.IsFocused);
    }

    [AvaloniaFact]
    public void Closing_a_sheet_restores_the_focus_it_took()
    {
        using var harness = Harness();
        var view = harness.View;

        var row = view.CommandRowButtonsForTests[0];
        row.Focus();
        harness.Pump();
        Assert.True(row.IsFocused);

        harness.Click(row);
        harness.Pump();
        Assert.Equal(MobileSheet.Run, view.ViewModel.Sheet);

        Assert.True(view.PressBackForTests());
        harness.Pump();
        Assert.Equal(MobileSheet.None, view.ViewModel.Sheet);
        Assert.True(row.IsFocused, "关闭弹层后没有把焦点还给打开它的控件。");
    }

    [AvaloniaFact]
    public void The_page_back_button_returns_to_the_tool_library()
    {
        using var harness = Harness();
        var view = harness.View;

        // The sub-navigation back button uses the same convention as the other mobile tools.
        harness.Click(view.BackButtonForTests);
        Assert.Contains(("", ""), harness.Module.Navigations);
    }

    private static SurfaceHarness Harness()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        return SurfaceHarness.Create(module);
    }
}
