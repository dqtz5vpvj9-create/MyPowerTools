using System.Text.Json.Nodes;
using InputMonitor.Core;

namespace MyPowerTools.Tests;

public sealed class InputMonitorProductTests
{
    private static readonly string Root = FindRepositoryRoot();

    [Fact]
    public void Input_monitor_declares_a_dotnet_surface()
    {
        var tool = JsonNode.Parse(File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "input-monitor",
            "current-integration",
            "modules",
            "input-monitor",
            "ui",
            "tool.json")))!.AsObject();

        Assert.Equal("dotnet-surface", tool["type"]?.GetValue<string>());
        Assert.Equal("available", tool["availability"]?.GetValue<string>());
        var surface = tool["routes"]?[0]?["surface"];
        Assert.Equal("dotnet", surface?["kind"]?.GetValue<string>());
        Assert.Equal("surface/InputMonitor.Surface.dll", surface?["assembly"]?.GetValue<string>());
        Assert.Equal("InputMonitor.Surface.InputMonitorSurfaceFactory", surface?["type"]?.GetValue<string>());
    }

    [Fact]
    public void Input_monitor_inproc_policy_allows_hooks_and_background_threads()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "input-monitor",
            "current-integration",
            "modules",
            "input-monitor",
            "module.json")))!.AsObject();
        var rules = Assert.IsType<JsonObject>(manifest["runtimePolicy"]!["inProcRules"]);

        Assert.Equal("input-monitor", manifest["id"]!.GetValue<string>());
        Assert.Equal("inproc-dotnet", manifest["entrypoints"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("InputMonitor.MyPowerTools.InputMonitorModule", manifest["entrypoints"]![0]!["type"]!.GetValue<string>());
        Assert.True(rules["allowNativeDll"]!.GetValue<bool>());
        Assert.True(rules["allowBackgroundThreads"]!.GetValue<bool>());
        Assert.True(rules["allowWindow"]!.GetValue<bool>());
        Assert.Equal("collectible", rules["loadContext"]!.GetValue<string>());
    }

    [Fact]
    public void Event_sampler_uses_distance_or_interval_and_ignores_subpixel_idle()
    {
        var sampler = new EventSampler(minDistance: 30, minIntervalNs: 50_000_000);

        var first = sampler.Feed(0, 0, 0);
        var idle = sampler.Feed(0.2, 0, 10_000_000);
        var far = sampler.Feed(40, 0, 20_000_000);

        Assert.True(first.Sampled);
        Assert.False(idle.Sampled);
        Assert.True(far.Sampled);
        Assert.True(far.MoveDelta >= 30);

        sampler.Reset();
        sampler.Feed(0, 0, 0);
        sampler.Feed(1, 0, 1_000_000);
        var timed = sampler.Feed(1.2, 0, 51_000_000);
        Assert.True(timed.Sampled);
    }

    [Fact]
    public void Aggregator_drops_auto_repeat_and_holds_longer_than_ten_minutes()
    {
        var stamp = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.FromHours(8));
        var aggregator = new MetricsAggregator(EventRepository.DayString(stamp));

        aggregator.Process(Event(InputEventKind.KeyDown, stamp, keyCode: 65, autoRepeat: false, timestampNs: 0));
        aggregator.Process(Event(InputEventKind.KeyDown, stamp.AddMilliseconds(30), keyCode: 65, autoRepeat: true, timestampNs: 30_000_000));
        aggregator.Process(Event(InputEventKind.KeyUp, stamp.AddMilliseconds(150), keyCode: 65, timestampNs: 150_000_000));
        aggregator.Process(Event(InputEventKind.KeyDown, stamp.AddMinutes(1), keyCode: 66, timestampNs: 1_000_000_000));
        aggregator.Process(Event(InputEventKind.KeyUp, stamp.AddMinutes(12), keyCode: 66, timestampNs: 700_000_000_000));
        aggregator.Process(Event(InputEventKind.LeftClick, stamp.AddMinutes(1).AddSeconds(1)));
        aggregator.Process(Event(InputEventKind.Scroll, stamp.AddMinutes(1).AddSeconds(2), scrollDelta: 2));

        var snapshot = aggregator.Snapshot();
        Assert.Equal(2, snapshot.KeyCount);
        Assert.Equal(150, snapshot.KeyDurationMs);
        Assert.Equal(1, snapshot.ClickCount);
        Assert.Equal(2, snapshot.ScrollCount);
        Assert.Equal(180, snapshot.InteractionSeconds);
    }

    [Fact]
    public void Fatigue_reminds_once_per_full_interval_and_skip_starts_another()
    {
        var settings = new MonitorSettings { RemindIntervalMinutes = 1 };
        var engine = new FatigueEngine(settings);
        var reminded = 0;
        engine.OnShouldRemind = () => reminded++;
        engine.NotifyActivity(FatigueActivitySource.Keyboard);

        var now = DateTimeOffset.Now;
        for (var tick = 0; tick < 59; tick++)
        {
            engine.Tick(now);
        }

        Assert.True(engine.Value < 100);
        Assert.Equal(0, reminded);

        engine.Tick(now);
        Assert.Equal(100, engine.Value, 3);
        Assert.Equal(1, reminded);
        Assert.Equal(100, engine.Percentage);

        for (var tick = 0; tick < 30; tick++)
        {
            engine.Tick(now);
        }

        Assert.Equal(1, reminded);
        Assert.Equal(100, engine.Percentage);

        engine.Skip();
        Assert.Equal(0, engine.Value, 3);
        Assert.Equal(100, engine.Threshold);
        Assert.Equal(0, engine.Percentage);
        Assert.False(engine.IsResting);

        for (var tick = 0; tick < 59; tick++)
        {
            engine.Tick(now);
        }

        Assert.Equal(1, reminded);
        engine.Tick(now);
        Assert.Equal(2, reminded);

        engine.RestDone();
        Assert.Equal(0, engine.Value);
        Assert.Equal(100, engine.Threshold);
        Assert.False(engine.IsResting);
    }

    [Fact]
    public void Fatigue_pause_caps_at_the_threshold()
    {
        var settings = new MonitorSettings
        {
            RemindIntervalMinutes = 1,
            FatigueIsPaused = true,
            FatigueValue = 40
        };
        var engine = new FatigueEngine(settings);
        var reminded = 0;
        engine.OnShouldRemind = () => reminded++;
        engine.NotifyActivity(FatigueActivitySource.Keyboard);

        var now = DateTimeOffset.Now;
        for (var tick = 0; tick < 120; tick++)
        {
            engine.Tick(now);
        }

        Assert.Equal(0, reminded);
        Assert.Equal(100, engine.Value, 3);
        Assert.Equal(100, engine.Percentage);
        Assert.Equal(100, settings.FatigueValue, 3);
    }

    [Fact]
    public void Fatigue_runaway_persisted_value_resets_to_zero()
    {
        var settings = new MonitorSettings
        {
            FatigueValue = 158904,
            FatigueThreshold = 120,
            FatigueIsPaused = true
        };

        var engine = new FatigueEngine(settings);

        Assert.True(engine.SanitizedPersistedValue);
        Assert.Equal(0, engine.Value);
        Assert.Equal(100, engine.Threshold);
        Assert.Equal(0, engine.Percentage);
        Assert.Equal(0, settings.FatigueValue);
        Assert.Equal(100, settings.FatigueThreshold);
        Assert.True(engine.IsPaused);
    }

    [Fact]
    public void Manual_rest_skip_restores_progress_unless_already_due()
    {
        var settings = new MonitorSettings { RemindIntervalMinutes = 1, FatigueValue = 40 };
        var engine = new FatigueEngine(settings);
        engine.ManualRest();
        engine.BeginResting();
        engine.Skip();

        Assert.Equal(40, engine.Value, 3);
        Assert.Equal(100, engine.Threshold);
        Assert.False(engine.IsResting);

        var dueSettings = new MonitorSettings { FatigueValue = 100, FatigueThreshold = 100 };
        var due = new FatigueEngine(dueSettings);
        due.ManualRest();
        due.BeginResting();
        due.Skip();

        Assert.Equal(0, due.Value);
        Assert.Equal(100, due.Threshold);
        Assert.False(due.IsResting);
    }

    [Fact]
    public void Foreground_app_identity_prefers_description_and_falls_back_past_unknown()
    {
        var chrome = ForegroundAppIdentity.Resolve(
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "Google Chrome",
            "chrome",
            "Inbox - Gmail");
        Assert.Equal("chrome.exe", chrome.BundleId);
        Assert.Equal("Google Chrome", chrome.AppName);
        Assert.False(ForegroundAppIdentity.IsUnresolved(chrome.BundleId));

        var processOnly = ForegroundAppIdentity.Resolve(null, null, "devenv", "Program.cs");
        Assert.Equal("devenv", processOnly.BundleId);
        Assert.Equal("devenv", processOnly.AppName);

        var titleOnly = ForegroundAppIdentity.Resolve(null, null, null, "设置");
        Assert.Equal("unknown", titleOnly.BundleId);
        Assert.Equal("设置", titleOnly.AppName);

        var unknown = ForegroundAppIdentity.Resolve(null, "  ", null, null);
        Assert.Equal("unknown", unknown.BundleId);
        Assert.Equal("Unknown", unknown.AppName);
        Assert.True(ForegroundAppIdentity.IsUnresolved(unknown.BundleId));
    }

    [Fact]
    public void Unresolved_foreground_probe_must_leave_previous_resolved_session()
    {
        Assert.True(ForegroundAppIdentity.MustLeaveResolvedSession("chrome.exe", "unknown"));
        Assert.True(ForegroundAppIdentity.MustLeaveResolvedSession("chrome.exe", null));
        Assert.True(ForegroundAppIdentity.MustLeaveResolvedSession("chrome.exe", "  "));
        Assert.False(ForegroundAppIdentity.MustLeaveResolvedSession("chrome.exe", "devenv.exe"));
        Assert.False(ForegroundAppIdentity.MustLeaveResolvedSession("unknown", "unknown"));
        Assert.False(ForegroundAppIdentity.MustLeaveResolvedSession("unknown", "chrome.exe"));
        Assert.False(ForegroundAppIdentity.MustLeaveResolvedSession(null, "unknown"));
    }

    [Fact]
    public void Host_skip_rest_dismisses_overlay_without_double_skip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mpt-input-monitor-skip", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var overlay = new RecordingOverlay();
        using var host = new InputMonitorHost(directory, overlay: overlay);
        host.Fatigue.BeginResting();
        overlay.Show(30, host.Fatigue.Skip, host.Fatigue.RestDone);

        Assert.True(overlay.IsShowing);
        Assert.True(host.Fatigue.IsResting);

        host.SkipRest();

        Assert.False(overlay.IsShowing);
        Assert.Equal(1, overlay.DismissCount);
        Assert.False(host.Fatigue.IsResting);
        Assert.Equal(0, host.Fatigue.Value);
        // Dismiss closes the overlay only; it must not invoke the Show(skip) callback again.
        Assert.Equal(0, overlay.SkipCallbackCount);
    }

    [Fact]
    public void Host_stop_dismisses_overlay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mpt-input-monitor-stop", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var overlay = new RecordingOverlay();
        using var host = new InputMonitorHost(directory, overlay: overlay);
        host.Start();
        overlay.Show(300, host.Fatigue.Skip, host.Fatigue.RestDone);
        Assert.True(overlay.IsShowing);

        host.Stop();

        Assert.False(overlay.IsShowing);
        Assert.Equal(1, overlay.DismissCount);
    }

    [Fact]
    public void Category_map_matches_windows_process_names_and_honors_overrides()
    {
        var map = new AppCategoryMap();

        Assert.Equal(AppCategory.Development, map.CategoryFor("devenv.exe"));
        Assert.Equal(AppCategory.Browser, map.CategoryFor("msedge"));
        Assert.Equal(AppCategory.Social, map.CategoryFor("WeChat"));
        Assert.Equal(AppCategory.Other, map.CategoryFor("notepad"));

        map.SetOverride("notepad", AppCategory.Office);
        Assert.Equal(AppCategory.Office, map.CategoryFor("notepad"));
        map.SetOverride("notepad", null);
        Assert.Equal(AppCategory.Other, map.CategoryFor("notepad"));
    }

    [Fact]
    public void Sqlite_round_trip_keeps_events_stats_and_key_heat()
    {
        var stamp = new DateTimeOffset(2026, 8, 18, 15, 4, 0, TimeSpan.FromHours(8));
        var day = EventRepository.DayString(stamp);
        var directory = Path.Combine(Path.GetTempPath(), "mpt-input-monitor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var database = new MonitorDatabase(Path.Combine(directory, "input-monitor.db"));
        var repository = new EventRepository(database, new AppCategoryMap());

        repository.InsertEvents(
        [
            Event(InputEventKind.KeyDown, stamp, keyCode: 65, characters: "a"),
            Event(InputEventKind.LeftClick, stamp.AddSeconds(2))
        ]);
        repository.InsertTrackPoints(
        [
            Event(InputEventKind.MouseMoveSample, stamp.AddSeconds(3), x: 10, y: 20, moveDelta: 12)
        ]);
        repository.MergeDailyStats(day, new DaySummary
        {
            Day = day,
            KeyCount = 1,
            ClickCount = 1,
            MoveDistance = 12
        });

        var summary = repository.DaySummaryFor(day);
        var heat = Assert.Single(repository.KeyHeat(day));
        Assert.Equal(1, summary.KeyCount);
        Assert.Equal(1, summary.ClickCount);
        Assert.Equal(12, summary.MoveDistance);
        Assert.Equal("a", heat.Label);
        Assert.Equal(1, heat.Count);
        Assert.True(repository.InteractionSeconds(day) >= 60);
    }

    [Fact]
    public void Stats_payload_includes_dimension_heatmaps_and_track_grid()
    {
        var stamp = new DateTimeOffset(2026, 8, 18, 15, 4, 0, TimeSpan.FromHours(8));
        var day = EventRepository.DayString(stamp);
        var directory = Path.Combine(Path.GetTempPath(), "mpt-input-monitor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var host = new InputMonitorHost(directory);

        host.Repository.InsertEvents(
        [
            Event(InputEventKind.KeyDown, stamp, keyCode: 65, characters: "a"),
            Event(InputEventKind.LeftClick, stamp.AddSeconds(2))
        ]);
        host.Repository.InsertTrackPoints(
        [
            Event(InputEventKind.MouseMoveSample, stamp.AddSeconds(3), x: 100, y: 50, moveDelta: 12)
        ]);

        var json = System.Text.Json.JsonSerializer.Serialize(
            host.BuildStatsPayload(new StatsQuery
            {
                Day = day,
                Grain = "day",
                Dimension = "mouse",
                ScreenWidth = 1920,
                ScreenHeight = 1080
            }),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("mouse", root.GetProperty("dimension").GetString());
        Assert.Equal(7, root.GetProperty("recent7Hourly").GetArrayLength());
        Assert.Equal(24, root.GetProperty("hourlyActivity").GetArrayLength());
        Assert.True(root.GetProperty("trackHeat").GetProperty("sampleCount").GetInt32() >= 1);
        Assert.True(root.GetProperty("trackSampleCount").GetInt32() >= 1);
        Assert.Equal(48 * 27, root.GetProperty("trackHeat").GetProperty("counts").GetArrayLength());
        Assert.Equal(48 * 27, root.GetProperty("trackCounts").GetArrayLength());
        Assert.True(root.GetProperty("trackScreens").GetArrayLength() >= 1);
        Assert.Contains(root.GetProperty("trackHeat").GetProperty("counts").EnumerateArray(), item => item.GetInt32() > 0);

        var keyboardJson = System.Text.Json.JsonSerializer.Serialize(
            host.BuildStatsPayload(new StatsQuery
            {
                Day = day,
                Grain = "day",
                Dimension = "keyboard",
                ScreenWidth = 1920,
                ScreenHeight = 1080
            }),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        using var keyboardDocument = System.Text.Json.JsonDocument.Parse(keyboardJson);
        Assert.True(keyboardDocument.RootElement.GetProperty("trackHeat").GetProperty("sampleCount").GetInt32() >= 1);
    }

    [Fact]
    public void Track_heat_grid_bins_points_and_ignores_empty_max()
    {
        var map = TrackHeatMap.FromPoints([(10, 10), (10, 10), (1919, 1079)], 1920, 1080, 0, 0);
        Assert.Equal(3, map.SampleCount);
        Assert.Equal(2, map.Counts[0]);
        Assert.True(map.Counts[^1] >= 1);
        Assert.True(TrackHeatMap.FromPoints([], 1920, 1080, 0, 0).Counts.All(count => count == 0));
        var left = new ScreenBounds(-864, 0, 864, 2232, false, "显示器 2");
        var primary = new ScreenBounds(0, 0, 1536, 1080, true, "主屏");
        var points = new (double X, double Y)[] { (-543, 100), (1200, 800) };
        var leftMap = TrackHeatMap.FromPoints(points, left);
        var primaryMap = TrackHeatMap.FromPoints(points, primary);
        Assert.Equal(1, leftMap.SampleCount);
        Assert.Equal(1, primaryMap.SampleCount);
        Assert.True(leftMap.RowsCount > leftMap.ColsCount);
        Assert.True(primaryMap.ColsCount > primaryMap.RowsCount);
        Assert.Equal(48, TrackHeatMap.GridSize(1920, 1080).Cols);
        Assert.Equal(27, TrackHeatMap.GridSize(1920, 1080).Rows);
    }

    [Fact]
    public void Stats_payload_splits_track_heat_per_screen()
    {
        var stamp = new DateTimeOffset(2026, 8, 18, 15, 4, 0, TimeSpan.FromHours(8));
        var day = EventRepository.DayString(stamp);
        var directory = Path.Combine(Path.GetTempPath(), "mpt-input-monitor", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var host = new InputMonitorHost(directory);
        host.Repository.InsertTrackPoints(
        [
            Event(InputEventKind.MouseMoveSample, stamp, x: 100, y: 50, moveDelta: 12),
            Event(InputEventKind.MouseMoveSample, stamp.AddSeconds(1), x: -500, y: 200, moveDelta: 12)
        ]);

        var json = System.Text.Json.JsonSerializer.Serialize(
            host.BuildStatsPayload(new StatsQuery
            {
                Day = day,
                Grain = "day",
                Dimension = "mouse",
                Screens =
                [
                    new ScreenBounds(0, 0, 1920, 1080, true, "主屏"),
                    new ScreenBounds(-1080, 0, 1080, 1920, false, "显示器 2")
                ]
            }),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var screens = document.RootElement.GetProperty("trackScreens");
        Assert.Equal(2, screens.GetArrayLength());
        Assert.Equal(1, screens[0].GetProperty("sampleCount").GetInt32());
        Assert.Equal(1, screens[1].GetProperty("sampleCount").GetInt32());
        Assert.Equal(1920, screens[0].GetProperty("width").GetInt32());
        Assert.Equal(1080, screens[1].GetProperty("width").GetInt32());
        Assert.Equal(2, document.RootElement.GetProperty("trackSampleCount").GetInt32());
    }

    [Fact]
    public void Host_strips_key_characters_in_privacy_mode()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mpt-input-monitor-privacy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.json"), """{"privacyMode":true}""");
        var capture = new RecordingCapture();
        using var host = new InputMonitorHost(directory, capture);
        host.Start();
        try
        {
            capture.Push(Event(InputEventKind.KeyDown, DateTimeOffset.Now, keyCode: 65, characters: "a"));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (host.Snapshot().Metrics.KeyCount == 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }

            Assert.Equal(1, host.Snapshot().Metrics.KeyCount);
            host.Stop();
            var heat = host.Repository.KeyHeat(host.Snapshot().Metrics.Day);
            Assert.DoesNotContain(heat, item => item.Label == "a");
        }
        finally
        {
            host.Stop();
        }
    }

    [Fact]
    public void Surface_factory_posts_initialization_instead_of_blocking()
    {
        var factory = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "input-monitor",
            "current-integration",
            "src",
            "InputMonitor.Surface",
            "InputMonitorSurfaceFactory.cs"));
        var view = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "input-monitor",
            "current-integration",
            "src",
            "InputMonitor.Surface",
            "Views",
            "InputMonitorView.axaml"));

        Assert.Contains("Dispatcher.UIThread.Post", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAwaiter().GetResult()", factory, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RestCommand}\"", view, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding PauseCommand}\"", view, StringComparison.Ordinal);
        Assert.Contains("Classes.paused=\"{Binding IsReminderPaused}\"", view, StringComparison.Ordinal);
        Assert.Contains("已暂停休息提醒", File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "input-monitor",
            "current-integration",
            "src",
            "InputMonitor.Surface",
            "ViewModels",
            "InputMonitorViewModel.cs")), StringComparison.Ordinal);
        Assert.Contains("粒度", view, StringComparison.Ordinal);
        Assert.Contains("维度", view, StringComparison.Ordinal);
        Assert.Contains("活动时长", view, StringComparison.Ordinal);
        Assert.Contains("操作频次", view, StringComparison.Ordinal);
        Assert.Contains("ctrl:TrackHeatmapControl", view, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding TrackScreens}\"", view, StringComparison.Ordinal);
        Assert.Contains("WrapPanel", view, StringComparison.Ordinal);
        Assert.Contains("ctrl:HourlyHeatmapControl", view, StringComparison.Ordinal);
        Assert.Contains("ctrl:WeekGridHeatmapControl", view, StringComparison.Ordinal);

        var palette = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "input-monitor",
            "current-integration",
            "src",
            "InputMonitor.Surface",
            "DashboardPalette.cs"));
        var viewModel = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "input-monitor",
            "current-integration",
            "src",
            "InputMonitor.Surface",
            "ViewModels",
            "InputMonitorViewModel.cs"));
        var charts = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "input-monitor",
            "current-integration",
            "src",
            "InputMonitor.Surface",
            "Controls",
            "ChartControls.cs"));
        var heatmaps = File.ReadAllText(Path.Combine(
            Root,
            "tools",
            "input-monitor",
            "current-integration",
            "src",
            "InputMonitor.Surface",
            "Controls",
            "HeatmapControls.cs"));
        Assert.Contains("ImmutableSolidColorBrush", palette, StringComparison.Ordinal);
        Assert.DoesNotContain("new SolidColorBrush", palette, StringComparison.Ordinal);
        Assert.DoesNotContain("new SolidColorBrush", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("new SolidColorBrush", charts, StringComparison.Ordinal);
        Assert.DoesNotContain("new SolidColorBrush", heatmaps, StringComparison.Ordinal);
        Assert.Contains("CheckAccess", factory, StringComparison.Ordinal);
    }

    private static InputEventRecord Event(
        InputEventKind kind,
        DateTimeOffset wallTime,
        long? keyCode = null,
        string? characters = null,
        bool autoRepeat = false,
        ulong timestampNs = 0,
        long scrollDelta = 0,
        double? x = null,
        double? y = null,
        double moveDelta = 0) =>
        new(kind, timestampNs, wallTime, x, y, keyCode, characters, 0, scrollDelta, autoRepeat, moveDelta);

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

    private sealed class RecordingCapture : IInputCapture
    {
        public event Action<InputEventRecord>? EventReceived;
        public bool IsRunning { get; private set; }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void UpdateTrackSampleDistance(double pixels) { }
        public void Dispose() => Stop();
        public void Push(InputEventRecord record) => EventReceived?.Invoke(record);
    }

    private sealed class RecordingOverlay : IRestOverlay
    {
        private Action? _skip;

        public bool IsShowing { get; private set; }
        public int DismissCount { get; private set; }
        public int SkipCallbackCount { get; private set; }

        public void Show(int totalSeconds, Action skip, Action finished)
        {
            _skip = () =>
            {
                SkipCallbackCount++;
                skip();
            };
            IsShowing = true;
        }

        public void Dismiss()
        {
            if (!IsShowing && DismissCount > 0)
            {
                return;
            }

            IsShowing = false;
            DismissCount++;
        }
    }
}
