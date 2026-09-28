using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace MyPowerTools.Shell.Avalonia.Services.Mobile;

/// <summary>
/// Supplies the six <c>MptMobile*</c> brush keys from the approved palette only when the application
/// theme does not already define them. The mobile design resources are owned by the shared theme;
/// this keeps the phone shell usable (and screenshot-comparable) before that theme is included, and
/// steps aside completely once it is.
/// </summary>
internal static class MobileThemeFallback
{
    private static readonly (string Key, string Light, string Dark)[] Brushes =
    [
        ("MptMobileBackgroundBrush", "#F6F7F9", "#171B22"),
        ("MptMobileCardBrush", "#FFFFFF", "#222731"),
        ("MptMobileTextBrush", "#1C2025", "#EBEDF2"),
        ("MptMobileSecondaryTextBrush", "#69737F", "#A0AABA"),
        ("MptMobileAccentBrush", "#0965EE", "#6BA5FF"),
        ("MptMobileDividerBrush", "#EDF0F3", "#333A46")
    ];

    /// <summary>Additional keys the phone layout uses for status colours and surfaces.</summary>
    private static readonly (string Key, string Light, string Dark)[] SupportBrushes =
    [
        ("MptMobileSuccessBrush", "#248568", "#7BC6A1"),
        ("MptMobileWarningBrush", "#B26A00", "#E0A33E"),
        ("MptMobileErrorBrush", "#C0392B", "#F08A7A"),
        ("MptMobileSoftFillBrush", "#EDF3FF", "#27364E"),
        ("MptMobileTrackBrush", "#E7EBF0", "#2C3340"),
        ("MptMobileOnAccentBrush", "#FFFFFF", "#0B1220"),
        ("MptMobileBackdropBrush", "#66161B22", "#99000000")
    ];

    public static void Apply(Control root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (Application.Current is not { } application)
        {
            return;
        }

        // The shared mobile theme owns the MptMobile* palette. It ships inside Application.Styles, so a
        // plain Application.TryFindResource check would miss it and inject fixed light colours that
        // then shadow the theme's theme-variant brushes - the phone must never do that.
        if (HasSharedMobileTheme(application))
        {
            return;
        }

        var dark = application.ActualThemeVariant == ThemeVariant.Dark;
        foreach (var (key, light, darkValue) in Brushes.Concat(SupportBrushes))
        {
            if (application.TryFindResource(key, out _) || root.Resources.ContainsKey(key))
            {
                continue;
            }

            root.Resources[key] = new SolidColorBrush(Color.Parse(dark ? darkValue : light));
        }
    }

    /// <summary>True when the shared design theme (M1) is part of the application styles.</summary>
    internal static bool HasSharedMobileTheme(Application application)
    {
        if (application.Resources.ContainsKey("MptMobileBackgroundBrush"))
        {
            return true;
        }

        foreach (var style in application.Styles)
        {
            if (style is StyleInclude include &&
                include.Source?.OriginalString.Contains("MptMobileTheme.axaml", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return false;
    }
}
