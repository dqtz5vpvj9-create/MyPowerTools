using Avalonia.Headless.XUnit;
using RemoteCommands.Surface.Services;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// The phone's command editing path: the form produces document text, the module validates it with the
/// shipped parser and writes the canonical <c>commands.yaml</c>. Nothing here invents a second catalog,
/// and a rejected edit keeps what the user typed.
/// </summary>
public sealed class CommandEditorTests
{
    [AvaloniaFact]
    public void Adding_a_command_writes_the_shared_document_through_the_module()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.AddCommandButtonForTests);
        Assert.Equal(MobileSheet.CommandEditor, view.ViewModel.Sheet);

        view.CommandLabelFieldForTests.Text = "查看服务日志";
        view.CommandCommandFieldForTests.Text = "journalctl --lines=200";
        harness.Pump();
        Assert.True(view.ViewModel.CanSaveCommandForm);

        harness.Complete(view.SaveCommandForTestsAsync());

        var save = Assert.Single(harness.Module.Calls.Where(call => call.CommandId == FakeModule.CatalogSaveCommand));
        var content = RemoteCommandsMobileJson.Text(save.Args, "content");
        var commands = RemoteCommandsYaml.ParseCommands(content);

        Assert.Equal(5, commands.Count);
        var added = Assert.Single(commands.Where(command => command.Label == "查看服务日志"));
        Assert.Equal("journalctl --lines=200", added.Command);
        Assert.Equal("shell", added.Type);

        // The document keeps the entries and the comment the user did not touch.
        Assert.Contains("# Shared command catalog. Keep the comments", content);
        Assert.Contains("id: disk_usage", content);
        Assert.Contains("types:", content);

        // The catalog the page lists comes back from the module, not from the form.
        harness.WaitFor(() => view.ViewModel.VisibleCommands.Count == 5, "模块目录没有刷新。");
        Assert.Equal(MobileSheet.None, view.ViewModel.Sheet);
    }

    [AvaloniaFact]
    public void Editing_a_command_replaces_only_that_entry()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CommandRowButtonsForTests[1]);
        harness.Click(view.EditCommandButtonForTests);
        Assert.Equal(MobileSheet.CommandEditor, view.ViewModel.Sheet);
        Assert.Equal("disk_usage", view.ViewModel.FormId);

        view.CommandCommandFieldForTests.Text = "df -h --total";
        harness.Pump();
        harness.Complete(view.SaveCommandForTestsAsync());

        var save = Assert.Single(harness.Module.Calls.Where(call => call.CommandId == FakeModule.CatalogSaveCommand));
        var content = RemoteCommandsMobileJson.Text(save.Args, "content");
        var commands = RemoteCommandsYaml.ParseCommands(content);

        Assert.Equal(4, commands.Count);
        Assert.Equal("df -h --total", Assert.Single(commands.Where(command => command.Id == "disk_usage")).Command);
        Assert.Equal("uptime", Assert.Single(commands.Where(command => command.Id == "server_status")).Command);
        Assert.Contains("# Shared command catalog. Keep the comments", content);
    }

    [AvaloniaFact]
    public void Editing_a_command_takes_effect_on_the_page_that_runs_it()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.EditCommandButtonForTests);
        view.CommandCommandFieldForTests.Text = "uptime -p";
        harness.Pump();
        harness.Complete(view.SaveCommandForTestsAsync());

        harness.WaitFor(
            () => view.ViewModel.VisibleCommands.Any(command => command.Command == "uptime -p"),
            "编辑后的命令没有回到列表。");
        Assert.Contains("$ uptime -p", view.ViewModel.OutputHeaderText);
    }

    [AvaloniaFact]
    public void Deleting_a_command_needs_two_taps_and_refuses_to_empty_the_catalog()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        module.SetCatalog("""
            commands:
              - id: only_command
                label: "唯一命令"
                command: "uptime"
                type: "shell"
            """);
        using var harness = SurfaceHarness.Create(module);
        var view = harness.View;
        harness.WaitFor(() => view.ViewModel.VisibleCommands.Count == 1, "目录没有加载。");

        harness.Click(view.CommandRowButtonsForTests[0]);
        harness.Click(view.EditCommandButtonForTests);
        harness.Click(view.DeleteCommandButtonForTests);

        // The first tap only arms the confirmation; nothing was written yet.
        Assert.True(view.ViewModel.CommandDeletePending);
        Assert.Empty(module.Calls.Where(call => call.CommandId == FakeModule.CatalogSaveCommand));

        harness.Click(view.DeleteCommandButtonForTests);
        harness.WaitFor(
            () => view.ViewModel.CommandFormMessage.Contains("至少要保留一条命令"),
            "删除最后一条命令没有被拒绝。");

        Assert.Contains("至少要保留一条命令", view.ViewModel.CommandFormMessage);
        Assert.Empty(module.Calls.Where(call => call.CommandId == FakeModule.CatalogSaveCommand));
        Assert.Equal(MobileSheet.CommandEditor, view.ViewModel.Sheet);
    }

    [AvaloniaFact]
    public void Deleting_one_of_several_commands_writes_the_document_without_it()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CommandRowButtonsForTests[1]);
        harness.Click(view.EditCommandButtonForTests);
        harness.Click(view.DeleteCommandButtonForTests);
        Assert.True(view.ViewModel.CommandDeletePending, "第一次点击没有进入确认状态。");
        harness.Click(view.DeleteCommandButtonForTests);
        harness.WaitFor(
            () => harness.Module.Calls.Any(call => call.CommandId == FakeModule.CatalogSaveCommand),
            $"删除没有写入模块：{view.ViewModel.CommandFormMessage}");

        var save = Assert.Single(harness.Module.Calls.Where(call => call.CommandId == FakeModule.CatalogSaveCommand));
        var commands = RemoteCommandsYaml.ParseCommands(RemoteCommandsMobileJson.Text(save.Args, "content"));
        Assert.Equal(3, commands.Count);
        Assert.DoesNotContain(commands, command => command.Id == "disk_usage");
        Assert.Contains(commands, command => command.Id == "server_status");
    }

    [AvaloniaFact]
    public void A_module_rejection_keeps_the_typed_command_and_shows_the_module_message()
    {
        using var harness = Harness();
        var view = harness.View;
        harness.Module.SaveFailure = "Command id 'server_status' is duplicated.";

        harness.Click(view.AddCommandButtonForTests);
        view.CommandLabelFieldForTests.Text = "重复命令";
        view.CommandCommandFieldForTests.Text = "uptime";
        harness.Pump();
        harness.Complete(view.SaveCommandForTestsAsync());

        Assert.Contains("is duplicated", view.ViewModel.CommandFormMessage);
        Assert.Equal(MobileSheet.CommandEditor, view.ViewModel.Sheet);
        Assert.Equal("重复命令", view.ViewModel.FormLabel);
        Assert.Equal("uptime", view.ViewModel.FormCommand);
        Assert.True(view.CommandLabelFieldForTests.Text == "重复命令");
    }

    [AvaloniaFact]
    public void An_invalid_identifier_blocks_the_save_before_the_module_is_called()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.AddCommandButtonForTests);
        view.CommandLabelFieldForTests.Text = "非法标识";
        view.CommandCommandFieldForTests.Text = "uptime";
        view.CommandIdFieldForTests.Text = "has space";
        harness.Pump();

        Assert.False(view.ViewModel.CanSaveCommandForm);
        harness.Complete(view.SaveCommandForTestsAsync());
        Assert.Empty(harness.Module.Calls.Where(call => call.CommandId == FakeModule.CatalogSaveCommand));
    }

    [AvaloniaFact]
    public void The_raw_editor_rejects_invalid_yaml_with_the_parser_message_and_keeps_the_text()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CatalogButtonForTests);
        Assert.Equal(MobileSheet.Catalog, view.ViewModel.Sheet);

        view.ViewModel.CatalogYaml = "这不是命令配置";
        harness.Pump();
        Assert.True(view.ViewModel.CatalogDirty);
        Assert.True(view.ViewModel.CanSaveCatalog);

        harness.Complete(view.ViewModel.SaveCatalogAsync());

        Assert.Contains("top-level 'commands' key", view.ViewModel.CatalogSaveMessage);
        Assert.Equal("这不是命令配置", view.ViewModel.CatalogYaml);
        Assert.True(view.ViewModel.CatalogDirty);
        Assert.Equal(MobileSheet.Catalog, view.ViewModel.Sheet);
    }

    [AvaloniaFact]
    public void The_raw_editor_saves_a_valid_document_and_the_page_reloads_it()
    {
        using var harness = Harness();
        var view = harness.View;

        harness.Click(view.CatalogButtonForTests);
        view.ViewModel.CatalogYaml = """
            commands:
              - id: new_command
                label: "新命令"
                command: "echo hi"
                type: "shell"
            """;
        harness.Pump();
        harness.Complete(view.ViewModel.SaveCatalogAsync());

        Assert.Contains("已保存", view.ViewModel.CatalogSaveMessage);
        Assert.False(view.ViewModel.CatalogDirty);
        harness.WaitFor(
            () => view.ViewModel.VisibleCommands.Count == 1 && view.ViewModel.VisibleCommands[0].Id == "new_command",
            "保存后的目录没有刷新。");
    }

    private static SurfaceHarness Harness()
    {
        var module = new FakeModule();
        module.AddHost("lab-host");
        return SurfaceHarness.Create(module);
    }
}
