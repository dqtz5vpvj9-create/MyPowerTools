using Avalonia.Media;
using Avalonia;

namespace MyPowerTools.UI;

public static class MptTheme
{
    public const double FontSizeTitle = MptThemeTokens.FontSizeTitle;
    public const double FontSizeSection = MptThemeTokens.FontSizeSection;
    public const double FontSizeCardTitle = MptThemeTokens.FontSizeCardTitle;
    public const double FontSizeBody = MptThemeTokens.FontSizeBody;
    public const double FontSizeMeta = MptThemeTokens.FontSizeMeta;
    public const double ControlHeight = MptThemeTokens.ControlHeight;

    public static IBrush AppBackground { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorAppBackground);
    public static IBrush ShellChrome { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorShellChrome);
    public static IBrush CardBackground { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorCardBackground);
    public static IBrush LayerBackground { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorLayer);
    public static IBrush LayerAltBackground { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorLayerAlt);
    public static IBrush SurfaceRaised { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorSurfaceRaised);
    public static IBrush SurfaceMuted { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorSurfaceMuted);
    public static IBrush SurfaceInset { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorSurfaceInset);
    public static IBrush ControlFill { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorControlFill);
    public static IBrush ControlStroke { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorControlStroke);
    public static IBrush Border { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorBorder);
    public static IBrush BorderStrong { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorBorderStrong);
    public static IBrush Accent { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorAccent);
    public static IBrush AccentHover { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorAccentHover);
    public static IBrush AccentPressed { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorAccentPressed);
    public static IBrush TextPrimary { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorTextPrimary);
    public static IBrush TextSecondary { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorTextSecondary);
    public static IBrush TextMuted { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorTextMuted);
    public static IBrush Success { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorSuccess);
    public static IBrush Warning { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorWarning);
    public static IBrush WarningBackground { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorWarningBackground);
    public static IBrush Danger { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorDanger);
    public static IBrush Info { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorInfo);
    public static IBrush Neutral { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorNeutral);

    public static Thickness ShellMargin => MptThemeTokens.ShellMargin;
    public static Thickness TopBarMargin => MptThemeTokens.TopBarMargin;
    public static Thickness ModuleCardMargin => MptThemeTokens.ModuleCardMargin;
    public static Thickness CardPadding => MptThemeTokens.CardPadding;
    public static Thickness CompactPadding => MptThemeTokens.CompactPadding;
    public static Thickness FieldPadding => MptThemeTokens.FieldPadding;
    public static Thickness LogRowPadding => MptThemeTokens.LogRowPadding;
    public static Thickness BadgePadding => MptThemeTokens.BadgePadding;
    public static Thickness BorderThickness => MptThemeTokens.BorderThickness;
    public static Thickness PermissionPanelMargin => MptThemeTokens.PermissionPanelMargin;
    public static Thickness AuditPanelMargin => MptThemeTokens.AuditPanelMargin;
    public static Thickness ButtonPadding => MptThemeTokens.ButtonPadding;
    public static Thickness InputPadding => MptThemeTokens.InputPadding;

    public static CornerRadius CardRadius => MptThemeTokens.CardRadius;
    public static CornerRadius ControlRadius => MptThemeTokens.ControlRadius;
    public static CornerRadius PanelRadius => MptThemeTokens.PanelRadius;
    public static CornerRadius OverlayRadius => MptThemeTokens.OverlayRadius;
    public static CornerRadius PillRadius => MptThemeTokens.PillRadius;

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

    public static void ApplyPalette(Application application, bool dark)
    {
        application.Resources["MptBrushAppBackground"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorAppBackgroundDark : MptThemeTokens.ColorAppBackground);
        application.Resources["MptBrushShellChrome"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorShellChromeDark : MptThemeTokens.ColorShellChrome);
        application.Resources["MptBrushCardBackground"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorCardBackgroundDark : MptThemeTokens.ColorCardBackground);
        application.Resources["MptBrushLayerBackground"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorLayerDark : MptThemeTokens.ColorLayer);
        application.Resources["MptBrushLayerAltBackground"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorLayerAltDark : MptThemeTokens.ColorLayerAlt);
        application.Resources["MptBrushSurfaceRaised"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorSurfaceRaisedDark : MptThemeTokens.ColorSurfaceRaised);
        application.Resources["MptBrushSurfaceMuted"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorSurfaceMutedDark : MptThemeTokens.ColorSurfaceMuted);
        application.Resources["MptBrushSurfaceInset"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorSurfaceInsetDark : MptThemeTokens.ColorSurfaceInset);
        application.Resources["MptBrushControlFillDefault"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorControlFillDark : MptThemeTokens.ColorControlFill);
        application.Resources["MptBrushControlFillHover"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorControlFillHoverDark : MptThemeTokens.ColorControlFillHover);
        application.Resources["MptBrushControlFillPressed"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorControlFillPressedDark : MptThemeTokens.ColorControlFillPressed);
        application.Resources["MptBrushControlStrokeDefault"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorControlStrokeDark : MptThemeTokens.ColorControlStroke);
        application.Resources["MptBrushBorder"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorBorderDark : MptThemeTokens.ColorBorder);
        application.Resources["MptBrushBorderStrong"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorBorderStrongDark : MptThemeTokens.ColorBorderStrong);
        application.Resources["MptBrushDivider"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorDividerDark : MptThemeTokens.ColorDivider);
        application.Resources["MptBrushNavigationBackground"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorNavigationBackgroundDark : MptThemeTokens.ColorNavigationBackground);
        application.Resources["MptBrushNavigationHover"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorNavigationHoverDark : MptThemeTokens.ColorNavigationHover);
        application.Resources["MptBrushNavigationSelected"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorNavigationSelectedDark : MptThemeTokens.ColorNavigationSelected);
        application.Resources["MptBrushAccent"] = MptThemeTokens.Brush(MptThemeTokens.ColorAccent);
        application.Resources["MptBrushAccentHover"] = MptThemeTokens.Brush(MptThemeTokens.ColorAccentHover);
        application.Resources["MptBrushAccentPressed"] = MptThemeTokens.Brush(MptThemeTokens.ColorAccentPressed);
        application.Resources["MptBrushTextPrimary"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorTextPrimaryDark : MptThemeTokens.ColorTextPrimary);
        application.Resources["MptBrushTextSecondary"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorTextSecondaryDark : MptThemeTokens.ColorTextSecondary);
        application.Resources["MptBrushTextMuted"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorTextMutedDark : MptThemeTokens.ColorTextMuted);
        application.Resources["MptBrushNeutral"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorTextMutedDark : MptThemeTokens.ColorTextMuted);
        application.Resources["MptBrushWarningBackground"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorWarningBackgroundDark : MptThemeTokens.ColorWarningBackground);
        application.Resources["MptBrushOverlayScrim"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorOverlayScrimDark : MptThemeTokens.ColorOverlayScrim);
        application.Resources["MptBrushAccentText"] = MptThemeTokens.Brush(0xffffffff);
    }
}
