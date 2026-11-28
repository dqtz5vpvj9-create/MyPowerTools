using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>
/// Crisp 24x24 vector icon system for the mobile shell. Maps both semantic keys and legacy
/// ViewModel glyph strings to razor-sharp Lucide-style paths with harmonious semantic tints,
/// eliminating all raw Unicode dingbat fallbacks.
/// </summary>
internal static class MobileIcons
{
    public readonly record struct IconSpec(string PathData, string LightBg, string LightFg, string DarkBg, string DarkFg, bool Filled = false);

    // 24x24 Lucide-style geometric stroke paths
    private const string PathHome = "M3,10.5 L12,3 L21,10.5 V20 A1,1 0 0,1 20,21 H15 V14.5 H9 V21 H4 A1,1 0 0,1 3,20 Z";
    private const string PathGrid = "M3,3 H10 V10 H3 Z M14,3 H21 V10 H14 Z M14,14 H21 V21 H14 Z M3,14 H10 V21 H3 Z";
    private const string PathDevices = "M3,6 A2,2 0 0,1 5,4 H17 A2,2 0 0,1 19,6 V10 M11,17 H4 A1,1 0 0,1 3,16 V6 M15,10 H20 A1.5,1.5 0 0,1 21.5,11.5 V19.5 A1.5,1.5 0 0,1 20,21 H15 A1.5,1.5 0 0,1 13.5,19.5 V11.5 A1.5,1.5 0 0,1 15,10 Z";
    private const string PathActivity = "M12,6 V12 L16,14 M22,12 A10,10 0 1,1 2,12 A10,10 0 1,1 22,12 Z";
    private const string PathSettings = "M12,15 A3,3 0 1,0 12,9 A3,3 0 1,0 12,15 Z M19.4,15 A1.65,1.65 0 0,0 19.73,16.82 L19.79,16.88 A2,2 0 1,1 16.96,19.71 L16.9,19.65 A1.65,1.65 0 0,0 15.08,19.32 A1.65,1.65 0 0,0 14.08,20.83 L14.08,21 A2,2 0 1,1 10.08,21 L10.08,20.91 A1.65,1.65 0 0,0 9,19.4 A1.65,1.65 0 0,0 7.18,19.73 L7.12,19.79 A2,2 0 1,1 4.29,16.96 L4.35,16.9 A1.65,1.65 0 0,0 4.68,15.08 A1.65,1.65 0 0,0 3.17,14.08 L3,14.08 A2,2 0 1,1 3,10.08 L3.09,10.08 A1.65,1.65 0 0,0 4.6,9 A1.65,1.65 0 0,0 4.27,7.18 L4.21,7.12 A2,2 0 1,1 7.04,4.29 L7.1,4.35 A1.65,1.65 0 0,0 8.92,4.68 L9,4.68 A1.65,1.65 0 0,0 10,3.17 L10,3 A2,2 0 1,1 14,3 L14,3.09 A1.65,1.65 0 0,0 15,4.6 A1.65,1.65 0 0,0 16.82,4.27 L16.88,4.21 A2,2 0 1,1 19.71,7.04 L19.65,7.1 A1.65,1.65 0 0,0 19.32,8.92 L19.32,9 A1.65,1.65 0 0,0 20.83,10 L21,10 A2,2 0 1,1 21,14 L20.91,14 A1.65,1.65 0 0,0 19.4,15 Z";
    private const string PathChevronLeft = "M15,18 L9,12 L15,6";
    private const string PathChevronRight = "M9,18 L15,12 L9,6";
    private const string PathChevronDown = "M6,9 L12,15 L18,9";
    private const string PathChevronUp = "M18,15 L12,9 L6,15";
    private const string PathClose = "M18,6 L6,18 M6,6 L18,18";
    private const string PathPlus = "M12,5 V19 M5,12 H19";
    private const string PathSearch = "M11,19 A8,8 0 1,0 11,3 A8,8 0 1,0 11,19 Z M21,21 L16.65,16.65";
    private const string PathCheck = "M20,6 L9,17 L4,12";
    private const string PathArrowRight = "M5,12 H19 M13,6 L19,12 L13,18";
    private const string PathTransfer = "M7,16 L3,12 L7,8 M3,12 H21 M17,8 L21,12 L17,16";
    private const string PathBell = "M18,8 A6,6 0 0,0 6,8 C6,15 3,17 3,17 H21 C21,17 18,15 18,8 M13.73,21 A2,2 0 0,1 10.27,21";
    private const string PathTerminal = "M4,17 L10,11 L4,5 M12,19 H20";
    private const string PathSun = "M12,16 A4,4 0 1,0 12,8 A4,4 0 1,0 12,16 Z M12,2 V4 M12,20 V22 M4.93,4.93 L6.34,6.34 M17.66,17.66 L19.07,19.07 M2,12 H4 M20,12 H22 M6.34,17.66 L4.93,19.07 M19.07,4.93 L17.66,6.34";
    private const string PathMoon = "M21,12.79 A9,9 0 1,1 11.21,3 A7,7 0 0,0 21,12.79 Z";
    private const string PathAutoTheme = "M12,3 A9,9 0 1,0 12,21 A9,9 0 1,0 12,3 Z M12,3 V21";
    private const string PathPulse = "M22,12 H18 L15,21 L9,3 L6,12 H2";
    private const string PathImage = "M5,3 H19 A2,2 0 0,1 21,5 V19 A2,2 0 0,1 19,21 H5 A2,2 0 0,1 3,19 V5 A2,2 0 0,1 5,3 Z M8.5,10 A1.5,1.5 0 1,0 8.5,7 A1.5,1.5 0 1,0 8.5,10 Z M21,15 L16,10 L5,21";
    private const string PathPhone = "M7,2 H17 A2,2 0 0,1 19,4 V20 A2,2 0 0,1 17,22 H7 A2,2 0 0,1 5,20 V4 A2,2 0 0,1 7,2 Z M12,18 H12.01";
    private const string PathDesktop = "M4,4 H20 A2,2 0 0,1 22,6 V15 A2,2 0 0,1 20,17 H4 A2,2 0 0,1 2,15 V6 A2,2 0 0,1 4,4 Z M8,21 H16 M12,17 V21";
    private const string PathShield = "M12,2 L4,5 V11 C4,16.5 7.4,20.7 12,22 C16.6,20.7 20,16.5 20,11 V5 L12,2 Z M9,12 L11,14 L15,10";
    private const string PathCloud = "M18,10 H16.74 A8,8 0 1,0 9,20 H18 A5,5 0 0,0 18,10 Z";
    private const string PathUpdate = "M21,12 A9,9 0 0,0 6,5.3 L3,8 M3,3 V8 H8 M3,12 A9,9 0 0,0 18,18.7 L21,16 M21,21 V16 H16";
    private const string PathInfo = "M12,22 A10,10 0 1,0 12,2 A10,10 0 1,0 12,22 Z M12,16 V12 M12,8 H12.01";
    private const string PathStar = "M12,2 L15.09,8.26 L22,9.27 L17,14.14 L18.18,21.02 L12,17.77 L5.82,21.02 L7,14.14 L2,9.27 L8.91,8.26 Z";
    private const string PathSliders = "M4,21 V14 M4,10 V3 M12,21 V12 M12,8 V3 M20,21 V16 M20,12 V3 M1,14 H7 M9,8 H15 M17,16 H23";
    private const string PathSpark = "M13,2 L3,14 H12 L11,22 L21,10 H12 L13,2 Z";
    private const string PathFolder = "M3,7 A2,2 0 0,1 5,5 H9.5 L11.5,7.5 H19 A2,2 0 0,1 21,9.5 V18 A2,2 0 0,1 19,20 H5 A2,2 0 0,1 3,18 Z";
    private const string PathStorage = "M4,6 C4,4.34 7.58,3 12,3 C16.42,3 20,4.34 20,6 V18 C20,19.66 16.42,21 12,21 C7.58,21 4,19.66 4,18 Z M4,6 C4,7.66 7.58,9 12,9 C16.42,9 20,7.66 20,6 M4,12 C4,13.66 7.58,15 12,15 C16.42,15 20,13.66 20,12";
    private const string PathTrash = "M3,6 H21 M8,6 V4 A1,1 0 0,1 9,3 H15 A1,1 0 0,1 16,4 V6 M19,6 L18,20 A2,2 0 0,1 16,22 H8 A2,2 0 0,1 6,20 L5,6 M10,11 V17 M14,11 V17";

    public static IconSpec Resolve(string? keyOrGlyph)
    {
        return (keyOrGlyph ?? "").Trim() switch
        {
            "home" or "\u2302" or "⌂" => new(PathHome, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA"),
            "tools" or "\u25A6" or "▦" => new(PathGrid, "#EEF2FF", "#4F46E5", "#23264F", "#818CF8"),
            "devices" or "回" => new(PathDevices, "#ECFDF5", "#059669", "#14332A", "#34D399"),
            "activity" or "\u21BA" or "↺" => new(PathActivity, "#F3EEFF", "#7C3AED", "#2B1F4A", "#A78BFA"),
            "settings" or "⚙" => new(PathSettings, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA"),
            "back" or "\u2039" or "‹" => new(PathChevronLeft, "#F1F5F9", "#1E293B", "#1E2638", "#E2E8F0"),
            "chevron" => new(PathChevronRight, "#F1F5F9", "#94A3B8", "#1E2638", "#64748B"),
            "chevron-down" => new(PathChevronDown, "#F1F5F9", "#64748B", "#1E2638", "#94A3B8"),
            "chevron-up" => new(PathChevronUp, "#F1F5F9", "#64748B", "#1E2638", "#94A3B8"),
            "close" or "\u2715" or "✕" => new(PathClose, "#F1F5F9", "#475569", "#232C3F", "#CBD5E1"),
            "plus" or "+" => new(PathPlus, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA"),
            "search" => new(PathSearch, "#F1F5F9", "#64748B", "#1E2638", "#94A3B8"),
            "check" or "\u2713" or "✓" => new(PathCheck, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA"),
            "arrow-right" => new(PathArrowRight, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA"),

            // Feature & tool categories (with semantic color badges)
            "transfer" or "file-transfer" or "\u21C4" or "⇄" or "↑" or "↓" =>
                new(PathTransfer, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA"),
            "bell" or "remote-notifications" or "\u25CF" or "●" or "🔔" =>
                new(PathBell, "#F3EEFF", "#7C3AED", "#2B1F4A", "#A78BFA"),
            "terminal" or "remote-commands" or ">" or "›" or "›_" =>
                new(PathTerminal, "#ECFDF5", "#059669", "#14332A", "#34D399"),
            "sun" or "light" or "\u2600" or "☀" =>
                new(PathSun, "#FFFBEB", "#D97706", "#3B2D14", "#FBBF24"),
            "moon" or "dark" =>
                new(PathMoon, "#EEF2FF", "#4F46E5", "#23264F", "#818CF8"),
            "appearance" or "auto-theme" or "\u25D0" or "◐" =>
                new(PathAutoTheme, "#F3EEFF", "#7C3AED", "#2B1F4A", "#A78BFA"),
            "pulse" or "input-monitor" or "\u25CE" or "◎" =>
                new(PathPulse, "#ECFEFF", "#0891B2", "#13313B", "#22D3EE"),
            "image" or "paste-image" or "\u25A1" or "□" =>
                new(PathImage, "#FFF1F2", "#E11D48", "#3D1A24", "#FB7185"),
            "phone" or "\u25A3" or "▣" or "📱" =>
                new(PathPhone, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA"),
            "desktop" or "pc" or "💻" or "🖥" or "\u25A2" =>
                new(PathDesktop, "#EEF2FF", "#4F46E5", "#23264F", "#818CF8"),
            "shield" or "\u26E8" or "⛨" =>
                new(PathShield, "#ECFDF5", "#059669", "#14332A", "#34D399"),
            "cloud" or "\u2601" or "☁" =>
                new(PathCloud, "#F0F9FF", "#0284C7", "#132D3F", "#38BDF8"),
            "update" or "refresh" or "\u21BB" or "↻" =>
                new(PathUpdate, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA"),
            "info" =>
                new(PathInfo, "#F1F5F9", "#475569", "#1E2638", "#94A3B8"),
            "sliders" =>
                new(PathSliders, "#F1F5F9", "#475569", "#1E2638", "#94A3B8"),
            "star" or "☆" =>
                new(PathStar, "#FFFBEB", "#94A3B8", "#2B2516", "#64748B"),
            "star-filled" or "★" =>
                new(PathStar, "#FFFBEB", "#F59E0B", "#3B2D14", "#FBBF24", Filled: true),
            "folder" =>
                new(PathFolder, "#ECFDF5", "#059669", "#14332A", "#34D399"),
            "storage" =>
                new(PathStorage, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA"),
            "trash" =>
                new(PathTrash, "#FEF2F2", "#DC2626", "#3B181E", "#F87171"),
            _ =>
                new(PathSpark, "#EBF3FF", "#2563EB", "#1B2E4F", "#60A5FA")
        };
    }

    public static global::Avalonia.Controls.Shapes.Path Icon(
        string? keyOrGlyph,
        double size = 20,
        double strokeThickness = 1.85,
        IBrush? brush = null)
    {
        var spec = Resolve(keyOrGlyph);
        var path = new global::Avalonia.Controls.Shapes.Path
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = strokeThickness,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse(spec.PathData)
        };
        if (brush is not null)
        {
            path.Stroke = brush;
            if (spec.Filled) path.Fill = brush;
        }
        else
        {
            var fg = SolidColorBrush.Parse(spec.LightFg);
            path.Stroke = fg;
            if (spec.Filled) path.Fill = fg;
        }
        return path;
    }

    public static global::Avalonia.Controls.Shapes.Path TabIcon(string pageKey, Button owner)
    {
        var iconKey = pageKey switch
        {
            "mobile.home" => "home",
            "mobile.tools" => "tools",
            "mobile.devices" => "devices",
            "mobile.activity" => "activity",
            _ => "home"
        };
        var spec = Resolve(iconKey);
        var path = new global::Avalonia.Controls.Shapes.Path
        {
            Width = 21,
            Height = 21,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = 1.9,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Data = Geometry.Parse(spec.PathData)
        };
        path.Bind(Shape.StrokeProperty, new global::Avalonia.Data.Binding(nameof(Button.Foreground)) { Source = owner });
        return path;
    }

    public static Border Badge(string? keyOrGlyph, double boxSize = 40, double iconSize = 19, string extraClasses = "")
    {
        var spec = Resolve(keyOrGlyph);
        var fgBrush = SolidColorBrush.Parse(spec.LightFg);
        var bgBrush = SolidColorBrush.Parse(spec.LightBg);
        var icon = Icon(keyOrGlyph, iconSize, 1.9, fgBrush);

        var box = new Border
        {
            Width = boxSize,
            Height = boxSize,
            CornerRadius = new CornerRadius(12),
            Background = bgBrush,
            Child = icon,
            VerticalAlignment = VerticalAlignment.Center
        };
        box.Classes.Add("MptMobileIconBox");
        foreach (var name in extraClasses.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            box.Classes.Add(name);
        }

        // Adapt smoothly to Light/Dark theme changes
        void ApplyTheme()
        {
            var isDark = box.ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;
            box.Background = SolidColorBrush.Parse(isDark ? spec.DarkBg : spec.LightBg);
            var c = SolidColorBrush.Parse(isDark ? spec.DarkFg : spec.LightFg);
            icon.Stroke = c;
            if (spec.Filled) icon.Fill = c;
        }
        box.ActualThemeVariantChanged += (_, _) => ApplyTheme();
        box.AttachedToVisualTree += (_, _) => ApplyTheme();
        return box;
    }
}
