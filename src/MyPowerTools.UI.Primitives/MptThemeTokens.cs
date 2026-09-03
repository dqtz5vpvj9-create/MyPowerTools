using Avalonia;
using Avalonia.Media;

namespace MyPowerTools.UI;

public static class MptThemeTokens
{
    public const double LayoutSidebarWidth = 240;
    public const double LayoutTopBarHeight = 56;
    public const double LayoutPageMaxWidth = 1440;
    public const double LayoutDashboardMaxWidth = 1400;
    public const double LayoutDashboardTwoColumnMinWidth = 940;
    public const double LayoutSettingsMaxWidth = 1040;
    public const double LayoutSettingsTwoColumnMinWidth = 760;
    public const double LayoutSearchMaxWidth = 640;
    public const double FontSizeTitle = 30;
    public const double FontSizeSection = 22;
    public const double FontSizeCardTitle = 16;
    public const double FontSizeBody = 15;
    public const double FontSizeMeta = 13;
    public const double FontSizePageHeading = 20;
    public const double ControlHeight = 36;

    public const uint ColorAppBackground = 0xfff5f5f5;
    public const uint ColorShellChrome = 0xffffffff;
    public const uint ColorCardBackground = 0xffffffff;
    public const uint ColorSurfaceRaised = 0xffffffff;
    public const uint ColorSurfaceMuted = 0xfff7f7f7;
    public const uint ColorSurfaceInset = 0xfff5f5f5;
    public const uint ColorLayer = 0xfff5f5f5;
    public const uint ColorLayerAlt = 0xfff7f7f7;
    public const uint ColorControlFill = 0xfffbfbfb;
    public const uint ColorControlFillHover = 0xfff6f6f6;
    public const uint ColorControlFillPressed = 0xffeeeeee;
    public const uint ColorBorder = 0xffe5e5e5;
    public const uint ColorBorderStrong = 0xffd1d1d1;
    public const uint ColorControlStroke = 0xffc9c9c9;
    public const uint ColorDivider = 0xffe5e5e5;
    public const uint ColorNavigationBackground = 0xfff5f5f5;
    public const uint ColorNavigationHover = 0xffefefef;
    public const uint ColorNavigationSelected = 0xffe8e8e8;
    public const uint ColorAccent = 0xff0067c0;
    public const uint ColorAccentHover = 0xff005a9e;
    public const uint ColorAccentPressed = 0xff004578;
    public const uint ColorTextPrimary = 0xff1a1a1a;
    public const uint ColorTextSecondary = 0xff5d5d5d;
    public const uint ColorTextMuted = 0xff767676;
    public const uint ColorSuccess = 0xff0f7b0f;
    public const uint ColorWarning = 0xff9d5d00;
    public const uint ColorWarningBackground = 0xfffff4ce;
    public const uint ColorDanger = 0xffc42b1c;
    public const uint ColorInfo = ColorAccent;
    public const uint ColorNeutral = ColorTextMuted;
    public const uint ColorAppBackgroundDark = 0xff202020;
    public const uint ColorShellChromeDark = 0xff252525;
    public const uint ColorCardBackgroundDark = 0xff2b2b2b;
    public const uint ColorSurfaceDark = 0xff2b2b2b;
    public const uint ColorSurfaceRaisedDark = 0xff323232;
    public const uint ColorSurfaceMutedDark = 0xff292929;
    public const uint ColorSurfaceInsetDark = 0xff1c1c1c;
    public const uint ColorLayerDark = 0xff2b2b2b;
    public const uint ColorLayerAltDark = 0xff252525;
    public const uint ColorControlFillDark = 0xff323232;
    public const uint ColorControlFillHoverDark = 0xff3a3a3a;
    public const uint ColorControlFillPressedDark = 0xff242424;
    public const uint ColorBorderDark = 0xff3d3d3d;
    public const uint ColorBorderStrongDark = 0xff525252;
    public const uint ColorControlStrokeDark = 0xff5a5a5a;
    public const uint ColorDividerDark = 0xff3d3d3d;
    public const uint ColorNavigationBackgroundDark = 0xff202020;
    public const uint ColorNavigationHoverDark = 0xff303030;
    public const uint ColorNavigationSelectedDark = 0xff383838;
    public const uint ColorTextPrimaryDark = 0xffffffff;
    public const uint ColorTextSecondaryDark = 0xffd6d6d6;
    public const uint ColorTextMutedDark = 0xff9d9d9d;
    public const uint ColorWarningBackgroundDark = 0xff433519;
    public const uint ColorOverlayScrim = 0x66000000;
    public const uint ColorOverlayScrimDark = 0x99000000;

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
    public static readonly Thickness ButtonPadding = new(14, 6);
    public static readonly Thickness InputPadding = new(12, 6);

    public static readonly CornerRadius ControlRadius = new(6);
    public static readonly CornerRadius CardRadius = new(8);
    public static readonly CornerRadius PanelRadius = new(12);
    public static readonly CornerRadius OverlayRadius = new(12);
    public static readonly CornerRadius PillRadius = new(999);

    public static IBrush Brush(uint argb)
    {
        return new SolidColorBrush(Color.FromUInt32(argb));
    }
}
