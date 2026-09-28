using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MyPowerTools.MobileNotifications;

/// <summary>
/// The phone design contract as shipped by <c>MyPowerTools.AvaloniaSdk</c> 0.3.0
/// (<c>Themes/MptMobile*.axaml</c>, prototyped in <c>.lavish/mpt-mobile/app.css</c>).
///
/// Every class name and brush key below is the real SDK one, so the page is styled and re-themed by
/// the shared mobile theme instead of a private palette: resources are bound with
/// <see cref="DynamicResourceExtension"/>, which resolves through the ambient
/// <c>ThemeVariant</c> dictionary and therefore follows <c>Application.RequestedThemeVariant</c>
/// or a <c>ThemeVariantScope</c> at runtime. The only locally assigned colours left are data-driven
/// (the per-message icon badge colours that come from the shipped message model).
/// </summary>
internal static class MobileNotificationTheme
{
    // ---------------------------------------------------------------- classes (SDK selectors)

    public const string RootClass = "MptMobileRoot";
    public const string PageClass = "MptMobilePage";
    public const string PageNarrowClass = "MptMobilePageNarrow";
    public const string PageHeaderClass = "MptMobilePageHeader";
    public const string SectionHeaderClass = "MptMobileSectionHeader";
    public const string PageTitleClass = "MptMobilePageTitle";
    public const string PageSubtitleClass = "MptMobilePageSubtitle";
    public const string SectionTitleClass = "MptMobileSectionTitle";
    public const string BodyClass = "MptMobileBody";
    public const string CaptionClass = "MptMobileCaption";
    public const string MetaClass = "MptMobileMeta";
    public const string MicroClass = "MptMobileMicro";
    public const string RowTitleClass = "MptMobileRowTitle";
    public const string RowSubtitleClass = "MptMobileRowSubtitle";
    public const string FieldLabelClass = "MptMobileFieldLabel";
    public const string EmptyTitleClass = "MptMobileEmptyTitle";
    public const string SheetTitleClass = "MptMobileSheetTitle";
    public const string WarningTextClass = "MptMobileWarningText";
    public const string SuccessTextClass = "MptMobileSuccessText";

    public const string CardClass = "MptMobileCard";
    public const string ListCardClass = "MptMobileListCard";
    public const string ListRowClass = "MptMobileListRow";
    public const string RowDividerClass = "MptMobileRowDivider";
    public const string NoticeClass = "MptMobileNotice";
    public const string NoticeWarningClass = "warning";
    public const string PillClass = "MptMobilePill";
    public const string PillTextClass = "MptMobilePillText";
    public const string PillOfflineClass = "offline";
    public const string UnreadDotClass = "MptMobileUnreadDot";
    public const string IconBoxClass = "MptMobileIconBox";
    public const string SearchBoxClass = "MptMobileSearchBox";
    public const string SearchClass = "MptMobileSearch";
    public const string FieldClass = "MptMobileField";
    public const string SwitchClass = "MptMobileSwitch";
    public const string CheckClass = "MptMobileCheck";

    public const string PrimaryClass = "MptMobilePrimary";
    public const string SecondaryClass = "MptMobileSecondary";
    public const string QuietButtonClass = "MptMobileQuietButton";
    public const string TextButtonClass = "MptMobileTextButton";
    public const string IconButtonClass = "MptMobileIconButton";
    public const string CloseButtonClass = "MptMobileCloseButton";
    public const string BackButtonClass = "MptMobileBackButton";
    public const string FilterClass = "MptMobileFilter";
    public const string FilterActiveClass = "active";

    public const string OverlayClass = "MptMobileOverlay";
    public const string SheetClass = "MptMobileSheet";
    public const string SheetGrabberClass = "MptMobileSheetGrabber";

    // ---------------------------------------------------------------- brush tokens (SDK)

    public const string BackgroundBrushKey = "MptMobileBackgroundBrush";
    public const string CardBrushKey = "MptMobileCardBrush";
    public const string TextBrushKey = "MptMobileTextBrush";
    public const string SecondaryTextBrushKey = "MptMobileSecondaryTextBrush";
    public const string AccentBrushKey = "MptMobileAccentBrush";
    public const string DividerBrushKey = "MptMobileDividerBrush";
    public const string AccentSoftBrushKey = "MptMobileAccentSoftBrush";
    public const string SuccessBrushKey = "MptMobileSuccessBrush";
    public const string SuccessSoftBrushKey = "MptMobileSuccessSoftBrush";
    public const string WarningBrushKey = "MptMobileWarningBrush";
    public const string WarningSoftBrushKey = "MptMobileWarningSoftBrush";
    public const string NeutralPillBackgroundBrushKey = "MptMobileNeutralPillBackgroundBrush";
    public const string NeutralPillTextBrushKey = "MptMobileNeutralPillTextBrush";
    public const string IconButtonSizeKey = "MptMobileIconButtonSize";
    public const string ListRowMinHeightKey = "MptMobileListRowMinHeight";
    public const string TouchTargetMinKey = "MptMobileTouchTargetMin";
    public const string PrimaryButtonMinHeightKey = "MptMobilePrimaryButtonMinHeight";
    public const string PageTitleFontSizeKey = "MptMobileFontSizePageTitle";
    public const string BodyFontSizeKey = "MptMobileFontSizeBody";
    public const string CaptionFontSizeKey = "MptMobileFontSizeCaption";
    public const string PagePaddingKey = "MptMobilePagePadding";
    public const string PagePaddingNarrowKey = "MptMobilePagePaddingNarrow";
    public const string IconBoxSizeKey = "MptMobileIconBoxSize";

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

    // ---------------------------------------------------------------- control factories

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

    public static TextBlock PageSubtitle(string text = "") => Text(PageSubtitleClass, text);

    public static TextBlock SectionTitle(string text = "") => Text(SectionTitleClass, text);

    public static TextBlock Body(string text = "") => Text(BodyClass, text);

    public static TextBlock Caption(string text = "") => Text(CaptionClass, text);

    public static TextBlock Meta(string text = "") => Text(MetaClass, text);

    public static TextBlock RowTitle(string text = "") => Text(RowTitleClass, text);

    public static TextBlock RowSubtitle(string text = "") => Text(RowSubtitleClass, text);

    public static TextBlock SheetTitle(string text = "") => Text(SheetTitleClass, text);

    public static TextBlock FieldLabel(string text = "") => Text(FieldLabelClass, text);

    public static Button PrimaryButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(PrimaryClass);
        return button;
    }

    public static Button SecondaryButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(SecondaryClass);
        return button;
    }

    public static Button QuietButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(QuietButtonClass);
        return button;
    }

    public static Button TextButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(TextButtonClass);
        return button;
    }

    public static Button IconButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(IconButtonClass);
        return button;
    }

    public static Button CloseButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(CloseButtonClass);
        return button;
    }

    public static Button BackButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(BackButtonClass);
        return button;
    }

    public static Button FilterButton(string text)
    {
        var button = new Button { Content = text };
        button.Classes.Add(FilterClass);
        return button;
    }

    public static Control RowDivider()
    {
        var divider = new Border();
        divider.Classes.Add(RowDividerClass);
        return divider;
    }

    public static Border UnreadDot()
    {
        var dot = new Border { VerticalAlignment = VerticalAlignment.Center };
        dot.Classes.Add(UnreadDotClass);
        return dot;
    }

    public static Border SheetGrabber()
    {
        var grabber = new Border();
        grabber.Classes.Add(SheetGrabberClass);
        return grabber;
    }

    public static ToggleSwitch Switch()
    {
        var toggle = new ToggleSwitch();
        toggle.Classes.Add(SwitchClass);
        return toggle;
    }

    public static CheckBox Check(string content)
    {
        var check = new CheckBox { Content = content };
        check.Classes.Add(CheckClass);
        return check;
    }

    public static ComboBox ComboBox()
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        box.Classes.Add(FieldClass);
        return box;
    }

    public static TextBox Field(string watermark)
    {
        var box = new TextBox { PlaceholderText = watermark };
        box.Classes.Add(FieldClass);
        return box;
    }

    /// <summary>Status pill: the SDK shape and typography, with real state colours from SDK tokens.</summary>
    public static (Border Pill, TextBlock Text) StatusPill()
    {
        var text = new TextBlock();
        text.Classes.Add(PillTextClass);
        var pill = new Border { Child = text };
        pill.Classes.Add(PillClass);
        return (pill, text);
    }

    /// <summary>Applies the SDK token pair that matches a real connection state.</summary>
    public static void StyleStatusPill(Border pill, TextBlock text, string connectionState)
    {
        var (background, foreground, neutral) = connectionState switch
        {
            "running" => (AccentSoftBrushKey, AccentBrushKey, false),
            "ok" => (SuccessSoftBrushKey, SuccessBrushKey, false),
            "idle" => (NeutralPillBackgroundBrushKey, NeutralPillTextBrushKey, true),
            _ => (WarningSoftBrushKey, WarningBrushKey, false)
        };

        ApplyBackground(pill, background);
        ApplyForeground(text, foreground);
        if (neutral)
        {
            text.Classes.Add(PillOfflineClass);
        }
        else
        {
            text.Classes.Remove(PillOfflineClass);
        }
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
}
