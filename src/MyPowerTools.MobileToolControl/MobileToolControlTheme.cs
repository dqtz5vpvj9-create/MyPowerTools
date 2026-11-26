using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MyPowerTools.MobileToolControl;

/// <summary>
/// The shipped mobile theme contract this page is written against
/// (<c>MyPowerTools.AvaloniaSdk</c> <c>Themes/MptMobileTheme.axaml</c>, the same entry point the Android
/// Shell adds on top of Fluent).
///
/// Every control this page builds carries the SDK's own class and this helper never restates a colour,
/// radius, font size or touch metric the theme already owns. Where a brush has to be picked at runtime
/// (a status tone), it binds the SDK's <c>MptMobile*Brush</c> token dynamically, so the value follows
/// the ambient theme variant instead of being frozen at construction time. There is no private palette
/// that could override the theme.
/// </summary>
internal static class MobileToolControlTheme
{
    // ---------------------------------------------------------------- class contract (SDK)

    public const string RootClass = "MptMobileRoot";
    public const string PageClass = "MptMobilePage";
    public const string PageNarrowClass = "MptMobilePageNarrow";
    public const string PageHeaderClass = "MptMobilePageHeader";
    public const string SectionHeaderClass = "MptMobileSectionHeader";
    public const string BottomSpaceClass = "MptMobileBottomSpace";

    public const string PageTitleClass = "MptMobilePageTitle";
    public const string PageSubtitleClass = "MptMobilePageSubtitle";
    public const string SectionTitleClass = "MptMobileSectionTitle";
    public const string CardTitleClass = "MptMobileCardTitle";
    public const string BodyClass = "MptMobileBody";
    public const string CaptionClass = "MptMobileCaption";
    public const string RowTitleClass = "MptMobileRowTitle";
    public const string RowSubtitleClass = "MptMobileRowSubtitle";
    public const string RowMetaClass = "MptMobileRowMeta";
    public const string FieldLabelClass = "MptMobileFieldLabel";
    public const string SheetTitleClass = "MptMobileSheetTitle";
    public const string MonoClass = "MptMobileMono";
    public const string AccentTextClass = "MptMobileAccentText";
    public const string SuccessTextClass = "MptMobileSuccessText";
    public const string WarningTextClass = "MptMobileWarningText";
    public const string FadedTextClass = "MptMobileFadedText";

    public const string CardClass = "MptMobileCard";
    public const string ListCardClass = "MptMobileListCard";
    public const string InsetPanelClass = "MptMobileInsetPanel";
    public const string ListRowClass = "MptMobileListRow";
    public const string NoticeClass = "MptMobileNotice";
    public const string NoticeWarningClass = "warning";
    public const string PillClass = "MptMobilePill";
    public const string PillTextClass = "MptMobilePillText";
    public const string PillNeutralClass = "offline";
    public const string IconBoxClass = "MptMobileIconBox";

    public const string PrimaryClass = "MptMobilePrimary";
    public const string SecondaryClass = "MptMobileSecondary";
    public const string QuietButtonClass = "MptMobileQuietButton";
    public const string IconButtonClass = "MptMobileIconButton";

    public const string FieldClass = "MptMobileField";
    public const string SwitchClass = "MptMobileSwitch";
    public const string ProgressClass = "MptMobileProgress";
    public const string CommandOutputClass = "MptMobileCommandOutput";
    public const string OverlayClass = "MptMobileOverlay";
    public const string SheetClass = "MptMobileSheet";
    public const string SheetGrabberClass = "MptMobileSheetGrabber";

    // ---------------------------------------------------------------- SDK token keys

    public const string BackgroundBrushKey = "MptMobileBackgroundBrush";
    public const string CardBrushKey = "MptMobileCardBrush";
    public const string TextBrushKey = "MptMobileTextBrush";
    public const string SecondaryTextBrushKey = "MptMobileSecondaryTextBrush";
    public const string AccentBrushKey = "MptMobileAccentBrush";
    public const string AccentSoftBrushKey = "MptMobileAccentSoftBrush";
    public const string SuccessBrushKey = "MptMobileSuccessBrush";
    public const string SuccessSoftBrushKey = "MptMobileSuccessSoftBrush";
    public const string WarningBrushKey = "MptMobileWarningBrush";
    public const string WarningSoftBrushKey = "MptMobileWarningSoftBrush";
    public const string NeutralPillBackgroundBrushKey = "MptMobileNeutralPillBackgroundBrush";
    public const string NeutralPillTextBrushKey = "MptMobileNeutralPillTextBrush";
    public const string DividerBrushKey = "MptMobileDividerBrush";
    public const string MonoFontFamilyKey = "MptMobileFontFamilyMono";

    // Metrics the page reads for layout decisions and for its assertions.
    public const string PagePaddingKey = "MptMobilePagePadding";
    public const string PagePaddingNarrowKey = "MptMobilePagePaddingNarrow";
    public const string TouchTargetMinKey = "MptMobileTouchTargetMin";
    public const string PrimaryButtonMinHeightKey = "MptMobilePrimaryButtonMinHeight";
    public const string ListRowMinHeightKey = "MptMobileListRowMinHeight";
    public const string PageTitleFontSizeKey = "MptMobileFontSizePageTitle";
    public const string BodyFontSizeKey = "MptMobileFontSizeBody";
    public const string CaptionFontSizeKey = "MptMobileFontSizeCaption";

    /// <summary>Prototype page padding falls back to the narrow variant below this width.</summary>
    public const double NarrowPageWidth = 340;

    // ---------------------------------------------------------------- resource helpers

    /// <summary>
    /// Binds a brush to the shared SDK token. The binding stays dynamic, so the value follows the
    /// ambient theme variant instead of being frozen at construction time.
    /// </summary>
    public static void ApplyBrush(AvaloniaObject target, AvaloniaProperty<IBrush?> property, string key) =>
        target.Bind(property, new DynamicResourceExtension(key));

    public static void ApplyBackground(Border border, string key) =>
        ApplyBrush(border, Border.BackgroundProperty, key);

    public static void ApplyForeground(TextBlock text, string key) =>
        ApplyBrush(text, TextBlock.ForegroundProperty, key);

    /// <summary>Resolves a token for the current variant (metrics, tests, diagnostics).</summary>
    public static bool TryGetResource<T>(string key, out T? value)
    {
        value = default;
        if (Application.Current is not { } application)
        {
            return false;
        }

        if (!application.TryFindResource(key, application.ActualThemeVariant, out var found) &&
            !application.TryFindResource(key, out found))
        {
            return false;
        }

        if (found is T typed)
        {
            value = typed;
            return true;
        }

        return false;
    }

    public static void SetClass(Control control, string className, bool enabled)
    {
        if (enabled)
        {
            if (!control.Classes.Contains(className))
            {
                control.Classes.Add(className);
            }
        }
        else
        {
            control.Classes.Remove(className);
        }
    }

    /// <summary>Applies the SDK token pair that matches a real state (the M5 status-pill contract).</summary>
    public static void StylePill(Border pill, TextBlock text, string tone)
    {
        var (background, foreground, neutral) = tone switch
        {
            "success" => (SuccessSoftBrushKey, SuccessBrushKey, false),
            "warning" => (WarningSoftBrushKey, WarningBrushKey, false),
            "muted" => (NeutralPillBackgroundBrushKey, NeutralPillTextBrushKey, true),
            _ => (AccentSoftBrushKey, AccentBrushKey, false)
        };

        ApplyBackground(pill, background);
        ApplyForeground(text, foreground);
        SetClass(text, PillNeutralClass, neutral);
    }

    /// <summary>Swaps the SDK text-tone class; the theme supplies the colour for the active variant.</summary>
    public static void ApplyTextTone(TextBlock text, string tone)
    {
        SetClass(text, SuccessTextClass, tone == "success");
        SetClass(text, WarningTextClass, tone is "warning" or "danger");
        SetClass(text, AccentTextClass, tone is "accent" or "neutral");
        SetClass(text, FadedTextClass, tone == "muted");
    }

    // ---------------------------------------------------------------- factories

    public static Border Card(string className = CardClass)
    {
        var card = new Border();
        card.Classes.Add(className);
        return card;
    }

    public static TextBlock Text(string className, string text = "")
    {
        var block = new TextBlock { Text = text };
        block.Classes.Add(className);
        return block;
    }

    public static TextBlock PageTitle(string text = "") => Text(PageTitleClass, text);

    public static TextBlock SectionTitle(string text = "") => Text(SectionTitleClass, text);

    public static TextBlock Body(string text = "") => Text(BodyClass, text);

    public static TextBlock Caption(string text = "") => Text(CaptionClass, text);

    public static Button PrimaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Classes.Add(PrimaryClass);
        return button;
    }

    public static Button SecondaryButton(string text)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Classes.Add(SecondaryClass);
        return button;
    }

    public static Button QuietButton(string text)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Classes.Add(QuietButtonClass);
        return button;
    }

    public static Button IconButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(IconButtonClass);
        return button;
    }

    public static TextBox Field(string placeholder, string text, bool multiline = false)
    {
        var box = new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = multiline
        };
        box.Classes.Add(FieldClass);
        return box;
    }
}
