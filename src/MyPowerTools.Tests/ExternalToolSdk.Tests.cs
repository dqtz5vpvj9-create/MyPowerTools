using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runtime;
using System.Text.Json.Nodes;

namespace MyPowerTools.Tests;

public sealed class ExternalToolSdkTests
{
    [Fact]
    public async Task Refresh_discovers_and_removes_a_standalone_tool_without_shell_code()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-external-tool-test", Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(root, "modules");
        var tools = Path.Combine(root, "tools");
        var tool = Path.Combine(tools, "external-proof");
        Directory.CreateDirectory(modules);
        Directory.CreateDirectory(tool);
        await File.WriteAllTextAsync(Path.Combine(tool, "index.html"), "<h1>External proof</h1>");
        await File.WriteAllTextAsync(Path.Combine(tool, "settings.mpt.json"), """
        {
          "panelUrl": "http://127.0.0.1:18991/",
          "apiEndpoint": "http://127.0.0.1:18991"
        }
        """);
        await File.WriteAllTextAsync(Path.Combine(tool, "tool.json"), """
        {
          "schemaVersion": "1.0",
          "version": "0.1.0",
          "toolId": "external.refresh.proof",
          "title": "External refresh proof",
          "description": "Loaded from a temporary directory.",
          "icon": "tool.external",
          "category": "Tests",
          "type": "web-surface",
          "availability": "available",
          "primaryRouteId": "main",
          "routes": [{
            "routeId": "main",
            "surfaceId": "external.refresh.proof.main",
            "title": "Overview",
            "surface": { "kind": "web", "source": "${settings.panelUrl}", "openExternal": true }
          }],
          "homeCard": { "summary": "External proof", "primaryActionLabel": "Open", "order": 500 },
          "runtime": { "transport": "loopback-http", "endpoint": "${settings.apiEndpoint}", "healthPath": "/health" },
          "settings": { "values": "settings.mpt.json" },
          "permissions": []
        }
        """);

        await using var runtime = new MptHostRuntime(
            new PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(root, "data")));
        runtime.Load(modules, [tools]);

        var discovered = Assert.Single(runtime.ListTools(includeDisabled: true));
        Assert.Equal("external.refresh.proof", discovered.Descriptor.ToolId);
        Assert.Equal(Path.GetFullPath(tool), discovered.Descriptor.SourceDirectory);
        Assert.Equal("http://127.0.0.1:18991/", Assert.Single(discovered.Descriptor.Routes).Source);
        Assert.Equal("http://127.0.0.1:18991", discovered.Descriptor.Runtime?.Endpoint);
        var published = runtime.PublishToolEvent("external.refresh.proof", "external.refresh.proof.updated", new JsonObject { ["value"] = 1 });
        Assert.Equal("external.refresh.proof.updated", published.Type);
        Assert.True(published.Seq > 0);

        File.Delete(Path.Combine(tool, "tool.json"));
        var afterRemoval = await runtime.RefreshToolCatalogAsync(CancellationToken.None);

        Assert.Empty(afterRemoval);
        Assert.DoesNotContain("external.refresh.proof", File.ReadAllText(Path.Combine(RootPath(), "src", "MyPowerTools.Shell.Avalonia", "Services", "ShellWorkspaceController.ExternalTools.cs")));
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
