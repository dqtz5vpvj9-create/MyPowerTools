using RemoteCommands.Surface.Services;

namespace MyPowerTools.MobileRemoteCommands.Tests;

/// <summary>
/// Pure text transforms for <c>commands.yaml</c>. Every result is checked against the shipped parser
/// (<see cref="RemoteCommandsYaml"/>), which is what the module uses to validate the document the phone
/// hands it.
/// </summary>
public sealed class CatalogEditorTests
{
    private const string Document = """
        # Top comment survives an edit.
        commands:
          - id: first
            label: "第一条"
            command: "uptime"
            description: "first"
            type: "shell"
            host: "lab-host"

          - id: second
            label: "第二条"
            command: "df -h"
            type: "shell"

        types:
          - shell
          - py
        """;

    [Fact]
    public void Adding_an_entry_appends_it_and_keeps_the_document_valid()
    {
        var draft = Draft("third", "第三条", "echo hi");
        Assert.True(MobileCommandsYamlEditor.TryAdd(Document, draft, out var updated, out var error), error);

        var commands = RemoteCommandsYaml.ParseCommands(updated);
        Assert.Equal(3, commands.Count);
        Assert.Equal("third", commands[2].Id);
        Assert.Equal("echo hi", commands[2].Command);
        Assert.True(RemoteCommandsYaml.TryValidate(updated, out var validation), validation);
        Assert.Contains("# Top comment survives an edit.", updated);
        Assert.Contains("id: first", updated);
        Assert.Contains("types:", updated);
    }

    [Fact]
    public void Updating_replaces_one_entry_and_leaves_the_others_byte_for_byte()
    {
        var draft = Draft("second", "第二条（已改）", "df -h --total");
        Assert.True(MobileCommandsYamlEditor.TryUpdate(Document, "second", draft, out var updated, out var error), error);

        var commands = RemoteCommandsYaml.ParseCommands(updated);
        Assert.Equal(2, commands.Count);
        Assert.Equal("第二条（已改）", Assert.Single(commands.Where(command => command.Id == "second")).Label);
        Assert.Equal("df -h --total", Assert.Single(commands.Where(command => command.Id == "second")).Command);

        // The untouched entry is still exactly as it was, comment and blank line included.
        Assert.Contains("""
              - id: first
                label: "第一条"
                command: "uptime"
                description: "first"
                type: "shell"
                host: "lab-host"
            """, updated);
    }

    [Fact]
    public void Removing_an_entry_keeps_the_section_and_the_rest_of_the_document()
    {
        Assert.True(MobileCommandsYamlEditor.TryRemove(Document, "first", out var updated, out var error), error);

        var commands = RemoteCommandsYaml.ParseCommands(updated);
        Assert.Single(commands);
        Assert.Equal("second", commands[0].Id);
        Assert.True(RemoteCommandsYaml.TryValidate(updated, out var validation), validation);
        Assert.Contains("# Top comment survives an edit.", updated);
    }

    [Fact]
    public void Removing_the_last_entry_is_refused()
    {
        Assert.False(MobileCommandsYamlEditor.TryRemove(Document, "missing", out _, out var missingError));
        Assert.Contains("没有 id 为 'missing'", missingError);

        const string single = """
            commands:
              - id: only
                label: "唯一"
                command: "uptime"
                type: "shell"
            """;
        Assert.False(MobileCommandsYamlEditor.TryRemove(single, "only", out _, out var lastError));
        Assert.Contains("至少要保留一条命令", lastError);
    }

    [Fact]
    public void Values_with_quotes_colons_and_unicode_round_trip_through_the_parser()
    {
        var draft = Draft("quoted", "名称：带\"引号\"和:冒号", "echo \"a: b\"") with
        {
            Description = "说明 #不是注释",
            Input1Label = "输入\"一\"",
            Input1Placeholder = "占位: 值"
        };

        Assert.True(MobileCommandsYamlEditor.TryAdd("commands:\n", draft, out var updated, out var error), error);
        var parsed = Assert.Single(RemoteCommandsYaml.ParseCommands(updated));

        Assert.Equal("名称：带\"引号\"和:冒号", parsed.Label);
        Assert.Equal("echo \"a: b\"", parsed.Command);
        Assert.Equal("说明 #不是注释", parsed.Description);
        Assert.Equal("输入\"一\"", parsed.Input1Label);
        Assert.Equal("占位: 值", parsed.Input1Placeholder);
    }

    [Fact]
    public void A_newline_inside_a_value_is_refused_instead_of_creating_a_key()
    {
        var draft = Draft("bad", "名称", "uptime\nhost: evil");
        Assert.False(MobileCommandsYamlEditor.TryAdd(Document, draft, out _, out var error));
        Assert.Contains("不能包含换行", error);
    }

    [Fact]
    public void Duplicate_ids_are_refused_before_the_module_is_called()
    {
        Assert.False(MobileCommandsYamlEditor.TryAdd(Document, Draft("first", "重复", "uptime"), out _, out var error));
        Assert.Contains("已经存在", error);

        // Renaming onto another id is refused too; renaming onto itself is allowed.
        Assert.False(MobileCommandsYamlEditor.TryUpdate(Document, "second", Draft("first", "第二条", "df -h"), out _, out _));
        Assert.True(MobileCommandsYamlEditor.TryUpdate(Document, "second", Draft("second", "第二条", "df -h -k"), out _, out _));
    }

    [Fact]
    public void A_document_without_a_commands_section_is_only_extended_when_it_is_empty()
    {
        var draft = Draft("first", "第一条", "uptime");
        Assert.True(MobileCommandsYamlEditor.TryAdd("", draft, out var fresh, out var error), error);
        Assert.True(RemoteCommandsYaml.TryValidate(fresh, out var validation), validation);
        Assert.Single(RemoteCommandsYaml.ParseCommands(fresh));

        Assert.False(MobileCommandsYamlEditor.TryAdd("owner: somebody\n", draft, out _, out var error2));
        Assert.Contains("缺少顶层 commands:", error2);
    }

    [Fact]
    public void An_unknown_id_is_reported_instead_of_silently_creating_an_entry()
    {
        Assert.False(MobileCommandsYamlEditor.TryUpdate(Document, "ghost", Draft("ghost", "幽灵", "uptime"), out _, out var error));
        Assert.Contains("没有 id 为 'ghost'", error);
    }

    [Fact]
    public void Suggested_ids_are_stable_and_slug_shaped()
    {
        Assert.Equal("disk_usage", MobileCommandsYamlEditor.SuggestId("Disk Usage", ""));
        Assert.Equal("cmd", MobileCommandsYamlEditor.SuggestId("cmd", ""));

        var chinese = MobileCommandsYamlEditor.SuggestId("查看服务器状态", "uptime");
        Assert.StartsWith("cmd_", chinese);
        Assert.True(MobileCommandsYamlEditor.IsValidIdentifier(chinese));
        Assert.Equal(chinese, MobileCommandsYamlEditor.SuggestId("查看服务器状态", "uptime"));

        Assert.False(MobileCommandsYamlEditor.IsValidIdentifier("has space"));
        Assert.False(MobileCommandsYamlEditor.IsValidIdentifier("-leading"));
        Assert.False(MobileCommandsYamlEditor.IsValidIdentifier(""));
    }

    private static MobileCommandDraft Draft(string id, string label, string command) => new(
        id,
        label,
        command,
        "",
        "shell",
        "",
        "输入内容",
        "粘贴输入",
        "附加输入",
        "可选",
        false);
}
