using InputMonitor.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace MyPowerTools.Tests;

public sealed class InputMonitorWorkflowTests
{
    [Theory]
    [InlineData("month", 29)]
    [InlineData("quarter", 91)]
    [InlineData("year", 366)]
    public void History_periods_fill_calendar_days_and_include_synthetic_activity(string grain, int days)
    {
        using var data = new TemporaryData();
        using var host = new InputMonitorHost(data.Path);
        var stamp = new DateTimeOffset(2024, 2, 29, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 2, 29)));
        host.Repository.InsertEvents([new InputEventRecord(InputEventKind.LeftClick, 0, stamp, 10, 20, null, null, 0, 0, false, 0)]);
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(host.BuildStatsPayload(
            new StatsQuery { Day = "2024-02-29", Grain = grain, Dimension = "mouse" })));
        var frequencies = payload.RootElement.GetProperty("perDayFrequency");
        Assert.Equal(days, frequencies.GetArrayLength());
        Assert.Equal(1, frequencies.EnumerateArray().Sum(day => day.GetProperty("count").GetInt32()));
        Assert.Equal(1, frequencies.EnumerateArray().Single(day => day.GetProperty("day").GetString() == "2024-02-29").GetProperty("count").GetInt32());
    }

    [Fact]
    public void Retention_purges_old_events_stats_and_tracks_while_preserving_current_data()
    {
        using var data = new TemporaryData();
        using var host = new InputMonitorHost(data.Path);
        var now = DateTimeOffset.Now;
        var old = now.AddDays(-10);
        foreach (var stamp in new[] { old, now })
        {
            var day = EventRepository.DayString(stamp);
            host.Repository.InsertEvents([new InputEventRecord(InputEventKind.LeftClick, 0, stamp, 10, 20, null, null, 0, 0, false, 0)]);
            host.Repository.InsertTrackPoints([new InputEventRecord(InputEventKind.MouseMoveSample, 0, stamp, 10, 20, null, null, 0, 0, false, 10)]);
            host.Repository.MergeDailyStats(day, new DaySummary { Day = day, ClickCount = 1 });
        }
        host.Repository.PurgeExpiredData(1);
        Assert.Equal(0, host.Repository.DaySummaryFor(EventRepository.DayString(old)).ClickCount);
        Assert.Empty(host.Repository.TrackPoints(EventRepository.DayString(old)));
        Assert.Empty(host.Repository.HourlyCounts(EventRepository.DayString(old), [InputEventKinds.LeftClick]));
        Assert.Equal(1, host.Repository.DaySummaryFor(EventRepository.DayString(now)).ClickCount);
        Assert.Single(host.Repository.TrackPoints(EventRepository.DayString(now)));
        Assert.Equal(1, host.Repository.HourlyCounts(EventRepository.DayString(now), [InputEventKinds.LeftClick]).Values.Sum());
    }

    [Fact]
    public void Stop_persists_every_event_already_accepted_and_restart_does_not_replay_it()
    {
        using var data = new TemporaryData();
        var capture = new ControlledCapture { EventsOnStop = 600 };
        using (var host = new InputMonitorHost(data.Path, capture))
        {
            host.Start();
            host.Stop();
            var day = EventRepository.DayString(DateTimeOffset.Now);
            Assert.Equal(600, host.Snapshot().Metrics.ClickCount);
            Assert.Equal(600, host.Repository.DaySummaryFor(day).ClickCount);
            host.Start();
            host.Stop();
            Assert.Equal(600, host.Snapshot().Metrics.ClickCount);
            Assert.Equal(600, host.Repository.DaySummaryFor(day).ClickCount);
        }
        using var reopened = new InputMonitorHost(data.Path);
        Assert.Equal(600, reopened.Repository.DaySummaryFor(EventRepository.DayString(DateTimeOffset.Now)).ClickCount);
    }

    [Fact]
    public void Settings_clamp_propagate_to_capture_and_survive_reopen_with_categories()
    {
        using var data = new TemporaryData();
        var capture = new ControlledCapture();
        using (var host = new InputMonitorHost(data.Path, capture))
        {
            var settings = host.Settings.Clone();
            settings.TrackSampleDistance = 900;
            settings.RestDurationSeconds = 1;
            settings.DataRetentionDays = 0;
            host.ApplySettings(settings);
            host.Categories.SetOverride("notepad.exe", AppCategory.Office);
            Assert.Equal(200, capture.SampleDistance);
            Assert.Equal(10, host.Settings.RestDurationSeconds);
            Assert.Equal(1, host.Settings.DataRetentionDays);
        }
        using var reopened = new InputMonitorHost(data.Path);
        Assert.Equal(200, reopened.Settings.TrackSampleDistance);
        Assert.Equal(10, reopened.Settings.RestDurationSeconds);
        Assert.Equal(AppCategory.Office, reopened.Categories.CategoryFor("NOTEPAD.EXE"));
        reopened.Categories.SetOverride("notepad.exe", null);
        Assert.Equal(AppCategory.Other, reopened.Categories.CategoryFor("notepad.exe"));
    }

    [Fact]
    public void Capture_pause_resume_and_clear_leave_empty_persisted_history()
    {
        using var data = new TemporaryData();
        var capture = new ControlledCapture();
        using (var host = new InputMonitorHost(data.Path, capture))
        {
            host.Start();
            Assert.True(host.CaptureRunning);
            capture.PushClick();
            Assert.True(SpinWait.SpinUntil(() => host.Snapshot().Metrics.ClickCount == 1, TimeSpan.FromSeconds(3)));
            host.SetCaptureRunning(false);
            Assert.False(host.CaptureRunning);
            host.ClearCollectedData();
            Assert.Equal(0, host.Snapshot().Metrics.ClickCount);
            Assert.Equal(0, host.Repository.DaySummaryFor(EventRepository.DayString(DateTimeOffset.Now)).ClickCount);
            host.SetCaptureRunning(true);
            Assert.True(host.CaptureRunning);
            host.Stop();
        }
        using var reopened = new InputMonitorHost(data.Path);
        Assert.Equal(0, reopened.Repository.DaySummaryFor(EventRepository.DayString(DateTimeOffset.Now)).ClickCount);
    }

    [Fact]
    public void Invalid_settings_json_is_preserved_and_defaults_allow_recovery()
    {
        using var data = new TemporaryData();
        var settingsPath = System.IO.Path.Combine(data.Path, "settings.json");
        File.WriteAllText(settingsPath, "{invalid");
        using var host = new InputMonitorHost(data.Path);
        Assert.True(host.Settings.PrivacyMode);
        Assert.Equal("{invalid", File.ReadAllText(settingsPath + ".corrupt"));
        host.ApplySettings(host.Settings.Clone());
        Assert.True(File.Exists(settingsPath));
    }

    private sealed class ControlledCapture : IInputCapture
    {
        public event Action<InputEventRecord>? EventReceived;
        public bool IsRunning { get; private set; }
        public int EventsOnStop { get; set; }
        public double SampleDistance { get; private set; }
        public void Start() => IsRunning = true;
        public void Stop()
        {
            for (var index = 0; index < EventsOnStop; index++) PushClick();
            EventsOnStop = 0;
            IsRunning = false;
        }
        public void PushClick() => EventReceived?.Invoke(new InputEventRecord(
            InputEventKind.LeftClick, 0, DateTimeOffset.Now, 10, 20, null, null, 0, 0, false, 0));
        public void UpdateTrackSampleDistance(double pixels) => SampleDistance = pixels;
        public void Dispose() => Stop();
    }

    private sealed class TemporaryData : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mpt-input-e2e", Guid.NewGuid().ToString("N"));
        public TemporaryData() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            // Dispose closes logical connections; release this fixture's native
            // pooled handle before removing its isolated database on Windows.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = System.IO.Path.Combine(Path, "input-monitor.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString());
            SqliteConnection.ClearPool(connection);
            Directory.Delete(Path, recursive: true);
        }
    }
}
