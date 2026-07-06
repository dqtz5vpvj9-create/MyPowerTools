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
    public static IBrush CardBackground { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorCardBackground);
    public static IBrush SurfaceMuted { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorSurfaceMuted);
    public static IBrush Border { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorBorder);
    public static IBrush BorderStrong { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorBorderStrong);
    public static IBrush Accent { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorAccent);
    public static IBrush AccentHover { get; } = MptThemeTokens.Brush(MptThemeTokens.ColorAccentHover);
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
        application.Resources["MptBrushCardBackground"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorSurfaceDark : MptThemeTokens.ColorCardBackground);
        application.Resources["MptBrushSurfaceRaised"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorSurfaceRaisedDark : MptThemeTokens.ColorCardBackground);
        application.Resources["MptBrushSurfaceMuted"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorSurfaceMutedDark : MptThemeTokens.ColorSurfaceMuted);
        application.Resources["MptBrushSurfaceInset"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorSurfaceMutedDark : MptThemeTokens.ColorSurfaceMuted);
        application.Resources["MptBrushBorder"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorBorderDark : MptThemeTokens.ColorBorder);
        application.Resources["MptBrushBorderStrong"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorBorderDark : MptThemeTokens.ColorBorderStrong);
        application.Resources["MptBrushTextPrimary"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorTextPrimaryDark : MptThemeTokens.ColorTextPrimary);
        application.Resources["MptBrushTextSecondary"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorTextSecondaryDark : MptThemeTokens.ColorTextSecondary);
        application.Resources["MptBrushTextMuted"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorTextMutedDark : MptThemeTokens.ColorTextMuted);
        application.Resources["MptBrushNeutral"] = MptThemeTokens.Brush(dark ? MptThemeTokens.ColorTextMutedDark : MptThemeTokens.ColorTextMuted);
        application.Resources["MptBrushOverlayScrim"] = MptThemeTokens.Brush(MptThemeTokens.ColorOverlayScrim);
        application.Resources["MptBrushAccentText"] = MptThemeTokens.Brush(0xffffffff);
    }
}
