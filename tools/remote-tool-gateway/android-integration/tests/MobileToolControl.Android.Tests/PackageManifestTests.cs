using System.Text.Json.Nodes;
using MobileToolControl.Android;

namespace MobileToolControl.Android.Tests;

/// <summary>
/// The packaged manifests and the phone page are wired by literal strings; these tests fail if a
/// rename silently breaks the module id, the entry point, the surface factory, a command id or the
/// package manifest.
/// </summary>
public sealed class PackageManifestTests
{
    private static string IntegrationRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null &&
                   !File.Exists(Path.Combine(directory.FullName, "tools", "remote-tool-gateway", "android-integration", "package", "module.json")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory!.FullName, "tools", "remote-tool-gateway", "android-integration");
        }
    }

    private static JsonObject Read(string relative)
    {
        var path = Path.Combine(IntegrationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"缺少打包文件：{path}");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    [Fact]
    public void ModuleManifestMatchesTheModuleIdentityAndEntryPoint()
    {
        var module = Read("package/module.json");
        Assert.Equal(MobileToolControlOptions.ModuleId, module["id"]!.GetValue<string>());
        Assert.Equal(MobileToolControlOptions.PackageId, module["packageId"]!.GetValue<string>());

        var entrypoint = module["entrypoints"]!.AsArray()[0]!;
        Assert.Equal("inproc-dotnet", entrypoint["kind"]!.GetValue<string>());
        Assert.Equal(MobileToolControlOptions.ModuleAssemblyFileName, entrypoint["assembly"]!.GetValue<string>());
        Assert.Equal(MobileToolControlOptions.ModuleTypeName, entrypoint["type"]!.GetValue<string>());

        var capabilities = module["requires"]!.AsArray()
            .Select(item => item!["capability"]!.GetValue<string>())
            .ToArray();
        Assert.Contains("secret.store", capabilities);
    }

    [Fact]
    public void ToolManifestPointsAtThePhonePageFactory()
    {
        var tool = Read("package/ui/tool.json");
        Assert.Equal(MobileToolControlOptions.ToolId, tool["toolId"]!.GetValue<string>());
        Assert.Equal(MobileToolControlOptions.ModuleId, tool["ownerModuleId"]!.GetValue<string>());

        var surface = tool["routes"]!.AsArray()[0]!["surface"]!;
        Assert.Equal("dotnet", surface["kind"]!.GetValue<string>());
        Assert.Equal("surface/" + MobileToolControlOptions.SurfaceAssemblyFileName, surface["assembly"]!.GetValue<string>());
        Assert.Equal(MobileToolControlOptions.SurfaceTypeName, surface["type"]!.GetValue<string>());

        var prefixes = tool["activationUriPrefixes"]!.AsArray().Select(item => item!.GetValue<string>()).ToArray();
        Assert.Contains("mypowertools://device-tool", prefixes);
        // The desktop connection code is a real entry point (system link / QR scan).
        Assert.Contains("mpt://control/", prefixes);
    }

    [Fact]
    public void CommandIndexCoversEveryCommandTheModuleImplements()
    {
        var index = Read("package/commands.index.json");
        var ids = index["commands"]!.AsArray()
            .Select(item => item!["id"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var commandId in MobileToolControlOptions.CommandIds)
        {
            Assert.Contains(commandId, ids);
        }

        Assert.Contains(MobileToolControlOptions.ModuleId + ".workspace.open", ids);
    }

    [Fact]
    public void PackageManifestListsTheModuleAndTheSurface()
    {
        var manifest = Read("manifest/android-package-manifest.json");
        Assert.Equal(MobileToolControlOptions.PackageId, manifest["packageId"]!.GetValue<string>());

        var paths = manifest["files"]!.AsArray()
            .Select(item => item!["path"]!.GetValue<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("module.json", paths);
        Assert.Contains("MobileToolControl.Android.dll", paths);
        Assert.Contains("ui/surface/" + MobileToolControlOptions.SurfaceAssemblyFileName, paths);
        Assert.Contains("ui/tool.json", paths);
    }

    [Fact]
    public void TheBuildScriptNeverWritesTheRepositoryModulesTreeByDefault()
    {
        var script = File.ReadAllText(Path.Combine(IntegrationRoot, "build.ps1"));
        Assert.Contains("if ($Mirror) {", script, StringComparison.Ordinal);
        Assert.Contains("-p:StageRepositoryModule=false", script, StringComparison.Ordinal);
        Assert.Contains("modules/mobile-tool-control", script, StringComparison.Ordinal);
    }
}
