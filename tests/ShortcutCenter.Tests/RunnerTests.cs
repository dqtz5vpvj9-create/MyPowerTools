using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Abstractions;
using MyPowerTools.Packaging;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Runner;
using MyPowerTools.Runtime;
using Sdk = MyPowerTools.Abstractions;

namespace ShortcutCenter.Tests;

public sealed class RunnerTests
{
    [Fact]
    public async Task Failed_rebind_retains_the_original_system_gesture_and_command()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = new MptHostRuntime(new PackageReader(), new PlatformId("windows", "x64"), RuntimePaths.Create(temp.Path));
        await using var service = new FakeHotkeys();
        var sync = new RunnerHotkeySynchronizer(service, runtime);
        runtime.ConfigureShortcutSynchronization(async token => { await sync.SyncAsync(token); }, "Test platform");
        await sync.SyncAsync(default);
        service.RejectGesture = "Ctrl+Alt+K";
        await Edit(runtime, [new("runner.command-palette", [new("Ctrl+Alt+K")])]);
        var registration = runtime.GetShortcutCatalog().Registrations.Single();
        Assert.Equal("kept-previous", registration.State);
        Assert.Equal("Ctrl+Alt+Space", registration.ActualGesture);
        var active = service.Registered.Single();
        Assert.Equal("shell.command-palette.open", sync.CreateCommandRequest(new(active.Key, active.Value, "system", DateTimeOffset.UtcNow))!.CommandId);
    }

    [Fact]
    public async Task Successful_rebind_then_disable_releases_old_and_new_native_registrations()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = new MptHostRuntime(new PackageReader(), new PlatformId("windows", "x64"), RuntimePaths.Create(temp.Path));
        await using var service = new FakeHotkeys(); var sync = new RunnerHotkeySynchronizer(service, runtime);
        runtime.ConfigureShortcutSynchronization(async token => { await sync.SyncAsync(token); }, "Test platform");
        await sync.SyncAsync(default);
        await Edit(runtime, [new("runner.command-palette", [new("Ctrl+Alt+K"), new("Ctrl+Alt+J")])]);
        Assert.Equal(2, service.Registered.Count);
        Assert.DoesNotContain("Ctrl+Alt+Space", service.Registered.Values);
        await Edit(runtime, [new("runner.command-palette", [new("Ctrl+Alt+K")], true)]);
        Assert.Empty(service.Registered);
        Assert.Empty(runtime.GetShortcutCatalog().Registrations);
    }

    [Fact]
    public async Task System_shortcut_configuration_is_returned_over_existing_settings_contract()
    {
        using var temp = new TemporaryDirectory();
        await using var runtime = new MptHostRuntime(new PackageReader(), new PlatformId("linux", "x64"), RuntimePaths.Create(temp.Path));
        await Edit(runtime, [new("shell.refresh", [new("F8")])]);
        var snapshot = runtime.GetSettings(ShortcutCatalog.SettingsModuleId);
        Assert.Equal(1ul, snapshot.Revision);
        var catalog = snapshot.Values.Deserialize<ShortcutCatalogSnapshot>(ShortcutCatalog.JsonOptions)!;
        Assert.Equal("F8", ShortcutCatalog.Effective(catalog).Single(item => item.Definition.Id == "shell.refresh").Gesture);
    }

    private static Task<SettingsUpdateResult> Edit(MptHostRuntime runtime, IReadOnlyList<ShortcutEdit> edits) =>
        runtime.UpdateSettingsWithApplyAsync(new Sdk.SettingsPatch(ShortcutCatalog.SettingsModuleId,
            runtime.GetShortcutCatalog().Configuration.Revision,
            new JsonObject { ["edits"] = JsonSerializer.SerializeToNode(edits, ShortcutCatalog.JsonOptions) }), default);

    private sealed class FakeHotkeys : IHotkeyService
    {
        public Dictionary<string, string> Registered { get; } = [];
        public string RejectGesture { get; set; } = "";
        public event EventHandler<HotkeyInvocation>? Pressed { add { } remove { } }
        public Task<HotkeyRegistrationResult> RegisterAsync(HotkeyRegistration registration, CancellationToken token)
        {
            if (registration.Gesture == RejectGesture || Registered.ContainsValue(registration.Gesture))
                return Task.FromResult(new HotkeyRegistrationResult(false, "conflict", "The OS rejected this key."));
            Registered.Add(registration.Id, registration.Gesture);
            return Task.FromResult(new HotkeyRegistrationResult(true, "registered", "Registered"));
        }
        public Task<HotkeyRegistrationResult> UnregisterAsync(string id, CancellationToken token) =>
            Task.FromResult(new HotkeyRegistrationResult(Registered.Remove(id), "removed", "Removed"));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
