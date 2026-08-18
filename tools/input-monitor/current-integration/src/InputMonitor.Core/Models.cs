namespace InputMonitor.Core;

public enum InputEventKind
{
    KeyDown,
    KeyUp,
    FlagsChanged,
    LeftClick,
    RightClick,
    Scroll,
    MouseMoveSample
}

public enum AppCategory
{
    Development,
    Browser,
    Office,
    Design,
    Social,
    Media,
    Other
}

public enum FatigueActivitySource
{
    Keyboard,
    Mouse,
    App
}

public static class InputEventKinds
{
    public const string KeyDown = "keyDown";
    public const string KeyUp = "keyUp";
    public const string FlagsChanged = "flagsChanged";
    public const string LeftClick = "leftClick";
    public const string RightClick = "rightClick";
    public const string Scroll = "scroll";
    public const string MouseMoveSample = "mouseMoveSample";

    public static string ToStorage(InputEventKind kind) => kind switch
    {
        InputEventKind.KeyDown => KeyDown,
        InputEventKind.KeyUp => KeyUp,
        InputEventKind.FlagsChanged => FlagsChanged,
        InputEventKind.LeftClick => LeftClick,
        InputEventKind.RightClick => RightClick,
        InputEventKind.Scroll => Scroll,
        InputEventKind.MouseMoveSample => MouseMoveSample,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static InputEventKind Parse(string value) => value switch
    {
        KeyDown => InputEventKind.KeyDown,
        KeyUp => InputEventKind.KeyUp,
        FlagsChanged => InputEventKind.FlagsChanged,
        LeftClick => InputEventKind.LeftClick,
        RightClick => InputEventKind.RightClick,
        Scroll => InputEventKind.Scroll,
        MouseMoveSample => InputEventKind.MouseMoveSample,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null)
    };
}

public static class AppCategories
{
    public static string ToStorage(AppCategory category) => category.ToString().ToLowerInvariant();

    public static string DisplayName(AppCategory category) => category switch
    {
        AppCategory.Development => "开发",
        AppCategory.Browser => "浏览器",
        AppCategory.Office => "办公",
        AppCategory.Design => "设计",
        AppCategory.Social => "社交",
        AppCategory.Media => "影音",
        _ => "其他"
    };

    public static bool TryParse(string? value, out AppCategory category)
    {
        category = AppCategory.Other;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Enum.TryParse(value, ignoreCase: true, out category))
        {
            return true;
        }

        category = AppCategory.Other;
        return false;
    }
}

public sealed record InputEventRecord(
    InputEventKind Kind,
    ulong TimestampNs,
    DateTimeOffset WallTime,
    double? X,
    double? Y,
    long? KeyCode,
    string? Characters,
    ulong Modifiers,
    long ScrollDelta,
    bool IsAutoRepeat,
    double MoveDelta);

public sealed class FrontAppSession
{
    public required string BundleId { get; init; }
    public required string AppName { get; init; }
    public string? WindowTitle { get; set; }
    public required DateTimeOffset Start { get; init; }
    public DateTimeOffset? End { get; set; }
    public required AppCategory Category { get; set; }

    public double? DurationSeconds => End is { } end ? (end - Start).TotalSeconds : null;
}

public sealed class DaySummary
{
    public string Day { get; init; } = "";
    public int KeyCount { get; set; }
    public int ClickCount { get; set; }
    public int ScrollCount { get; set; }
    public long KeyDurationMs { get; set; }
    public double MoveDistance { get; set; }
    public double ActiveInputSeconds { get; set; }
    public double ActiveAppSeconds { get; set; }
}

public sealed record AppUsageSummary(string AppName, string BundleId, AppCategory Category, double TotalSeconds);

public sealed record KeyHeatItem(string Label, int Count);

public sealed record MetricsSnapshot(
    string Day,
    int KeyCount,
    int ClickCount,
    int ScrollCount,
    double MoveDistance,
    long KeyDurationMs,
    double ActiveInputSeconds,
    double ActiveAppSeconds,
    double InteractionSeconds);

public sealed record FatigueSnapshot(
    double Value,
    double Threshold,
    bool IsResting,
    bool IsPaused,
    int Percentage);

public sealed record LiveSnapshot(
    MetricsSnapshot Metrics,
    FatigueSnapshot Fatigue,
    string? FrontAppName,
    bool CaptureRunning,
    bool ScreenLocked);
