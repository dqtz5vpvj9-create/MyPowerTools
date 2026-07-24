using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Platform.Mac;
using MyPowerTools.Shell.Avalonia;

namespace MyPowerTools.Tests;

public sealed class MacReleaseSyncTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Mac_release_keeps_the_full_tool_catalog_and_only_defaults_notifications_on()
    {
        var publishScript = File.ReadAllText(Path.Combine(Root, "scripts", "publish-macos.ps1"));
        var runner = File.ReadAllText(Path.Combine(Root, "src", "MyPowerTools.Runner", "Program.cs"));

        foreach (var destination in new[]
        {
            "adb-forwarder",
            "doubao-agent",
            "paste-image",
            "screenease",
            "smartbird-thermostat"
        })
        {
            Assert.Contains($"Destination = '{destination}'", publishScript, StringComparison.Ordinal);
        }

        Assert.Contains("modules/android-tools-suite", publishScript, StringComparison.Ordinal);
        Assert.Contains("? [\"android-tools.notifications\"]", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void Mac_release_standalone_manifests_cover_arm64_and_x64()
    {
        foreach (var manifestPath in new[]
        {
            Path.Combine("tools", "adb-forwarder", "current-integration", "modules", "adb-forwarder", "module.json"),
            Path.Combine("tools", "doubao-computer-use", "current-integration", "modules", "doubao-agent", "module.json"),
            Path.Combine("tools", "paste-image", "current-integration", "modules", "paste-image", "module.json"),
            Path.Combine("tools", "screenease", "current-integration", "modules", "screenease", "module.json"),
            Path.Combine("tools", "smartbird-thermostat", "current-integration", "modules", "smartbird-thermostat", "module.json")
        })
        {
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, manifestPath)))!.AsObject();
            var inProc = manifest["entrypoints"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(entrypoint => entrypoint["kind"]!.GetValue<string>() == "inproc-dotnet");
            var platforms = inProc["platforms"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.True(
                platforms.Contains("macos") ||
                (platforms.Contains("macos-arm64") && platforms.Contains("macos-x64")),
                $"{manifest["id"]} must support both macOS architectures.");
        }
    }

    [Fact]
    public void Mac_webview_forwards_the_current_shell_shortcut_contract()
    {
        var nativeSource = File.ReadAllText(Path.Combine(
            Root,
            "native",
            "macos",
            "MptMacNative",
            "MptMacNative.mm"));

        Assert.Contains("gesture = 'Ctrl+Shift+P'", nativeSource, StringComparison.Ordinal);
        Assert.Contains("gesture = 'Ctrl+R'", nativeSource, StringComparison.Ordinal);
        Assert.DoesNotContain("['f', 'k', 'r']", nativeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_manager_release_bootstrap_uses_the_bundled_deploy_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-mac-bootstrap-tests", Guid.NewGuid().ToString("N"));
        var units = Path.Combine(root, "ServiceUnits", "units");
        Directory.CreateDirectory(units);
        try
        {
            var startInfo = ShellServiceManagerBootstrapper.CreateStartInfo(
                Path.Combine(root, "ServiceManager", "MyPowerTools.ServiceManager"),
                root);
            var arguments = startInfo.ArgumentList.ToArray();
            var deployRootIndex = Array.IndexOf(arguments, "--deploy-root");

            Assert.True(deployRootIndex >= 0);
            Assert.Equal(Path.Combine(root, "ServiceUnits"), arguments[deployRootIndex + 1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Mac_platform_pack_exposes_native_pasteboard_images()
    {
        IPlatformPack platform = new MacPlatformPack();

        var capability = platform.Capabilities.Resolve("clipboard.image");
        Assert.True(capability.Supported);
        Assert.Equal("NSPasteboard", capability.Provider);
        Assert.IsType<MacPasteboardImageService>(platform.ClipboardImages);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the MyPowerTools repository root.");
    }
}
