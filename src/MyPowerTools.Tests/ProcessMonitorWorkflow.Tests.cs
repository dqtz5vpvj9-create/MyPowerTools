using System.Diagnostics;
using System.Text.Json.Nodes;
using AndroidTools.MyPowerTools;
using MyPowerTools.Abstractions;
using MptErrorCodes = MyPowerTools.Protocol.MptErrorCodes;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    private static async Task<AndroidToolsProcessMonitorModule> CreateProcessWatchModule(ModuleContext context)
    {
        var module = new AndroidToolsProcessMonitorModule();
        await module.InitializeAsync(context, CancellationToken.None);
        return module;
    }

    private static async Task<JsonObject> ProcessWatchCommand(AndroidToolsProcessMonitorModule module, string command, JsonObject? args = null)
    {
        var result = await module.ExecuteCommandAsync(new CommandRequest(Guid.NewGuid().ToString(), "android-tools.process-monitor." + command, args ?? new JsonObject()), CancellationToken.None);
        Assert.True(result.Success, result.Error?.Message);
        return JsonNode.Parse(result.Output)!.AsObject();
    }

    [Fact]
    public async Task ProcessMonitorWorkflow_save_after_host_settings_updates_active_watch_list()
    {
        var context = CreateModuleContext("android-tools-suite", "android-tools.process-monitor", "pm-save-settings", ["process.monitor"]);
        var module = await CreateProcessWatchModule(context);
        await module.ApplySettingsAsync(new SettingsSnapshotDocument(module.Id, 1, new JsonObject { ["processes"] = new JsonArray("mpt_old") }, DateTimeOffset.UtcNow), CancellationToken.None);
        await ProcessWatchCommand(module, "watch.save", new JsonObject { ["processes"] = new JsonArray("mpt_new") });
        Assert.Equal("mpt_new", (await ProcessWatchCommand(module, "watch.list"))["processes"]![0]!.GetValue<string>());
        Assert.Equal("mpt_new", (await module.GetSettingsAsync(CancellationToken.None)).Values["processes"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task ProcessMonitorWorkflow_clearing_settings_persists_empty_watch_list()
    {
        var context = CreateModuleContext("android-tools-suite", "android-tools.process-monitor", "pm-clear", ["process.monitor"]);
        var module = await CreateProcessWatchModule(context);
        await ProcessWatchCommand(module, "watch.save", new JsonObject { ["processes"] = new JsonArray("mpt_old") });
        await module.ApplySettingsAsync(new SettingsSnapshotDocument(module.Id, 2, new JsonObject { ["processes"] = new JsonArray() }, DateTimeOffset.UtcNow), CancellationToken.None);
        var restored = await CreateProcessWatchModule(context);
        Assert.Empty((await ProcessWatchCommand(restored, "watch.list"))["processes"]!.AsArray());
        Assert.Equal("degraded", (await restored.GetStatusAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task ProcessMonitorWorkflow_save_restore_deduplicates_and_invalid_commands_preserve_state()
    {
        var context = CreateModuleContext("android-tools-suite", "android-tools.process-monitor", "pm-restore", ["process.monitor"]);
        var module = await CreateProcessWatchModule(context);
        await ProcessWatchCommand(module, "watch.save", new JsonObject { ["processes"] = new JsonArray("mpt_test", "MPT_TEST", "", "  ") });
        var restored = await CreateProcessWatchModule(context);
        Assert.Single((await ProcessWatchCommand(restored, "watch.list"))["processes"]!.AsArray());
        var invalid = await restored.ExecuteCommandAsync(new CommandRequest("invalid", "android-tools.process-monitor.watch.save", new JsonObject { ["processes"] = new JsonArray() }), CancellationToken.None);
        Assert.Equal(MptErrorCodes.ValidationFailed, invalid.Error!.Code);
        var unknown = await restored.ExecuteCommandAsync(new CommandRequest("unknown", "android-tools.process-monitor.unknown", new JsonObject()), CancellationToken.None);
        Assert.Equal(MptErrorCodes.NotFound, unknown.Error!.Code);
        Assert.Single((await ProcessWatchCommand(restored, "watch.list"))["processes"]!.AsArray());
        Assert.Equal(3, (await restored.ListCommandsAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ProcessMonitorWorkflow_scan_tracks_owned_child_process_start_and_exit()
    {
        var context = CreateModuleContext("android-tools-suite", "android-tools.process-monitor", "pm-owned-process", ["process.monitor"]);
        var module = await CreateProcessWatchModule(context);
        var directory = Path.GetDirectoryName(context.DataDirectory)!;
        Directory.CreateDirectory(directory);
        var name = "mptpm_" + Guid.NewGuid().ToString("N")[..12];
        var executable = Path.Combine(directory, name + ".exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), executable);
        await ProcessWatchCommand(module, "watch.save", new JsonObject { ["processes"] = new JsonArray(name + ".exe") });
        Assert.False((await ProcessWatchCommand(module, "status.summary"))["states"]![0]!["running"]!.GetValue<bool>());
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("127.0.0.1");
        using var child = Process.Start(start)!;
        try
        {
            var state = (await ProcessWatchCommand(module, "status.summary"))["states"]![0]!;
            Assert.True(state["running"]!.GetValue<bool>());
            Assert.Equal(1, state["instanceCount"]!.GetValue<int>());
            await module.ApplySettingsAsync(new SettingsSnapshotDocument(module.Id, 1, new JsonObject { ["processes"] = new JsonArray(name + ".exe"), ["scanIntervalSeconds"] = 5 }, DateTimeOffset.UtcNow), CancellationToken.None);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var events = module.SubscribeEventsAsync(new EventCursor(0), deadline.Token).GetAsyncEnumerator();
            Assert.True(await events.MoveNextAsync());
            Assert.Equal("process.started", events.Current.Type);
            Assert.Equal(1, events.Current.Payload["runningCount"]!.GetValue<int>());
            child.Kill();
            await child.WaitForExitAsync(deadline.Token);
            Assert.False((await ProcessWatchCommand(module, "status.summary"))["states"]![0]!["running"]!.GetValue<bool>());
            Assert.True(await events.MoveNextAsync());
            Assert.Equal("watch.alert", events.Current.Type);
            Assert.Equal(0, events.Current.Payload["runningCount"]!.GetValue<int>());
            Assert.Equal(2UL, events.Current.Seq);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); }
            File.Delete(executable);
        }
    }
}
