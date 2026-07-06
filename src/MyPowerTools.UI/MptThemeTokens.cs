using Avalonia;
using Avalonia.Media;

namespace MyPowerTools.UI;

public static class MptThemeTokens
{
    public const double FontSizeTitle = 28;
    public const double FontSizeSection = 18;
    public const double FontSizeCardTitle = 16;
    public const double FontSizeBody = 14;
    public const double FontSizeMeta = 12;
    public const double ControlHeight = 34;

    public const uint ColorAppBackground = 0xfff8fafc;
    public const uint ColorCardBackground = 0xffffffff;
    public const uint ColorSurfaceMuted = 0xfff1f5f9;
    public const uint ColorBorder = 0xffe2e8f0;
    public const uint ColorBorderStrong = 0xffcbd5e1;
    public const uint ColorAccent = 0xff2563eb;
    public const uint ColorAccentHover = 0xff1d4ed8;
    public const uint ColorTextPrimary = 0xff0f172a;
    public const uint ColorTextSecondary = 0xff475569;
    public const uint ColorTextMuted = 0xff64748b;
    public const uint ColorSuccess = 0xff16a34a;
    public const uint ColorWarning = 0xffd97706;
    public const uint ColorWarningBackground = 0xfffff7ed;
    public const uint ColorDanger = 0xffdc2626;
    public const uint ColorInfo = 0xff0891b2;
    public const uint ColorNeutral = ColorTextMuted;
    public const uint ColorAppBackgroundDark = 0xff0b1120;
    public const uint ColorSurfaceDark = 0xff111827;
    public const uint ColorSurfaceRaisedDark = 0xff1f2937;
    public const uint ColorSurfaceMutedDark = 0xff0f172a;
    public const uint ColorBorderDark = 0xff273449;
    public const uint ColorTextPrimaryDark = 0xffe5e7eb;
    public const uint ColorTextSecondaryDark = 0xffcbd5e1;
    public const uint ColorTextMutedDark = 0xff94a3b8;
    public const uint ColorOverlayScrim = 0x990f172a;

    public static readonly Thickness ShellMargin = new(16);
    public static readonly Thickness TopBarMargin = new(16, 10);
    public static readonly Thickness ModuleCardMargin = new(0, 0, 16, 16);
    public static readonly Thickness CardPadding = new(16);
    public static readonly Thickness CompactPadding = new(12);
    public static readonly Thickness FieldPadding = new(10);
    public static readonly Thickness LogRowPadding = new(10, 8);
    public static readonly Thickness BadgePadding = new(8, 2);
    public static readonly Thickness BorderThickness = new(1);
    public static readonly Thickness PermissionPanelMargin = new(0, 0, 0, 10);
    public static readonly Thickness AuditPanelMargin = new(0, 10, 0, 0);
    public static readonly Thickness ButtonPadding = new(14, 7);
    public static readonly Thickness InputPadding = new(12, 8);

    public static readonly CornerRadius CardRadius = new(8);
    public static readonly CornerRadius ControlRadius = new(6);
    public static readonly CornerRadius PanelRadius = new(10);
    public static readonly CornerRadius OverlayRadius = new(14);
    public static readonly CornerRadius PillRadius = new(999);

    public static IBrush Brush(uint argb)
    {
        return new SolidColorBrush(Color.FromUInt32(argb));
    }
}
