namespace InputMonitor.Core;

/// <summary>
/// Fatigue state machine ported from the macOS InputMonitor FatigueEngine.
/// </summary>
public sealed class FatigueEngine
{
    public const double IdleGapSeconds = 120;
    public const double DefaultThreshold = 100;
    public const double SkipThreshold = 120;

    private readonly MonitorSettings _settings;
    private readonly object _gate = new();
    private DateTimeOffset? _lastActivity;
    private (double Value, double Threshold)? _manualRestBackup;

    public double Value { get; private set; }
    public double Threshold { get; private set; } = DefaultThreshold;
    public bool IsResting { get; private set; }
    public bool IsPaused { get; private set; }
    public int Percentage => (int)Math.Round(Value);
    public Action? OnShouldRemind { get; set; }
    public Action? OnChanged { get; set; }

    public FatigueEngine(MonitorSettings settings)
    {
        _settings = settings;
        Value = settings.FatigueValue;
        Threshold = settings.FatigueThreshold <= 0 ? DefaultThreshold : settings.FatigueThreshold;
        IsPaused = settings.FatigueIsPaused;
    }

    public FatigueSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(Value, Threshold, IsResting, IsPaused, Percentage);
        }
    }

    public void NotifyActivity(FatigueActivitySource source)
    {
        var allowed = source switch
        {
            FatigueActivitySource.Keyboard => _settings.FatigueFromKeyboard,
            FatigueActivitySource.Mouse => _settings.FatigueFromMouse,
            FatigueActivitySource.App => _settings.FatigueFromApp,
            _ => false
        };
        if (!allowed)
        {
            return;
        }

        lock (_gate)
        {
            _lastActivity = DateTimeOffset.Now;
        }
    }

    public void Tick(DateTimeOffset now)
    {
        Action? remind = null;
        lock (_gate)
        {
            if (IsResting)
            {
                return;
            }

            if (_lastActivity is not { } last || (now - last).TotalSeconds > IdleGapSeconds)
            {
                return;
            }

            var pointsPerSecond = 100.0 / Math.Max(1, _settings.RemindIntervalMinutes * 60);
            Value += pointsPerSecond;
            Persist();
            if (Value >= Threshold && !IsPaused)
            {
                remind = OnShouldRemind;
            }
        }

        remind?.Invoke();
        OnChanged?.Invoke();
    }

    public void ManualRest()
    {
        lock (_gate)
        {
            if (IsResting)
            {
                return;
            }

            _manualRestBackup = (Value, Threshold);
        }

        OnShouldRemind?.Invoke();
    }

    public void BeginResting()
    {
        lock (_gate)
        {
            IsResting = true;
        }
    }

    public void Skip()
    {
        lock (_gate)
        {
            if (_manualRestBackup is { } backup)
            {
                Value = backup.Value;
                Threshold = backup.Threshold;
                _manualRestBackup = null;
            }
            else
            {
                Value = DefaultThreshold;
                Threshold = SkipThreshold;
            }

            IsResting = false;
            Persist();
        }

        OnChanged?.Invoke();
    }

    public void RestDone()
    {
        lock (_gate)
        {
            Value = 0;
            Threshold = DefaultThreshold;
            _manualRestBackup = null;
            IsResting = false;
            Persist();
        }

        OnChanged?.Invoke();
    }

    public void SetPaused(bool paused)
    {
        var remind = false;
        lock (_gate)
        {
            IsPaused = paused;
            Persist();
            remind = !paused && _settings.RemindAfterResume && Value >= Threshold;
        }

        if (remind)
        {
            OnShouldRemind?.Invoke();
        }

        OnChanged?.Invoke();
    }

    private void Persist()
    {
        _settings.FatigueValue = Value;
        _settings.FatigueThreshold = Threshold;
        _settings.FatigueIsPaused = IsPaused;
    }
}
