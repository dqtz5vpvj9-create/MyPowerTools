namespace InputMonitor.Core;

public sealed class MonitorSettings
{
    public const int DefaultRemindIntervalMinutes = 20;
    public const int DefaultRestDurationSeconds = 300;
    public const int DefaultDataRetentionDays = 365;
    public const double DefaultTrackSampleDistance = 30;
    public const double DefaultAppHeartbeatSeconds = 30;
    public const double DefaultSoundVolume = 0.8;

    public double RemindIntervalMinutes { get; set; } = DefaultRemindIntervalMinutes;
    public int RestDurationSeconds { get; set; } = DefaultRestDurationSeconds;
    public bool FatigueFromKeyboard { get; set; } = true;
    public bool FatigueFromMouse { get; set; } = true;
    public bool FatigueFromApp { get; set; } = true;
    public bool SoundEnabled { get; set; } = true;
    public double SoundVolume { get; set; } = DefaultSoundVolume;
    public string SoundName { get; set; } = "Asterisk";
    public bool PrivacyMode { get; set; }
    public double TrackSampleDistance { get; set; } = DefaultTrackSampleDistance;
    public double AppHeartbeatSeconds { get; set; } = DefaultAppHeartbeatSeconds;
    public bool RemindAfterResume { get; set; } = true;
    public bool LaunchAtLogin { get; set; }
    public int DataRetentionDays { get; set; } = DefaultDataRetentionDays;
    public double FatigueValue { get; set; }
    public double FatigueThreshold { get; set; } = 100;
    public bool FatigueIsPaused { get; set; }
    public Dictionary<string, AppCategory> CategoryOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Clamp()
    {
        RemindIntervalMinutes = Math.Clamp(RemindIntervalMinutes, 5, 120);
        RestDurationSeconds = Math.Clamp(RestDurationSeconds, 10, 7200);
        SoundVolume = Math.Clamp(SoundVolume, 0, 1);
        TrackSampleDistance = Math.Clamp(TrackSampleDistance, 5, 200);
        AppHeartbeatSeconds = Math.Clamp(AppHeartbeatSeconds, 5, 120);
        DataRetentionDays = Math.Clamp(DataRetentionDays, 1, 36500);
        FatigueThreshold = FatigueThreshold <= 0 ? 100 : FatigueThreshold;
        if (string.IsNullOrWhiteSpace(SoundName))
        {
            SoundName = "Asterisk";
        }
    }

    public MonitorSettings Clone()
    {
        return new MonitorSettings
        {
            RemindIntervalMinutes = RemindIntervalMinutes,
            RestDurationSeconds = RestDurationSeconds,
            FatigueFromKeyboard = FatigueFromKeyboard,
            FatigueFromMouse = FatigueFromMouse,
            FatigueFromApp = FatigueFromApp,
            SoundEnabled = SoundEnabled,
            SoundVolume = SoundVolume,
            SoundName = SoundName,
            PrivacyMode = PrivacyMode,
            TrackSampleDistance = TrackSampleDistance,
            AppHeartbeatSeconds = AppHeartbeatSeconds,
            RemindAfterResume = RemindAfterResume,
            LaunchAtLogin = LaunchAtLogin,
            DataRetentionDays = DataRetentionDays,
            FatigueValue = FatigueValue,
            FatigueThreshold = FatigueThreshold,
            FatigueIsPaused = FatigueIsPaused,
            CategoryOverrides = new Dictionary<string, AppCategory>(CategoryOverrides, StringComparer.OrdinalIgnoreCase)
        };
    }
}
