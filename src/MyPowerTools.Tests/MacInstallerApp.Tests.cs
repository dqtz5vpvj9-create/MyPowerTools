using System.Text.Json.Nodes;

namespace MyPowerTools.Tests;

/// <summary>
/// Contract checks for "MyPowerTools Installer.app": the names that the project, the bundle
/// plist, the publisher, the embedding in MyPowerTools.app, the release workflow and the
/// artifacts policy must agree on. The bundle itself can only be built and run on macOS CI.
/// </summary>
public sealed class MacInstallerAppTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Bundle_executable_matches_the_project_assembly_name()
    {
        var project = Read("src", "MyPowerTools.Installer.Mac", "MyPowerTools.Installer.Mac.csproj");
        var plist = Read("packaging", "macos", "Installer.Info.plist");

        Assert.Contains("<AssemblyName>MyPowerToolsInstaller</AssemblyName>", project, StringComparison.Ordinal);
        Assert.Contains("MyPowerTools.Packaging.csproj", project, StringComparison.Ordinal);
        Assert.Matches(@"<key>CFBundleExecutable</key>\s*<string>MyPowerToolsInstaller</string>", plist);
        Assert.Matches(@"<key>CFBundleIdentifier</key>\s*<string>com\.mypowertools\.installer</string>", plist);
        Assert.Matches(@"<key>LSMinimumSystemVersion</key>\s*<string>12\.0</string>", plist);
        Assert.Matches(@"<key>CFBundleIconFile</key>\s*<string>MyPowerTools</string>", plist);
        Assert.Contains("src/MyPowerTools.Installer.Mac/MyPowerTools.Installer.Mac.csproj", Read("MyPowerTools.slnx"), StringComparison.Ordinal);
    }

    [Fact]
    public void Publisher_builds_a_self_contained_single_file_bundle_and_zips_it_with_ditto()
    {
        var publisher = Read("scripts", "publish-macos-installer.ps1");

        Assert.Contains("'--self-contained', 'true'", publisher, StringComparison.Ordinal);
        Assert.Contains("-p:PublishSingleFile=true", publisher, StringComparison.Ordinal);
        Assert.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", publisher, StringComparison.Ordinal);
        Assert.Contains("'MyPowerTools Installer.app'", publisher, StringComparison.Ordinal);
        Assert.Contains("MyPowerTools-Installer-macos-$Architecture.zip", publisher, StringComparison.Ordinal);
        Assert.Contains("'-c', '-k', '--keepParent'", publisher, StringComparison.Ordinal);
        Assert.Contains("notarytool", publisher, StringComparison.Ordinal);
        Assert.Contains("MPT_NOTARY_APPLE_ID", publisher, StringComparison.Ordinal);
        Assert.Contains("iconutil", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_workflow_uploads_both_installer_archives()
    {
        var workflow = Read(".github", "workflows", "macos-ota-release.yml");

        Assert.Contains("scripts/publish-macos-installer.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("MyPowerTools-Installer-macos-$architecture.zip", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Installer_output_is_declared_before_the_generic_publish_entry()
    {
        var policy = JsonNode.Parse(Read("scripts", "artifacts-policy.json"))!.AsObject();
        var paths = policy["entries"]!.AsArray()
            .Select(entry => entry!["path"]!.GetValue<string>())
            .ToList();

        var installer = paths.IndexOf("publish/macos-installer-*");
        var generic = paths.IndexOf("publish/*");
        Assert.True(installer >= 0, "artifacts-policy.json must declare publish/macos-installer-*.");
        Assert.True(generic < 0 || installer < generic, "The specific installer entry must precede publish/*.");
    }

    private static string Read(params string[] segments) =>
        File.ReadAllText(Path.Combine([Root, .. segments]));

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
