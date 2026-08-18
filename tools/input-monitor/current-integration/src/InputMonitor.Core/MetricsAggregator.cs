namespace InputMonitor.Core;

/// <summary>
/// In-memory daily counters. Must be fed from a single consumer thread.
/// </summary>
public sealed class MetricsAggregator
{
    public TimeSpan ActiveGapThreshold { get; set; } = TimeSpan.FromSeconds(60);

    public string Day { get; private set; }
    public int KeyCount { get; private set; }
    public int ClickCount { get; private set; }
    public int ScrollCount { get; private set; }
    public double MoveDistance { get; private set; }
    public long KeyDurationMs { get; private set; }
    public double ActiveInputSeconds { get; private set; }
    public double ActiveAppSeconds { get; private set; }

    private int _deltaKeyCount;
    private int _deltaClickCount;
    private int _deltaScrollCount;
    private double _deltaMoveDistance;
    private long _deltaKeyDurationMs;
    private double _deltaActiveInputSeconds;
    private double _deltaActiveAppSeconds;

    private readonly Dictionary<long, ulong> _keyDownTimestamps = [];
    private DateTimeOffset? _lastEventWallTime;
    private readonly HashSet<int> _activeMinutes = [];

    public MetricsAggregator(string? day = null)
    {
        Day = day ?? EventRepository.DayString(DateTimeOffset.Now);
    }

    public double InteractionSeconds => _activeMinutes.Count * 60d;

    public void Process(InputEventRecord record)
    {
        RotateIfNeeded(record.WallTime);
        AccumulateActiveTime(record.WallTime);
        _activeMinutes.Add(MinuteIndex(record.WallTime));

        switch (record.Kind)
        {
            case InputEventKind.KeyDown:
                if (record.IsAutoRepeat)
                {
                    return;
                }

                KeyCount++;
                _deltaKeyCount++;
                if (record.KeyCode is { } code)
                {
                    _keyDownTimestamps[code] = record.TimestampNs;
                }

                break;

            case InputEventKind.KeyUp:
                if (record.KeyCode is not { } upCode ||
                    !_keyDownTimestamps.Remove(upCode, out var downTs))
                {
                    return;
                }

                var ms = (long)((record.TimestampNs - downTs) / 1_000_000);
                if (ms is < 0 or >= 600_000)
                {
                    return;
                }

                KeyDurationMs += ms;
                _deltaKeyDurationMs += ms;
                break;

            case InputEventKind.LeftClick:
            case InputEventKind.RightClick:
                ClickCount++;
                _deltaClickCount++;
                break;

            case InputEventKind.Scroll:
                var lines = Math.Max(1, Math.Abs(record.ScrollDelta));
                ScrollCount += (int)lines;
                _deltaScrollCount += (int)lines;
                break;

            case InputEventKind.MouseMoveSample:
                MoveDistance += record.MoveDelta;
                _deltaMoveDistance += record.MoveDelta;
                break;
        }
    }

    public void ProcessAppHeartbeat(TimeSpan interval, DateTimeOffset now, bool screenLocked)
    {
        RotateIfNeeded(now);
        ActiveAppSeconds += interval.TotalSeconds;
        _deltaActiveAppSeconds += interval.TotalSeconds;
        if (!screenLocked)
        {
            _activeMinutes.Add(MinuteIndex(now));
        }
    }

    public void RestoreInteractionBaseline(IEnumerable<int> minutes)
    {
        foreach (var minute in minutes)
        {
            if (minute is >= 0 and <= 1439)
            {
                _activeMinutes.Add(minute);
            }
        }
    }

    public (string Day, DaySummary Delta) DrainDelta()
    {
        var delta = new DaySummary
        {
            Day = Day,
            KeyCount = _deltaKeyCount,
            ClickCount = _deltaClickCount,
            ScrollCount = _deltaScrollCount,
            KeyDurationMs = _deltaKeyDurationMs,
            MoveDistance = _deltaMoveDistance,
            ActiveInputSeconds = _deltaActiveInputSeconds,
            ActiveAppSeconds = _deltaActiveAppSeconds
        };
        _deltaKeyCount = 0;
        _deltaClickCount = 0;
        _deltaScrollCount = 0;
        _deltaKeyDurationMs = 0;
        _deltaMoveDistance = 0;
        _deltaActiveInputSeconds = 0;
        _deltaActiveAppSeconds = 0;
        return (Day, delta);
    }

    public MetricsSnapshot Snapshot()
    {
        return new MetricsSnapshot(
            Day,
            KeyCount,
            ClickCount,
            ScrollCount,
            MoveDistance,
            KeyDurationMs,
            ActiveInputSeconds,
            ActiveAppSeconds,
            InteractionSeconds);
    }

    private void AccumulateActiveTime(DateTimeOffset wallTime)
    {
        if (_lastEventWallTime is { } last)
        {
            var gap = (wallTime - last).TotalSeconds;
            if (gap is >= 0 and <= 60)
            {
                ActiveInputSeconds += gap;
                _deltaActiveInputSeconds += gap;
            }
        }

        _lastEventWallTime = wallTime;
    }

    private void RotateIfNeeded(DateTimeOffset date)
    {
        var currentDay = EventRepository.DayString(date);
        if (currentDay == Day)
        {
            return;
        }

        Day = currentDay;
        KeyCount = 0;
        ClickCount = 0;
        ScrollCount = 0;
        MoveDistance = 0;
        KeyDurationMs = 0;
        ActiveInputSeconds = 0;
        ActiveAppSeconds = 0;
        _keyDownTimestamps.Clear();
        _lastEventWallTime = null;
        _activeMinutes.Clear();
        _deltaKeyCount = 0;
        _deltaClickCount = 0;
        _deltaScrollCount = 0;
        _deltaKeyDurationMs = 0;
        _deltaMoveDistance = 0;
        _deltaActiveInputSeconds = 0;
        _deltaActiveAppSeconds = 0;
    }

    public static int MinuteIndex(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return (local.Hour * 60) + local.Minute;
    }
}
