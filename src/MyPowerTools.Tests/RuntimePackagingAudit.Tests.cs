using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Packaging;
using MyPowerTools.Runtime;
using SettingsPatch = MyPowerTools.Abstractions.SettingsPatch;

namespace MyPowerTools.Tests;

// These regressions only touch their own temporary directories. They never start
// modules, services, browsers, native installers, or external integrations.
public sealed class RuntimePackagingAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mpt-runtime-packaging-audit", Guid.NewGuid().ToString("N"));

    public RuntimePackagingAuditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Settings_update_after_restart_loads_persisted_revision_and_values()
    {
        var settingsRoot = Path.Combine(_root, "settings");
        var original = new SettingsStore(settingsRoot);
        var saved = original.Update(new SettingsPatch("sample", 1, new JsonObject { ["existing"] = "keep" }));

        var restarted = new SettingsStore(settingsRoot);
        var updated = restarted.Update(new SettingsPatch("sample", saved.Revision, new JsonObject { ["new"] = "added" }));

        Assert.Equal(saved.Revision + 1, updated.Revision);
        Assert.Equal("keep", updated.Values["existing"]!.GetValue<string>());
        Assert.Equal("added", new SettingsStore(settingsRoot).Get("sample").Values["new"]!.GetValue<string>());
    }

    [Fact]
    public void Settings_update_after_restart_rejects_stale_revision_without_overwriting_data()
    {
        var settingsRoot = Path.Combine(_root, "settings");
        var original = new SettingsStore(settingsRoot);
        original.Update(new SettingsPatch("sample", 1, new JsonObject { ["existing"] = "keep" }));
        var restarted = new SettingsStore(settingsRoot);

        Assert.Throws<SettingsConflictException>(() => restarted.Update(
            new SettingsPatch("sample", 1, new JsonObject { ["existing"] = "lost" })));
        Assert.Equal("keep", new SettingsStore(settingsRoot).Get("sample").Values["existing"]!.GetValue<string>());
    }

    [Fact]
    public void Failed_settings_save_does_not_publish_an_unpersisted_revision()
    {
        var settingsRoot = Path.Combine(_root, "settings");
        var store = new SettingsStore(settingsRoot);
        var initial = store.Get("sample");
        var blockedPath = Path.Combine(settingsRoot, "sample.settings.json");
        Directory.CreateDirectory(blockedPath);
        var patch = new SettingsPatch("sample", initial.Revision, new JsonObject { ["value"] = "retry" });

        Assert.ThrowsAny<IOException>(() => store.Update(patch));
        Assert.Equal(initial.Revision, store.Get("sample").Revision);
        Assert.Null(store.Get("sample").Values["value"]);

        Directory.Delete(blockedPath);
        Assert.Equal(initial.Revision + 1, store.Update(patch).Revision);
    }

    [Fact]
    public void Failed_settings_import_does_not_replace_the_cached_snapshot()
    {
        var settingsRoot = Path.Combine(_root, "settings");
        var store = new SettingsStore(settingsRoot);
        var initial = store.Get("sample");
        Directory.CreateDirectory(Path.Combine(settingsRoot, "sample.settings.json"));
        var importPath = Path.Combine(_root, "import.json");
        File.WriteAllText(importPath, JsonSerializer.Serialize(new[]
        {
            new PersistedSettingsSnapshot("sample", 9, "{\"value\":\"unpersisted\"}", DateTimeOffset.UtcNow)
        }));

        Assert.ThrowsAny<IOException>(() => store.Import(importPath));
        Assert.Equal(initial.Revision, store.Get("sample").Revision);
        Assert.Null(store.Get("sample").Values["value"]);
    }

    [Fact]
    public void Failed_module_enable_save_does_not_change_the_cached_enable_state()
    {
        var stateRoot = Path.Combine(_root, "state");
        var store = new ModuleStateStore(stateRoot);
        Assert.True(store.IsEnabled("sample"));
        Directory.CreateDirectory(Path.Combine(stateRoot, "modules.enabled.json"));

        Assert.ThrowsAny<IOException>(() => store.SetModuleEnabled("sample", false));
        Assert.True(store.IsEnabled("sample"));
        Assert.Empty(store.DisabledModules());
    }

    [Fact]
    public void Failed_initial_allowlist_save_is_retried_on_the_next_read()
    {
        var stateRoot = Path.Combine(_root, "state");
        var store = new ModuleStateStore(stateRoot, ["sample"]);
        var statePath = Path.Combine(stateRoot, "modules.enabled.json");
        Directory.CreateDirectory(statePath);
        Assert.ThrowsAny<IOException>(() => store.IsEnabled("sample"));

        Directory.Delete(statePath);
        Assert.True(store.IsEnabled("sample"));
        Assert.True(File.Exists(statePath));
        Assert.False(new ModuleStateStore(stateRoot).IsEnabled("other"));
    }

    [Fact]
    public void Failed_process_policy_save_does_not_publish_the_unpersisted_policy()
    {
        var stateRoot = Path.Combine(_root, "state");
        var store = new RuntimeProcessPolicyStore(stateRoot);
        var initial = store.GetSnapshot();
        Directory.CreateDirectory(Path.Combine(stateRoot, "runtime.process-policies.json"));

        Assert.ThrowsAny<IOException>(() => store.Record(
            new RuntimeProcessPolicyResult(true, "grpc-ipc", "sample", "stopped", "manual", "test", ["sample"]),
            "test", "audit", null));
        Assert.Equal(initial, store.GetSnapshot());
    }

    [Fact]
    public void Failed_policy_expiry_save_does_not_forget_the_policy_or_advance_revision()
    {
        var stateRoot = Path.Combine(_root, "state");
        var store = new RuntimeProcessPolicyStore(stateRoot);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(-1);
        var saved = store.Record(
            new RuntimeProcessPolicyResult(true, "grpc-ipc", "sample", "stopped", "manual", "test", ["sample"]),
            "test", "audit", expiry);
        var statePath = Path.Combine(stateRoot, "runtime.process-policies.json");
        File.Move(statePath, statePath + ".saved");
        Directory.CreateDirectory(statePath);

        Assert.ThrowsAny<IOException>(() => store.Expire(DateTimeOffset.UtcNow, "audit"));
        Assert.Equal(saved, store.GetSnapshot());
        Assert.Single(store.CurrentPolicies());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"not-an-object\"")]
    public void Optional_tool_directory_configuration_ignores_scalar_json(string json)
    {
        var dataRoot = Path.Combine(_root, "data");
        Directory.CreateDirectory(Path.Combine(dataRoot, "settings"));
        File.WriteAllText(Path.Combine(dataRoot, "settings", "tool-directories.json"), json);

        var directories = ToolDiscoveryConfiguration.Resolve(_root, dataRoot);

        Assert.Contains(Path.Combine(_root, "tools"), directories);
        Assert.Contains(Path.Combine(dataRoot, "custom-tools"), directories);
    }

    [Fact]
    public async Task A_catalog_change_during_refresh_is_queued_instead_of_lost()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0;
        await using var watcher = new DevelopmentToolCatalogWatcher([], async _ =>
        {
            if (Interlocked.Increment(ref refreshes) == 1)
            {
                entered.SetResult();
                await release.Task;
            }
        });
        // Invoke the guarded operation directly, avoiding nondeterministic OS
        // watcher timing while exercising the same gate used by every event.
        var refresh = typeof(DevelopmentToolCatalogWatcher).GetMethod("RefreshGuardedAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var first = (Task)refresh.Invoke(watcher, new object[] { CancellationToken.None })!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = (Task)refresh.Invoke(watcher, new object[] { CancellationToken.None })!;
        var wasQueued = !second.IsCompleted;
        release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(wasQueued, "The final catalog change must wait for an in-flight refresh, not be discarded.");
        Assert.Equal(2, refreshes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("child/..")]
    [InlineData("../store")]
    [InlineData("../STORE/other")]
    [InlineData("child\\..")]
    public void Package_store_rejects_non_leaf_identifiers(string packageId)
    {
        var store = new PackageStore(Path.Combine(_root, "store"), _root);
        Assert.Throws<InvalidOperationException>(() => store.Repair(packageId));
    }

    [Fact]
    public void Package_store_uninstall_rejects_store_root_without_moving_other_packages()
    {
        var storeRoot = Path.Combine(_root, "store");
        var store = new PackageStore(storeRoot, _root);
        var sentinel = Path.Combine(storeRoot, "unrelated-package.txt");
        File.WriteAllText(sentinel, "keep");

        var error = Record.Exception(() => store.Uninstall(""));

        Assert.True(File.Exists(sentinel), "An invalid package ID must not move the complete package store.");
        Assert.IsType<InvalidOperationException>(error);
        Assert.False(Directory.Exists(storeRoot + ".rollback"));
    }

    [Fact]
    public void Package_store_rejects_absolute_identifiers_even_inside_the_store()
    {
        var storeRoot = Path.Combine(_root, "store");
        var store = new PackageStore(storeRoot, _root);
        Assert.Throws<InvalidOperationException>(() => store.Repair(Path.Combine(storeRoot, "sample")));
    }

    [Fact]
    public void Package_store_rejects_a_symbolic_package_directory()
    {
        if (OperatingSystem.IsWindows()) return; // Creating symlinks requires an OS privilege on Windows.
        var store = new PackageStore(Path.Combine(_root, "store"), _root);
        var outside = CreatePackage("outside");
        Directory.CreateSymbolicLink(Path.Combine(store.StoreRoot, "sample"), outside);

        Assert.Throws<InvalidOperationException>(() => store.Repair("sample"));
        Assert.True(File.Exists(Path.Combine(outside, "package.json")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("../PACKAGE/outside.txt")]
    public void Hash_manifest_rejects_entries_outside_its_package(string entryPath)
    {
        var package = CreatePackage("package");
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "outside");
        Directory.CreateDirectory(Path.Combine(_root, "PACKAGE"));
        File.WriteAllText(Path.Combine(_root, "PACKAGE", "outside.txt"), "outside");
        // A literal backslash filename is legal on Unix. It must still be rejected
        // because package paths have the same portable meaning on every OS.
        if (!OperatingSystem.IsWindows()) File.WriteAllText(Path.Combine(package, entryPath), "outside");
        WriteHashManifest(package, new PackageHashEntry(entryPath, PackageIntegrity.ComputeSha256(Path.Combine(_root, "outside.txt"))));

        Assert.Contains(new PackageIntegrity().VerifyHashManifest(package, "hashes.json"),
            issue => issue.Severity == "error" && issue.Message.Contains("package directory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Hash_manifest_rejects_absolute_entries_inside_the_package()
    {
        var package = CreatePackage("package");
        var file = Path.Combine(package, "package.json");
        WriteHashManifest(package, new PackageHashEntry(file, PackageIntegrity.ComputeSha256(file)));
        Assert.Contains(new PackageIntegrity().VerifyHashManifest(package, "hashes.json"), issue => issue.Severity == "error");
    }

    [Fact]
    public void Hash_manifest_rejects_symbolic_link_entries()
    {
        if (OperatingSystem.IsWindows()) return;
        var package = CreatePackage("package");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(Path.Combine(package, "linked.txt"), outside);
        WriteHashManifest(package, new PackageHashEntry("linked.txt", PackageIntegrity.ComputeSha256(outside)));

        Assert.Contains(new PackageIntegrity().VerifyHashManifest(package, "hashes.json"), issue => issue.Severity == "error");
    }

    [Fact]
    public void Hash_manifest_rejects_symbolic_directory_entries()
    {
        if (OperatingSystem.IsWindows()) return;
        var package = CreatePackage("package");
        var outside = CreatePackage("outside");
        Directory.CreateSymbolicLink(Path.Combine(package, "linked"), outside);
        WriteHashManifest(package, new PackageHashEntry("linked/package.json", PackageIntegrity.ComputeSha256(Path.Combine(outside, "package.json"))));

        Assert.Contains(new PackageIntegrity().VerifyHashManifest(package, "hashes.json"), issue => issue.Severity == "error");
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"Algorithm\":\"sha256\",\"Files\":null}")]
    [InlineData("{\"Algorithm\":\"sha256\",\"Files\":[null]}")]
    [InlineData("{\"Algorithm\":\"sha256\",\"Files\":[{\"Path\":null,\"Sha256\":null}]}")]
    [InlineData("{\"Algorithm\":\"md5\",\"Files\":[]}")]
    public void Malformed_hash_manifests_return_validation_errors(string json)
    {
        var package = CreatePackage("package");
        File.WriteAllText(Path.Combine(package, "hashes.json"), json);
        Assert.Contains(new PackageIntegrity().VerifyHashManifest(package, "hashes.json"), issue => issue.Severity == "error");
    }

    [Fact]
    public void Writing_a_custom_hash_manifest_twice_does_not_hash_itself()
    {
        var package = CreatePackage("package");
        var integrity = new PackageIntegrity();
        integrity.WriteHashManifest(package, "custom-hashes.json");
        integrity.WriteHashManifest(package, "custom-hashes.json");
        Assert.Empty(integrity.VerifyHashManifest(package, "custom-hashes.json"));
    }

    [Theory]
    [InlineData("CUSTOM-HASHES.JSON")]
    [InlineData("CUSTOM-SIGNATURE.JSON")]
    public void Case_ambiguous_custom_metadata_is_rejected_without_writing_or_hiding_payload(string payloadName)
    {
        if (OperatingSystem.IsWindows()) return; // Windows retains its existing case-insensitive path policy.
        var package = CreatePackage("package", "custom-hashes.json", "custom-signature.json");
        File.WriteAllText(Path.Combine(package, payloadName), "ordinary case-distinct payload");
        var originalFiles = Directory.GetFiles(package)
            .ToDictionary(path => path, File.ReadAllBytes, StringComparer.Ordinal);

        var exception = Assert.Throws<InvalidOperationException>(() => new PackageTrustVerifier().WriteLocalSignatureHook(package));

        Assert.Contains("canonical metadata casing", exception.Message, StringComparison.Ordinal);
        Assert.Equal(originalFiles.Keys.OrderBy(path => path, StringComparer.Ordinal),
            Directory.GetFiles(package).OrderBy(path => path, StringComparer.Ordinal));
        foreach (var (path, bytes) in originalFiles)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void Case_ambiguous_metadata_outputs_are_rejected_before_either_is_written()
    {
        if (OperatingSystem.IsWindows()) return; // Windows retains its existing case-insensitive path policy.
        var package = CreatePackage("package", "metadata.json", "METADATA.JSON");
        var definitionPath = Path.Combine(package, "package.json");
        var definition = File.ReadAllBytes(definitionPath);

        var exception = Assert.Throws<InvalidOperationException>(() => new PackageTrustVerifier().WriteLocalSignatureHook(package));

        Assert.Contains("output paths differ only by case", exception.Message, StringComparison.Ordinal);
        Assert.Equal(definitionPath, Assert.Single(Directory.GetFiles(package)));
        Assert.Equal(definition, File.ReadAllBytes(definitionPath));
    }

    [Theory]
    [InlineData("custom-hashes.json", "custom-signature.json")]
    [InlineData("Metadata/CUSTOM-Hashes.json", "Metadata/CUSTOM-Signature.json")]
    public void Re_signing_custom_integrity_paths_keeps_the_package_verifiable(string hashes, string signature)
    {
        var package = CreatePackage("package", hashes, signature);
        var trust = new PackageTrustVerifier();
        trust.WriteLocalSignatureHook(package);
        trust.WriteLocalSignatureHook(package);
        var report = trust.Verify(package, PackageTrustPolicy.StrictSigned);
        Assert.True(report.IsTrusted, string.Join(Environment.NewLine, report.Issues.Select(issue => issue.Message)));
    }

    [Fact]
    public void Trust_rejects_a_symbolic_hash_manifest_path()
    {
        if (OperatingSystem.IsWindows()) return;
        var package = CreatePackage("package", "linked/hashes.json");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "hashes.json"), JsonSerializer.Serialize(new PackageHashManifest("sha256", [])));
        Directory.CreateSymbolicLink(Path.Combine(package, "linked"), outside);
        Assert.False(new PackageTrustVerifier().Verify(package).IsTrusted);
    }

    [Fact]
    public void Valid_nested_hash_entries_and_default_local_signing_still_work()
    {
        var package = CreatePackage("package");
        Directory.CreateDirectory(Path.Combine(package, "nested"));
        File.WriteAllText(Path.Combine(package, "nested", "payload.txt"), "payload");
        var trust = new PackageTrustVerifier();
        trust.WriteLocalSignatureHook(package);
        trust.WriteLocalSignatureHook(package);
        Assert.True(trust.Verify(package, PackageTrustPolicy.StrictSigned).IsTrusted);
        File.WriteAllText(Path.Combine(package, "nested", "payload.txt"), "tampered");
        Assert.False(trust.Verify(package).IsTrusted);
    }

    private string CreatePackage(string name, string? hashes = null, string? signature = null)
    {
        var package = Path.Combine(_root, name);
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "package.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0", id = "sample", displayName = "Sample", version = "1.0.0", modules = Array.Empty<string>(),
            hashes,
            trust = new { policy = "local", signature = new { path = signature } }
        }));
        return package;
    }

    private static void WriteHashManifest(string package, params PackageHashEntry[] entries) =>
        File.WriteAllText(Path.Combine(package, "hashes.json"), JsonSerializer.Serialize(new PackageHashManifest("sha256", entries)));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
