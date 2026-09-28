using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Shape = Avalonia.Controls.Shapes.Shape;

namespace MyPowerTools.MobileRemoteCommands;

/// <summary>
/// The mobile theme contract this page writes against.
/// </summary>
/// <remarks>
/// M1 owns the real mobile resources/styles in the shared Avalonia SDK
/// (<c>Themes/MptMobileTokens.axaml</c>, <c>MptMobileTypography.axaml</c>, <c>MptMobileSurfaces.axaml</c>,
/// <c>MptMobileControls.axaml</c>). The names here are that contract: the page adds these classes and
/// sets no local colour on them, so the SDK theme always wins when it is present.
///
/// Until <c>MptMobileCardBrush</c> is resolvable from the application resources the page installs a
/// fallback style set built from the approved prototype palette, keyed to exactly the same class names -
/// so the page renders correctly against an older SDK, and nothing else changes when the theme appears.
/// </remarks>
internal static class RemoteCommandsMobileTheme
{
    // ------------------------------------------------------------------ resource keys (M1 tokens)
    public const string BackgroundBrushKey = "MptMobileBackgroundBrush";
    public const string CardBrushKey = "MptMobileCardBrush";
    public const string TextBrushKey = "MptMobileTextBrush";
    public const string SecondaryTextBrushKey = "MptMobileSecondaryTextBrush";
    public const string AccentBrushKey = "MptMobileAccentBrush";
    public const string DividerBrushKey = "MptMobileDividerBrush";

    // ------------------------------------------------------------------ classes (M1 selectors)
    public const string RootClass = "MptMobileRoot";
    public const string PageClass = "MptMobilePage";
    public const string PageNarrowClass = "MptMobilePageNarrow";

    public const string PageTitleClass = "MptMobilePageTitle";
    public const string PageSubtitleClass = "MptMobilePageSubtitle";
    public const string SectionTitleClass = "MptMobileSectionTitle";
    public const string BodyClass = "MptMobileBody";
    public const string CaptionClass = "MptMobileCaption";
    public const string MetaClass = "MptMobileMeta";
    public const string MicroClass = "MptMobileMicro";
    public const string NoteClass = "MptMobileNote";
    public const string MonoClass = "MptMobileMono";
    public const string RowTitleClass = "MptMobileRowTitle";
    public const string RowSubtitleClass = "MptMobileRowSubtitle";
    public const string RowMetaClass = "MptMobileRowMeta";
    public const string FieldLabelClass = "MptMobileFieldLabel";
    public const string SheetTitleClass = "MptMobileSheetTitle";
    public const string EmptyTitleClass = "MptMobileEmptyTitle";
    public const string FadedTextClass = "MptMobileFadedText";
    public const string AccentTextClass = "MptMobileAccentText";
    public const string SuccessTextClass = "MptMobileSuccessText";
    public const string WarningTextClass = "MptMobileWarningText";

    public const string CardClass = "MptMobileCard";
    public const string ListCardClass = "MptMobileListCard";
    public const string InsetPanelClass = "MptMobileInsetPanel";
    public const string CommandOutputClass = "MptMobileCommandOutput";
    public const string IconBoxClass = "MptMobileIconBox";
    public const string NoticeClass = "MptMobileNotice";
    public const string WarningClass = "warning";
    public const string PillClass = "MptMobilePill";
    public const string PillTextClass = "MptMobilePillText";
    public const string OfflineClass = "offline";
    public const string RowDividerClass = "MptMobileRowDivider";
    public const string SearchBoxClass = "MptMobileSearchBox";
    public const string SearchClass = "MptMobileSearch";
    public const string SheetClass = "MptMobileSheet";
    public const string SheetGrabberClass = "MptMobileSheetGrabber";
    public const string OverlayClass = "MptMobileOverlay";
    public const string ProgressClass = "MptMobileProgress";

    public const string PrimaryClass = "MptMobilePrimary";
    public const string SecondaryClass = "MptMobileSecondary";
    public const string IconButtonClass = "MptMobileIconButton";
    public const string QuietButtonClass = "MptMobileQuietButton";
    public const string TextButtonClass = "MptMobileTextButton";
    public const string BackButtonClass = "MptMobileBackButton";
    public const string CloseButtonClass = "MptMobileCloseButton";
    public const string ListRowClass = "MptMobileListRow";
    public const string FieldClass = "MptMobileField";
    public const string CheckClass = "MptMobileCheck";
    public const string IconClass = "MptMobileIcon";
    public const string IconMutedClass = "MptMobileIconMuted";
    public const string IconAccentClass = "MptMobileIconAccent";
    public const string IconSuccessClass = "MptMobileIconSuccess";
    public const string IconWarningClass = "MptMobileIconWarning";

    /// <summary>
    /// True when the shared SDK already provides the mobile theme. The check is deliberately about the
    /// resource keys the plan fixed, not about a type name, so a differently packaged SDK theme is still
    /// detected.
    /// </summary>
    public static bool SdkThemeAvailable => _sdkThemeAvailable ??= IsSdkThemeAvailable(CurrentVariant());

    private static bool? _sdkThemeAvailable;

    /// <summary>The ambient theme variant of the running application (Light when unset).</summary>
    public static ThemeVariant CurrentVariant() =>
        Application.Current?.ActualThemeVariant ?? ThemeVariant.Light;

    /// <summary>
    /// Variant-aware lookup of the contract resource. The mobile tokens are declared per theme variant
    /// (<c>MptMobileTokens.axaml</c> has Light and Dark dictionaries), so a lookup without the variant can
    /// answer for the wrong palette - or miss the resource entirely.
    /// </summary>
    public static bool IsSdkThemeAvailable(ThemeVariant? variant)
    {
        if (Application.Current is not { } application)
        {
            return false;
        }

        try
        {
            return application.TryFindResource(CardBrushKey, variant ?? CurrentVariant(), out _);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Resolves a brush from the SDK theme for the given variant, or null when it is absent.</summary>
    public static IBrush? ResolveBrush(string key, ThemeVariant? variant = null) =>
        Application.Current is { } application &&
        application.TryFindResource(key, variant ?? CurrentVariant(), out var value) &&
        value is IBrush brush
            ? brush
            : null;

    /// <summary>
    /// Every <c>MptMobile*</c> class the fallback style set covers. A test compares the classes the page
    /// actually uses against this list, so a class that would be unstyled without the SDK theme is caught
    /// here instead of on a device.
    /// </summary>
    public static IReadOnlySet<string> FallbackClassNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RootClass, PageClass, PageNarrowClass,
        PageTitleClass, PageSubtitleClass, SectionTitleClass, BodyClass, CaptionClass, MetaClass, MicroClass,
        NoteClass, MonoClass, RowTitleClass, RowSubtitleClass, RowMetaClass, FieldLabelClass, SheetTitleClass,
        EmptyTitleClass, FadedTextClass, AccentTextClass, SuccessTextClass, WarningTextClass,
        CardClass, ListCardClass, InsetPanelClass, CommandOutputClass, IconBoxClass, NoticeClass, WarningClass,
        PillClass, PillTextClass, OfflineClass, RowDividerClass, SearchBoxClass, SearchClass, SheetClass,
        SheetGrabberClass, OverlayClass, ProgressClass,
        PrimaryClass, SecondaryClass, IconButtonClass, QuietButtonClass, TextButtonClass, BackButtonClass,
        CloseButtonClass, ListRowClass, FieldClass, CheckClass,
        IconClass, IconMutedClass, IconAccentClass, IconSuccessClass, IconWarningClass
    };

    /// <summary>The fallback class styles, or <see langword="null"/> when the SDK theme is present.</summary>
    public static Styles? CreateFallbackStyles(bool dark)
    {
        if (SdkThemeAvailable)
        {
            return null;
        }

        var p = new RemoteCommandsMobilePalette(dark);
        var styles = new Styles();

        // ---------------------------------------------------------------- text roles
        Text(styles, PageTitleClass, 28, FontWeight.SemiBold, p.Brush("Text"));
        Text(styles, PageSubtitleClass, 13, FontWeight.Normal, p.Brush("Secondary"));
        Text(styles, SectionTitleClass, 16, FontWeight.SemiBold, p.Brush("Text"));
        Text(styles, BodyClass, 14, FontWeight.Normal, p.Brush("Text"));
        Text(styles, CaptionClass, 12, FontWeight.Normal, p.Brush("Secondary"));
        Text(styles, MetaClass, 11, FontWeight.Normal, p.Brush("Secondary"));
        Text(styles, MicroClass, 10, FontWeight.Normal, p.Brush("Secondary"));
        Text(styles, NoteClass, 12, FontWeight.Normal, p.Brush("Secondary"));
        Text(styles, MonoClass, 11, FontWeight.Normal, p.Brush("TerminalText"), mono: true);
        Text(styles, RowTitleClass, 14, FontWeight.Medium, p.Brush("Text"));
        Text(styles, RowSubtitleClass, 12, FontWeight.Normal, p.Brush("Secondary"));
        Text(styles, RowMetaClass, 11, FontWeight.Normal, p.Brush("Secondary"));
        Text(styles, FieldLabelClass, 12, FontWeight.Normal, p.Brush("Secondary"), margin: new Thickness(0, 15, 0, 7));
        Text(styles, SheetTitleClass, 23, FontWeight.SemiBold, p.Brush("Text"));
        Text(styles, EmptyTitleClass, 16, FontWeight.SemiBold, p.Brush("Text"));
        Text(styles, FadedTextClass, 12, FontWeight.Normal, p.Brush("Secondary"));
        Text(styles, AccentTextClass, 12, FontWeight.Normal, p.Brush("Accent"));
        Text(styles, SuccessTextClass, 12, FontWeight.Normal, p.Brush("Success"));
        Text(styles, WarningTextClass, 12, FontWeight.Normal, p.Brush("Warning"));

        // ---------------------------------------------------------------- surfaces
        Add(
            styles,
            x => x.OfType<Grid>().Class(RootClass),
            (Panel.BackgroundProperty, p.Brush("Page")));
        Add(
            styles,
            x => x.OfType<StackPanel>().Class(PageClass),
            (Layoutable.MarginProperty, new Thickness(22, 14, 22, 24)));
        Add(
            styles,
            x => x.OfType<StackPanel>().Class(PageNarrowClass),
            (Layoutable.MarginProperty, new Thickness(18, 14, 18, 24)));
        Add(
            styles,
            x => x.OfType<Border>().Class(CardClass),
            (Border.BackgroundProperty, p.Brush("Card")),
            (Border.BorderBrushProperty, p.Brush("Divider")),
            (Border.BorderThicknessProperty, new Thickness(1)),
            (Border.CornerRadiusProperty, new CornerRadius(20)),
            (Border.PaddingProperty, new Thickness(16)));
        Add(
            styles,
            x => x.OfType<Border>().Class(ListCardClass),
            (Border.BackgroundProperty, p.Brush("Card")),
            (Border.BorderBrushProperty, p.Brush("Divider")),
            (Border.BorderThicknessProperty, new Thickness(1)),
            (Border.CornerRadiusProperty, new CornerRadius(20)),
            (Border.PaddingProperty, new Thickness(0)),
            (Border.ClipToBoundsProperty, true));
        Add(
            styles,
            x => x.OfType<Border>().Class(InsetPanelClass),
            (Border.BackgroundProperty, p.Brush("Inset")),
            (Border.CornerRadiusProperty, new CornerRadius(14)),
            (Border.PaddingProperty, new Thickness(12)));
        Add(
            styles,
            x => x.OfType<Border>().Class(CommandOutputClass),
            (Border.BackgroundProperty, p.Brush("Terminal")),
            (Border.CornerRadiusProperty, new CornerRadius(15)),
            (Border.PaddingProperty, new Thickness(14)));
        Add(
            styles,
            x => x.OfType<Border>().Class(IconBoxClass),
            (Border.BackgroundProperty, p.Brush("AccentSoft")),
            (Border.CornerRadiusProperty, new CornerRadius(11)),
            (Border.WidthProperty, 36d),
            (Border.HeightProperty, 36d));
        Add(
            styles,
            x => x.OfType<Border>().Class(NoticeClass),
            (Border.BackgroundProperty, p.Brush("AccentSoft")),
            (Border.CornerRadiusProperty, new CornerRadius(14)),
            (Border.PaddingProperty, new Thickness(14)));
        Add(
            styles,
            x => x.OfType<Border>().Class(NoticeClass).Class(WarningClass),
            (Border.BackgroundProperty, p.Brush("WarningSurface")));
        Add(
            styles,
            x => x.OfType<Border>().Class(PillClass),
            (Border.BackgroundProperty, p.Brush("SuccessSoft")),
            (Border.CornerRadiusProperty, new CornerRadius(20)),
            (Border.PaddingProperty, new Thickness(8, 5)));
        Add(
            styles,
            x => x.OfType<Border>().Class(PillClass).Class(OfflineClass),
            (Border.BackgroundProperty, p.Brush("Inset")));
        Add(
            styles,
            x => x.OfType<Border>().Class(SearchBoxClass),
            (Border.BackgroundProperty, p.Brush("Search")),
            (Border.CornerRadiusProperty, new CornerRadius(12)),
            (Border.MinHeightProperty, 44d),
            (Border.PaddingProperty, new Thickness(13, 0)));
        Add(
            styles,
            x => x.OfType<Border>().Class(SheetClass),
            (Border.BackgroundProperty, p.Brush("Card")),
            (Border.CornerRadiusProperty, new CornerRadius(28, 28, 0, 0)),
            (Border.PaddingProperty, new Thickness(22, 12, 22, 26)));
        Add(
            styles,
            x => x.OfType<Border>().Class(OverlayClass),
            (Border.BackgroundProperty, p.Brush("Scrim")));
        Add(
            styles,
            x => x.OfType<Border>().Class(SheetGrabberClass),
            (Border.BackgroundProperty, p.Brush("Divider")),
            (Border.WidthProperty, 32d),
            (Border.HeightProperty, 4d),
            (Border.CornerRadiusProperty, new CornerRadius(2)),
            (Border.HorizontalAlignmentProperty, HorizontalAlignment.Center),
            (Border.MarginProperty, new Thickness(0, 0, 0, 14)));
        Add(
            styles,
            x => x.OfType<Border>().Class(RowDividerClass),
            (Border.BorderBrushProperty, p.Brush("Divider")),
            (Border.BorderThicknessProperty, new Thickness(0, 1, 0, 0)),
            (Border.MarginProperty, new Thickness(15, 0, 15, 0)));

        // ---------------------------------------------------------------- controls
        Add(
            styles,
            x => x.OfType<Button>().Class(PrimaryClass),
            (Button.BackgroundProperty, p.Brush("Accent")),
            (Button.ForegroundProperty, p.Brush("AccentText")),
            (Button.CornerRadiusProperty, new CornerRadius(15)),
            (Button.MinHeightProperty, 48d),
            (Button.PaddingProperty, new Thickness(16, 12)),
            (Button.FontSizeProperty, 15d),
            (Button.FontWeightProperty, FontWeight.SemiBold),
            (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
            (Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Add(
            styles,
            x => x.OfType<Button>().Class(SecondaryClass),
            (Button.BackgroundProperty, p.Brush("Inset")),
            (Button.ForegroundProperty, p.Brush("Text")),
            (Button.CornerRadiusProperty, new CornerRadius(14)),
            (Button.MinHeightProperty, 44d),
            (Button.PaddingProperty, new Thickness(14, 10)),
            (Button.FontSizeProperty, 13d),
            (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
            (Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Add(
            styles,
            x => x.OfType<Button>().Class(IconButtonClass),
            (Button.BackgroundProperty, Brushes.Transparent),
            (Button.ForegroundProperty, p.Brush("Text")),
            (Button.CornerRadiusProperty, new CornerRadius(22)),
            (Button.WidthProperty, 44d),
            (Button.HeightProperty, 44d),
            (Button.MinWidthProperty, 44d),
            (Button.MinHeightProperty, 44d),
            (Button.PaddingProperty, new Thickness(0)),
            (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
            (Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Add(
            styles,
            x => x.OfType<Button>().Class(BackButtonClass),
            (Button.BackgroundProperty, Brushes.Transparent),
            (Button.ForegroundProperty, p.Brush("Accent")),
            (Button.BorderThicknessProperty, new Thickness(0)),
            (Button.CornerRadiusProperty, new CornerRadius(14)),
            (Button.MinHeightProperty, 44d),
            (Button.PaddingProperty, new Thickness(5)),
            (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
            (Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Add(
            styles,
            x => x.OfType<Button>().Class(CloseButtonClass),
            (Button.BackgroundProperty, p.Brush("Inset")),
            (Button.ForegroundProperty, p.Brush("Secondary")),
            (Button.BorderThicknessProperty, new Thickness(0)),
            (Button.CornerRadiusProperty, new CornerRadius(22)),
            (Button.WidthProperty, 44d),
            (Button.HeightProperty, 44d),
            (Button.MinWidthProperty, 44d),
            (Button.MinHeightProperty, 44d),
            (Button.PaddingProperty, new Thickness(0)),
            (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center),
            (Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Add(
            styles,
            x => x.OfType<Button>().Class(QuietButtonClass),
            (Button.BackgroundProperty, Brushes.Transparent),
            (Button.ForegroundProperty, p.Brush("Secondary")),
            (Button.BorderThicknessProperty, new Thickness(0)),
            (Button.CornerRadiusProperty, new CornerRadius(14)),
            (Button.MinHeightProperty, 44d),
            (Button.PaddingProperty, new Thickness(8, 4)),
            (Button.FontSizeProperty, 13d),
            (Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch),
            (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        Add(
            styles,
            x => x.OfType<Button>().Class(TextButtonClass),
            (Button.BackgroundProperty, Brushes.Transparent),
            (Button.ForegroundProperty, p.Brush("Accent")),
            (Button.BorderThicknessProperty, new Thickness(0)),
            (Button.CornerRadiusProperty, new CornerRadius(14)),
            (Button.MinHeightProperty, 44d),
            (Button.PaddingProperty, new Thickness(4, 0)),
            (Button.FontSizeProperty, 13d),
            (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        Add(
            styles,
            x => x.OfType<Button>().Class(ListRowClass),
            (Button.BackgroundProperty, Brushes.Transparent),
            (Button.BorderThicknessProperty, new Thickness(0)),
            (Button.MinHeightProperty, 72d),
            (Button.PaddingProperty, new Thickness(15, 12)),
            (Button.CornerRadiusProperty, new CornerRadius(0)),
            (Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch),
            (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            (Button.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Add(
            styles,
            x => x.OfType<TextBox>().Class(FieldClass),
            (TextBox.MinHeightProperty, 44d),
            (TextBox.FontSizeProperty, 14d),
            (TextBox.CornerRadiusProperty, new CornerRadius(12)),
            (TextBox.BorderBrushProperty, p.Brush("Border")));
        Add(
            styles,
            x => x.OfType<TextBox>().Class(SearchClass),
            (TextBox.BackgroundProperty, Brushes.Transparent),
            (TextBox.BorderThicknessProperty, new Thickness(0)),
            (TextBox.MinHeightProperty, 40d),
            (TextBox.FontSizeProperty, 13d));
        Add(
            styles,
            x => x.OfType<ProgressBar>().Class(ProgressClass),
            (ProgressBar.ForegroundProperty, p.Brush("Accent")),
            (ProgressBar.BackgroundProperty, p.Brush("Divider")),
            (ProgressBar.MinHeightProperty, 6d));
        Add(
            styles,
            x => x.OfType<ShapePath>().Class(IconClass),
            (Shape.StrokeProperty, p.Brush("Text")),
            (Shape.StrokeThicknessProperty, 1.65d),
            (Shape.StrokeLineCapProperty, PenLineCap.Round),
            (Shape.StrokeJoinProperty, PenLineJoin.Round),
            (Shape.WidthProperty, 24d),
            (Shape.HeightProperty, 24d),
            (Shape.StretchProperty, Stretch.None));
        Add(
            styles,
            x => x.OfType<ShapePath>().Class(IconMutedClass),
            (Shape.StrokeProperty, p.Brush("Secondary")));
        Add(
            styles,
            x => x.OfType<ShapePath>().Class(IconAccentClass),
            (Shape.StrokeProperty, p.Brush("Accent")));
        Add(
            styles,
            x => x.OfType<ShapePath>().Class(IconSuccessClass),
            (Shape.StrokeProperty, p.Brush("Success")));
        Add(
            styles,
            x => x.OfType<ShapePath>().Class(IconWarningClass),
            (Shape.StrokeProperty, p.Brush("Warning")));

        return styles;
    }

    private static void Text(
        Styles styles,
        string className,
        double fontSize,
        FontWeight weight,
        IBrush brush,
        bool mono = false,
        Thickness? margin = null)
    {
        var setters = new List<(AvaloniaProperty, object)>
        {
            (TextBlock.FontSizeProperty, fontSize),
            (TextBlock.FontWeightProperty, weight),
            (TextBlock.ForegroundProperty, brush),
            (TextBlock.TextWrappingProperty, TextWrapping.Wrap)
        };

        if (mono)
        {
            setters.Add((TextBlock.FontFamilyProperty, new FontFamily("Cascadia Mono, Consolas, monospace")));
        }

        if (margin is { } value)
        {
            setters.Add((Layoutable.MarginProperty, value));
        }

        Add(styles, x => x.OfType<TextBlock>().Class(className), setters.ToArray());
    }

    private static void Add(Styles styles, Func<Selector?, Selector> selector, params (AvaloniaProperty Property, object Value)[] setters)
    {
        var style = new Style(selector);
        foreach (var (property, value) in setters)
        {
            style.Setters.Add(new Setter(property, value));
        }

        styles.Add(style);
    }
}

/// <summary>
/// Page palette for everything the SDK class contract does not cover (terminal text, sheet scrim, stage
/// marks, tone colours, the pre-M1 fallback styles). Values follow the approved prototype's table.
/// </summary>
internal sealed record RemoteCommandsMobilePalette(bool Dark)
{
    public static RemoteCommandsMobilePalette ForTheme(string? theme) =>
        new(string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase));

    /// <summary>Palette for the ambient theme variant, used when the theme switches on a live page.</summary>
    public static RemoteCommandsMobilePalette ForThemeVariant(ThemeVariant? variant) =>
        new(variant == ThemeVariant.Dark);

    public static RemoteCommandsMobilePalette Current { get; set; } = new(false);

    public IBrush Brush(string role) => new SolidColorBrush(Color.Parse(ColorFor(role)));

    private string ColorFor(string role) => (Dark, role) switch
    {
        (false, "Page") => "#F6F7F9",
        (false, "Card") => "#FFFFFF",
        (false, "Inset") => "#F0F2F5",
        (false, "Search") => "#E9ECF0",
        (false, "Divider") => "#EDF0F3",
        (false, "Border") => "#DCE2EA",
        (false, "Text") => "#1C2025",
        (false, "Muted") => "#69737F",
        (false, "Secondary") => "#69737F",
        (false, "Accent") => "#0965EE",
        (false, "AccentSoft") => "#EDF3FF",
        (false, "AccentText") => "#FFFFFF",
        (false, "Success") => "#248568",
        (false, "SuccessSoft") => "#EDF6F0",
        (false, "Warning") => "#8D6B3D",
        (false, "WarningSurface") => "#FBF2E6",
        (false, "Error") => "#C0392B",
        (false, "ErrorSurface") => "#FDECEA",
        (false, "Terminal") => "#1F2836",
        (false, "TerminalText") => "#E6EDF9",
        (false, "Scrim") => "#0F172A66",
        (true, "Page") => "#171B22",
        (true, "Card") => "#222731",
        (true, "Inset") => "#2A313D",
        (true, "Search") => "#262D38",
        (true, "Divider") => "#333A46",
        (true, "Border") => "#3B434F",
        (true, "Text") => "#EBEDF2",
        (true, "Muted") => "#A0AABA",
        (true, "Secondary") => "#A0AABA",
        (true, "Accent") => "#6BA5FF",
        (true, "AccentSoft") => "#27364E",
        (true, "AccentText") => "#101B2C",
        (true, "Success") => "#7BC6A1",
        (true, "SuccessSoft") => "#263F37",
        (true, "Warning") => "#E2CDA7",
        (true, "WarningSurface") => "#423A2E",
        (true, "Error") => "#FCA5A5",
        (true, "ErrorSurface") => "#4A2A2A",
        (true, "Terminal") => "#161C26",
        (true, "TerminalText") => "#E6EDF9",
        (true, "Scrim") => "#00000099",
        _ => "#00000000"
    };
}
