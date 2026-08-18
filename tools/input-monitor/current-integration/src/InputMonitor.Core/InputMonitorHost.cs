using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InputMonitor.Core;

public sealed class InputMonitorHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _settingsPath;
    private readonly MonitorDatabase _database;
    private readonly EventRepository _repository;
    private readonly MetricsAggregator _aggregator;
    private readonly EventBuffer _buffer;
    private readonly FatigueEngine _fatigue;
    private readonly ConcurrentQueue<InputEventRecord> _events = new();
    private readonly IInputCapture? _capture;
    private readonly IFrontAppTracker? _frontApp;
    private readonly IRestOverlay? _overlay;
    private readonly Timer _drainTimer;
    private readonly Timer _fatigueTimer;
    private readonly object _settingsGate = new();
    private CancellationTokenSource _lifetime = new();
    private Thread? _consumer;
    private bool _running;
    private bool _disposed;

    public MonitorSettings Settings { get; }
    public AppCategoryMap Categories { get; }

    public InputMonitorHost(
        string dataDirectory,
        IInputCapture? capture = null,
        IFrontAppTracker? frontApp = null,
        IRestOverlay? overlay = null,
        AppCategoryMap? categories = null)
    {
        Directory.CreateDirectory(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, "settings.json");
        Settings = LoadSettings(_settingsPath);
        Categories = categories ?? new AppCategoryMap();
        Categories.ReplaceOverrides(Settings.CategoryOverrides);
        Categories.Changed += PersistSettings;
        _database = new MonitorDatabase(Path.Combine(dataDirectory, "input-monitor.db"));
        _repository = new EventRepository(_database, Categories);
        _aggregator = new MetricsAggregator();
        _aggregator.RestoreInteractionBaseline(_repository.InteractionMinutes(EventRepository.DayString(DateTimeOffset.Now)));
        _buffer = new EventBuffer();
        _buffer.OnFlush = FlushBuffer;
        _fatigue = new FatigueEngine(Settings)
        {
            OnShouldRemind = ShowRest,
            OnChanged = PersistSettings
        };
        _capture = capture;
        _frontApp = frontApp;
        _overlay = overlay;
        if (_capture is not null)
        {
            _capture.EventReceived += Enqueue;
        }

        if (_frontApp is not null)
        {
            _frontApp.SessionCompleted += session => _repository.InsertAppSession(session);
            _frontApp.Heartbeat += (_, _) =>
            {
                _fatigue.NotifyActivity(FatigueActivitySource.App);
                _aggregator.ProcessAppHeartbeat(
                    TimeSpan.FromSeconds(Settings.AppHeartbeatSeconds),
                    DateTimeOffset.Now,
                    _frontApp.ScreenLocked);
            };
        }

        _drainTimer = new Timer(_ => Drain(), null, Timeout.Infinite, Timeout.Infinite);
        _fatigueTimer = new Timer(_ => _fatigue.Tick(DateTimeOffset.Now), null, Timeout.Infinite, Timeout.Infinite);
        _repository.PurgeExpiredData(Settings.DataRetentionDays);
    }

    public bool CaptureRunning => _running && (_capture?.IsRunning ?? false);

    public void Start()
    {
        if (_running)
        {
            return;
        }

        if (_lifetime.IsCancellationRequested)
        {
            _lifetime.Dispose();
            _lifetime = new CancellationTokenSource();
        }

        _running = true;
        _consumer = new Thread(Consume)
        {
            IsBackground = true,
            Name = "InputMonitor.Consumer"
        };
        _consumer.Start();
        _capture?.UpdateTrackSampleDistance(Settings.TrackSampleDistance);
        _capture?.Start();
        _frontApp?.UpdateHeartbeatInterval(TimeSpan.FromSeconds(Settings.AppHeartbeatSeconds));
        _frontApp?.Start();
        _drainTimer.Change(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _fatigueTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        _capture?.Stop();
        _frontApp?.Stop();
        _drainTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _fatigueTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (!_lifetime.IsCancellationRequested)
        {
            _lifetime.Cancel();
        }

        _buffer.Flush();
        Drain();
        _consumer?.Join(TimeSpan.FromSeconds(2));
        _consumer = null;
    }

    public LiveSnapshot Snapshot()
    {
        return new LiveSnapshot(
            _aggregator.Snapshot(),
            _fatigue.Snapshot(),
            _frontApp?.CurrentSession?.AppName,
            CaptureRunning,
            _frontApp?.ScreenLocked ?? false);
    }

    public EventRepository Repository => _repository;
    public FatigueEngine Fatigue => _fatigue;

    public object BuildStatsPayload(string? day, string grain) =>
        BuildStatsPayload(new StatsQuery { Day = day, Grain = grain });

    public object BuildStatsPayload(StatsQuery query) =>
        StatsPayloadBuilder.Build(_repository, Snapshot(), Settings.Clone(), ToMetrics, query);

    public void ApplySettings(MonitorSettings next)
    {
        next.Clamp();
        lock (_settingsGate)
        {
            Settings.RemindIntervalMinutes = next.RemindIntervalMinutes;
            Settings.RestDurationSeconds = next.RestDurationSeconds;
            Settings.FatigueFromKeyboard = next.FatigueFromKeyboard;
            Settings.FatigueFromMouse = next.FatigueFromMouse;
            Settings.FatigueFromApp = next.FatigueFromApp;
            Settings.SoundEnabled = next.SoundEnabled;
            Settings.SoundVolume = next.SoundVolume;
            Settings.SoundName = next.SoundName;
            Settings.PrivacyMode = next.PrivacyMode;
            Settings.TrackSampleDistance = next.TrackSampleDistance;
            Settings.AppHeartbeatSeconds = next.AppHeartbeatSeconds;
            Settings.RemindAfterResume = next.RemindAfterResume;
            Settings.LaunchAtLogin = next.LaunchAtLogin;
            Settings.DataRetentionDays = next.DataRetentionDays;
        }

        _capture?.UpdateTrackSampleDistance(Settings.TrackSampleDistance);
        _frontApp?.UpdateHeartbeatInterval(TimeSpan.FromSeconds(Settings.AppHeartbeatSeconds));
        _repository.PurgeExpiredData(Settings.DataRetentionDays);
        PersistSettings();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            Stop();
        }
        catch
        {
            // Host shutdown must not throw from Dispose.
        }

        _overlay?.Dismiss();
        _buffer.Dispose();
        _drainTimer.Dispose();
        _fatigueTimer.Dispose();
        _capture?.Dispose();
        _frontApp?.Dispose();
        Categories.Changed -= PersistSettings;
        _database.Dispose();
        _lifetime.Dispose();
    }

    private void Enqueue(InputEventRecord record)
    {
        if (Settings.PrivacyMode && record.Kind is InputEventKind.KeyDown or InputEventKind.KeyUp)
        {
            record = record with { Characters = null };
        }

        _events.Enqueue(record);
    }

    private void Consume()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            if (!_events.TryDequeue(out var record))
            {
                Thread.Sleep(8);
                continue;
            }

            _aggregator.Process(record);
            _buffer.Append(record);
            var source = record.Kind switch
            {
                InputEventKind.KeyDown or InputEventKind.KeyUp or InputEventKind.FlagsChanged => FatigueActivitySource.Keyboard,
                _ => FatigueActivitySource.Mouse
            };
            _fatigue.NotifyActivity(source);
        }
    }

    private void FlushBuffer(IReadOnlyList<InputEventRecord> batch)
    {
        _repository.InsertEvents(batch);
        _repository.InsertTrackPoints(batch);
    }

    private void Drain()
    {
        var (day, delta) = _aggregator.DrainDelta();
        if (delta.KeyCount == 0 &&
            delta.ClickCount == 0 &&
            delta.ScrollCount == 0 &&
            delta.KeyDurationMs == 0 &&
            delta.MoveDistance == 0 &&
            delta.ActiveInputSeconds == 0 &&
            delta.ActiveAppSeconds == 0)
        {
            return;
        }

        _repository.MergeDailyStats(day, delta);
    }

    private void ShowRest()
    {
        if (_overlay is { IsShowing: true })
        {
            return;
        }

        _fatigue.BeginResting();
        _overlay?.Show(Settings.RestDurationSeconds, _fatigue.Skip, _fatigue.RestDone);
        if (_overlay is null)
        {
            _fatigue.RestDone();
        }
    }

    private void PersistSettings()
    {
        lock (_settingsGate)
        {
            Settings.CategoryOverrides = new Dictionary<string, AppCategory>(Categories.Overrides, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
    }

    private static MonitorSettings LoadSettings(string path)
    {
        if (!File.Exists(path))
        {
            return new MonitorSettings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<MonitorSettings>(File.ReadAllText(path), JsonOptions) ?? new MonitorSettings();
            loaded.Clamp();
            return loaded;
        }
        catch (JsonException)
        {
            return new MonitorSettings();
        }
    }

    private static MetricsSnapshot ToMetrics(DaySummary summary, double interactionSeconds) =>
        new(
            summary.Day,
            summary.KeyCount,
            summary.ClickCount,
            summary.ScrollCount,
            summary.MoveDistance,
            summary.KeyDurationMs,
            summary.ActiveInputSeconds,
            summary.ActiveAppSeconds,
            interactionSeconds);
}
