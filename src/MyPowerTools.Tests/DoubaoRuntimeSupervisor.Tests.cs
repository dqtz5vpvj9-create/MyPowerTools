using System.Text.Json;
using MyPowerTools.Runner;

namespace MyPowerTools.Tests;

public sealed class DoubaoRuntimeSupervisorTests
{
    [Fact]
    public void Auto_start_setting_defaults_to_enabled_and_reads_explicit_false()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsPath = Path.Combine(root, "Doubao", "settings.json");
            Assert.True(DoubaoRuntimeSupervisor.ReadAutoStartEnabled(settingsPath));

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, "{\"AutoStartEnabled\":false}");

            Assert.False(DoubaoRuntimeSupervisor.ReadAutoStartEnabled(settingsPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Offline_services_start_once_and_online_services_are_left_running()
    {
        var root = CreateTemporaryDirectory();
        var dataRoot = Path.Combine(root, "data");
        try
        {
            CreateInstalledLayout(root);
            var online = false;
            var starts = 0;
            await using var supervisor = new DoubaoRuntimeSupervisor(
                root,
                dataRoot,
                portProbe: (_, _) => Task.FromResult(online),
                runtimeStarter: _ =>
                {
                    starts++;
                    online = true;
                    return Task.FromResult(0);
                });

            var first = await supervisor.RunCycleAsync();
            var second = await supervisor.RunCycleAsync();

            Assert.True(first.AttemptedStart);
            Assert.True(first.AllServicesOnline);
            Assert.Equal("recovered", first.State);
            Assert.Equal("online", second.State);
            Assert.False(second.AttemptedStart);
            Assert.Equal(1, starts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Disabled_setting_prevents_runtime_start_and_stale_manifest_is_detected()
    {
        var root = CreateTemporaryDirectory();
        var dataRoot = Path.Combine(root, "data");
        try
        {
            CreateInstalledLayout(root);
            var doubaoRoot = Path.Combine(dataRoot, "Doubao");
            Directory.CreateDirectory(Path.Combine(doubaoRoot, "logs"));
            File.WriteAllText(
                Path.Combine(doubaoRoot, "settings.json"),
                JsonSerializer.Serialize(new { AutoStartEnabled = false }));
            var manifestPath = Path.Combine(doubaoRoot, "logs", "mypowertools-secure-runtime.json");
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(new { Processes = new[] { new { ProcessId = 424242 } } }));
            var starts = 0;
            await using var supervisor = new DoubaoRuntimeSupervisor(
                root,
                dataRoot,
                portProbe: (_, _) => Task.FromResult(false),
                runtimeStarter: _ =>
                {
                    starts++;
                    return Task.FromResult(0);
                });

            var result = await supervisor.RunCycleAsync();

            Assert.Equal("disabled", result.State);
            Assert.False(result.AttemptedStart);
            Assert.Equal(0, starts);
            Assert.True(DoubaoRuntimeSupervisor.IsManifestStale(manifestPath, _ => false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_recovery_is_bounded_to_three_attempts_before_cooldown()
    {
        var root = CreateTemporaryDirectory();
        var dataRoot = Path.Combine(root, "data");
        try
        {
            CreateInstalledLayout(root);
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
            var starts = 0;
            await using var supervisor = new DoubaoRuntimeSupervisor(
                root,
                dataRoot,
                portProbe: (_, _) => Task.FromResult(false),
                runtimeStarter: _ =>
                {
                    starts++;
                    return Task.FromResult(5);
                },
                timeProvider: clock);

            await supervisor.RunCycleAsync();
            clock.Advance(TimeSpan.FromSeconds(20));
            await supervisor.RunCycleAsync();
            clock.Advance(TimeSpan.FromSeconds(31));
            await supervisor.RunCycleAsync();
            clock.Advance(TimeSpan.FromMinutes(1));
            var cooledDown = await supervisor.RunCycleAsync();

            Assert.Equal(3, starts);
            Assert.Equal("backoff", cooledDown.State);
            Assert.False(cooledDown.AttemptedStart);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-doubao-supervisor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateInstalledLayout(string root)
    {
        var shellDirectory = Path.Combine(root, "Shell");
        Directory.CreateDirectory(shellDirectory);
        File.WriteAllText(Path.Combine(shellDirectory, "MyPowerTools.Shell.Avalonia.exe"), "test");
        Directory.CreateDirectory(Path.Combine(root, "Runtimes", "Doubao"));
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
