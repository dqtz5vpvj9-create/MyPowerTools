using System.Text.Json;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Tests;

public sealed class ShellRuntimeIdentityTests
{
    [Fact]
    public void Installed_layout_without_overlay_is_labeled_installed()
    {
        using var layout = TestLayout.CreateInstalled();

        var identity = ShellRuntimeIdentityResolver.Resolve(layout.ShellDirectory);

        Assert.Equal(ShellRuntimeKind.Installed, identity.Kind);
        Assert.Equal("INSTALLED", identity.ModeLabel);
        Assert.Equal("", identity.LocationLabel);
        Assert.Equal("MyPowerTools — INSTALLED", identity.WindowCaption);
    }

    [Fact]
    public void Development_overlay_uses_shell_configuration_and_repository_root()
    {
        using var layout = TestLayout.CreateInstalled();
        var repositoryRoot = Path.Combine(layout.Root, "source");
        Directory.CreateDirectory(repositoryRoot);
        var manifest = new
        {
            schemaVersion = 1,
            repositoryRoot,
            configuration = "Debug",
            components = new object[]
            {
                new
                {
                    kind = "managed",
                    relativePath = "Shell",
                    configuration = "Release"
                }
            }
        };
        File.WriteAllText(
            Path.Combine(layout.Root, "dev-update.manifest.json"),
            JsonSerializer.Serialize(manifest));

        var identity = ShellRuntimeIdentityResolver.Resolve(layout.ShellDirectory);

        Assert.Equal(ShellRuntimeKind.Development, identity.Kind);
        Assert.Equal("DEV · Release", identity.ModeLabel);
        Assert.Equal(Path.GetFullPath(repositoryRoot), identity.LocationLabel);
        Assert.Contains(repositoryRoot, identity.WindowCaption, StringComparison.Ordinal);
    }

    [Fact]
    public void Repository_build_is_labeled_development()
    {
        using var layout = TestLayout.CreateRepository();

        var identity = ShellRuntimeIdentityResolver.Resolve(layout.ShellDirectory);

        Assert.Equal(ShellRuntimeKind.Development, identity.Kind);
        Assert.StartsWith("DEV · ", identity.ModeLabel, StringComparison.Ordinal);
        Assert.Equal(layout.Root, identity.LocationLabel);
    }

    private sealed class TestLayout : IDisposable
    {
        private TestLayout(string root, string shellDirectory)
        {
            Root = root;
            ShellDirectory = shellDirectory;
        }

        public string Root { get; }
        public string ShellDirectory { get; }

        public static TestLayout CreateInstalled()
        {
            var root = CreateTemporaryRoot();
            var shell = Path.Combine(root, "Shell");
            Directory.CreateDirectory(shell);
            Directory.CreateDirectory(Path.Combine(root, "Runner"));
            Directory.CreateDirectory(Path.Combine(root, "modules"));
            return new TestLayout(root, shell);
        }

        public static TestLayout CreateRepository()
        {
            var root = CreateTemporaryRoot();
            File.WriteAllText(Path.Combine(root, "MyPowerTools.slnx"), "");
            var shell = Path.Combine(
                root,
                "artifacts",
                "build",
                "bin",
                "MyPowerTools.Shell.Avalonia",
                "debug");
            Directory.CreateDirectory(shell);
            return new TestLayout(root, shell);
        }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }

        private static string CreateTemporaryRoot()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "MyPowerTools.Tests",
                "runtime-identity",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return Path.GetFullPath(root);
        }
    }
}
