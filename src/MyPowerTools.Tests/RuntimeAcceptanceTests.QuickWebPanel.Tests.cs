using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using MyPowerTools.Packaging;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    [Fact]
    public void Quick_panel_minimal_file_normalizes_to_a_schema_valid_web_surface_manifest()
    {
        var raw = new JsonObject
        {
            ["title"] = "My Dashboard",
            ["url"] = "http://192.168.1.42:8080"
        };

        var normalized = WebSurfaceDefaults.NormalizeQuickPanel(raw, "my-dashboard", "my-dashboard.mpt.json");

        var schema = ToolContractManifestSchema.Value;
        using var document = JsonDocument.Parse(normalized.ToJsonString());
        var evaluation = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        Assert.True(evaluation.IsValid, "Normalized quick panel must pass the strict tool schema.");

        Assert.Equal("custom.my-dashboard", normalized["toolId"]!.GetValue<string>());
        Assert.Equal("web-surface", normalized["type"]!.GetValue<string>());
        Assert.Equal(WebSurfaceDefaults.Category, normalized["category"]!.GetValue<string>());
        Assert.False(normalized.ContainsKey("url"), "The non-schema url shortcut must be consumed, not carried.");
        var surface = normalized["routes"]!.AsArray()[0]!["surface"]!.AsObject();
        Assert.Equal("web", surface["kind"]!.GetValue<string>());
        Assert.Equal("http://192.168.1.42:8080", surface["source"]!.GetValue<string>());
        Assert.True(surface["openExternal"]!.GetValue<bool>());
        Assert.Empty(surface["allowedOrigins"]!.AsArray());
    }

    [Fact]
    public void Quick_panel_user_fields_override_defaults_and_restore_full_capability()
    {
        var raw = new JsonObject
        {
            ["title"] = "My Dashboard",
            ["url"] = "http://192.168.1.42:8080",
            ["toolId"] = "my.custom.panel",
            ["category"] = "Monitoring",
            ["homeCard"] = new JsonObject { ["order"] = 100 },
            ["commands"] = new JsonArray
            {
                new JsonObject { ["id"] = "my.custom.panel.refresh", ["title"] = "Refresh" }
            },
            ["routes"] = new JsonArray
            {
                new JsonObject
                {
                    ["routeId"] = "main",
                    ["surfaceId"] = "my.custom.panel.main",
                    ["surface"] = new JsonObject
                    {
                        ["kind"] = "web",
                        ["source"] = "http://192.168.1.42:8080",
                        ["openExternal"] = false,
                        ["allowedOrigins"] = new JsonArray("http://192.168.1.42:8080")
                    }
                }
            }
        };

        var normalized = WebSurfaceDefaults.NormalizeQuickPanel(raw, "my-dashboard", "my-dashboard.mpt.json");

        Assert.Equal("my.custom.panel", normalized["toolId"]!.GetValue<string>());
        Assert.Equal("Monitoring", normalized["category"]!.GetValue<string>());
        // Deep merge: user order wins, default summary survives.
        Assert.Equal(100, normalized["homeCard"]!["order"]!.GetValue<int>());
        Assert.Equal("Open My Dashboard", normalized["homeCard"]!["summary"]!.GetValue<string>());
        // Arrays replace wholesale: the user route list is taken over completely.
        var routes = normalized["routes"]!.AsArray();
        var route = Assert.Single(routes);
        Assert.False(route!["surface"]!["openExternal"]!.GetValue<bool>());
        Assert.Single(route["surface"]!["allowedOrigins"]!.AsArray());
        Assert.Single(normalized["commands"]!.AsArray());
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com")]
    [InlineData("")]
    public void Quick_panel_requires_an_absolute_http_url(string url)
    {
        var raw = new JsonObject { ["title"] = "Bad", ["url"] = url };
        Assert.Throws<InvalidDataException>(() =>
            WebSurfaceDefaults.NormalizeQuickPanel(raw, "bad", "bad.mpt.json"));
    }

    [Fact]
    public void Quick_panel_candidate_detection_is_shape_based()
    {
        Assert.True(WebSurfaceDefaults.IsQuickPanelCandidate(new JsonObject
        {
            ["title"] = "Panel",
            ["url"] = "http://127.0.0.1:1/"
        }));
        // A document that already declares routes is a full manifest, never a quick panel.
        Assert.False(WebSurfaceDefaults.IsQuickPanelCandidate(new JsonObject
        {
            ["url"] = "http://127.0.0.1:1/",
            ["routes"] = new JsonArray()
        }));
        Assert.False(WebSurfaceDefaults.IsQuickPanelCandidate(new JsonObject
        {
            ["title"] = "No url here"
        }));
    }

    [Theory]
    [InlineData("grafana", "custom.grafana")]
    [InlineData("My Panel", "custom.my-panel")]
    [InlineData("foo_bar v2", "custom.foo-bar-v2")]
    [InlineData("--weird--", "custom.weird")]
    [InlineData("", "custom.panel")]
    public void Quick_panel_tool_id_derivation_is_schema_valid(string stem, string expected)
    {
        var derived = WebSurfaceDefaults.DeriveToolIdFromFileName(stem);
        Assert.Equal(expected, derived);
        Assert.Matches(WebSurfaceDefaults.ToolIdPattern, derived);
    }
}
