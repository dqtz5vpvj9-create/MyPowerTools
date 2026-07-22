using MyPowerTools.Runtime;

namespace MyPowerTools.Tests;

public sealed class ToolDiscoveryConfigurationTests
{
    [Fact]
    public void Resolve_always_includes_the_custom_tools_drop_folder_after_app_tools()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "mpt-discovery-test", Guid.NewGuid().ToString("N"));
        var appRoot = Path.Combine(sandbox, "app");
        var dataRoot = Path.Combine(sandbox, "data");
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(dataRoot);

        var paths = ToolDiscoveryConfiguration.Resolve(appRoot, dataRoot);

        var appTools = Path.GetFullPath(Path.Combine(appRoot, "tools"));
        var customTools = Path.GetFullPath(ToolDiscoveryConfiguration.CustomToolsDirectory(dataRoot));
        var appIndex = Array.IndexOf(paths.ToArray(), appTools);
        var customIndex = Array.IndexOf(paths.ToArray(), customTools);
        Assert.True(appIndex >= 0, "appRoot/tools must be discovered");
        Assert.True(customIndex >= 0, "the custom-tools drop folder must always be discovered");
        Assert.True(customIndex > appIndex, "the drop folder follows the repository tools directory");
    }

    [Fact]
    public void Resolve_deduplicates_configured_directories_case_insensitively()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "mpt-discovery-test", Guid.NewGuid().ToString("N"));
        var dataRoot = Path.Combine(sandbox, "data");
        Directory.CreateDirectory(dataRoot);
        var extra = Path.Combine(sandbox, "extra tools");

        var paths = ToolDiscoveryConfiguration.Resolve(
            sandbox,
            dataRoot,
            [extra, extra.ToUpperInvariant()]);

        Assert.Single(paths.Where(path =>
            string.Equals(path, Path.GetFullPath(extra), StringComparison.OrdinalIgnoreCase)));
    }
}
