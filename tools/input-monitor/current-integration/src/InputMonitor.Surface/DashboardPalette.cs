using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace InputMonitor.Surface;

internal static class DashboardPalette
{
    public static readonly Color Page = Color.Parse("#F4F6F9");
    public static readonly Color Card = Colors.White;
    public static readonly Color Inset = Color.Parse("#F4F5F7");
    public static readonly Color HeatEmpty = Color.Parse("#E9EBEF");
    public static readonly Color HeatLow = Color.Parse("#DCE9FF");
    public static readonly Color HeatHigh = Color.Parse("#5E5CE6");
    public static readonly Color AccentBlue = Color.Parse("#0A85FF");
    public static readonly Color AccentIndigo = Color.Parse("#5E5CE6");
    public static readonly Color AccentGreen = Color.Parse("#29C24F");
    public static readonly Color AccentOrange = Color.Parse("#FF9E0A");
    public static readonly Color Secondary = Color.Parse("#6B7280");
    public static readonly Color Axis = Color.Parse("#9CA3AF");
    public static readonly Color Grid = Color.Parse("#E5E7EB");
    public static readonly Color Tag = Color.Parse("#F0F0F0");
    public static readonly Color Neutral = Color.Parse("#B3B3B3");
    public static readonly Color Social = Color.Parse("#FF453A");
    public static readonly Color Media = Color.Parse("#B051DE");

    public static readonly IImmutableSolidColorBrush PageBrush = new ImmutableSolidColorBrush(Page);
    public static readonly IImmutableSolidColorBrush CardBrush = new ImmutableSolidColorBrush(Card);
    public static readonly IImmutableSolidColorBrush InsetBrush = new ImmutableSolidColorBrush(Inset);
    public static readonly IImmutableSolidColorBrush HeatEmptyBrush = new ImmutableSolidColorBrush(HeatEmpty);
    public static readonly IImmutableSolidColorBrush BlueBrush = new ImmutableSolidColorBrush(AccentBlue);
    public static readonly IImmutableSolidColorBrush IndigoBrush = new ImmutableSolidColorBrush(AccentIndigo);
    public static readonly IImmutableSolidColorBrush GreenBrush = new ImmutableSolidColorBrush(AccentGreen);
    public static readonly IImmutableSolidColorBrush SecondaryBrush = new ImmutableSolidColorBrush(Secondary);
    public static readonly IImmutableSolidColorBrush AxisBrush = new ImmutableSolidColorBrush(Axis);
    public static readonly IImmutableSolidColorBrush AppBarBrush = new ImmutableSolidColorBrush(AccentGreen, 0.75);
    public static readonly IImmutableSolidColorBrush BarFillBrush = new ImmutableSolidColorBrush(AccentBlue, 0.75);
    public static readonly IImmutableSolidColorBrush HairlineBrush = new ImmutableSolidColorBrush(Colors.Black, 0.1);

    public static readonly IImmutableSolidColorBrush[] CategoryBrushes =
    [
        new ImmutableSolidColorBrush(AccentBlue),
        new ImmutableSolidColorBrush(AccentIndigo),
        new ImmutableSolidColorBrush(AccentGreen),
        new ImmutableSolidColorBrush(AccentOrange),
        new ImmutableSolidColorBrush(Social),
        new ImmutableSolidColorBrush(Media),
        new ImmutableSolidColorBrush(Neutral)
    ];

    public static readonly ILinearGradientBrush DefaultBarFill = new ImmutableLinearGradientBrush(
        [
            new ImmutableGradientStop(0, AccentBlue),
            new ImmutableGradientStop(1, AccentIndigo)
        ],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
        endPoint: new RelativePoint(1, 0, RelativeUnit.Relative));

    public static readonly ILinearGradientBrush FrequencyFill = new ImmutableLinearGradientBrush(
        [
            new ImmutableGradientStop(0, Color.FromArgb(90, 10, 133, 255)),
            new ImmutableGradientStop(1, Color.FromArgb(12, 94, 92, 230))
        ],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
        endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));

    public static IImmutableSolidColorBrush Fill(Color color, double opacity = 1) =>
        new ImmutableSolidColorBrush(color, opacity);

    public static IImmutableSolidColorBrush CategoryBrush(string category) => category switch
    {
        "开发" or "development" => CategoryBrushes[0],
        "浏览器" or "browser" => CategoryBrushes[1],
        "办公" or "office" => CategoryBrushes[2],
        "设计" or "design" => CategoryBrushes[3],
        "社交" or "social" => CategoryBrushes[4],
        "影音" or "media" => CategoryBrushes[5],
        _ => CategoryBrushes[6]
    };

    public static Color Heat(double value, double max)
    {
        if (max <= 0 || value <= 0)
        {
            return HeatEmpty;
        }

        return Mix(HeatLow, HeatHigh, Math.Min(1, value / max));
    }

    public static Color Mix(Color from, Color to, double t) =>
        Color.FromRgb(
            (byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));

    public static Color CategoryColor(string category) => category switch
    {
        "开发" or "development" => AccentBlue,
        "浏览器" or "browser" => AccentIndigo,
        "办公" or "office" => AccentGreen,
        "设计" or "design" => AccentOrange,
        "社交" or "social" => Social,
        "影音" or "media" => Media,
        _ => Neutral
    };
}
