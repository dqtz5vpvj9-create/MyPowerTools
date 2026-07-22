using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using MyPowerTools.Cli;

namespace MyPowerTools.Tests;

public sealed class QuickWebPanelTests
{
    [Fact]
    public async Task Minimal_tool_json_and_handwritten_full_manifest_produce_equivalent_descriptors()
    {
        // Loaded in separate runtimes: both forms resolve to the SAME tool identity
        // ("custom.quick-panel"), so hosting both at once is (correctly) a duplicate.
        var quickRoot = TempRoot();
        var quickDir = Path.Combine(quickRoot, "tools", "quick-panel");
        Directory.CreateDirectory(Path.Combine(quickRoot, "modules"));
        Directory.CreateDirectory(quickDir);
        await File.WriteAllTextAsync(Path.Combine(quickDir, "tool.json"), """
            { "title": "Quick Panel", "url": "http://127.0.0.1:9999/" }
            """);

        var fullRoot = TempRoot();
        var fullDir = Path.Combine(fullRoot, "tools", "full-panel");
        Directory.CreateDirectory(Path.Combine(fullRoot, "modules"));
        Directory.CreateDirectory(fullDir);
        await File.WriteAllTextAsync(Path.Combine(fullDir, "tool.json"), """
            {
              "schemaVersion": "1.0",
              "toolId": "custom.quick-panel",
              "title": "Quick Panel",
              "description": "Quick panel for http://127.0.0.1:9999/",
              "icon": "tool.external",
              "category": "Custom panels",
              "type": "web-surface",
              "availability": "available",
              "primaryRouteId": "main",
              "routes": [{
                "routeId": "main",
                "surfaceId": "custom.quick-panel.main",
                "title": "Overview",
                "surface": { "kind": "web", "source": "http://127.0.0.1:9999/",
                             "openExternal": true, "allowedOrigins": [] }
              }],
              "homeCard": { "summary": "Open Quick Panel",
                            "primaryActionLabel": "Open", "order": 500 },
              "development": { "loose": true }
            }
            """);

        await using var quickRuntime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(quickRoot, "data")));
        quickRuntime.Load(Path.Combine(quickRoot, "modules"), [Path.Combine(quickRoot, "tools")]);
        var quick = Assert.Single(quickRuntime.ListTools(includeDisabled: true));

        await using var fullRuntime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(fullRoot, "data")));
        fullRuntime.Load(Path.Combine(fullRoot, "modules"), [Path.Combine(fullRoot, "tools")]);
        var full = Assert.Single(fullRuntime.ListTools(includeDisabled: true));

        Assert.Null(quick.Descriptor.LoadError);
        Assert.Equal(full.Descriptor.ToolId, quick.Descriptor.ToolId);
        Assert.Equal(full.Descriptor.Title, quick.Descriptor.Title);
        Assert.Equal(full.Descriptor.Description, quick.Descriptor.Description);
        Assert.Equal(full.Descriptor.Icon, quick.Descriptor.Icon);
        Assert.Equal(full.Descriptor.Category, quick.Descriptor.Category);
        Assert.Equal(full.Descriptor.ToolType, quick.Descriptor.ToolType);
        Assert.Equal(full.Descriptor.Availability, quick.Descriptor.Availability);
        Assert.Equal(full.Descriptor.PrimaryRouteId, quick.Descriptor.PrimaryRouteId);
        Assert.Equal(full.Descriptor.HomeCard, quick.Descriptor.HomeCard);
        var fullRoute = Assert.Single(full.Descriptor.Routes);
        var quickRoute = Assert.Single(quick.Descriptor.Routes);
        Assert.Equal(fullRoute.RouteId, quickRoute.RouteId);
        Assert.Equal(fullRoute.SurfaceId, quickRoute.SurfaceId);
        Assert.Equal(fullRoute.Title, quickRoute.Title);
        Assert.Equal(fullRoute.SurfaceKind, quickRoute.SurfaceKind);
        Assert.Equal(fullRoute.Source, quickRoute.Source);
        Assert.Equal(fullRoute.OpenExternal, quickRoute.OpenExternal);
        Assert.Equal(fullRoute.AllowedOrigins ?? [], quickRoute.AllowedOrigins ?? []);
        Assert.Equal("ready", quick.State);
    }

    [Fact]
    public async Task Standalone_mpt_json_files_are_discovered_as_tools_with_derived_ids()
    {
        var root = TempRoot();
        var modules = Path.Combine(root, "modules");
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(modules);
        Directory.CreateDirectory(tools);
        await File.WriteAllTextAsync(Path.Combine(tools, "dashboard.mpt.json"), """
            { "title": "Dashboard", "url": "http://192.168.1.100:3000" }
            """);

        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(root, "data")));
        runtime.Load(modules, [tools]);

        var tool = Assert.Single(runtime.ListTools(includeDisabled: true));
        Assert.Equal("custom.dashboard", tool.Descriptor.ToolId);
        Assert.Equal("Dashboard", tool.Descriptor.Title);
        Assert.Equal("Custom panels", tool.Descriptor.Category);
        var route = Assert.Single(tool.Descriptor.Routes);
        Assert.Equal("web", route.SurfaceKind);
        Assert.Equal("http://192.168.1.100:3000", route.Source);
        Assert.True(route.OpenExternal);
        Assert.Empty(route.AllowedOrigins ?? []);
        Assert.Null(tool.Descriptor.LoadError);
    }

    [Fact]
    public async Task A_malformed_development_tool_surfaces_as_an_error_card_without_emptying_the_catalog()
    {
        var root = TempRoot();
        var modules = Path.Combine(root, "modules");
        var tools = Path.Combine(root, "tools");
        var goodDir = Path.Combine(tools, "good-tool");
        var badDir = Path.Combine(tools, "bad-tool");
        Directory.CreateDirectory(modules);
        Directory.CreateDirectory(goodDir);
        Directory.CreateDirectory(badDir);

        await File.WriteAllTextAsync(Path.Combine(goodDir, "tool.json"), """
            {
              "schemaVersion": "1.0", "toolId": "good.tool", "title": "Good",
              "description": "Works", "icon": "tool.external", "category": "Tests",
              "type": "web-surface", "primaryRouteId": "main",
              "routes": [{ "routeId": "main", "surfaceId": "good.main",
                "surface": { "kind": "web", "source": "http://127.0.0.1:1111/" } }],
              "homeCard": { "summary": "Good tool" }
            }
            """);
        // Valid JSON but not a usable manifest: no routes (and no url, so not a quick panel).
        await File.WriteAllTextAsync(Path.Combine(badDir, "tool.json"), """
            { "toolId": "bad.tool", "title": "Bad" }
            """);
        // Not even valid JSON — stage-1 failure inside the reader.
        await File.WriteAllTextAsync(Path.Combine(tools, "broken.mpt.json"), "{ not json");

        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(root, "data")));
        runtime.Load(modules, [tools]);

        var allTools = runtime.ListTools(includeDisabled: true);
        var good = allTools.FirstOrDefault(tool => tool.Descriptor.ToolId == "good.tool");
        Assert.NotNull(good);
        Assert.Null(good!.Descriptor.LoadError);
        Assert.Equal("ready", good.State);

        var bad = allTools.FirstOrDefault(tool => tool.Descriptor.ToolId == "bad.tool");
        Assert.NotNull(bad);
        Assert.False(string.IsNullOrWhiteSpace(bad!.Descriptor.LoadError));
        Assert.Equal("error", bad.State);
        Assert.False(string.IsNullOrWhiteSpace(bad.StateSummary));

        var broken = allTools.FirstOrDefault(tool => tool.Descriptor.ToolId == "custom.broken");
        Assert.NotNull(broken);
        Assert.Equal("error", broken!.State);

        // The dashboard (module-level state) must agree: the persisted-state pass must
        // not overwrite a load failure with an initial "ready" status.
        var dashboard = runtime.GetDashboardSnapshot();
        var brokenCard = dashboard.Cards.FirstOrDefault(card => card.ModuleId == "dev.custom.broken");
        Assert.NotNull(brokenCard);
        Assert.Equal("error", brokenCard!.State);
    }

    [Fact]
    public async Task Refresh_removes_a_deleted_quick_panel_file()
    {
        var root = TempRoot();
        var modules = Path.Combine(root, "modules");
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(modules);
        Directory.CreateDirectory(tools);
        var panelFile = Path.Combine(tools, "my-panel.mpt.json");
        await File.WriteAllTextAsync(panelFile, """
            { "title": "My Panel", "url": "http://127.0.0.1:5555/" }
            """);

        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(root, "data")));
        runtime.Load(modules, [tools]);
        Assert.Single(runtime.ListTools(includeDisabled: true));

        File.Delete(panelFile);
        var afterRemoval = await runtime.RefreshToolCatalogAsync(CancellationToken.None);

        Assert.Empty(afterRemoval);
    }

    [Fact]
    public async Task Settings_like_mpt_json_files_are_not_treated_as_tools()
    {
        var root = TempRoot();
        var modules = Path.Combine(root, "modules");
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(modules);
        Directory.CreateDirectory(tools);
        // A settings file that happens to match the *.mpt.json glob (a convention used
        // by other tooling) carries no tool signal and must be ignored, not an error card.
        await File.WriteAllTextAsync(Path.Combine(tools, "settings.mpt.json"), """
            { "connectionTimeoutMs": 5000, "autoRefresh": true }
            """);
        await File.WriteAllTextAsync(Path.Combine(tools, "real.mpt.json"), """
            { "title": "Real Panel", "url": "http://127.0.0.1:7070/" }
            """);

        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(root, "data")));
        runtime.Load(modules, [tools]);

        var tool = Assert.Single(runtime.ListTools(includeDisabled: true));
        Assert.Equal("custom.real", tool.Descriptor.ToolId);
        Assert.Null(tool.Descriptor.LoadError);
    }

    [Fact]
    public void Scaffolder_web_tool_output_still_passes_schema_validation()
    {
        var output = Path.Combine(Path.GetTempPath(), "mpt-scaffold-test", Guid.NewGuid().ToString("N"));
        var createExit = ToolScaffolder.Create(
            "web",
            "scaffold.test",
            output,
            Path.Combine(RootPath(), "artifacts", "sdk", "nuget"));
        Assert.Equal(0, createExit);

        var validateExit = ToolScaffolder.Validate(output, Path.Combine(RootPath(), "schemas"));
        Assert.Equal(0, validateExit);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-quick-panel-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string RootPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
