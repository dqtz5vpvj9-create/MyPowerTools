using MyPowerTools.Packaging.Ota;

namespace MyPowerTools.Tests;

public sealed class MacOtaCompletionTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void The_installed_bundle_receives_the_cli_and_canonical_ota_entrypoint()
    {
        var publisher = ReadScript("publish-macos.ps1");

        Assert.Contains("src/MyPowerTools.Cli/MyPowerTools.Cli.csproj", publisher, StringComparison.Ordinal);
        Assert.Contains("'ota-update-macos.ps1' = 'ota-update.ps1'", publisher, StringComparison.Ordinal);
        Assert.Contains("'ota-apply-macos.ps1' = 'ota-apply-macos.ps1'", publisher, StringComparison.Ordinal);
        Assert.Contains("'install-macos-base.ps1' = 'install-macos-base.ps1'", publisher, StringComparison.Ordinal);
        Assert.Contains("ota-signing-public-key.txt", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_checkouts_on_macos_prefer_the_macos_updater()
    {
        var candidates = OtaUpdaterLocator.UpdaterScriptCandidates(
            RepositoryRoot,
            macBundleRoot: null,
            isMacOs: true);

        Assert.Equal(
            Path.Combine(RepositoryRoot, OtaUpdaterLocator.MacSourceUpdaterScriptName),
            candidates[0]);
        Assert.Equal(
            Path.Combine(RepositoryRoot, "scripts", OtaUpdaterLocator.MacSourceUpdaterScriptName),
            candidates[1]);
        Assert.Contains(
            Path.Combine(RepositoryRoot, "scripts", OtaUpdaterLocator.UpdaterScriptName),
            candidates);
    }

    [Fact]
    public void Mac_updates_use_platform_specific_full_assets_and_no_delta_path()
    {
        var updater = ReadScript("ota-update-macos.ps1");

        Assert.Contains("return 'osx-arm64'", updater, StringComparison.Ordinal);
        Assert.Contains("return 'osx-x64'", updater, StringComparison.Ordinal);
        Assert.Contains("MyPowerTools-$RuntimeIdentifier.zip", updater, StringComparison.Ordinal);
        Assert.Contains("channel-$ChannelName-$RuntimeIdentifier.json", updater, StringComparison.Ordinal);
        Assert.Contains("macOS OTA feeds must not publish file-level delta packages", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-DeltaApply", updater, StringComparison.Ordinal);
        Assert.DoesNotContain("invoke-ota-update.ps1", updater, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_mac_updater_verifies_and_persists_the_post_signing_manifest()
    {
        var updater = ReadScript("ota-update-macos.ps1");

        Assert.Contains("manifestSha256", updater, StringComparison.Ordinal);
        Assert.Contains("Assert-MacManifest", updater, StringComparison.Ordinal);
        Assert.Contains("installed-files.manifest.json", updater, StringComparison.Ordinal);
        Assert.Contains("ota-apply-macos.ps1", updater, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath $manifestPath -Destination $installedManifestPath", updater, StringComparison.Ordinal);
    }

    [Fact]
    public void The_installer_supports_transactional_apply_and_packaged_bundle_installation()
    {
        var installer = ReadScript("install-macos.ps1");
        var apply = ReadScript("ota-apply-macos.ps1");

        Assert.Contains("[switch]$SkipOtaState", installer, StringComparison.Ordinal);
        Assert.Contains("install-macos-base.ps1", installer, StringComparison.Ordinal);
        Assert.Contains("Resolve-BundledSourceApp", installer, StringComparison.Ordinal);
        Assert.Contains("Contents/Info.plist", installer, StringComparison.Ordinal);
        Assert.Contains("-SkipOtaState", apply, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "scripts", "install-macos-base.ps1")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "scripts", "publish-macos-base.ps1")));
    }

    [Fact]
    public void Regular_macos_install_seeds_version_channel_manifest_and_runtime_identifier()
    {
        var installer = ReadScript("install-macos.ps1");

        Assert.Contains("installed-release.json", installer, StringComparison.Ordinal);
        Assert.Contains("installed-files.manifest.json", installer, StringComparison.Ordinal);
        Assert.Contains("runtimeIdentifier =", installer, StringComparison.Ordinal);
        Assert.Contains("Get-InstalledRuntimeIdentifier", installer, StringComparison.Ordinal);
        Assert.Contains("ota-history/ota-signing-public-key.txt", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_artifacts_are_named_after_the_dotnet_runtime_identifier()
    {
        var publisher = ReadScript("publish-macos.ps1");

        Assert.Contains("MyPowerTools-$runtimeIdentifier.zip", publisher, StringComparison.Ordinal);
        Assert.Contains("MyPowerTools-$runtimeIdentifier.manifest.json", publisher, StringComparison.Ordinal);
        Assert.Contains("channel-$Channel-$runtimeIdentifier.json", publisher, StringComparison.Ordinal);
        Assert.Contains("DeltaPackages = @()", publisher, StringComparison.Ordinal);
        Assert.Contains("MyPowerTools-macos-$Architecture.zip", publisher, StringComparison.Ordinal);
        Assert.Contains("[string]$Version = ''", publisher, StringComparison.Ordinal);
        Assert.Contains("Set-BundleVersion -BundlePath $appBundle", publisher, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_generation_happens_after_the_final_signing_pass()
    {
        var publisher = ReadScript("publish-macos.ps1");
        var signing = publisher.IndexOf("Sign-AppBundle -BundlePath", StringComparison.Ordinal);
        var manifest = publisher.IndexOf("-OutputPath $manifestPath", StringComparison.Ordinal);
        var archive = publisher.IndexOf("create macOS release archive", StringComparison.Ordinal);

        Assert.True(signing >= 0, "The final bundle signing call is missing.");
        Assert.True(manifest > signing, "The file manifest must be measured after codesign.");
        Assert.True(archive > manifest, "The archive must contain the same signed bytes measured by the manifest.");
    }

    [Fact]
    public void Bootstrap_copies_the_updater_outside_the_bundle_before_apply()
    {
        var updater = ReadScript("ota-update-macos.ps1");

        Assert.Contains("bootstrap-macos", updater, StringComparison.Ordinal);
        Assert.Contains("$PSCommandPath", updater, StringComparison.Ordinal);
        Assert.Contains("-BootstrapReady", updater, StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::None", updater, StringComparison.Ordinal);
    }

    private static string ReadScript(string name)
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot, "scripts", name));
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
