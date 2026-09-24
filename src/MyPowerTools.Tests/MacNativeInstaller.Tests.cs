using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using MyPowerTools.Packaging.Ota;

namespace MyPowerTools.Tests;

public sealed class MacNativeInstallerLogicTests
{
    private const string Rid = OtaFeedLayout.OsxArm64;

    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("0.9.9", "1.0.0", -1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    public void Versions_compare_numerically(string left, string right, int expected)
    {
        Assert.Equal(expected, MacInstallLogic.CompareVersion(left, right));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3-beta")]
    [InlineData("v1.2.3")]
    public void Invalid_versions_are_rejected(string version)
    {
        Assert.Throws<FormatException>(() => MacInstallLogic.CompareVersion(version, "1.0.0"));
    }

    [Theory]
    [InlineData(false, "0.0.0", "1.0.0", false, true, "not-installed")]
    [InlineData(true, "1.0.0", "1.1.0", false, true, "update-available")]
    [InlineData(true, "1.1.0", "1.1.0", false, false, "up-to-date")]
    [InlineData(true, "1.2.0", "1.1.0", false, false, "downgrade-blocked")]
    [InlineData(true, "1.1.0", "1.1.0", true, true, "forced")]
    [InlineData(true, "1.2.0", "1.1.0", true, true, "forced")]
    public void Update_decision_matches_the_pwsh_updater(
        bool installed, string current, string latest, bool force, bool available, string reason)
    {
        Assert.Equal((available, reason), MacInstallLogic.DecideUpdate(installed, current, latest, force));
    }

    [Fact]
    public void Valid_feed_yields_the_full_package()
    {
        var package = MacInstallLogic.ValidateFeed(JsonNode.Parse(Feed()), "stable", Rid);

        Assert.Equal("1.4.0", package.Version);
        Assert.Equal("MyPowerTools-osx-arm64.zip", package.Asset);
        Assert.Equal("MyPowerTools-osx-arm64.manifest.json", package.ManifestAsset);
        Assert.Equal(1234, package.Size);
        Assert.Equal(new string('a', 64), package.Sha256);
        Assert.True(package.Signed);
    }

    [Theory]
    [InlineData("kind", "\"something-else\"")]
    [InlineData("schemaVersion", "2")]
    [InlineData("channel", "\"nightly\"")]
    [InlineData("version", "\"1.4\"")]
    [InlineData("deltas", "[{\"asset\":\"x\"}]")]
    public void Feed_with_bad_top_level_fields_is_rejected(string field, string value)
    {
        var feed = JsonNode.Parse(Feed())!.AsObject();
        feed[field] = JsonNode.Parse(value);

        Assert.Throws<InvalidDataException>(() => MacInstallLogic.ValidateFeed(feed, "stable", Rid));
    }

    [Theory]
    [InlineData("asset", "\"MyPowerTools-osx-x64.zip\"")]
    [InlineData("manifestAsset", "\"MyPowerTools-win-x64.manifest.json\"")]
    [InlineData("sha256", "\"not-a-hash\"")]
    [InlineData("manifestSha256", "\"abc\"")]
    [InlineData("size", "0")]
    public void Feed_with_bad_package_metadata_is_rejected(string field, string value)
    {
        var feed = JsonNode.Parse(Feed())!.AsObject();
        feed["full"]![field] = JsonNode.Parse(value);

        Assert.Throws<InvalidDataException>(() => MacInstallLogic.ValidateFeed(feed, "stable", Rid));
    }

    [Fact]
    public void Null_deltas_are_accepted()
    {
        var feed = JsonNode.Parse(Feed())!.AsObject();
        feed["deltas"] = null;

        Assert.Equal("1.4.0", MacInstallLogic.ValidateFeed(feed, "stable", Rid).Version);
    }

    [Fact]
    public void Manifest_must_match_kind_product_and_version()
    {
        var manifest = JsonNode.Parse(
            "{\"schemaVersion\":1,\"kind\":\"mypowertools-ota-file-manifest\",\"product\":\"MyPowerTools\",\"version\":\"1.4.0\"}");

        MacInstallLogic.ValidateManifest(manifest, "1.4.0");
        Assert.Throws<InvalidDataException>(() => MacInstallLogic.ValidateManifest(manifest, "1.4.1"));
        manifest!["product"] = "Other";
        Assert.Throws<InvalidDataException>(() => MacInstallLogic.ValidateManifest(manifest, "1.4.0"));
    }

    [Fact]
    public void Signature_is_verified_over_the_feed_bytes()
    {
        var privateKey = Mpt.Ed25519.GeneratePrivateKey();
        var publicKeyHex = Convert.ToHexString(Mpt.Ed25519.PublicKeyFromPrivate(privateKey)).ToLowerInvariant();
        var bytes = Encoding.UTF8.GetBytes(Feed());
        var signature = Convert.ToBase64String(Mpt.Ed25519.Sign(bytes, privateKey));

        MacInstallLogic.VerifyFeedSignature(bytes, signature, publicKeyHex, feedSigned: true, allowUnsigned: false);

        // A UTF-8 BOM is dropped the way the pwsh updater's ReadAllText drops it.
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
        MacInstallLogic.VerifyFeedSignature(withBom, signature, publicKeyHex, feedSigned: true, allowUnsigned: false);

        var tampered = Encoding.UTF8.GetBytes(Feed().Replace("1.4.0", "1.4.1", StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() =>
            MacInstallLogic.VerifyFeedSignature(tampered, signature, publicKeyHex, true, false));

        var otherKey = Convert.ToHexString(Mpt.Ed25519.PublicKeyFromPrivate(Mpt.Ed25519.GeneratePrivateKey()));
        Assert.Throws<InvalidDataException>(() =>
            MacInstallLogic.VerifyFeedSignature(bytes, signature, otherKey, true, false));
        Assert.Throws<InvalidDataException>(() =>
            MacInstallLogic.VerifyFeedSignature(bytes, "", publicKeyHex, true, false));
        Assert.Throws<InvalidDataException>(() =>
            MacInstallLogic.VerifyFeedSignature(bytes, "!!notbase64!!", publicKeyHex, true, false));
    }

    [Fact]
    public void Unsigned_feed_requires_explicit_permission()
    {
        var bytes = Encoding.UTF8.GetBytes(Feed(signed: false));

        Assert.Throws<InvalidDataException>(() =>
            MacInstallLogic.VerifyFeedSignature(bytes, null, MacInstallLogic.EmbeddedPublicKeyHex, false, false));
        MacInstallLogic.VerifyFeedSignature(bytes, null, MacInstallLogic.EmbeddedPublicKeyHex, false, true);
    }

    [Fact]
    public void Embedded_public_key_matches_the_repository_key()
    {
        var root = FindRepositoryRoot();
        var key = File.ReadAllText(Path.Combine(root, "ota-history", "ota-signing-public-key.txt")).Trim();
        Assert.Equal(key, MacInstallLogic.EmbeddedPublicKeyHex);
    }

    [Theory]
    [InlineData("arm64", OtaFeedLayout.OsxArm64)]
    [InlineData("x86_64\n", OtaFeedLayout.OsxX64)]
    [InlineData("x86_64 arm64", null)]
    [InlineData("", null)]
    public void Launcher_architecture_decides_the_runtime_identifier(string lipo, string? expected)
    {
        Assert.Equal(expected, MacInstallLogic.RuntimeIdentifierFromLipo(lipo));
    }

    [Theory]
    [InlineData(0, "1\n", OtaFeedLayout.OsxArm64)]
    [InlineData(0, "0\n", OtaFeedLayout.OsxX64)]
    [InlineData(1, "", OtaFeedLayout.OsxX64)]
    public void Hardware_architecture_comes_from_sysctl(int exitCode, string output, string expected)
    {
        Assert.Equal(expected, MacInstallLogic.RuntimeIdentifierFromSysctl(exitCode, output));
    }

    [Fact]
    public void Only_processes_executing_from_the_bundle_are_selected()
    {
        const string bundle = "/Users/me/Applications/MyPowerTools.app";
        var ps = string.Join('\n',
            "    1 /sbin/launchd",
            "  501 /Users/me/Applications/MyPowerTools.app/Contents/MacOS/MyPowerTools",
            "  502 /Users/me/Applications/MyPowerTools.app/Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner",
            "  503 /Users/me/Applications/MyPowerTools.app.old/Contents/MacOS/MyPowerTools",
            "  504 /usr/bin/vim",
            "  777 /Users/me/Applications/MyPowerTools.app/Contents/MacOS/Cli/MyPowerTools.Cli",
            "  garbage line",
            "  502 /Users/me/Applications/MyPowerTools.app/Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner");

        var selected = MacInstallLogic.SelectBundleProcesses(ps, bundle + "/", [777]);

        Assert.Equal([501, 502], selected.Select(process => process.ProcessId));
        Assert.EndsWith("MyPowerTools.Runner", selected[1].ExecutablePath, StringComparison.Ordinal);
    }

    [Fact]
    public void LaunchAgent_plist_matches_the_pwsh_installer_byte_for_byte()
    {
        const string target = "/Users/me/Applications/MyPowerTools.app";
        const string data = "/Users/me/Library/Application Support/MyPowerTools";
        var arguments = MacInstallLogic.LaunchAgentProgramArguments(MacInstallLogic.RunnerLabel, target, data);
        var plist = MacInstallLogic.BuildLaunchAgentPlist(
            MacInstallLogic.RunnerLabel,
            arguments,
            target + "/Contents/MacOS",
            "/Users/me/Library/Logs/MyPowerTools");

        const string expected =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n" +
            "<dict>\n" +
            "  <key>Label</key><string>com.mypowertools.runner</string>\n" +
            "  <key>ProgramArguments</key><array>" +
            "<string>/Users/me/Applications/MyPowerTools.app/Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner</string>" +
            "<string>--modules</string>" +
            "<string>/Users/me/Applications/MyPowerTools.app/Contents/MacOS/modules</string>" +
            "<string>--data-root</string>" +
            "<string>/Users/me/Library/Application Support/MyPowerTools</string></array>\n" +
            "  <key>WorkingDirectory</key><string>/Users/me/Applications/MyPowerTools.app/Contents/MacOS</string>\n" +
            "  <key>RunAtLoad</key><true/>\n" +
            "  <key>KeepAlive</key><true/>\n" +
            "  <key>ProcessType</key><string>Background</string>\n" +
            "  <key>StandardOutPath</key><string>/Users/me/Library/Logs/MyPowerTools/com.mypowertools.runner.log</string>\n" +
            "  <key>StandardErrorPath</key><string>/Users/me/Library/Logs/MyPowerTools/com.mypowertools.runner.error.log</string>\n" +
            "</dict>\n" +
            "</plist>";
        Assert.Equal(expected, plist);
    }

    [Fact]
    public void ServiceManager_agent_deploys_service_units_from_the_bundle()
    {
        var arguments = MacInstallLogic.LaunchAgentProgramArguments(
            MacInstallLogic.ServiceManagerLabel, "/A/MyPowerTools.app", "/D");

        Assert.Equal(
            [
                "/A/MyPowerTools.app/Contents/MacOS/Helpers/MyPowerTools ServiceManager.app/Contents/MacOS/MyPowerTools.ServiceManager",
                "--data-root",
                "/D",
                "--deploy-root",
                "/A/MyPowerTools.app/Contents/MacOS/ServiceUnits"
            ],
            arguments);
        Assert.Equal(
            [MacInstallLogic.ServiceManagerLabel, MacInstallLogic.RunnerLabel],
            MacInstallLogic.AgentLabels);
    }

    [Fact]
    public void LaunchAgent_plist_escapes_xml_and_stays_well_formed()
    {
        var plist = MacInstallLogic.BuildLaunchAgentPlist(
            MacInstallLogic.RunnerLabel,
            ["/Users/a&b/<x>/Runner", "--data-root", "/Users/a&b/data"],
            "/Users/a&b",
            "/Users/a&b/Logs");

        Assert.Contains("<string>/Users/a&amp;b/&lt;x&gt;/Runner</string>", plist, StringComparison.Ordinal);
        var document = XDocument.Parse(plist);
        Assert.Equal("/Users/a&b", MacInstallLogic.ReadPlistString(plist, "WorkingDirectory"));
        Assert.Equal(3, document.Descendants("array").Single().Elements("string").Count());
    }

    [Fact]
    public void Launchctl_program_path_is_parsed_from_print_output()
    {
        const string withProgram = """
            gui/501/com.mypowertools.runner = {
            	active count = 1
            	path = /Users/me/Library/LaunchAgents/com.mypowertools.runner.plist
            	state = running

            	program = /Users/me/Applications/MyPowerTools.app/Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner
            	arguments = {
            		/ignored
            	}
            }
            """;
        const string argumentsOnly = """
            gui/501/com.mypowertools.runner = {
            	arguments = {
            		/Users/me/Applications/MyPowerTools.app/Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner
            		--modules
            	}
            }
            """;

        Assert.Equal(
            "/Users/me/Applications/MyPowerTools.app/Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner",
            MacInstallLogic.ParseLaunchctlProgram(withProgram));
        Assert.Equal(
            "/Users/me/Applications/MyPowerTools.app/Contents/MacOS/Helpers/MyPowerTools Runner.app/Contents/MacOS/MyPowerTools.Runner",
            MacInstallLogic.ParseLaunchctlProgram(argumentsOnly));
        Assert.Null(MacInstallLogic.ParseLaunchctlProgram("Could not find service"));
    }

    [Fact]
    public void Paths_inside_a_bundle_are_matched_by_directory_boundary()
    {
        Assert.True(MacInstallLogic.IsInside("/A/MyPowerTools.app/Contents/x", "/A/MyPowerTools.app"));
        Assert.True(MacInstallLogic.IsInside("/A/MyPowerTools.app/Contents/x", "/A/MyPowerTools.app/"));
        Assert.False(MacInstallLogic.IsInside("/A/MyPowerTools.app.backup/Contents/x", "/A/MyPowerTools.app"));
        Assert.False(MacInstallLogic.IsInside("/A/MyPowerTools.app", "/A/MyPowerTools.app"));
        Assert.False(MacInstallLogic.IsInside(null, "/A/MyPowerTools.app"));
    }

    [Fact]
    public void Nightly_feed_is_found_in_the_github_release_list()
    {
        var releases = JsonNode.Parse("""
            [
              { "assets": [ { "name": "channel-nightly-osx-x64.json", "browser_download_url": "https://x/a/channel-nightly-osx-x64.json" } ] },
              { "assets": [ { "name": "channel-nightly-osx-arm64.json", "browser_download_url": "https://x/b/channel-nightly-osx-arm64.json" } ] }
            ]
            """);

        Assert.Equal(
            "https://x/b/channel-nightly-osx-arm64.json",
            MacInstallLogic.FindReleaseAssetUrl(releases, "channel-nightly-osx-arm64.json"));
        Assert.Null(MacInstallLogic.FindReleaseAssetUrl(releases, "channel-nightly-linux-x64.json"));
        Assert.Equal("https://x/b", MacInstallLogic.FeedBaseUrl("https://x/b/channel-nightly-osx-arm64.json"));
        Assert.Equal(
            "https://github.com/dqtz5vpvj9-create/MyPowerTools/releases/latest/download/channel-stable-osx-arm64.json",
            MacInstallLogic.StableFeedUrl(Rid));
    }

    [Theory]
    [InlineData(0, 100L, 0)]
    [InlineData(50, 100L, 50)]
    [InlineData(100, 100L, 100)]
    [InlineData(10, null, null)]
    public void Download_percent_is_clamped(long received, long? total, int? expected)
    {
        Assert.Equal(expected, MacInstallLogic.Percent(received, total));
    }

    [Fact]
    public void Installed_release_keeps_the_pwsh_schema()
    {
        var release = MacInstallLogic.BuildInstalledRelease(
            "1.4.0", "stable", "/A/MyPowerTools.app", "/D", new string('b', 64), Rid, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            [
                "schemaVersion", "product", "version", "channel", "installedAt", "installDir", "dataRoot",
                "repository", "manifestPath", "manifestSha256", "packageKind", "distributionMode", "runtimeIdentifier"
            ],
            release.Select(pair => pair.Key));
        Assert.Equal("installed-files.manifest.json", release["manifestPath"]!.GetValue<string>());
        Assert.Equal("https://github.com/dqtz5vpvj9-create/MyPowerTools", release["repository"]!.GetValue<string>());
    }

    [Fact]
    public void Default_options_follow_the_macos_layout()
    {
        var options = MacInstallOptions.CreateDefault();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(home, "Applications", "MyPowerTools.app"), options.AppBundlePath);
        Assert.Equal(Path.Combine(home, "Library", "Application Support", "MyPowerTools"), options.DataRoot);
        Assert.Equal("stable", options.Channel);
        Assert.True(options.Relaunch);
        Assert.False(options.Force);
        Assert.False(options.AllowUnsigned);
    }

    internal static string Feed(string version = "1.4.0", bool signed = true)
    {
        return $$"""
            {
              "schemaVersion": 1,
              "kind": "mypowertools-ota-channel-feed",
              "product": "MyPowerTools",
              "channel": "stable",
              "version": "{{version}}",
              "publishedAtUtc": "2026-01-01T00:00:00Z",
              "full": {
                "asset": "MyPowerTools-osx-arm64.zip",
                "sha256": "{{new string('a', 64)}}",
                "size": 1234,
                "manifestAsset": "MyPowerTools-osx-arm64.manifest.json",
                "manifestSha256": "{{new string('c', 64)}}"
              },
              "deltas": [],
              "signing": {
                "algorithm": "ed25519",
                "signed": {{(signed ? "true" : "false")}},
                "publicKeyHex": "",
                "signatureAsset": ""
              }
            }
            """;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ota-history", "ota-signing-public-key.txt")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}

public sealed class MacNativeInstallerCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-mac-installer-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _privateKey = Mpt.Ed25519.GeneratePrivateKey();

    public MacNativeInstallerCheckTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "feed"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "ota-state"));
        File.WriteAllText(
            Path.Combine(_root, "data", "ota-state", "ota-signing-public-key.txt"),
            Convert.ToHexString(Mpt.Ed25519.PublicKeyFromPrivate(_privateKey)).ToLowerInvariant());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Missing_installation_reports_not_installed_and_records_last_check()
    {
        var installer = CreateInstaller(WriteFeed("1.4.0"));

        Assert.Null(installer.ReadInstalledVersion());
        var check = await installer.CheckAsync();

        Assert.False(check["installed"]!.GetValue<bool>());
        Assert.Equal("0.0.0", check["currentVersion"]!.GetValue<string>());
        Assert.Equal("1.4.0", check["latestVersion"]!.GetValue<string>());
        Assert.True(check["available"]!.GetValue<bool>());
        Assert.Equal("not-installed", check["reason"]!.GetValue<string>());
        Assert.True(check["signed"]!.GetValue<bool>());
        Assert.Equal("MyPowerTools-osx-arm64.zip", check["package"]!["asset"]!.GetValue<string>());
        var lastCheck = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "data", "ota-state", "last-check.json")));
        Assert.Equal("not-installed", lastCheck!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task Installed_current_version_is_up_to_date()
    {
        WriteInfoPlist("1.4.0");
        var installer = CreateInstaller(WriteFeed("1.4.0"));

        Assert.Equal("1.4.0", installer.ReadInstalledVersion());
        var check = await installer.CheckAsync();

        Assert.True(check["installed"]!.GetValue<bool>());
        Assert.False(check["available"]!.GetValue<bool>());
        Assert.Equal("up-to-date", check["reason"]!.GetValue<string>());
        Assert.Null(check["package"]);
    }

    [Fact]
    public async Task Newer_feed_is_an_update()
    {
        WriteInfoPlist("1.3.9");
        var check = await CreateInstaller(WriteFeed("1.4.0")).CheckAsync();

        Assert.Equal("update-available", check["reason"]!.GetValue<string>());
        Assert.Equal("1.3.9", check["currentVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task Feed_signed_by_another_key_is_rejected()
    {
        var feedPath = WriteFeed("1.4.0", Mpt.Ed25519.GeneratePrivateKey());

        await Assert.ThrowsAsync<InvalidDataException>(() => CreateInstaller(feedPath).CheckAsync());
    }

    [Fact]
    public async Task Apply_outside_macos_fails_without_throwing()
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        var result = await CreateInstaller(WriteFeed("1.4.0")).ApplyAsync();

        Assert.False(result["success"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(result["error"]!.GetValue<string>()));
        Assert.True(File.Exists(Path.Combine(_root, "data", "ota-state", "last-update.json")));
    }

    private MacNativeInstaller CreateInstaller(string feedPath)
    {
        return new MacNativeInstaller(new MacInstallOptions
        {
            AppBundlePath = Path.Combine(_root, "Applications", "MyPowerTools.app"),
            DataRoot = Path.Combine(_root, "data"),
            FeedUrl = feedPath,
            Relaunch = false
        })
        {
            RuntimeIdentifierOverride = OtaFeedLayout.OsxArm64
        };
    }

    private string WriteFeed(string version, byte[]? signingKey = null)
    {
        var path = Path.Combine(_root, "feed", "channel-stable-osx-arm64.json");
        var json = MacNativeInstallerLogicTests.Feed(version);
        File.WriteAllText(path, json, new UTF8Encoding(false));
        File.WriteAllText(
            path + ".sig",
            Convert.ToBase64String(Mpt.Ed25519.Sign(Encoding.UTF8.GetBytes(json), signingKey ?? _privateKey)));
        return path;
    }

    private void WriteInfoPlist(string version)
    {
        var contents = Path.Combine(_root, "Applications", "MyPowerTools.app", "Contents");
        Directory.CreateDirectory(contents);
        File.WriteAllText(Path.Combine(contents, "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>CFBundleIdentifier</key><string>com.mypowertools.desktop</string>
              <key>CFBundleShortVersionString</key><string>{version}</string>
            </dict>
            </plist>
            """);
    }
}
