using System.Runtime.InteropServices;
using MyPowerTools.Packaging.Ota;

namespace MyPowerTools.Tests;

public sealed class OtaFeedLayoutTests
{
    [Theory]
    [InlineData(OtaFeedLayout.WindowsX64, "MyPowerTools-win-x64.zip", "MyPowerTools-win-x64.manifest.json")]
    [InlineData(OtaFeedLayout.OsxArm64, "MyPowerTools-osx-arm64.zip", "MyPowerTools-osx-arm64.manifest.json")]
    [InlineData(OtaFeedLayout.OsxX64, "MyPowerTools-osx-x64.zip", "MyPowerTools-osx-x64.manifest.json")]
    public void Full_package_and_manifest_are_named_after_the_runtime_identifier(
        string runtimeIdentifier,
        string expectedPackage,
        string expectedManifest)
    {
        Assert.Equal(expectedPackage, OtaFeedLayout.FullPackageAsset(runtimeIdentifier));
        Assert.Equal(expectedManifest, OtaFeedLayout.FileManifestAsset(runtimeIdentifier));
    }

    [Fact]
    public void Windows_keeps_the_unsuffixed_channel_assets_that_released_clients_request()
    {
        // Every shipped Windows client downloads exactly these names, and verifies the feed's
        // Ed25519 signature over its exact bytes. Adding macOS must not rename or rewrite them.
        Assert.Equal("channel-stable.json", OtaFeedLayout.ChannelFeedAsset("stable", OtaFeedLayout.WindowsX64));
        Assert.Equal("channel-nightly.json", OtaFeedLayout.ChannelFeedAsset("nightly", OtaFeedLayout.WindowsX64));
        Assert.Equal(
            "channel-stable-web.json",
            OtaFeedLayout.ChannelFeedAsset("stable", OtaFeedLayout.WindowsX64, OtaDistributionMode.Web));
        Assert.Equal(
            "channel-stable.json.sig",
            OtaFeedLayout.ChannelSignatureAsset("stable", OtaFeedLayout.WindowsX64));
    }

    [Fact]
    public void Every_other_platform_publishes_its_own_channel_file()
    {
        Assert.Equal(
            "channel-stable-osx-arm64.json",
            OtaFeedLayout.ChannelFeedAsset("stable", OtaFeedLayout.OsxArm64));
        Assert.Equal(
            "channel-nightly-osx-x64.json",
            OtaFeedLayout.ChannelFeedAsset("nightly", OtaFeedLayout.OsxX64));
        Assert.Equal(
            "channel-stable-osx-arm64.json.sig",
            OtaFeedLayout.ChannelSignatureAsset("stable", OtaFeedLayout.OsxArm64));
    }

    [Fact]
    public void The_web_distribution_exists_only_for_windows()
    {
        Assert.Equal(
            "MyPowerTools-core-win-x64.zip",
            OtaFeedLayout.FullPackageAsset(OtaFeedLayout.WindowsX64, OtaDistributionMode.Web));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OtaFeedLayout.FullPackageAsset(OtaFeedLayout.OsxArm64, OtaDistributionMode.Web));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OtaFeedLayout.ChannelFeedAsset("stable", OtaFeedLayout.OsxArm64, OtaDistributionMode.Web));
    }

    [Fact]
    public void Asset_names_are_case_insensitive_in_their_runtime_identifier()
    {
        Assert.Equal("MyPowerTools-osx-arm64.zip", OtaFeedLayout.FullPackageAsset("OSX-ARM64"));
        Assert.Equal("channel-stable.json", OtaFeedLayout.ChannelFeedAsset("stable", "WIN-X64"));
    }

    [Fact]
    public void Deltas_are_published_for_windows_and_withheld_on_macos()
    {
        // A macOS delta would replace individual files inside a code-signed .app: the executable
        // bit is lost, Contents/_CodeSignature stops matching, and the compatibility symlinks under
        // Contents/MacOS/<Host>/ would be overwritten with plain files.
        Assert.True(OtaFeedLayout.SupportsDeltaPackages(OtaFeedLayout.WindowsX64));
        Assert.False(OtaFeedLayout.SupportsDeltaPackages(OtaFeedLayout.OsxArm64));
        Assert.False(OtaFeedLayout.SupportsDeltaPackages(OtaFeedLayout.OsxX64));
    }

    [Theory]
    [InlineData(true, false, Architecture.X64, "win-x64")]
    [InlineData(false, true, Architecture.Arm64, "osx-arm64")]
    [InlineData(false, true, Architecture.X64, "osx-x64")]
    [InlineData(false, false, Architecture.X64, "linux-x64")]
    public void Runtime_identifiers_follow_the_dotnet_rid_spelling(
        bool isWindows,
        bool isMacOs,
        Architecture architecture,
        string expected)
    {
        Assert.Equal(expected, OtaFeedLayout.RuntimeIdentifierFor(isWindows, isMacOs, architecture));
    }

    [Fact]
    public void The_current_runtime_identifier_matches_the_running_process()
    {
        var expected = OtaFeedLayout.RuntimeIdentifierFor(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
            RuntimeInformation.ProcessArchitecture);
        Assert.Equal(expected, OtaFeedLayout.CurrentRuntimeIdentifier());
    }

    [Fact]
    public void A_blank_runtime_identifier_or_channel_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => OtaFeedLayout.FullPackageAsset("  "));
        Assert.Throws<ArgumentException>(() => OtaFeedLayout.ChannelFeedAsset(" ", OtaFeedLayout.WindowsX64));
    }
}

public sealed class OtaUpdaterLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mpt-ota-locator-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_cli_file_name_carries_an_exe_suffix_only_on_windows()
    {
        Assert.Equal("MyPowerTools.Cli.exe", OtaUpdaterLocator.CliFileName(isWindows: true));
        Assert.Equal("MyPowerTools.Cli", OtaUpdaterLocator.CliFileName(isWindows: false));
    }

    [Fact]
    public void The_bundle_root_of_a_nested_helper_is_the_container_app()
    {
        // MyPowerTools.app/Contents/MacOS/Helpers/MyPowerTools Shell.app/Contents/MacOS
        var container = CreateBundle(Path.Combine(_root, "Applications", "MyPowerTools.app"));
        var helper = CreateBundle(Path.Combine(
            container, "Contents", "MacOS", "Helpers", "MyPowerTools Shell.app"));
        var shellBase = Path.Combine(helper, "Contents", "MacOS");
        Directory.CreateDirectory(shellBase);

        Assert.Equal(container, OtaUpdaterLocator.FindMacBundleRoot(shellBase));
    }

    [Fact]
    public void The_bundle_root_of_a_flat_layout_is_the_same_app()
    {
        var container = CreateBundle(Path.Combine(_root, "Applications", "MyPowerTools.app"));
        var shellBase = Path.Combine(container, "Contents", "MacOS", "Shell");
        Directory.CreateDirectory(shellBase);

        Assert.Equal(container, OtaUpdaterLocator.FindMacBundleRoot(shellBase));
    }

    [Fact]
    public void A_directory_outside_any_bundle_has_no_bundle_root()
    {
        var checkout = Path.Combine(_root, "repo", "artifacts", "build");
        Directory.CreateDirectory(checkout);

        Assert.Null(OtaUpdaterLocator.FindMacBundleRoot(checkout));
        // A .app directory without Contents/Info.plist is a name, not a bundle.
        var lookalike = Path.Combine(_root, "repo", "notes.app", "inner");
        Directory.CreateDirectory(lookalike);
        Assert.Null(OtaUpdaterLocator.FindMacBundleRoot(lookalike));
    }

    [Fact]
    public void The_updater_script_is_found_in_the_install_root_then_a_checkout_then_the_bundle()
    {
        var installRoot = Path.Combine(_root, "Programs", "MyPowerTools");
        var candidates = OtaUpdaterLocator.UpdaterScriptCandidates(installRoot, macBundleRoot: null);
        Assert.Equal(
            new[]
            {
                Path.Combine(installRoot, "ota-update.ps1"),
                Path.Combine(installRoot, "scripts", "ota-update.ps1")
            },
            candidates);

        var bundle = Path.Combine(_root, "MyPowerTools.app");
        var bundled = OtaUpdaterLocator.UpdaterScriptCandidates(
            Path.Combine(bundle, "Contents", "MacOS"),
            bundle);
        Assert.Contains(
            Path.Combine(bundle, "Contents", "Resources", "scripts", "ota-update.ps1"),
            bundled);
    }

    [Fact]
    public void The_updater_script_resolves_from_a_macos_bundle_where_no_flat_copy_exists()
    {
        var bundle = CreateBundle(Path.Combine(_root, "MyPowerTools.app"));
        var scripts = Path.Combine(bundle, "Contents", "Resources", "scripts");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "ota-update.ps1"), "# updater");

        // Contents/MacOS is what MyPowerTools.Cli resolves as its root inside the bundle.
        var resolved = OtaUpdaterLocator.ResolveFirstExisting(
            OtaUpdaterLocator.UpdaterScriptCandidates(Path.Combine(bundle, "Contents", "MacOS"), bundle));

        Assert.Equal(Path.Combine(scripts, "ota-update.ps1"), resolved);
    }

    [Fact]
    public void The_cli_resolves_from_the_bundle_when_the_shell_runs_out_of_a_helper()
    {
        var bundle = CreateBundle(Path.Combine(_root, "MyPowerTools.app"));
        var cliDirectory = Path.Combine(bundle, "Contents", "MacOS", "Cli");
        Directory.CreateDirectory(cliDirectory);
        File.WriteAllText(Path.Combine(cliDirectory, "MyPowerTools.Cli"), "#!/bin/sh");

        var helper = CreateBundle(Path.Combine(
            bundle, "Contents", "MacOS", "Helpers", "MyPowerTools Shell.app"));
        var shellBase = Path.Combine(helper, "Contents", "MacOS");
        Directory.CreateDirectory(shellBase);

        var resolved = OtaUpdaterLocator.ResolveFirstExisting(
            OtaUpdaterLocator.CliCandidates(shellBase, bundle, isWindows: false));

        Assert.Equal(Path.Combine(cliDirectory, "MyPowerTools.Cli"), resolved);
    }

    [Fact]
    public void The_cli_sibling_directory_still_wins_on_a_flat_install()
    {
        var installRoot = Path.Combine(_root, "Programs", "MyPowerTools");
        var cliDirectory = Path.Combine(installRoot, "Cli");
        Directory.CreateDirectory(cliDirectory);
        var cli = Path.Combine(cliDirectory, OtaUpdaterLocator.CliFileName(OperatingSystem.IsWindows()));
        File.WriteAllText(cli, "cli");
        var shellBase = Path.Combine(installRoot, "Shell");
        Directory.CreateDirectory(shellBase);

        var resolved = OtaUpdaterLocator.ResolveFirstExisting(
            OtaUpdaterLocator.CliCandidates(shellBase, macBundleRoot: null, OperatingSystem.IsWindows()));

        Assert.Equal(cli, resolved);
    }

    [Fact]
    public void Powershell_candidates_cover_path_then_the_two_macos_install_prefixes()
    {
        var pathVariable = string.Join(
            Path.PathSeparator,
            "/usr/bin",
            "/opt/tools/bin");
        var candidates = OtaUpdaterLocator.PowerShellCandidates(pathVariable, isWindows: false, isMacOs: true);

        Assert.Equal(
            new[]
            {
                "/usr/bin/pwsh",
                "/opt/tools/bin/pwsh",
                "/usr/local/bin/pwsh",
                "/opt/homebrew/bin/pwsh"
            },
            candidates);
    }

    [Fact]
    public void Powershell_candidates_on_windows_look_for_the_exe_and_add_no_unix_prefixes()
    {
        // PATH is split with the host's separator, so the entry here avoids the drive-letter
        // colon that would make this case host-dependent.
        var entry = Path.Combine("Program Files", "PowerShell", "7");
        var candidates = OtaUpdaterLocator.PowerShellCandidates(entry, isWindows: true, isMacOs: false);

        Assert.Equal(new[] { Path.Combine(entry, "pwsh.exe") }, candidates);
    }

    [Fact]
    public void An_empty_path_variable_still_leaves_the_macos_fallbacks()
    {
        var candidates = OtaUpdaterLocator.PowerShellCandidates(null, isWindows: false, isMacOs: true);

        Assert.Equal(new[] { "/usr/local/bin/pwsh", "/opt/homebrew/bin/pwsh" }, candidates);
    }

    [Fact]
    public void The_missing_powershell_message_tells_macos_users_how_to_install_it()
    {
        Assert.Contains("brew install", OtaUpdaterLocator.PowerShellMissingMessage(isMacOs: true));
        Assert.Contains("/opt/homebrew/bin", OtaUpdaterLocator.PowerShellMissingMessage(isMacOs: true));
        Assert.DoesNotContain("brew install", OtaUpdaterLocator.PowerShellMissingMessage(isMacOs: false));
    }

    [Fact]
    public void The_product_root_is_the_parent_off_bundle_and_contents_macos_inside_one()
    {
        var installRoot = Path.Combine(_root, "Programs", "MyPowerTools");
        Assert.Equal(
            Path.GetFullPath(installRoot),
            OtaUpdaterLocator.ProductRoot(Path.Combine(installRoot, "Shell"), macBundleRoot: null));

        var bundle = Path.Combine(_root, "MyPowerTools.app");
        Assert.Equal(
            Path.Combine(bundle, "Contents", "MacOS"),
            OtaUpdaterLocator.ProductRoot(
                Path.Combine(bundle, "Contents", "MacOS", "Helpers", "MyPowerTools Shell.app", "Contents", "MacOS"),
                bundle));
    }

    private static string CreateBundle(string bundlePath)
    {
        Directory.CreateDirectory(Path.Combine(bundlePath, "Contents"));
        File.WriteAllText(Path.Combine(bundlePath, "Contents", "Info.plist"), "<plist/>");
        return bundlePath;
    }
}

public sealed class MacOtaApplyScriptTests
{
    private static readonly string Script = File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), "scripts", "ota-apply-macos.ps1"));

    [Fact]
    public void The_package_is_expanded_with_ditto_rather_than_the_managed_zip_reader()
    {
        // publish-macos.ps1 packs with `ditto -c -k`, which stores symlinks, extended attributes
        // and the executable bit. [IO.Compression.ZipFile] restores none of them, and the
        // Contents/MacOS/<Host>/ compatibility symlinks would come back as text files.
        Assert.Contains("'-x', '-k'", Script, StringComparison.Ordinal);
        Assert.DoesNotContain("ZipFile]::", Script, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtractToDirectory", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Maintenance_mode_boots_out_both_launch_agents_and_restores_them()
    {
        Assert.Contains("'com.mypowertools.runner', 'com.mypowertools.servicemanager'", Script, StringComparison.Ordinal);
        Assert.Contains("'bootout'", Script, StringComparison.Ordinal);
        Assert.Contains("'bootstrap'", Script, StringComparison.Ordinal);
        Assert.Contains("'kickstart', '-k'", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_apply_restores_the_timestamped_backup_and_the_saved_plists()
    {
        Assert.Contains("MyPowerTools.backup.{0}.app", Script, StringComparison.Ordinal);
        Assert.Contains("Move-Item -LiteralPath $backupApp -Destination $appFull", Script, StringComparison.Ordinal);
        Assert.Contains("ROLLBACK-FAILED.txt", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_health_check_waits_for_the_runner_host_control_socket()
    {
        // IpcEndpoint.RunnerDefault on non-Windows.
        Assert.Contains("mypowertools.runner.hostcontrol.sock", Script, StringComparison.Ordinal);
        Assert.Contains("UnixDomainSocketEndPoint", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bundle_swap_delegates_to_the_new_packages_own_installer()
    {
        // The installer that shipped with a version is the only thing that knows its layout, so an
        // update that moves a host into a nested helper bundle still writes correct plists.
        Assert.Contains("Contents/Resources/scripts/install-macos.ps1", Script, StringComparison.Ordinal);
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
