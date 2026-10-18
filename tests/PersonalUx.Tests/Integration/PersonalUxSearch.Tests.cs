using MyPowerTools.Shell.Avalonia.ViewModels;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class PersonalUxSearchTests
{
    [Theory]
    [InlineData("remote commands", "Remote Commands", "remote-commands", "")]
    [InlineData("ｒｅｍｏｔｅ　commands", "Remote Commands", "remote-commands", "")]
    [InlineData("android\tcommands", "Commands", "remote-commands", "Android")]
    [InlineData("护眼 调节", "ScreenEase 护眼", "screenease", "亮度调节")]
    public void Tool_search_matches_words_across_fields_and_normalizes_unicode(string query, string title, string id, string description)
    {
        Assert.True(ToolSearchMatcher.Score(query, title, id, description) >= 0);
    }

    [Fact]
    public void Exact_names_beat_incidental_metadata_matches_and_empty_query_keeps_existing_order()
    {
        var incidental = Card("other", "Other", "use ScreenEase here");
        var exact = Card("screenease", "ScreenEase", "护眼");
        var catalog = new ToolCatalogViewModel([incidental, exact]) { Query = "screenease" };
        Assert.Same(exact, catalog.VisibleTools[0]);
        catalog.Query = "";
        Assert.Same(incidental, catalog.VisibleTools[0]);
        catalog.Query = "absent words";
        Assert.Empty(catalog.VisibleTools);
    }

    [Fact]
    public void Command_palette_can_find_a_tool_by_its_id_and_does_not_expose_hidden_routes()
    {
        var tool = new HostProto.ToolDescriptor
        {
            ToolId = "remote-commands", Title = "Remote workspace", State = "ready", PrimaryRouteId = "workspace",
            Routes = { new HostProto.ToolRoute { RouteId = "workspace", Title = "Commands" }, new HostProto.ToolRoute { RouteId = "diagnostics", Title = "Diagnostics" } }
        };
        var response = new HostProto.ListToolsResponse { Tools = { tool } };
        var result = ShellPageViewModelFactory.BuildToolSearchCommands("remote_commands", response, new HashSet<string> { tool.ToolId });
        Assert.Single(result.Commands);
        Assert.Equal("tool.remote-commands.workspace.open", result.Commands[0].CommandId);
        Assert.Empty(ShellPageViewModelFactory.BuildToolSearchCommands("diagnostics", response, new HashSet<string> { tool.ToolId }).Commands);
    }

    private static ToolCardViewModel Card(string id, string title, string description) =>
        new(id, title, description, "", "", "Ready", "", ToolAvailability.Available, false);
}
