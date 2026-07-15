using System.Text.Json.Nodes;
using MyPowerTools.Shell.Avalonia.ViewModels;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class SettingsSchemaRendererTests
{
    [Fact]
    public void Schema_renderer_honors_required_readonly_const_and_numeric_bounds()
    {
        var modules = new HostProto.ListModulesResponse();
        var selected = new HostProto.ModuleSummary
        {
            ModuleId = "sample",
            DisplayName = "Sample"
        };
        modules.Modules.Add(selected);
        var schema = """
            {
              "type": "object",
              "required": [ "port" ],
              "properties": {
                "endpoint": {
                  "type": "string",
                  "const": "http://127.0.0.1:19002",
                  "readOnly": true
                },
                "port": {
                  "type": "integer",
                  "minimum": 1,
                  "maximum": 65535
                }
              }
            }
            """;
        var values = new JsonObject
        {
            ["endpoint"] = "http://127.0.0.1:19002",
            ["port"] = 19002
        };

        var viewModel = ShellPageViewModelFactory.FromSettings(
            modules,
            selected,
            schema,
            values,
            values.ToJsonString(),
            1,
            DateTimeOffset.UtcNow);
        var endpoint = Assert.Single(viewModel.Fields, field => field.Key == "endpoint");
        var port = Assert.Single(viewModel.Fields, field => field.Key == "port");

        Assert.True(endpoint.IsReadOnly);
        Assert.False(endpoint.IsEditable);
        endpoint.Value = "http://evil.invalid";
        Assert.False(endpoint.IsDirty);

        Assert.True(port.IsRequired);
        port.Value = "";
        Assert.Equal("port is required.", port.ValidationMessage);
        port.Value = "70000";
        Assert.Equal("port must be at most 65535.", port.ValidationMessage);
        port.Value = "38189";
        Assert.Empty(port.ValidationMessage);

        var patch = ShellPageViewModelFactory.BuildSettingsPatch(viewModel);
        Assert.False(patch.ContainsKey("endpoint"));
        Assert.Equal(38189, patch["port"]!.GetValue<long>());
    }
}
