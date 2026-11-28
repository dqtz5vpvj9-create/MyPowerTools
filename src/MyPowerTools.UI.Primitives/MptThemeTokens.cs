using Avalonia;
using Avalonia.Media;

namespace MyPowerTools.UI;

public static class MptThemeTokens
{
    public const double LayoutSidebarWidth = 248;
    public const double LayoutTopBarHeight = 56;
    public const double LayoutPageMaxWidth = 1440;
    public const double LayoutDashboardMaxWidth = 1400;
    public const double LayoutDashboardTwoColumnMinWidth = 940;
    public const double LayoutSettingsMaxWidth = 1040;
    public const double LayoutSettingsTwoColumnMinWidth = 760;
    public const double LayoutSearchMaxWidth = 640;
    public const double FontSizeTitle = 28;
    public const double FontSizeSection = 20;
    public const double FontSizeCardTitle = 16;
    public const double FontSizeBody = 14;
    public const double FontSizeMeta = 12.5;
    public const double FontSizePageHeading = 20;
    public const double ControlHeight = 36;

    public const uint ColorAppBackground = 0xfff4f5f7;
    public const uint ColorShellChrome = 0xfffbfbfc;
    public const uint ColorCardBackground = 0xffffffff;
    public const uint ColorSurfaceRaised = 0xffffffff;
    public const uint ColorSurfaceMuted = 0xfff7f8fa;
    public const uint ColorSurfaceInset = 0xfff1f3f6;
    public const uint ColorLayer = 0xfff7f8fa;
    public const uint ColorLayerAlt = 0xfff1f3f6;
    public const uint ColorControlFill = 0xffffffff;
    public const uint ColorControlFillHover = 0xfff5f6f8;
    public const uint ColorControlFillPressed = 0xffeceef2;
    public const uint ColorBorder = 0xffe6e8ec;
    public const uint ColorBorderStrong = 0xffd4d8df;
    public const uint ColorControlStroke = 0xffd0d5dd;
    public const uint ColorDivider = 0xffebedf0;
    public const uint ColorNavigationBackground = 0xfffbfbfc;
    public const uint ColorNavigationHover = 0xfff0f2f5;
    public const uint ColorNavigationSelected = 0xffeaf1fe;
    public const uint ColorAccent = 0xff2563eb;
    public const uint ColorAccentHover = 0xff1d4ed8;
    public const uint ColorAccentPressed = 0xff1e40af;
    public const uint ColorTextPrimary = 0xff111827;
    public const uint ColorTextSecondary = 0xff4b5563;
    public const uint ColorTextMuted = 0xff6b7280;
    public const uint ColorSuccess = 0xff15803d;
    public const uint ColorWarning = 0xffb45309;
    public const uint ColorWarningBackground = 0xfffef3e2;
    public const uint ColorDanger = 0xffdc2626;
    public const uint ColorInfo = ColorAccent;
    public const uint ColorNeutral = ColorTextMuted;
    public const uint ColorAppBackgroundDark = 0xff141518;
    public const uint ColorShellChromeDark = 0xff1b1d22;
    public const uint ColorCardBackgroundDark = 0xff1f2126;
    public const uint ColorSurfaceDark = 0xff1f2126;
    public const uint ColorSurfaceRaisedDark = 0xff262930;
    public const uint ColorSurfaceMutedDark = 0xff23262c;
    public const uint ColorSurfaceInsetDark = 0xff17191d;
    public const uint ColorLayerDark = 0xff23262c;
    public const uint ColorLayerAltDark = 0xff1f2126;
    public const uint ColorControlFillDark = 0xff262930;
    public const uint ColorControlFillHoverDark = 0xff2d3038;
    public const uint ColorControlFillPressedDark = 0xff22252b;
    public const uint ColorBorderDark = 0xff2c2f36;
    public const uint ColorBorderStrongDark = 0xff3a3e47;
    public const uint ColorControlStrokeDark = 0xff41454f;
    public const uint ColorDividerDark = 0xff2a2d34;
    public const uint ColorNavigationBackgroundDark = 0xff1b1d22;
    public const uint ColorNavigationHoverDark = 0xff262930;
    public const uint ColorNavigationSelectedDark = 0xff1e2b45;
    public const uint ColorTextPrimaryDark = 0xfff3f4f6;
    public const uint ColorTextSecondaryDark = 0xffc3c8d0;
    public const uint ColorTextMutedDark = 0xff8b919b;
    public const uint ColorWarningBackgroundDark = 0xff3a2c12;
    public const uint ColorOverlayScrim = 0x66000000;
    public const uint ColorOverlayScrimDark = 0x99000000;

    public const uint ColorAccentDark = 0xff3b82f6;
    public const uint ColorAccentHoverDark = 0xff5b95f7;
    public const uint ColorAccentPressedDark = 0xff2f6fdb;
    public const uint ColorAccentSoft = 0xffeaf1fe;
    public const uint ColorAccentSoftDark = 0xff1e2b45;
    public const uint ColorSuccessDark = 0xff4ade80;
    public const uint ColorWarningDark = 0xfffbbf24;
    public const uint ColorDangerDark = 0xfff87171;
    public const uint ColorSuccessBackground = 0xffe7f6ed;
    public const uint ColorSuccessBackgroundDark = 0xff16301f;
    public const uint ColorDangerBackground = 0xfffdecec;
    public const uint ColorDangerBackgroundDark = 0xff3b1a1c;
    public const uint ColorInfoBackground = 0xffeaf1fe;
    public const uint ColorInfoBackgroundDark = 0xff1e2b45;
    public const uint ColorNeutralBackground = 0xfff1f3f6;
    public const uint ColorNeutralBackgroundDark = 0xff262930;

    /// <summary>Icon tile tints: (name, light background, light foreground, dark background, dark foreground).</summary>
    public static readonly (string Name, uint LightBackground, uint LightForeground, uint DarkBackground, uint DarkForeground)[] ToolTints =
    [
        ("Blue", 0xffeaf1fe, 0xff2563eb, 0xff1c2a44, 0xff60a5fa),
        ("Violet", 0xfff1ebfe, 0xff7c3aed, 0xff2a2142, 0xffa78bfa),
        ("Green", 0xffe7f6ee, 0xff059669, 0xff14302a, 0xff34d399),
        ("Amber", 0xfffef4e2, 0xffd97706, 0xff352a14, 0xfffbbf24),
        ("Cyan", 0xffe4f6fa, 0xff0891b2, 0xff123139, 0xff22d3ee),
        ("Rose", 0xfffdecef, 0xffe11d48, 0xff3a1a23, 0xfffb7185),
        ("Indigo", 0xffecedfd, 0xff4f46e5, 0xff23264a, 0xff818cf8),
        ("Slate", 0xffeef1f5, 0xff475569, 0xff252a33, 0xff94a3b8)
    ];

    public static readonly IBrush TransparentBrush = Brush(0x00000000);

    public static readonly Thickness ShellMargin = new(12);
    public static readonly Thickness PageMessageMargin = new(32);
    public static readonly Thickness NoThickness = new(0);
    public static readonly Thickness TopBarMargin = new(12, 8);
    public static readonly Thickness ModuleCardMargin = new(0, 0, 12, 12);
    public static readonly Thickness CardPadding = new(20, 16);
    public static readonly Thickness CompactPadding = new(12);
    public static readonly Thickness FieldPadding = new(10, 8);
    public static readonly Thickness LogRowPadding = new(10, 6);
    public static readonly Thickness BadgePadding = new(8, 1);
    public static readonly Thickness BorderThickness = new(1);
    public static readonly Thickness PermissionPanelMargin = new(0, 0, 0, 8);
    public static readonly Thickness AuditPanelMargin = new(0, 8, 0, 0);
    public static readonly Thickness LeftContentMargin = new(24, 0, 0, 0);
    public static readonly Thickness CompactPageMargin = new(18, 16, 18, 24);
    public static readonly Thickness PageMargin = new(28, 24, 28, 28);
    public static readonly Thickness BottomBorderThickness = new(0, 0, 0, 1);
    public static readonly Thickness RightBorderThickness = new(0, 0, 1, 0);
    public static readonly Thickness ShellBrandCompactMargin = new(16, 0);
    public static readonly Thickness ShellBrandExpandedMargin = new(16, 0, 10, 0);
    public static readonly Thickness ShellNavigationCompactMargin = new(0, 12, 0, 0);
    public static readonly Thickness ShellNavigationExpandedMargin = new(10, 12, 10, 0);
    public static readonly Thickness ShellFooterCompactMargin = new(0, 0, 0, 12);
    public static readonly Thickness ShellFooterExpandedMargin = new(10, 0, 10, 12);
    public static readonly Thickness ShellToolListCompactMargin = new(0, 0, 0, 8);
    public static readonly Thickness ShellToolListExpandedMargin = new(10, 0, 10, 8);
    public static readonly Thickness ButtonPadding = new(14, 6);
    public static readonly Thickness InputPadding = new(12, 6);

    public static readonly CornerRadius ControlRadius = new(8);
    public static readonly CornerRadius CardRadius = new(12);
    public static readonly CornerRadius PanelRadius = new(14);
    public static readonly CornerRadius OverlayRadius = new(14);
    public static readonly CornerRadius PillRadius = new(999);

    public static IBrush Brush(uint argb)
    {
        return new SolidColorBrush(Color.FromUInt32(argb));
    }
}
