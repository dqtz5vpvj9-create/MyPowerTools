using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyPowerTools.Abstractions;
using MyPowerTools.Broker;
using MyPowerTools.ServiceManager.Server;

namespace MyPowerTools.Tests;

/// <summary>
/// Command-line composition for units launched through CreateProcessW. The child re-parses the
/// single command-line string with the CRT rules, so a token that is quoted the naive way loses
/// its trailing backslash and swallows the argument behind it.
/// </summary>
public sealed class BreakawayProcessQuotingTests
{
    [Theory]
    // No whitespace and no quote: the token is passed through untouched, backslashes included.
    [InlineData("--pipe-only", "--pipe-only")]
    [InlineData(@"C:\Tools\App.exe", @"C:\Tools\App.exe")]
    // An empty argument still has to occupy a slot in argv.
    [InlineData("", @"""""")]
    [InlineData("has space", @"""has space""")]
    [InlineData("has\ttab", "\"has\ttab\"")]
    // The backslashes closing a quoted directory path are doubled so the quote survives.
    [InlineData(@"C:\Program Files\App\", @"""C:\Program Files\App\\""")]
    [InlineData(@"C:\Program Files\App\\", @"""C:\Program Files\App\\\\""")]
    // An embedded quote becomes \", and the run in front of it doubles first.
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    [InlineData(@"a\""b", @"""a\\\""b""")]
    public void Arguments_follow_the_runtime_quoting_rules(string value, string expected)
    {
        Assert.Equal(expected, BreakawayProcessStarter.QuoteIfNeeded(value));
    }
}

/// <summary>
/// Everything process discovery returns is a candidate for Kill(entireProcessTree), so a manifest
/// that cannot be matched precisely must yield nothing at all rather than every process on the
/// machine that happens to share the unit's file name.
/// </summary>
public sealed class UnitProcessDiscoveryTests
{
    [Fact]
    public void A_manifest_without_a_rooted_executable_matches_nothing()
    {
        using var current = Process.GetCurrentProcess();
        var manifest = new ServiceUnitManifest(
            Id: "sample.service",
            ToolId: "sample",
            DisplayName: "Sample Service",
            Exec: "dotnet",
            Arguments: Array.Empty<string>());

        Assert.Empty(UnitProcessDiscovery.FindMatching(manifest));
        Assert.False(UnitProcessDiscovery.Matches(current, manifest));
    }

    [Fact]
    public void This_process_matches_a_manifest_describing_it()
    {
        using var current = Process.GetCurrentProcess();
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.True(UnitProcessDiscovery.Matches(current, SelfManifest(dataRoot: null)));
    }

    [Fact]
    public void A_process_carrying_a_different_data_root_is_never_a_match()
    {
        using var current = Process.GetCurrentProcess();
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.False(UnitProcessDiscovery.Matches(current, SelfManifest("/nowhere/another-installation")));
    }

    /// <summary>A manifest that describes the running test host exactly, optionally pinned to a data root.</summary>
    private static ServiceUnitManifest SelfManifest(string? dataRoot) => new(
        Id: "self.service",
        ToolId: "sample",
        DisplayName: "Self",
        Exec: File.ResolveLinkTarget("/proc/self/exe", returnFinalTarget: true)!.FullName,
        Arguments: SelfArguments(),
        Environment: dataRoot is null
            ? null
            : new Dictionary<string, string> { ["MPT_DATA_ROOT"] = dataRoot });

    private static IReadOnlyList<string> SelfArguments() => Encoding.UTF8
        .GetString(File.ReadAllBytes("/proc/self/cmdline"))
        .TrimEnd('\0')
        .Split('\0')
        .Skip(1)
        .ToArray();
}

/// <summary>
/// Manifests are rewritten in place by the installer while the ServiceManager reloads them, so a
/// reload can catch a file half-written. The engine reads "absent from the reloaded catalog" as
/// "uninstalled" and force-stops the unit, which makes a torn read destructive.
/// </summary>
public sealed class ServiceUnitCatalogReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mypowertools-unit-catalog-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void An_unparseable_manifest_keeps_the_definition_loaded_before_it()
    {
        var path = WriteManifest("sample.service", "/opt/sample/sample");
        var catalog = new ServiceUnitCatalog(_root);
        Assert.Equal(1, catalog.Reload());

        File.WriteAllText(path, "{ \"id\": \"sample.service\", \"exec\": \"/opt/sam");

        Assert.Equal(1, catalog.Reload());
        var manifest = catalog.TryGet("sample.service");
        Assert.NotNull(manifest);
        Assert.Equal("/opt/sample/sample", manifest!.Exec);
    }

    [Fact]
    public void A_deleted_manifest_leaves_the_catalog()
    {
        var path = WriteManifest("sample.service", "/opt/sample/sample");
        var catalog = new ServiceUnitCatalog(_root);
        Assert.Equal(1, catalog.Reload());

        File.Delete(path);

        Assert.Equal(0, catalog.Reload());
        Assert.Null(catalog.TryGet("sample.service"));
    }

    [Fact]
    public void A_repaired_manifest_replaces_the_definition_that_was_kept()
    {
        var path = WriteManifest("sample.service", "/opt/sample/sample");
        var catalog = new ServiceUnitCatalog(_root);
        catalog.Reload();

        File.WriteAllText(path, "not json at all");
        catalog.Reload();
        File.WriteAllText(path, ManifestJson("sample.service", "/opt/sample/sample-v2"));

        Assert.Equal(1, catalog.Reload());
        Assert.Equal("/opt/sample/sample-v2", catalog.TryGet("sample.service")!.Exec);
    }

    private string WriteManifest(string unitId, string exec)
    {
        var units = Directory.CreateDirectory(Path.Combine(_root, "units")).FullName;
        var path = Path.Combine(units, $"{unitId}.json");
        File.WriteAllText(path, ManifestJson(unitId, exec));
        return path;
    }

    private static string ManifestJson(string unitId, string exec) => JsonSerializer.Serialize(new
    {
        id = unitId,
        toolId = "sample",
        displayName = "Sample Service",
        exec,
        arguments = Array.Empty<string>(),
        autostart = false
    });

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

/// <summary>
/// The elevated broker materializes the shared NSSM host into an ACL-protected directory. A
/// running managed service holds that executable as its image, so an unconditional replace fails
/// with a sharing violation and blocks every later install or migration.
/// </summary>
public sealed class ProtectedFileStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mypowertools-protected-staging-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Identical_content_needs_no_replacement()
    {
        var payload = new byte[] { 0x4D, 0x5A, 0x90, 0x00 };
        var destination = Write("nssm-manager.exe", payload);

        Assert.True(ProtectedFileStaging.AlreadyMatches(destination, SHA256.HashData(payload)));
    }

    [Fact]
    public void Different_content_is_staged_and_replaced()
    {
        var destination = Write("nssm-manager.exe", [0x4D, 0x5A, 0x90, 0x00]);

        Assert.False(ProtectedFileStaging.AlreadyMatches(destination, SHA256.HashData(new byte[] { 0x4D, 0x5A, 0x90, 0x01 })));
    }

    [Fact]
    public void A_missing_destination_is_staged_and_replaced()
    {
        Directory.CreateDirectory(_root);
        var destination = Path.Combine(_root, "nssm-manager.exe");

        Assert.False(ProtectedFileStaging.AlreadyMatches(destination, SHA256.HashData([0x4D, 0x5A])));
    }

    private string Write(string name, byte[] content)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}

/// <summary>
/// Restart accounting. Without a start-limit window the restart count only ever grows, so a unit
/// that crashed <c>maxRestarts</c> times across its whole lifetime is never restarted again.
/// </summary>
public sealed class UnitSupervisorRestartPolicyTests
{
    [Fact]
    public void A_short_backoff_still_gets_the_minimum_healthy_window()
    {
        var policy = new ServiceUnitRestartPolicy(3, TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(60), UnitSupervisor.HealthyWindowFor(policy));
    }

    [Fact]
    public void A_long_backoff_scales_the_healthy_window_with_it()
    {
        var policy = new ServiceUnitRestartPolicy(3, TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromMinutes(5), UnitSupervisor.HealthyWindowFor(policy));
    }

    [Fact]
    public void A_policy_without_backoff_still_defines_a_healthy_window()
    {
        var policy = new ServiceUnitRestartPolicy(3, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromSeconds(60), UnitSupervisor.HealthyWindowFor(policy));
    }

    [Fact]
    public void Backoff_doubles_per_attempt_and_stops_at_the_ceiling()
    {
        var policy = new ServiceUnitRestartPolicy(10, TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(2), UnitSupervisor.BackoffFor(policy, 0));
        Assert.Equal(TimeSpan.FromSeconds(4), UnitSupervisor.BackoffFor(policy, 1));
        Assert.Equal(TimeSpan.FromSeconds(16), UnitSupervisor.BackoffFor(policy, 3));
        Assert.Equal(TimeSpan.FromMinutes(2), UnitSupervisor.BackoffFor(policy, 20));
    }

    [Fact]
    public void A_policy_without_backoff_restarts_immediately()
    {
        var policy = new ServiceUnitRestartPolicy(3, TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, UnitSupervisor.BackoffFor(policy, 4));
    }
}

/// <summary>
/// Autostart ordering and adoption of newly declared units.
/// </summary>
public sealed class ServiceManagerEngineOrderingTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mypowertools-engine-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Units_start_after_the_units_they_depend_on()
    {
        var ordered = ServiceManagerEngine.OrderByDependencies([
            Manifest("c.service", "b.service"),
            Manifest("a.service"),
            Manifest("b.service", "a.service")
        ]);

        Assert.Equal(["a.service", "b.service", "c.service"], ordered.Select(item => item.Id));
    }

    [Fact]
    public void Independent_units_keep_a_stable_order()
    {
        var ordered = ServiceManagerEngine.OrderByDependencies([
            Manifest("z.service"),
            Manifest("m.service"),
            Manifest("a.service")
        ]);

        Assert.Equal(["a.service", "m.service", "z.service"], ordered.Select(item => item.Id));
    }

    [Fact]
    public void A_dependency_that_is_not_installed_is_skipped()
    {
        var ordered = ServiceManagerEngine.OrderByDependencies([
            Manifest("a.service", "absent.service")
        ]);

        Assert.Equal(["a.service"], ordered.Select(item => item.Id));
    }

    [Fact]
    public void A_dependency_cycle_still_yields_every_unit_exactly_once()
    {
        var ordered = ServiceManagerEngine.OrderByDependencies([
            Manifest("x.service", "y.service"),
            Manifest("y.service", "x.service")
        ]);

        Assert.Equal(["x.service", "y.service"], ordered.Select(item => item.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task A_reload_autostarts_a_unit_it_has_just_discovered()
    {
        var sleep = new[] { "/usr/bin/sleep", "/bin/sleep" }.FirstOrDefault(File.Exists);
        if (sleep is null)
        {
            return;
        }

        var deployRoot = Directory.CreateDirectory(Path.Combine(_root, "deploy")).FullName;
        var stateRoot = Directory.CreateDirectory(Path.Combine(_root, "state")).FullName;
        await using var engine = new ServiceManagerEngine(
            new ServiceUnitCatalog(deployRoot),
            new UnitEventBus(),
            new UnitStateStore(stateRoot));

        Assert.Equal(0, await engine.ReloadAsync());

        var units = Directory.CreateDirectory(Path.Combine(deployRoot, "units")).FullName;
        File.WriteAllText(Path.Combine(units, "sleeper.service.json"), JsonSerializer.Serialize(new
        {
            id = "sleeper.service",
            toolId = "sample",
            displayName = "Sleeper",
            exec = sleep,
            arguments = new[] { "45" },
            autostart = true
        }));

        Assert.Equal(1, await engine.ReloadAsync());
        try
        {
            var snapshot = Assert.Single(engine.List());
            Assert.Equal(ServiceUnitState.Active, snapshot.State);
            Assert.NotNull(snapshot.Pid);
        }
        finally
        {
            await engine.StopAsync("sleeper.service");
        }
    }

    private static ServiceUnitManifest Manifest(string unitId, params string[] dependsOn) => new(
        Id: unitId,
        ToolId: "sample",
        DisplayName: unitId,
        Exec: $"/opt/sample/{unitId}",
        Arguments: Array.Empty<string>(),
        DependsOn: dependsOn);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
