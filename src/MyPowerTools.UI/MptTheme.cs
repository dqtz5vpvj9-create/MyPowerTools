using Avalonia.Media;

namespace MyPowerTools.UI;

public static class MptTheme
{
    public static IBrush AppBackground { get; } = Brush.Parse("#f7f8fb");
    public static IBrush CardBackground { get; } = Brushes.White;
    public static IBrush Border { get; } = Brush.Parse("#dde2ea");
    public static IBrush Accent { get; } = Brush.Parse("#2563eb");
    public static IBrush TextPrimary { get; } = Brush.Parse("#374151");
    public static IBrush TextSecondary { get; } = Brush.Parse("#586174");
    public static IBrush TextMuted { get; } = Brush.Parse("#6b7280");
    public static IBrush Success { get; } = Brush.Parse("#107c41");
    public static IBrush Warning { get; } = Brush.Parse("#9a6700");
    public static IBrush WarningBackground { get; } = Brush.Parse("#fff8e6");
    public static IBrush Danger { get; } = Brush.Parse("#b42318");
    public static IBrush Neutral { get; } = Brush.Parse("#4b5563");

    public static IBrush StatusBrush(string state)
    {
        return state switch
        {
            "running" => Success,
            "degraded" => Warning,
            "error" => Danger,
            "permission-required" => Warning,
            _ => Neutral
        };
    }
}
