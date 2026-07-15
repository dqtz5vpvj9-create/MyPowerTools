using System.Text.Json.Nodes;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Protocol;
using MyPowerTools.Runtime;
using ScreenEase.MyPowerTools;
using CommandRequest = MyPowerTools.Abstractions.CommandRequest;

namespace MyPowerTools.Tests;

public sealed partial class RuntimeAcceptanceTests
{
    [Fact]
    public void ScreenEase_gamma_ramp_matches_the_original_driver_math()
    {
        var full = ScreenEaseWindowsGammaDisplayService.BuildGammaRamp(6500, 100);
        var half = ScreenEaseWindowsGammaDisplayService.BuildGammaRamp(6500, 50);
        var warm = ScreenEaseWindowsGammaDisplayService.BuildGammaRamp(3700, 100);
        var identity = ScreenEaseWindowsGammaDisplayService.BuildIdentityRamp();

        Assert.Equal(255 * 255, full.Red[255]);
        Assert.Equal(128 * 255, half.Red[255]);
        Assert.True(warm.Blue[255] < full.Blue[255]);
        Assert.Equal(0, identity.Red[0]);
        Assert.Equal(257 * 128, identity.Red[128]);
        Assert.Equal(ushort.MaxValue, identity.Red[255]);
    }

    [Fact]
    public async Task ScreenEase_rest_timer_state_survives_module_recreation_without_display_writes()
    {
        var display = new RecordingDisplayService();
        var context = CreateScreenEaseContext("screenease-persisted-rest-timer", display);
        var first = new ScreenEaseModule(display);
        await first.InitializeAsync(context, CancellationToken.None);

        var configure = await first.ExecuteCommandAsync(
            new CommandRequest("timer-configure", "screenease.reminder.configure", new JsonObject
            {
                ["enabled"] = true,
                ["autoStartNext"] = false,
                ["focusMinutes"] = 25,
                ["shortBreakMinutes"] = 5,
                ["longBreakMinutes"] = 15,
                ["longBreakInterval"] = 4
            }),
            CancellationToken.None);
        var started = await first.ExecuteCommandAsync(
            new CommandRequest("timer-start", "screenease.reminder.start", new JsonObject()),
            CancellationToken.None);

        var second = new ScreenEaseModule(display);
        await second.InitializeAsync(context, CancellationToken.None);
        var restored = await second.ExecuteCommandAsync(
            new CommandRequest("timer-restored", "screenease.reminder.status", new JsonObject()),
            CancellationToken.None);
        var restoredState = JsonNode.Parse(restored.Output)!.AsObject();

        Assert.True(configure.Success);
        Assert.True(started.Success);
        Assert.True(restored.Success);
        Assert.Equal("work", restoredState["phase"]!.GetValue<string>());
        Assert.True(restoredState["remainingSeconds"]!.GetValue<int>() > 0);
        Assert.Empty(display.AppliedIntents);
    }

    [Fact]
    public async Task ScreenEase_pause_resume_and_reset_are_persisted_module_actions()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-rest-timer-actions", display), CancellationToken.None);
        await module.ExecuteCommandAsync(
            new CommandRequest("timer-configure", "screenease.reminder.configure", new JsonObject
            {
                ["enabled"] = true,
                ["focusMinutes"] = 25,
                ["shortBreakMinutes"] = 5,
                ["longBreakMinutes"] = 15,
                ["longBreakInterval"] = 4
            }),
            CancellationToken.None);
        await module.ExecuteCommandAsync(
            new CommandRequest("timer-start", "screenease.reminder.start", new JsonObject()),
            CancellationToken.None);

        var paused = await module.ExecuteCommandAsync(
            new CommandRequest("timer-pause", "screenease.reminder.pause", new JsonObject()),
            CancellationToken.None);
        var resumed = await module.ExecuteCommandAsync(
            new CommandRequest("timer-resume", "screenease.reminder.resume", new JsonObject()),
            CancellationToken.None);
        var reset = await module.ExecuteCommandAsync(
            new CommandRequest("timer-reset", "screenease.reminder.reset", new JsonObject()),
            CancellationToken.None);

        Assert.Equal("paused", JsonNode.Parse(paused.Output)!["phase"]!.GetValue<string>());
        Assert.Equal("work", JsonNode.Parse(resumed.Output)!["phase"]!.GetValue<string>());
        Assert.Equal("stopped", JsonNode.Parse(reset.Output)!["phase"]!.GetValue<string>());
        Assert.Empty(display.AppliedIntents);
    }

    [Fact]
    public async Task ScreenEase_schedule_selects_the_original_night_profile_values()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-night-values", display), CancellationToken.None);
        var now = DateTimeOffset.Now;
        var sunrise = now.AddHours(1).ToString("HH:mm");
        var sunset = now.AddHours(-1).ToString("HH:mm");

        await module.ExecuteCommandAsync(
            new CommandRequest("schedule", "screenease.schedule.configure", new JsonObject
            {
                ["useNightValues"] = true,
                ["useSchedule"] = true,
                ["sunrise"] = sunrise,
                ["sunset"] = sunset
            }),
            CancellationToken.None);
        await module.ExecuteCommandAsync(
            new CommandRequest("profile", "screenease.profile.save", new JsonObject
            {
                ["id"] = "parity",
                ["name"] = "Parity",
                ["brightness"] = 91,
                ["colorTemperature"] = 6100,
                ["nightBrightness"] = 37,
                ["nightColorTemperature"] = 3300
            }),
            CancellationToken.None);
        var applied = await module.ExecuteCommandAsync(
            new CommandRequest("apply", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "parity",
                ["hardwareWrite"] = true
            }),
            CancellationToken.None);
        var intent = Assert.Single(display.AppliedIntents);

        Assert.True(applied.Success);
        Assert.Equal(37, intent.Brightness);
        Assert.Equal(3300, intent.ColorTemperature);
        Assert.True(JsonNode.Parse(applied.Output)!["isNightValue"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ScreenEase_logical_effect_applies_without_display_hardware_and_survives_recreation()
    {
        var display = new RecordingDisplayService();
        var context = CreateScreenEaseContext("screenease-logical-effect", display);
        var first = new ScreenEaseModule(display);
        await first.InitializeAsync(context, CancellationToken.None);

        var applied = await first.ExecuteCommandAsync(
            new CommandRequest("logical-apply", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "reading",
                ["hardwareWrite"] = false
            }),
            CancellationToken.None);
        var appliedEffect = JsonNode.Parse(applied.Output)!["effect"]!.AsObject();

        var second = new ScreenEaseModule(display);
        await second.InitializeAsync(context, CancellationToken.None);
        var restored = await second.ExecuteCommandAsync(
            new CommandRequest("effect-status", "screenease.effect.status", new JsonObject()),
            CancellationToken.None);
        var restoredEffect = JsonNode.Parse(restored.Output)!.AsObject();

        Assert.True(applied.Success);
        Assert.True(appliedEffect["enabled"]!.GetValue<bool>());
        Assert.Equal("long-read", restoredEffect["profileId"]!.GetValue<string>());
        Assert.True(restoredEffect["enabled"]!.GetValue<bool>());
        Assert.Equal(5000, restoredEffect["colorTemperatureKelvin"]!.GetValue<int>());
        Assert.Equal(85, restoredEffect["brightnessPercent"]!.GetValue<int>());
        var restoredIntent = Assert.Single(display.AppliedIntents);
        Assert.Equal("long-read", restoredIntent.ProfileId);
    }

    [Fact]
    public async Task ScreenEase_disable_changes_only_the_logical_effect()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-logical-disable", display), CancellationToken.None);
        await module.ExecuteCommandAsync(
            new CommandRequest("logical-apply", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "night",
                ["hardwareWrite"] = false
            }),
            CancellationToken.None);

        var disabled = await module.ExecuteCommandAsync(
            new CommandRequest("logical-disable", "screenease.effect.disable", new JsonObject()),
            CancellationToken.None);
        var effect = JsonNode.Parse(disabled.Output)!.AsObject();

        Assert.True(disabled.Success);
        Assert.False(effect["enabled"]!.GetValue<bool>());
        Assert.Equal("low-blue-evening", effect["profileId"]!.GetValue<string>());
        Assert.Empty(display.AppliedIntents);
    }

    [Fact]
    public async Task ScreenEase_manual_apply_creates_the_original_manual_effect_without_overwriting_a_saved_profile()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-manual-effect", display), CancellationToken.None);

        var applied = await module.ExecuteCommandAsync(
            new CommandRequest("manual-apply", "screenease.effect.apply", new JsonObject
            {
                ["colorTemperatureKelvin"] = 4300,
                ["brightnessPercent"] = 72,
                ["hardwareWrite"] = false
            }),
            CancellationToken.None);
        var effect = JsonNode.Parse(applied.Output)!["effect"]!.AsObject();
        var listed = await module.ExecuteCommandAsync(
            new CommandRequest("profiles", "screenease.profile.list", new JsonObject()),
            CancellationToken.None);
        var profiles = JsonNode.Parse(listed.Output)!["profiles"]!.AsArray();
        var reading = Assert.Single(profiles, profile => profile!["id"]!.GetValue<string>() == "long-read")!.AsObject();
        var manual = Assert.Single(profiles, profile => profile!["id"]!.GetValue<string>() == "manual-adjustment")!.AsObject();

        Assert.True(applied.Success);
        Assert.Equal("manual-adjustment", effect["profileId"]!.GetValue<string>());
        Assert.Equal(4300, effect["colorTemperatureKelvin"]!.GetValue<int>());
        Assert.Equal(72, effect["brightnessPercent"]!.GetValue<int>());
        Assert.Equal(5000, reading["colorTemperature"]!.GetValue<int>());
        Assert.Equal(4300, manual["colorTemperature"]!.GetValue<int>());
        Assert.Empty(display.AppliedIntents);
    }

    [Fact]
    public async Task ScreenEase_new_install_uses_the_original_profile_ids_order_and_default_effect()
    {
        var module = new ScreenEaseModule(new RecordingDisplayService());
        await module.InitializeAsync(CreateScreenEaseContext("screenease-source-defaults"), CancellationToken.None);

        var listed = await module.ExecuteCommandAsync(
            new CommandRequest("profiles", "screenease.profile.list", new JsonObject()),
            CancellationToken.None);
        var effectResult = await module.ExecuteCommandAsync(
            new CommandRequest("effect", "screenease.effect.status", new JsonObject()),
            CancellationToken.None);
        var listPayload = JsonNode.Parse(listed.Output)!.AsObject();
        var ids = listPayload["profiles"]!.AsArray()
            .Select(profile => profile!["id"]!.GetValue<string>())
            .ToArray();
        var effect = JsonNode.Parse(effectResult.Output)!.AsObject();

        Assert.Equal(
            ["day-office", "long-read", "detail-work", "warm-video", "bright-focus", "low-blue-evening", "personal"],
            ids);
        Assert.Equal("low-blue-evening", listPayload["activeProfileId"]!.GetValue<string>());
        Assert.False(effect["enabled"]!.GetValue<bool>());
        Assert.Equal("low-blue-evening", effect["profileId"]!.GetValue<string>());
        Assert.Equal(3700, effect["colorTemperatureKelvin"]!.GetValue<int>());
        Assert.Equal(75, effect["brightnessPercent"]!.GetValue<int>());
    }

    [Fact]
    public async Task ScreenEase_settings_schema_describes_all_eight_source_hotkeys()
    {
        var module = new ScreenEaseModule(new RecordingDisplayService());
        var schema = await module.GetSettingsSchemaAsync(CancellationToken.None);
        var root = JsonNode.Parse(schema.SchemaJson)!.AsObject();
        var hotkeys = root["properties"]!["hotkeys"]!.AsObject();
        var ids = hotkeys["items"]!["properties"]!["id"]!["enum"]!.AsArray()
            .Select(item => item!.GetValue<string>())
            .ToArray();

        Assert.Equal(
            [
                "toggle-enabled",
                "brightness-up",
                "brightness-down",
                "temperature-up",
                "temperature-down",
                "long-read-profile",
                "low-blue-evening-profile",
                "toggle-overlay"
            ],
            ids);
        Assert.Contains("gesture", hotkeys["items"]!["required"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.False(hotkeys["items"]!["properties"]!["enabled"]!["default"]!.GetValue<bool>());
    }

    [Fact]
    public void ScreenEase_imports_the_original_settings_contract_without_losing_manual_profiles()
    {
        var imported = ScreenEaseLegacySettingsImporter.Import(new JsonObject
        {
            ["enabled"] = true,
            ["activeProfileId"] = "long-read",
            ["useNightValues"] = true,
            ["useSchedule"] = false,
            ["smoothTransitions"] = false,
            ["transitionDuration"] = "00:01:03",
            ["hotkeys"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "renamed-by-user",
                    ["action"] = "ToggleEnabled",
                    ["gesture"] = "Ctrl+Alt+F10",
                    ["enabled"] = true
                },
                new JsonObject
                {
                    ["id"] = "another-renamed-binding",
                    ["action"] = 7,
                    ["gesture"] = "Ctrl+Alt+F8",
                    ["enabled"] = true
                }
            },
            ["profiles"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "long-read",
                    ["name"] = "长读柔光",
                    ["brightnessPercent"] = 85,
                    ["colorTemperatureKelvin"] = 5000,
                    ["nightBrightnessPercent"] = 75,
                    ["nightColorTemperatureKelvin"] = 4200
                },
                new JsonObject
                {
                    ["id"] = "manual-adjustment",
                    ["name"] = "自定义调节",
                    ["brightnessPercent"] = 100,
                    ["colorTemperatureKelvin"] = 5000
                },
                new JsonObject
                {
                    ["id"] = "personal",
                    ["name"] = "我的方案",
                    ["brightnessPercent"] = 100,
                    ["colorTemperatureKelvin"] = 5500
                }
            },
            ["restTimer"] = new JsonObject
            {
                ["enabled"] = true,
                ["autoStart"] = true,
                ["workMinutes"] = 240,
                ["shortBreakMinutes"] = 120,
                ["longBreakMinutes"] = 240,
                ["longBreakEveryWorkSessions"] = 12
            }
        });

        Assert.Equal("long-read", imported.ActiveProfileId);
        Assert.True(imported.GetEffect().Enabled);
        Assert.Equal(5500, imported.FindProfile("personal")!.ColorTemperature);
        Assert.Equal(100, imported.FindProfile("manual-adjustment")!.Brightness);
        Assert.Equal(240, imported.GetReminder().FocusMinutes);
        Assert.Equal(120, imported.GetReminder().ShortBreakMinutes);
        Assert.Equal(240, imported.GetReminder().LongBreakMinutes);
        Assert.False(imported.GetAdvanced().SmoothTransitions);
        Assert.Equal(63_000, imported.GetAdvanced().TransitionDurationMs);
        Assert.True(imported.HotkeysNeedSync);
        var toggle = imported.GetHotkeys().Single(binding => binding.Id == "toggle-enabled");
        Assert.True(toggle.Enabled);
        Assert.Equal("Ctrl+Alt+F10", toggle.Gesture);
        var overlayHotkey = imported.GetHotkeys().Single(binding => binding.Id == "toggle-overlay");
        Assert.True(overlayHotkey.Enabled);
        Assert.Equal("Ctrl+Alt+F8", overlayHotkey.Gesture);
    }

    [Fact]
    public async Task ScreenEase_serializes_concurrent_profile_updates_without_lost_state()
    {
        var module = new ScreenEaseModule(new RecordingDisplayService());
        await module.InitializeAsync(CreateScreenEaseContext("screenease-concurrent-writes"), CancellationToken.None);

        var saves = Enumerable.Range(1, 24)
            .Select(index => module.ExecuteCommandAsync(
                new CommandRequest($"save-{index}", "screenease.profile.save", new JsonObject
                {
                    ["id"] = $"concurrent-{index:D2}",
                    ["name"] = $"Concurrent {index:D2}",
                    ["brightness"] = 70 + index % 20,
                    ["colorTemperature"] = 4000 + index * 10,
                    ["nightBrightness"] = 60 + index % 20,
                    ["nightColorTemperature"] = 3500 + index * 10
                }),
                CancellationToken.None).AsTask())
            .ToArray();
        var saveResults = await Task.WhenAll(saves);

        var listed = await module.ExecuteCommandAsync(
            new CommandRequest("profiles", "screenease.profile.list", new JsonObject()),
            CancellationToken.None);
        var profiles = JsonNode.Parse(listed.Output)!["profiles"]!.AsArray();

        Assert.All(saveResults, result => Assert.True(result.Success));
        Assert.Equal(31, profiles.Count);
        Assert.Equal(24, profiles.Count(profile => profile!["id"]!.GetValue<string>().StartsWith("concurrent-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ScreenEase_resets_the_gamma_ramp_once_when_an_applied_module_is_disposed()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-dispose-reset", display), CancellationToken.None);
        Assert.Equal(1, display.ResetCalls);

        var applied = await module.ExecuteCommandAsync(
            new CommandRequest("apply", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "long-read",
                ["hardwareWrite"] = true
            }),
            CancellationToken.None);
        await module.DisposeAsync(new CancellationToken(canceled: true));
        await module.DisposeAsync(CancellationToken.None);

        Assert.True(applied.Success);
        Assert.Single(display.AppliedIntents);
        Assert.Equal(2, display.ResetCalls);
    }

    [Fact]
    public async Task ScreenEase_reports_a_hardware_reset_warning_after_logical_disable()
    {
        var display = new RecordingDisplayService(
            resetResult: new BrokerOperationResult(false, "partial-failure", "one display rejected identity gamma"));
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-reset-warning", display), CancellationToken.None);
        await module.ExecuteCommandAsync(
            new CommandRequest("apply", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "long-read",
                ["hardwareWrite"] = true
            }),
            CancellationToken.None);

        var disabled = await module.ExecuteCommandAsync(
            new CommandRequest("disable", "screenease.effect.disable", new JsonObject()),
            CancellationToken.None);
        var payload = JsonNode.Parse(disabled.Output)!.AsObject();

        Assert.True(disabled.Success);
        Assert.False(payload["enabled"]!.GetValue<bool>());
        Assert.True(payload["displayReset"]!["attempted"]!.GetValue<bool>());
        Assert.False(payload["displayReset"]!["success"]!.GetValue<bool>());
        Assert.Contains("rejected", payload["displayReset"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task ScreenEase_dispose_resets_after_a_partial_hardware_apply()
    {
        var display = new RecordingDisplayService(
            applyResult: new BrokerOperationResult(false, "partial-failure", "one display changed before another failed"));
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-partial-apply-reset", display), CancellationToken.None);

        var applied = await module.ExecuteCommandAsync(
            new CommandRequest("apply", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "long-read",
                ["hardwareWrite"] = true
            }),
            CancellationToken.None);
        await module.DisposeAsync(CancellationToken.None);

        Assert.False(applied.Success);
        Assert.Single(display.AppliedIntents);
        Assert.Equal(2, display.ResetCalls);
    }

    [Fact]
    public async Task ScreenEase_schedule_change_immediately_applies_the_current_night_values()
    {
        var display = new RecordingDisplayService();
        var module = new ScreenEaseModule(display);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-immediate-schedule", display), CancellationToken.None);
        await module.ExecuteCommandAsync(
            new CommandRequest("apply", "screenease.profile.apply", new JsonObject
            {
                ["profileId"] = "long-read",
                ["hardwareWrite"] = true
            }),
            CancellationToken.None);
        var now = DateTimeOffset.Now;

        var configured = await module.ExecuteCommandAsync(
            new CommandRequest("schedule", "screenease.schedule.configure", new JsonObject
            {
                ["useNightValues"] = true,
                ["useSchedule"] = true,
                ["sunrise"] = now.AddMinutes(1).ToString("HH:mm"),
                ["sunset"] = now.AddMinutes(-1).ToString("HH:mm")
            }),
            CancellationToken.None);
        var payload = JsonNode.Parse(configured.Output)!.AsObject();

        Assert.Equal(2, display.AppliedIntents.Count);
        Assert.Equal(85, display.AppliedIntents[0].Brightness);
        Assert.Equal(5000, display.AppliedIntents[0].ColorTemperature);
        Assert.Equal(75, display.AppliedIntents[1].Brightness);
        Assert.Equal(4200, display.AppliedIntents[1].ColorTemperature);
        Assert.True(payload["effect"]!["isNightValue"]!.GetValue<bool>());
    }

    [Fact]
    public void ScreenEase_store_imports_the_original_file_on_first_run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tests", "screenease-store-first-import", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "state", "screenease-state.json");
        var legacyPath = Path.Combine(root, "legacy", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, OriginalSettingsJson());
        var store = new ScreenEaseStore(statePath, legacyPath);

        store.EnsureDefaults();
        var imported = store.Load();

        Assert.Equal("long-read", imported.ActiveProfileId);
        Assert.True(imported.GetEffect().Enabled);
        Assert.Equal(5500, imported.FindProfile("personal")!.ColorTemperature);
        Assert.True(File.Exists(statePath));
    }

    [Fact]
    public void ScreenEase_store_replaces_generated_ids_from_the_original_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tests", "screenease-store-id-import", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "state", "screenease-state.json");
        var legacyPath = Path.Combine(root, "legacy", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, OriginalSettingsJson());
        var generatedStore = new ScreenEaseStore(statePath);
        generatedStore.Save(new ScreenEaseState(
            "reading",
            [new ScreenEaseProfile("reading", "Reading", 85, 5000)],
            [],
            new ScreenEaseNativeHostState(false, false, "legacy"),
            DateTimeOffset.UtcNow));
        var store = new ScreenEaseStore(statePath, legacyPath);

        store.EnsureDefaults();
        var imported = store.Load();

        Assert.Equal("long-read", imported.ActiveProfileId);
        Assert.True(imported.GetEffect().Enabled);
        Assert.Equal(100, imported.FindProfile("manual-adjustment")!.Brightness);
        Assert.Null(imported.Profiles.FirstOrDefault(profile => profile.Id == "reading"));
    }

    [Fact]
    public void ScreenEase_store_recovers_a_valid_backup_and_quarantines_corruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tests", "screenease-store-recovery", Guid.NewGuid().ToString("N"));
        var statePath = Path.Combine(root, "state", "screenease-state.json");
        var logPath = Path.Combine(root, "logs", "recovery.log");
        var store = new ScreenEaseStore(statePath, recoveryLogPath: logPath);
        var defaults = ScreenEaseState.Default();
        store.Save(defaults);
        store.Save(defaults with { ActiveProfileId = "long-read", UpdatedAt = DateTimeOffset.UtcNow });
        File.WriteAllText(statePath, "{ damaged json");

        var recovered = store.Load();

        Assert.Equal("low-blue-evening", recovered.ActiveProfileId);
        Assert.Contains("Recovered ScreenEase settings from backup", store.LastRecoveryMessage);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(statePath)!, "*.corrupt-*.json"));
        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public async Task ScreenEase_hotkey_commands_match_the_original_delta_toggle_and_profile_semantics()
    {
        var display = new RecordingDisplayService();
        var overlay = new RecordingOverlayService();
        var module = new ScreenEaseModule(display, overlay);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-hotkey-actions", display), CancellationToken.None);

        var toggled = await module.ExecuteCommandAsync(
            new CommandRequest("toggle", "screenease.effect.toggle", new JsonObject()),
            CancellationToken.None);
        var brighter = await module.ExecuteCommandAsync(
            new CommandRequest("brighter", "screenease.effect.brightness.increase", new JsonObject()),
            CancellationToken.None);
        var cooler = await module.ExecuteCommandAsync(
            new CommandRequest("cooler", "screenease.effect.temperature.decrease", new JsonObject()),
            CancellationToken.None);
        var longRead = await module.ExecuteCommandAsync(
            new CommandRequest("long-read", "screenease.profile.apply-long-read", new JsonObject()),
            CancellationToken.None);
        var profiles = await module.ExecuteCommandAsync(
            new CommandRequest("profiles", "screenease.profile.list", new JsonObject()),
            CancellationToken.None);
        var disabled = await module.ExecuteCommandAsync(
            new CommandRequest("toggle-off", "screenease.effect.toggle", new JsonObject()),
            CancellationToken.None);

        var toggledEffect = JsonNode.Parse(toggled.Output)!["effect"]!;
        var brighterEffect = JsonNode.Parse(brighter.Output)!["effect"]!;
        var coolerEffect = JsonNode.Parse(cooler.Output)!["effect"]!;
        var longReadEffect = JsonNode.Parse(longRead.Output)!["effect"]!;
        Assert.True(toggledEffect["enabled"]!.GetValue<bool>());
        Assert.Equal("low-blue-evening", brighterEffect["profileId"]!.GetValue<string>());
        Assert.Equal(80, brighterEffect["brightnessPercent"]!.GetValue<int>());
        Assert.Equal(3450, coolerEffect["colorTemperatureKelvin"]!.GetValue<int>());
        Assert.Equal("long-read", longReadEffect["profileId"]!.GetValue<string>());
        Assert.DoesNotContain(
            JsonNode.Parse(profiles.Output)!["profiles"]!.AsArray(),
            profile => profile!["id"]!.GetValue<string>() == "manual-adjustment");
        Assert.False(JsonNode.Parse(disabled.Output)!["enabled"]!.GetValue<bool>());
        Assert.True(display.ResetCalls >= 2);
    }

    [Fact]
    public async Task ScreenEase_overlay_normalizes_applies_toggles_and_cleans_up_with_a_cancelled_dispose_token()
    {
        var display = new RecordingDisplayService();
        var overlay = new RecordingOverlayService();
        var module = new ScreenEaseModule(display, overlay);
        await module.InitializeAsync(CreateScreenEaseContext("screenease-overlay-actions", display), CancellationToken.None);

        var configured = await module.ExecuteCommandAsync(
            new CommandRequest("overlay", "screenease.overlay.configure", new JsonObject
            {
                ["enabled"] = true,
                ["opacityPercent"] = 125,
                ["colorHex"] = "cc8844"
            }),
            CancellationToken.None);
        var payload = JsonNode.Parse(configured.Output)!.AsObject();
        var toggled = await module.ExecuteCommandAsync(
            new CommandRequest("overlay-toggle", "screenease.overlay.toggle", new JsonObject()),
            CancellationToken.None);
        await module.DisposeAsync(new CancellationToken(canceled: true));

        Assert.Equal(95, payload["settings"]!["opacityPercent"]!.GetValue<int>());
        Assert.Equal("#CC8844", payload["settings"]!["colorHex"]!.GetValue<string>());
        Assert.Equal(2, payload["runtime"]!["windowCount"]!.GetValue<int>());
        Assert.False(JsonNode.Parse(toggled.Output)!["settings"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(1, overlay.ApplyCalls);
        Assert.True(overlay.HideCalls >= 3);
        Assert.True(overlay.DisposeCalled);
    }

    [Fact]
    public async Task ScreenEase_apply_settings_updates_extended_values_preserves_partial_state_and_syncs_only_changed_hotkeys()
    {
        var display = new RecordingDisplayService();
        var overlay = new RecordingOverlayService();
        var hotkeys = new RecordingModuleHotkeyConfigurationService();
        var context = CreateScreenEaseContext("screenease-apply-extended-settings", display) with
        {
            CapabilityProviders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["display.profile"] = display,
                ["runtime.hotkeys"] = hotkeys
            }
        };
        var module = new ScreenEaseModule(display, overlay);
        await module.InitializeAsync(context, CancellationToken.None);
        var current = await module.GetSettingsAsync(CancellationToken.None);
        var values = current.Values.DeepClone().AsObject();
        values["activeProfileId"] = "long-read";
        values.Remove("effect");
        values["overlay"] = new JsonObject
        {
            ["enabled"] = true,
            ["opacityPercent"] = 33,
            ["colorHex"] = "#123456"
        };
        values["advanced"] = new JsonObject
        {
            ["smoothTransitions"] = false,
            ["transitionDurationMs"] = 120_001
        };
        var configuredHotkeys = values["hotkeys"]!.AsArray();
        configuredHotkeys[0]!["enabled"] = true;
        configuredHotkeys[0]!["gesture"] = "Ctrl+Alt+F10";

        var applied = await module.ApplySettingsAsync(
            new MyPowerTools.Abstractions.SettingsSnapshotDocument("screenease", current.Revision, values, DateTimeOffset.UtcNow),
            CancellationToken.None);
        var brighter = await module.ExecuteCommandAsync(
            new CommandRequest("brighter", "screenease.effect.brightness.increase", new JsonObject()),
            CancellationToken.None);
        Assert.True(brighter.Success);

        var partial = await module.ApplySettingsAsync(
            new MyPowerTools.Abstractions.SettingsSnapshotDocument(
                "screenease",
                current.Revision + 1,
                new JsonObject
                {
                    ["advanced"] = new JsonObject
                    {
                        ["smoothTransitions"] = true,
                        ["transitionDurationMs"] = 2500
                    }
                },
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        var hotkeyOnlyRuntimeSnapshot = await module.ApplySettingsAsync(
            new MyPowerTools.Abstractions.SettingsSnapshotDocument(
                "screenease",
                current.Revision + 2,
                new JsonObject(),
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.Equal("long-read", applied.Values["effect"]!["profileId"]!.GetValue<string>());
        Assert.Equal(120_000, applied.Values["advanced"]!["transitionDurationMs"]!.GetValue<int>());
        Assert.Equal("long-read", partial.Values["activeProfileId"]!.GetValue<string>());
        Assert.Equal("long-read", partial.Values["effect"]!["profileId"]!.GetValue<string>());
        Assert.Equal(90, partial.Values["effect"]!["brightnessPercent"]!.GetValue<int>());
        Assert.True(partial.Values["overlay"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(7, partial.Values["profiles"]!.AsArray().Count);
        Assert.True(partial.Values["hotkeys"]![0]!["enabled"]!.GetValue<bool>());
        Assert.Equal("Ctrl+Alt+F10", partial.Values["hotkeys"]![0]!["gesture"]!.GetValue<string>());
        Assert.Equal("long-read", hotkeyOnlyRuntimeSnapshot.Values["activeProfileId"]!.GetValue<string>());
        Assert.Equal(7, hotkeyOnlyRuntimeSnapshot.Values["profiles"]!.AsArray().Count);
        Assert.True(hotkeyOnlyRuntimeSnapshot.Values["overlay"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(90, hotkeyOnlyRuntimeSnapshot.Values["effect"]!["brightnessPercent"]!.GetValue<int>());
        Assert.True(hotkeyOnlyRuntimeSnapshot.Values["hotkeys"]![0]!["enabled"]!.GetValue<bool>());
        Assert.Equal(1, hotkeys.ApplyCount);
        Assert.True(hotkeys.LastApplied.Single(binding => binding.Id == "toggle-enabled").Enabled);
        Assert.Equal(3, overlay.ApplyCalls);
    }

    [Fact]
    public async Task ScreenEase_legacy_ini_import_maps_all_original_contract_fields_and_applies_them()
    {
        var root = Path.Combine(Path.GetTempPath(), "mpt-tests", "screenease-legacy-ini", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "CareUEyes.ini");
        File.WriteAllText(path,
            """
            ﻿; legacy settings
            [screen]
            mode=9
            health_colortemp=5000
            health_brightness=90
            health_night_colortemp=3700
            health_night_brightness=80
            read_colortemp=5500
            read_brightness=85
            read_night_colortemp=5200
            read_night_brightness=75
            enablesunset=1
            smooth=1
            transition_duration=65536
            [rest]
            enable_rest_timer=1
            work_duration=45
            short_duration=5
            long_duration=15
            long_pause_interval=4
            auto_restart_timer=1
            """);
        var display = new RecordingDisplayService();
        var hotkeys = new RecordingModuleHotkeyConfigurationService();
        var module = new ScreenEaseModule(display, new RecordingOverlayService());
        var context = CreateScreenEaseContext("screenease-legacy-ini-command", display) with
        {
            CapabilityProviders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["display.profile"] = display,
                ["runtime.hotkeys"] = hotkeys
            }
        };
        await module.InitializeAsync(context, CancellationToken.None);

        var imported = await module.ExecuteCommandAsync(
            new CommandRequest("import", "screenease.legacy.import", new JsonObject { ["path"] = path }),
            CancellationToken.None);
        var payload = JsonNode.Parse(imported.Output)!.AsObject();
        var reading = payload["profiles"]!.AsArray().Single(profile => profile!["id"]!.GetValue<string>() == "long-read")!;

        Assert.True(imported.Success);
        Assert.Equal("low-blue-evening", payload["activeProfileId"]!.GetValue<string>());
        Assert.Equal(5500, reading["colorTemperature"]!.GetValue<int>());
        Assert.Equal(45, payload["reminder"]!["focusMinutes"]!.GetValue<int>());
        Assert.True(payload["schedule"]!["useSchedule"]!.GetValue<bool>());
        Assert.Equal(1000, payload["advanced"]!["transitionDurationMs"]!.GetValue<int>());
        Assert.True(payload["effect"]!["enabled"]!.GetValue<bool>());
        Assert.NotEmpty(display.AppliedIntents);
        Assert.Equal(8, hotkeys.LastApplied.Count);
        Assert.All(hotkeys.LastApplied, binding => Assert.False(binding.Enabled));
    }

    [Fact]
    public async Task ScreenEase_manifest_hotkeys_are_all_disabled_until_the_user_enables_them()
    {
        await using var runtime = new MptHostRuntime(
            new MyPowerTools.Packaging.PackageReader(),
            PlatformId.Current(),
            RuntimePaths.Create(Path.Combine(Path.GetTempPath(), "mpt-tests", "screenease-hotkey-defaults", Guid.NewGuid().ToString("N"))));
        runtime.Load(Path.Combine(Root, "modules"));

        var diagnostics = runtime.ListHotkeyDiagnostics()
            .Where(item => item.ModuleId == "screenease")
            .ToArray();

        Assert.Equal(8, diagnostics.Length);
        Assert.All(diagnostics, item => Assert.Equal("disabled", item.State));
        Assert.DoesNotContain(runtime.ListHotkeyBindings(), item => item.ModuleId == "screenease");
    }

    private static string OriginalSettingsJson() =>
        """
        {
          "enabled": true,
          "activeProfileId": "long-read",
          "profiles": [
            {
              "id": "long-read",
              "name": "长读柔光",
              "brightnessPercent": 85,
              "colorTemperatureKelvin": 5000,
              "nightBrightnessPercent": 75,
              "nightColorTemperatureKelvin": 4200
            },
            {
              "id": "manual-adjustment",
              "name": "自定义调节",
              "brightnessPercent": 100,
              "colorTemperatureKelvin": 5000
            },
            {
              "id": "personal",
              "name": "我的方案",
              "brightnessPercent": 100,
              "colorTemperatureKelvin": 5500
            }
          ]
        }
        """;
}
