using ScreenEase.Surface.Services;
using ScreenEase.Surface.ViewModels;
using MyPowerTools.Shell.Avalonia.ViewModels;
using System.Text.Json.Nodes;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class ScreenEaseProductTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void View_model_matches_the_original_mode_and_adjustment_workflow()
    {
        var profiles = new ScreenEaseProfile[]
        {
            new("day", "日间办公", 100, 6500),
            new("reading", "长读柔光", 85, 5000),
            new("night", "夜间低蓝", 75, 3700)
        };
        var snapshot = Snapshot(profiles, "reading");

        using var viewModel = new ScreenEaseViewModel(snapshot);

        Assert.Equal(3, viewModel.Modes.Count);
        Assert.Equal("长读柔光", viewModel.ModeName);
        Assert.Equal("5000 K / 85%", viewModel.CurrentSummary);
        Assert.True(viewModel.EyeCareEnabled);
        Assert.Equal("关闭护眼", viewModel.EyeCareActionLabel);

        viewModel.ColorTemperature = 4551;
        viewModel.Brightness = 91.6;

        Assert.Equal("4600 K", viewModel.ColorTemperatureText);
        Assert.Equal("92%", viewModel.BrightnessText);
        Assert.Equal("5000 K / 85%", viewModel.CurrentSummary);
    }

    [Fact]
    public void Reminder_arguments_preserve_the_original_timer_fields()
    {
        var reminder = new ScreenEaseReminder(true, true, 25, 5, 15, 4);
        var json = ScreenEaseToolService.BuildReminderJson(reminder);

        Assert.True(json["enabled"]!.GetValue<bool>());
        Assert.True(json["autoStartNext"]!.GetValue<bool>());
        Assert.Equal(25, json["focusMinutes"]!.GetValue<int>());
        Assert.Equal(5, json["shortBreakMinutes"]!.GetValue<int>());
        Assert.Equal(15, json["longBreakMinutes"]!.GetValue<int>());
        Assert.Equal(4, json["longBreakInterval"]!.GetValue<int>());
    }

    [Fact]
    public async Task Reminder_controls_start_pause_continue_and_reset_a_real_timer_state()
    {
        var state = ScreenEaseReminderState.Stopped;
        using var viewModel = new ScreenEaseViewModel(
            Snapshot(
                [new ScreenEaseProfile("reading", "长读柔光", 85, 5000)],
                "reading"),
            saveReminder: _ => Task.CompletedTask,
            loadReminderState: () => Task.FromResult(state),
            startReminder: () => Task.FromResult(state = new ScreenEaseReminderState(
                "work", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(25), null, "", 0, 1500)),
            pauseReminder: () => Task.FromResult(state = state with
            {
                Phase = "paused",
                EndsAt = null,
                PausedRemainingSeconds = 1500,
                PausedFrom = "work",
                RemainingSeconds = 1500
            }),
            resumeReminder: () => Task.FromResult(state = state with
            {
                Phase = "work",
                EndsAt = DateTimeOffset.UtcNow.AddMinutes(25),
                PausedRemainingSeconds = null,
                PausedFrom = "",
                RemainingSeconds = 1500
            }),
            resetReminder: () => Task.FromResult(state = ScreenEaseReminderState.Stopped));

        Assert.False(viewModel.PauseReminderCommand.CanExecute(null));
        Assert.False(viewModel.ResumeReminderCommand.CanExecute(null));
        Assert.False(viewModel.ResetReminderCommand.CanExecute(null));

        viewModel.StartReminderCommand.Execute(null);
        await Task.Yield();
        Assert.Equal("专注中", viewModel.ReminderStatusText);
        Assert.Equal("25:00", viewModel.RemainingText);
        Assert.True(viewModel.PauseReminderCommand.CanExecute(null));
        Assert.True(viewModel.ResetReminderCommand.CanExecute(null));

        viewModel.PauseReminderCommand.Execute(null);
        await Task.Yield();
        Assert.Equal("已暂停", viewModel.ReminderStatusText);
        Assert.False(viewModel.PauseReminderCommand.CanExecute(null));
        Assert.True(viewModel.ResumeReminderCommand.CanExecute(null));

        viewModel.ResumeReminderCommand.Execute(null);
        await Task.Yield();
        Assert.Equal("专注中", viewModel.ReminderStatusText);

        viewModel.ResetReminderCommand.Execute(null);
        await Task.Yield();
        Assert.Equal("未开始", viewModel.ReminderStatusText);
        Assert.Equal("-", viewModel.RemainingText);
        Assert.False(viewModel.ResetReminderCommand.CanExecute(null));
    }

    [Fact]
    public void Profiles_and_schedule_preserve_original_day_and_night_fields()
    {
        var profile = new ScreenEaseProfile("reading", "长读柔光", 85, 5000, 75, 4200);
        var profileJson = ScreenEaseToolService.BuildProfileArgs(profile);
        var scheduleJson = ScreenEaseToolService.BuildScheduleJson(new ScreenEaseSchedule(true, true, "07:00", "19:00"));

        Assert.Equal(75, profileJson["nightBrightness"]!.GetValue<int>());
        Assert.Equal(4200, profileJson["nightColorTemperature"]!.GetValue<int>());
        Assert.True(scheduleJson["useNightValues"]!.GetValue<bool>());
        Assert.True(scheduleJson["useSchedule"]!.GetValue<bool>());
        Assert.Equal("07:00", scheduleJson["sunrise"]!.GetValue<string>());
        Assert.Equal("19:00", scheduleJson["sunset"]!.GetValue<string>());
    }

    [Fact]
    public async Task Read_only_session_applies_the_logical_profile_without_requesting_a_hardware_write()
    {
        var applied = new TaskCompletionSource<(int Kelvin, int Brightness, bool HardwareWrite)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ScreenEaseViewModel(
            Snapshot(
                [new ScreenEaseProfile("reading", "长读柔光", 85, 5000)],
                "reading",
                [new ScreenEaseDisplay(@"\\.\DISPLAY1", "Remote display", "connected", 1920, 1080, 60, "landscape", true, "Remote session")],
                new ScreenEaseNativeWriter(false, false, "unsupported", "Remote session")),
            applyManual: (kelvin, brightness, hardwareWrite) =>
            {
                applied.TrySetResult((kelvin, brightness, hardwareWrite));
                return Task.CompletedTask;
            });

        viewModel.ApplyCurrentCommand.Execute(null);
        var call = await applied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.Equal(5000, call.Kelvin);
        Assert.Equal(85, call.Brightness);
        Assert.False(call.HardwareWrite);
        Assert.True(viewModel.EyeCareEnabled);
        Assert.Contains("逻辑状态", viewModel.OperationMessage);
    }

    [Fact]
    public async Task Read_only_session_can_disable_the_persisted_logical_effect()
    {
        var disabled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ScreenEaseViewModel(
            Snapshot(
                [new ScreenEaseProfile("reading", "长读柔光", 85, 5000)],
                "reading",
                [new ScreenEaseDisplay(@"\\.\DISPLAY1", "Remote display", "connected", 1920, 1080, 60, "landscape", true, "Remote session")],
                new ScreenEaseNativeWriter(false, false, "unsupported", "Remote session")),
            disableEffect: () =>
            {
                disabled.TrySetResult(true);
                return Task.FromResult(new ScreenEaseDisableResult(
                    new ScreenEaseDisplayEffect(false, "long-read", 5000, 85, false, DateTimeOffset.UtcNow),
                    false,
                    true,
                    "logical-only",
                    "No hardware reset was required."));
            });

        Assert.True(viewModel.EyeCareEnabled);
        Assert.True(viewModel.ToggleEyeCareCommand.CanExecute(null));

        viewModel.ToggleEyeCareCommand.Execute(null);
        await disabled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.False(viewModel.EyeCareEnabled);
        Assert.Equal("开启护眼", viewModel.EyeCareActionLabel);
        Assert.Equal("护眼已关闭 · 硬件当前只读", viewModel.EyeCareStatusText);
    }

    [Fact]
    public async Task Selecting_a_mode_applies_it_immediately_like_the_original_desktop_tool()
    {
        var applied = new TaskCompletionSource<(string ProfileId, bool HardwareWrite)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ScreenEaseViewModel(
            Snapshot(
                [
                    new ScreenEaseProfile("reading", "长读柔光", 85, 5000),
                    new ScreenEaseProfile("night", "夜间低蓝", 75, 3700)
                ],
                "reading",
                [new ScreenEaseDisplay(@"\\.\DISPLAY1", "Remote display", "connected", 1920, 1080, 60, "landscape", true, "Remote session")],
                new ScreenEaseNativeWriter(false, false, "unsupported", "Remote session")),
            apply: (profileId, _, hardwareWrite) =>
            {
                applied.TrySetResult((profileId, hardwareWrite));
                return Task.CompletedTask;
            });

        var night = Assert.Single(viewModel.Modes, mode => mode.Id == "night");
        night.SelectCommand.Execute(null);
        var call = await applied.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.Equal("night", call.ProfileId);
        Assert.False(call.HardwareWrite);
        Assert.Equal("夜间低蓝", viewModel.ModeName);
        Assert.True(viewModel.EyeCareEnabled);
    }

    [Fact]
    public async Task Available_display_hardware_is_used_without_a_second_hidden_writer_toggle()
    {
        var applied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ScreenEaseViewModel(
            Snapshot(
                [new ScreenEaseProfile("reading", "长读柔光", 85, 5000)],
                "reading",
                nativeWriter: new ScreenEaseNativeWriter(true, true, "ready", "DDC/CI ready")),
            applyManual: (_, _, hardwareWrite) =>
            {
                applied.TrySetResult(hardwareWrite);
                return Task.CompletedTask;
            });

        viewModel.ApplyCurrentCommand.Execute(null);
        Assert.True(await applied.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("硬件调节可用", viewModel.NativeWriterStatusLabel);
    }

    [Fact]
    public void Effect_summary_stays_truthful_while_day_and_night_editor_values_change()
    {
        var profile = new ScreenEaseProfile("long-read", "长读柔光", 85, 5000, 75, 4200);
        var snapshot = Snapshot([profile], "long-read") with
        {
            Schedule = new ScreenEaseSchedule(true, false, "07:00", "19:00"),
            Effect = new ScreenEaseDisplayEffect(
                true,
                "long-read",
                5000,
                85,
                false,
                DateTimeOffset.UtcNow)
        };

        using var viewModel = new ScreenEaseViewModel(snapshot);

        Assert.True(viewModel.UseNightValues);
        Assert.False(viewModel.EditingNightValues);
        Assert.Equal("5000 K / 85%", viewModel.CurrentSummary);
        Assert.Equal("5000 K", viewModel.ColorTemperatureText);

        viewModel.EditingNightValues = true;

        Assert.Equal("4200 K", viewModel.ColorTemperatureText);
        Assert.Equal("75%", viewModel.BrightnessText);
        Assert.Equal("5000 K / 85%", viewModel.CurrentSummary);
    }

    [Fact]
    public async Task Reminder_editor_accepts_the_original_upper_bounds_and_rejects_values_above_them()
    {
        var saved = new TaskCompletionSource<ScreenEaseReminder>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ScreenEaseViewModel(
            Snapshot([new ScreenEaseProfile("long-read", "长读柔光", 85, 5000)], "long-read"),
            saveReminder: value =>
            {
                saved.TrySetResult(value);
                return Task.CompletedTask;
            },
            loadReminderState: () => Task.FromResult(ScreenEaseReminderState.Stopped));
        viewModel.FocusMinutes = "240";
        viewModel.ShortBreakMinutes = "120";
        viewModel.LongBreakMinutes = "240";
        viewModel.LongBreakInterval = "12";

        viewModel.SaveReminderCommand.Execute(null);
        var reminder = await saved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(240, reminder.FocusMinutes);
        Assert.Equal(120, reminder.ShortBreakMinutes);
        Assert.Equal(240, reminder.LongBreakMinutes);
        Assert.Equal(12, reminder.LongBreakInterval);

        viewModel.FocusMinutes = "241";
        viewModel.SaveReminderCommand.Execute(null);
        await Task.Yield();
        Assert.Contains("专注 1–240", viewModel.ReminderMessage);
    }

    [Fact]
    public async Task Disable_reports_a_hardware_reset_warning_after_logical_state_is_closed()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ScreenEaseViewModel(
            Snapshot([new ScreenEaseProfile("long-read", "长读柔光", 85, 5000)], "long-read"),
            disableEffect: () =>
            {
                completed.TrySetResult(true);
                return Task.FromResult(new ScreenEaseDisableResult(
                    new ScreenEaseDisplayEffect(false, "long-read", 5000, 85, false, DateTimeOffset.UtcNow),
                    true,
                    false,
                    "reset-failed",
                    "SetDeviceGammaRamp failed"));
            });

        viewModel.ToggleEyeCareCommand.Execute(null);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.False(viewModel.EyeCareEnabled);
        Assert.Contains("硬件复位失败", viewModel.OperationMessage);
        Assert.Contains("SetDeviceGammaRamp failed", viewModel.OperationMessage);
    }

    [Fact]
    public async Task Saving_a_schedule_refreshes_the_header_from_the_effect_returned_by_the_module()
    {
        var saved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ScreenEaseViewModel(
            Snapshot([
                new ScreenEaseProfile("long-read", "长读柔光", 85, 5000, 75, 4200)
            ], "long-read"),
            saveSchedule: schedule =>
            {
                saved.TrySetResult(schedule.UseSchedule);
                return Task.FromResult(new ScreenEaseDisplayEffect(
                    true,
                    "long-read",
                    4200,
                    75,
                    true,
                    DateTimeOffset.UtcNow));
            });
        viewModel.UseNightValues = true;
        viewModel.UseSchedule = true;

        viewModel.SaveScheduleCommand.Execute(null);
        Assert.True(await saved.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await Task.Yield();

        Assert.Equal("4200 K / 75%", viewModel.CurrentSummary);
        Assert.True(viewModel.EyeCareEnabled);
    }

    [Fact]
    public void Night_schedule_handles_overnight_and_same_day_windows()
    {
        Assert.True(ScreenEaseViewModel.IsNightAt(
            new TimeOnly(23, 30),
            new TimeOnly(7, 0),
            new TimeOnly(19, 0)));
        Assert.True(ScreenEaseViewModel.IsNightAt(
            new TimeOnly(6, 30),
            new TimeOnly(7, 0),
            new TimeOnly(19, 0)));
        Assert.False(ScreenEaseViewModel.IsNightAt(
            new TimeOnly(12, 0),
            new TimeOnly(7, 0),
            new TimeOnly(19, 0)));

        Assert.True(ScreenEaseViewModel.IsNightAt(
            new TimeOnly(2, 0),
            new TimeOnly(3, 0),
            new TimeOnly(1, 0)));
        Assert.False(ScreenEaseViewModel.IsNightAt(
            new TimeOnly(4, 0),
            new TimeOnly(3, 0),
            new TimeOnly(1, 0)));

        Assert.True(ScreenEaseViewModel.IsNightAt(
            new TimeOnly(12, 0),
            new TimeOnly(7, 0),
            new TimeOnly(7, 0)));
    }

    [Fact]
    public void Actual_effect_summary_preserves_the_full_source_api_range()
    {
        var snapshot = Snapshot([
            new ScreenEaseProfile("long-read", "长读柔光", 85, 5000)
        ], "long-read") with
        {
            Effect = new ScreenEaseDisplayEffect(
                true,
                "long-read",
                9000,
                130,
                false,
                DateTimeOffset.UtcNow)
        };

        using var viewModel = new ScreenEaseViewModel(snapshot);

        Assert.Equal("9000 K / 130%", viewModel.CurrentSummary);
    }

    [Fact]
    public async Task Mode_selection_ignores_reentry_while_an_apply_is_in_flight()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyCount = 0;
        using var viewModel = new ScreenEaseViewModel(
            Snapshot(
                [
                    new ScreenEaseProfile("long-read", "长读柔光", 85, 5000),
                    new ScreenEaseProfile("low-blue-evening", "夜间低蓝", 75, 3700)
                ],
                "long-read"),
            apply: async (_, _, _) =>
            {
                applyCount++;
                entered.TrySetResult(true);
                await release.Task;
            });
        var first = Assert.Single(viewModel.Modes, mode => mode.Id == "long-read");
        var second = Assert.Single(viewModel.Modes, mode => mode.Id == "low-blue-evening");

        first.SelectCommand.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        second.SelectCommand.Execute(null);
        await Task.Yield();

        Assert.Equal(1, applyCount);
        Assert.Same(first, viewModel.SelectedMode);
        release.TrySetResult(true);
    }

    [Fact]
    public void Apply_arguments_preserve_profile_target_and_hardware_intent()
    {
        var args = ScreenEaseToolService.BuildApplyArgs("reading", "all", hardwareWrite: true);

        Assert.Equal("reading", args["profileId"]!.GetValue<string>());
        Assert.Equal("all", args["displayId"]!.GetValue<string>());
        Assert.True(args["hardwareWrite"]!.GetValue<bool>());

        var manual = ScreenEaseToolService.BuildManualApplyArgs(4300, 72, "all", hardwareWrite: false);
        Assert.Equal(4300, manual["colorTemperatureKelvin"]!.GetValue<int>());
        Assert.Equal(72, manual["brightnessPercent"]!.GetValue<int>());
        Assert.False(manual["hardwareWrite"]!.GetValue<bool>());
    }

    [Fact]
    public void Connection_status_distinguishes_a_connected_display_from_ddc_hardware_control()
    {
        var connectedDisplay = new ScreenEaseDisplay(
            @"\\.\DISPLAY1",
            "Built-in display",
            "connected",
            2560,
            1600,
            120,
            "landscape",
            true,
            "Internal panel");
        using var connected = new ScreenEaseViewModel(Snapshot(
            [new ScreenEaseProfile("day", "日间办公", 100, 6500)],
            "day",
            [connectedDisplay],
            new ScreenEaseNativeWriter(false, false, "unsupported", "DDC/CI unavailable")));

        Assert.True(connected.IsConnected);
        Assert.False(connected.HardwareControlAvailable);
        Assert.Equal("已连接 · 当前会话只读", connected.ConnectionText);
        Assert.Equal("护眼逻辑已开启 · 硬件当前只读", connected.EyeCareStatusText);
        Assert.Equal("关闭护眼", connected.EyeCareActionLabel);
        Assert.Equal("应用当前调节", connected.ApplyCurrentLabel);
        Assert.Equal("当前会话无法访问显示器硬件", connected.NativeWriterStatusLabel);
        Assert.Contains("返回本地桌面", connected.NativeWriterStatusDetail);
        Assert.True(connected.ToggleEyeCareCommand.CanExecute(null));

        using var missing = new ScreenEaseViewModel(Snapshot(
            [new ScreenEaseProfile("day", "日间办公", 100, 6500)],
            "day",
            [],
            new ScreenEaseNativeWriter(false, false, "unsupported", "No display")));

        Assert.False(missing.IsConnected);
        Assert.Equal("未检测到显示器", missing.ConnectionText);
    }

    [Fact]
    public async Task Advanced_transition_and_overlay_controls_apply_the_runtime_response_headlessly()
    {
        var advancedSaved = new TaskCompletionSource<ScreenEaseAdvanced>(TaskCreationOptions.RunContinuationsAsynchronously);
        var overlaySaved = new TaskCompletionSource<ScreenEaseOverlayConfiguration>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewModel = new ScreenEaseViewModel(
            Snapshot([new ScreenEaseProfile("long-read", "长读柔光", 85, 5000)], "long-read") with
            {
                Advanced = new ScreenEaseAdvanced(true, 2000),
                Overlay = ScreenEaseOverlayResult.Default
            },
            saveAdvanced: value =>
            {
                advancedSaved.TrySetResult(value);
                return Task.FromResult(new ScreenEaseAdvancedSaveResult(3, value));
            },
            saveOverlay: value =>
            {
                overlaySaved.TrySetResult(value);
                return Task.FromResult(new ScreenEaseOverlayResult(
                    value,
                    new ScreenEaseOverlayRuntime(true, value.OpacityPercent, value.ColorHex, 2, "applied", "active")));
            });

        viewModel.SmoothTransitions = false;
        viewModel.TransitionDurationMs = "1750";
        viewModel.SaveAdvancedCommand.Execute(null);
        var advanced = await advancedSaved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.False(advanced.SmoothTransitions);
        Assert.Equal(1750, advanced.TransitionDurationMs);
        Assert.Equal("平滑过渡已关闭。", viewModel.AdvancedMessage);

        viewModel.OverlayEnabled = true;
        viewModel.OverlayOpacityPercent = "24";
        viewModel.OverlayColorHex = "#ffcc88";
        viewModel.SaveOverlayCommand.Execute(null);
        var overlay = await overlaySaved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Yield();

        Assert.True(overlay.Enabled);
        Assert.Equal(24, overlay.OpacityPercent);
        Assert.Equal("#FFCC88", overlay.ColorHex);
        Assert.Equal("遮罩正在显示", viewModel.OverlayStateText);
        Assert.Equal("2 个显示窗口", viewModel.OverlayWindowCountText);
    }

    [Fact]
    public async Task Legacy_import_refreshes_profiles_effect_overlay_and_hotkey_status_from_the_returned_snapshot()
    {
        var openedSettings = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var imported = Snapshot(
            [new ScreenEaseProfile("low-blue-evening", "夜间低蓝", 72, 3600)],
            "low-blue-evening") with
        {
            SettingsRevision = 8,
            Advanced = new ScreenEaseAdvanced(false, 900),
            Overlay = new ScreenEaseOverlayResult(
                new ScreenEaseOverlayConfiguration(true, 20, "#E0A060"),
                new ScreenEaseOverlayRuntime(true, 20, "#E0A060", 1, "applied", "active")),
            Hotkeys =
            [
                new ScreenEaseHotkey(
                    "toggle-enabled",
                    "开关护眼",
                    "screenease.effect.toggle",
                    "Ctrl+Alt+F9",
                    "Ctrl+Alt+F9",
                    true,
                    "registered",
                    "ready")
            ]
        };
        using var viewModel = new ScreenEaseViewModel(
            Snapshot([new ScreenEaseProfile("long-read", "长读柔光", 85, 5000)], "long-read"),
            importLegacy: path =>
            {
                Assert.Equal(@"C:\CareUEyes\settings.ini", path);
                return Task.FromResult(imported);
            },
            openHotkeySettings: () =>
            {
                openedSettings.TrySetResult(true);
                return Task.CompletedTask;
            });

        viewModel.LegacyIniPath = @"C:\CareUEyes\settings.ini";
        viewModel.ImportLegacyCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ModeName == "夜间低蓝");

        Assert.Single(viewModel.Modes);
        Assert.Equal("900", viewModel.TransitionDurationMs);
        Assert.True(viewModel.OverlayEnabled);
        Assert.Equal("1 个快捷键已启用", viewModel.HotkeyStatusText);
        Assert.Contains("已导入 1 个模式", viewModel.LegacyImportMessage);

        viewModel.OpenHotkeySettingsCommand.Execute(null);
        Assert.True(await openedSettings.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Advanced_command_arguments_preserve_settings_and_overlay_contracts()
    {
        var advanced = ScreenEaseToolService.BuildAdvancedJson(new ScreenEaseAdvanced(false, 120000));
        var overlay = ScreenEaseToolService.BuildOverlayJson(new ScreenEaseOverlayConfiguration(true, 95, "#FFC98A"));

        Assert.False(advanced["smoothTransitions"]!.GetValue<bool>());
        Assert.Equal(120000, advanced["transitionDurationMs"]!.GetValue<int>());
        Assert.True(overlay["enabled"]!.GetValue<bool>());
        Assert.Equal(95, overlay["opacityPercent"]!.GetValue<int>());
        Assert.Equal("#FFC98A", overlay["colorHex"]!.GetValue<string>());
    }

    [Fact]
    public void Default_disabled_screenease_hotkey_can_be_enabled_and_saved()
    {
        var modules = new HostProto.ListModulesResponse();
        modules.Modules.Add(new HostProto.ModuleSummary
        {
            ModuleId = "screenease",
            DisplayName = "ScreenEase",
            State = "running"
        });
        var diagnostics = new[]
        {
            new HostProto.RuntimeHotkeyDiagnostics
            {
                Id = "screenease.toggle-enabled",
                ModuleId = "screenease",
                CommandId = "screenease.effect.toggle",
                Gesture = "Ctrl+Alt+F9",
                DefaultGesture = "Ctrl+Alt+F9",
                State = "disabled",
                Message = "Disabled by module settings.",
                IsDefault = true
            }
        };
        var settings = ShellPageViewModelFactory.FromSettings(
            modules,
            modules.Modules.Single(),
            "{}",
            new JsonObject(),
            "{}",
            4,
            DateTimeOffset.UtcNow,
            hotkeys: diagnostics);
        var hotkey = Assert.Single(settings.Hotkeys);

        Assert.False(hotkey.Enabled);
        Assert.False(hotkey.CanEdit);

        hotkey.Enabled = true;
        var patch = ShellPageViewModelFactory.BuildSettingsPatch(settings);
        var edit = Assert.IsType<JsonObject>(Assert.Single(Assert.IsType<JsonArray>(patch["$hotkeys"])));

        Assert.Single(patch);
        Assert.False(patch.ContainsKey("profiles"));
        Assert.False(patch.ContainsKey("rules"));
        Assert.False(patch.ContainsKey("overlay"));
        Assert.True(hotkey.CanEdit);
        Assert.True(hotkey.IsDirty);
        Assert.True(settings.CanSave);
        Assert.False(edit["disabled"]!.GetValue<bool>());
        Assert.Equal("Ctrl+Alt+F9", edit["gesture"]!.GetValue<string>());
        Assert.Contains("enable", settings.PatchPreview);

        settings.ApplySaveResult("applied", "Saved", "Shortcut saved.", 5, saved: true);

        Assert.False(hotkey.IsDirty);
        Assert.False(settings.HasChanges);
        Assert.Equal("Pending registration", hotkey.StateLabel);
    }

    [Fact]
    public void Dynamic_screenease_surface_preserves_the_original_single_workspace()
    {
        var controller = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "MyPowerTools.Shell.Avalonia",
            "Services",
            "ShellWorkspaceController.Tools.cs"));
        var surfaceFactory = File.ReadAllText(Path.Combine(
            Root,
            "tools", "screenease", "current-integration", "src",
            "ScreenEase.Surface", "ScreenEaseSurfaceFactory.cs"));
        var view = File.ReadAllText(Path.Combine(
            Root,
            "tools", "screenease", "current-integration", "src",
            "ScreenEase.Surface", "Views", "ScreenEaseView.axaml"));

        Assert.Contains("LoadExternalSdkToolAsync", controller);
        Assert.DoesNotContain("LoadScreenEaseToolAsync", controller);
        Assert.DoesNotContain("new ScreenEaseView", controller);
        Assert.Contains("new ScreenEaseToolService(context.ServiceUnits)", surfaceFactory);
        Assert.Contains("new ScreenEaseView", surfaceFactory);
        Assert.Contains("护眼调节", view);
        Assert.Contains("休息提醒", view);
        Assert.Contains("UseNightValues", view);
        Assert.Contains("UseSchedule", view);
        Assert.Contains("SaveScheduleCommand", view);
        Assert.Contains("高级护眼设置", view);
        Assert.Contains("IsExpanded=\"False\"", view);
        Assert.Contains("SaveAdvancedCommand", view);
        Assert.Contains("SaveOverlayCommand", view);
        Assert.Contains("ImportLegacyCommand", view);
        Assert.Contains("OpenHotkeySettingsCommand", view);
        Assert.Contains("ItemsSource=\"{Binding Hotkeys}\"", view);
        Assert.Contains("ApplyCurrentLabel", view);
        Assert.Contains("ScreenEaseModeButton", view);
        Assert.Contains("MaxWidth=\"1500\"", view);
        Assert.Contains("MptBrushAppBackground", view);
        Assert.DoesNotContain("Text=\"{Binding NativeWriter.Message}\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding NativeWriter.State}\"", view, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenEaseCanvas", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Display profiles", view, StringComparison.Ordinal);
        Assert.DoesNotContain("Troubleshooting", view, StringComparison.Ordinal);

        var screenshotWriter = File.ReadAllText(Path.Combine(
            Root,
            "src",
            "Mpt.Cli.VisualTesting",
            "ShellRealScreenshotWriter.cs"));
        Assert.Contains("WriteSnapshotSetFromHostControlAsync", screenshotWriter);
        Assert.Contains("runner-hostcontrol", screenshotWriter);
    }

    [Fact]
    public void ScreenEase_implementation_tracks_the_original_source_state_contract()
    {
        var module = File.ReadAllText(Path.Combine(
            Root,
            "tools", "screenease", "current-integration", "src",
            "ScreenEase.MyPowerTools", "ScreenEaseModule.cs"));
        var proxy = File.ReadAllText(Path.Combine(
            Root,
            "tools", "screenease", "current-integration", "src",
            "ScreenEase.MyPowerTools", "ScreenEaseServiceProxyModule.cs"));
        var viewModel = File.ReadAllText(Path.Combine(
            Root,
            "tools", "screenease", "current-integration", "src",
            "ScreenEase.Surface", "ViewModels", "ScreenEaseViewModels.cs"));

        foreach (var required in new[]
                 {
                     "UseNightValues", "UseSchedule", "Sunrise", "Sunset",
                     "PausedRemainingSeconds", "PausedFrom", "CompletedWorkSessions",
                     "screenease.reminder.start", "screenease.reminder.pause",
                     "screenease.reminder.resume", "screenease.reminder.reset"
                 })
        {
            Assert.Contains(required, module + proxy + viewModel, StringComparison.Ordinal);
        }

        Assert.Contains("ServiceUnitClientFactory", proxy, StringComparison.Ordinal);
        Assert.DoesNotContain("ScreenEaseModule(context)", proxy, StringComparison.Ordinal);

        var originalRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Codex", "2026-07-01", "careueyes-ida-pro-core-service", "outputs", "ScreenEase");
        if (!Directory.Exists(originalRoot))
        {
            return;
        }

        var originalModels = File.ReadAllText(Path.Combine(originalRoot, "src", "ScreenEase.Core", "Models.cs"));
        var originalTimer = File.ReadAllText(Path.Combine(originalRoot, "src", "ScreenEase.Core", "RestTimerEngine.cs"));
        var originalController = File.ReadAllText(Path.Combine(originalRoot, "src", "ScreenEase.Core", "EyeCareController.cs"));
        Assert.Contains("UseNightValues", originalModels);
        Assert.Contains("UseSchedule", originalModels);
        Assert.Contains("PausedRemaining", originalModels);
        Assert.Contains("RestTimerEngine.Pause", originalController);
        Assert.Contains("StartBreak", originalTimer);
        Assert.Contains("IsNight", originalController);
    }

    private static ScreenEaseSnapshot Snapshot(
        IReadOnlyList<ScreenEaseProfile> profiles,
        string activeProfileId,
        IReadOnlyList<ScreenEaseDisplay>? displays = null,
        ScreenEaseNativeWriter? nativeWriter = null)
    {
        return new ScreenEaseSnapshot(
            activeProfileId,
            profiles,
            displays ?? [new ScreenEaseDisplay(@"\\.\DISPLAY1", "Test display", "connected", 1920, 1080, 60, "landscape", true, "DDC/CI ready")],
            [],
            nativeWriter ?? new ScreenEaseNativeWriter(true, true, "ready", "writer ready"),
            new ScreenEaseReminder(false, false, 25, 5, 15, 4),
            ScreenEasePlan.Empty,
            [],
            2,
            Effect: new ScreenEaseDisplayEffect(
                true,
                activeProfileId,
                profiles.FirstOrDefault(profile => profile.Id == activeProfileId)?.ColorTemperature ?? 5000,
                profiles.FirstOrDefault(profile => profile.Id == activeProfileId)?.Brightness ?? 85,
                false,
                DateTimeOffset.Now));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The expected ScreenEase state was not observed.");
            }

            await Task.Delay(10);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "MyPowerTools.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
