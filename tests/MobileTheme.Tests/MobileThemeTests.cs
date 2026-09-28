using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace MobileTheme.Tests;

/// <summary>
/// Regression tests for the M1 mobile theme (Themes/MptMobile*.axaml).
///
/// The application built in TestApp.cs already loads the theme through a StyleInclude, so a
/// malformed selector, an unresolved avares:// include or a broken ThemeDictionary fails every
/// test in this class at startup. Beyond that, these tests pin the plan's contract: the six
/// resource keys, the ten style classes plus MptMobileRoot, both theme variants, the 44/48dp
/// touch floor and the text/glyph contrast floor. The final test guards the desktop theme by
/// checking that class-less controls keep the platform theme values.
/// </summary>
public sealed class MobileThemeTests
{
    private static readonly string[] ContractKeys =
    [
        "MptMobileBackgroundBrush",
        "MptMobileCardBrush",
        "MptMobileTextBrush",
        "MptMobileSecondaryTextBrush",
        "MptMobileAccentBrush",
        "MptMobileDividerBrush"
    ];

    // Text pairs must clear 4.5:1; glyph-only pairs must clear 3:1 (WCAG 1.4.3 / 1.4.11).
    // Semi-transparent tokens (scrims, toast, hover overlays) are excluded: their effective
    // contrast depends on what they are composited over.
    private static readonly (string Label, string Foreground, string Background, double Minimum)[] ContrastPairs =
    [
        ("body text on page", "MptMobileTextBrush", "MptMobileBackgroundBrush", 4.5),
        ("body text on card", "MptMobileTextBrush", "MptMobileCardBrush", 4.5),
        ("body text on search field", "MptMobileTextBrush", "MptMobileSearchBackgroundBrush", 4.5),
        ("secondary text on page", "MptMobileSecondaryTextBrush", "MptMobileBackgroundBrush", 4.5),
        ("secondary text on card", "MptMobileSecondaryTextBrush", "MptMobileCardBrush", 4.5),
        ("muted text on page", "MptMobileMutedTextBrush", "MptMobileBackgroundBrush", 4.5),
        ("accent text on card", "MptMobileAccentBrush", "MptMobileCardBrush", 4.5),
        ("accent text on accent soft", "MptMobileAccentBrush", "MptMobileAccentSoftBrush", 4.5),
        ("button text on accent", "MptMobileOnAccentBrush", "MptMobileAccentBrush", 4.5),
        ("button text on accent hover", "MptMobileOnAccentBrush", "MptMobileAccentHoverBrush", 4.5),
        ("button text on accent pressed", "MptMobileOnAccentBrush", "MptMobileAccentPressedBrush", 4.5),
        ("success text on success soft", "MptMobileSuccessBrush", "MptMobileSuccessSoftBrush", 4.5),
        ("warning text on warning soft", "MptMobileWarningBrush", "MptMobileWarningSoftBrush", 4.5),
        ("notice text on notice surface", "MptMobileNoticeTextBrush", "MptMobileAccentSoftBrush", 4.5),
        ("inactive tab label on bar", "MptMobileInactiveTabBrush", "MptMobileCardBrush", 4.5),
        ("offline pill text on pill", "MptMobileNeutralPillTextBrush", "MptMobileNeutralPillBackgroundBrush", 4.5),
        ("offline hint on card", "MptMobileOfflineHintBrush", "MptMobileCardBrush", 4.5),
        ("file thumb label", "MptMobileFileThumbTextBrush", "MptMobileFileThumbBackgroundBrush", 4.5),
        ("photo thumb label", "MptMobileFileThumbPhotoTextBrush", "MptMobileFileThumbPhotoBackgroundBrush", 4.5),
        ("archive thumb label", "MptMobileFileThumbArchiveTextBrush", "MptMobileFileThumbArchiveBackgroundBrush", 4.5),
        ("command output on terminal", "MptMobileCommandTextBrush", "MptMobileCommandSurfaceBrush", 4.5),
        ("chevron on card", "MptMobileChevronBrush", "MptMobileCardBrush", 3.0),
        ("chevron on page", "MptMobileChevronBrush", "MptMobileBackgroundBrush", 3.0),
        ("device glyph on card", "MptMobileDeviceIconBrush", "MptMobileCardBrush", 3.0),
        ("purple glyph on soft", "MptMobilePurpleBrush", "MptMobilePurpleSoftBrush", 3.0),
        ("orange glyph on soft", "MptMobileOrangeBrush", "MptMobileOrangeSoftBrush", 3.0),
        ("status glyph on card", "MptMobileSuccessBrush", "MptMobileCardBrush", 3.0),
        ("focus ring on card", "MptMobileFocusBrush", "MptMobileCardBrush", 3.0),
        ("focus ring on page", "MptMobileFocusBrush", "MptMobileBackgroundBrush", 3.0),
        ("focus ring on accent button", "MptMobileFocusOnAccentBrush", "MptMobileAccentBrush", 3.0)
    ];

    [AvaloniaFact]
    public void Mobile_theme_serves_the_contract_resources_in_both_variants()
    {
        var window = Show(new Border());
        try
        {
            foreach (var key in ContractKeys)
            {
                var light = BrushColor(window, key, ThemeVariant.Light);
                var dark = BrushColor(window, key, ThemeVariant.Dark);
                Assert.NotEqual(light, dark);
            }

            Assert.Equal(Color.Parse("#FFF6F7F9"), BrushColor(window, "MptMobileBackgroundBrush", ThemeVariant.Light));
            Assert.Equal(Color.Parse("#FF171B22"), BrushColor(window, "MptMobileBackgroundBrush", ThemeVariant.Dark));
            Assert.Equal(Color.Parse("#FF0965EE"), BrushColor(window, "MptMobileAccentBrush", ThemeVariant.Light));
            Assert.Equal(Color.Parse("#FF6BA5FF"), BrushColor(window, "MptMobileAccentBrush", ThemeVariant.Dark));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Every_token_is_declared_in_both_theme_dictionaries()
    {
        var xaml = ReadTokenFile();
        var light = KeysOfThemeDictionary(xaml, "Light");
        var dark = KeysOfThemeDictionary(xaml, "Dark");

        Assert.True(light.Count >= 40, $"expected the full mobile token set, found {light.Count}");
        Assert.Equal(light.Count, dark.Count);
        Assert.Empty(light.Except(dark));
        Assert.Empty(dark.Except(light));

        var window = Show(new Border());
        try
        {
            foreach (var key in light)
            {
                Assert.NotNull(TryFind(window, key, ThemeVariant.Light));
                Assert.NotNull(TryFind(window, key, ThemeVariant.Dark));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MptSdkTheme_dictionary_also_carries_the_mobile_tokens()
    {
        var dictionary = new ResourceDictionary();
        dictionary.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://MyPowerTools.AvaloniaSdk/"))
        {
            Source = new Uri("avares://MyPowerTools.AvaloniaSdk/Themes/MptSdkTheme.axaml")
        });

        Assert.True(dictionary.TryGetResource("MptSdkAccentBrush", ThemeVariant.Light, out var sdk) && sdk is not null);
        Assert.True(dictionary.TryGetResource("MptMobileCardBrush", ThemeVariant.Dark, out var mobile) && mobile is not null);
        Assert.Equal(Color.Parse("#FF222731"), Assert.IsAssignableFrom<ISolidColorBrush>(mobile).Color);
    }

    [AvaloniaFact]
    public void Root_and_contract_classes_resolve_to_the_light_prototype_values()
    {
        var page = BuildContractPage();
        var window = Show(page.Root);
        try
        {
            Assert.Equal(Color.Parse("#FFF6F7F9"), ((ISolidColorBrush)page.Root.Background!).Color);
            Assert.Equal(new Thickness(22, 14, 22, 24), page.Page.Margin);

            Assert.Equal(28d, page.PageTitle.FontSize);
            Assert.Equal(FontWeight.SemiBold, page.PageTitle.FontWeight);
            Assert.Equal(Color.Parse("#FF1C2025"), ((ISolidColorBrush)page.PageTitle.Foreground!).Color);
            Assert.Equal(16d, page.SectionTitle.FontSize);
            Assert.Equal(14d, page.Body.FontSize);
            Assert.Equal(12d, page.Caption.FontSize);
            Assert.Equal(Color.Parse("#FF66707C"), ((ISolidColorBrush)page.Caption.Foreground!).Color);
            // Root inheritance: an unclassified TextBlock still reads as mobile body text.
            Assert.Equal(14d, page.Unstyled.FontSize);
            Assert.Equal(Color.Parse("#FF1C2025"), ((ISolidColorBrush)page.Unstyled.Foreground!).Color);

            Assert.Equal(Color.Parse("#FFFFFFFF"), ((ISolidColorBrush)page.Card.Background!).Color);
            Assert.Equal(new CornerRadius(22), page.Card.CornerRadius);
            Assert.Equal(new Thickness(1), page.Card.BorderThickness);
            Assert.Equal(new Thickness(19), page.Card.Padding);

            Assert.Equal(48d, page.Primary.MinHeight);
            Assert.Equal(Color.Parse("#FF0965EE"), ((ISolidColorBrush)page.Primary.Background!).Color);
            Assert.Equal(Color.Parse("#FFFFFFFF"), ((ISolidColorBrush)page.Primary.Foreground!).Color);
            Assert.Equal(new CornerRadius(15), page.Primary.CornerRadius);

            Assert.Equal(46d, page.Secondary.MinHeight);
            Assert.Equal(Color.Parse("#FFEDF3FF"), ((ISolidColorBrush)page.Secondary.Background!).Color);

            Assert.Equal(44d, page.IconButton.Width);
            Assert.Equal(44d, page.IconButton.MinHeight);
            Assert.Equal(72d, page.ListRow.MinHeight);
            Assert.Equal(44d, page.Search.MinHeight);
            Assert.Equal(1d, page.Divider.Height);
            Assert.Equal(58d, page.TabBar.MinHeight);
            Assert.Equal(52d, page.TabButton.MinHeight);
            Assert.Equal(new CornerRadius(29, 29, 0, 0), page.Sheet.CornerRadius);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Dark_variant_recolours_the_same_page_without_page_changes()
    {
        var page = BuildContractPage();
        var window = Show(page.Root, ThemeVariant.Dark);
        try
        {
            Assert.Equal(Color.Parse("#FF171B22"), ((ISolidColorBrush)page.Root.Background!).Color);
            Assert.Equal(Color.Parse("#FFEBEDF2"), ((ISolidColorBrush)page.PageTitle.Foreground!).Color);
            Assert.Equal(Color.Parse("#FFA0AABA"), ((ISolidColorBrush)page.Caption.Foreground!).Color);
            Assert.Equal(Color.Parse("#FF222731"), ((ISolidColorBrush)page.Card.Background!).Color);
            Assert.Equal(Color.Parse("#FF333A46"), ((ISolidColorBrush)page.Card.BorderBrush!).Color);
            Assert.Equal(Color.Parse("#FF6BA5FF"), ((ISolidColorBrush)page.Primary.Background!).Color);
            Assert.Equal(Color.Parse("#FF101B2C"), ((ISolidColorBrush)page.Primary.Foreground!).Color);
            Assert.Equal(Color.Parse("#FF27364E"), ((ISolidColorBrush)page.Secondary.Background!).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Interactive_classes_meet_the_44dp_touch_floor()
    {
        var controls = new (string Class, Control Control)[]
        {
            ("MptMobilePrimary", new Button { Classes = { "MptMobilePrimary" } }),
            ("MptMobileSecondary", new Button { Classes = { "MptMobileSecondary" } }),
            ("MptMobileQuietButton", new Button { Classes = { "MptMobileQuietButton" } }),
            ("MptMobileTextButton", new Button { Classes = { "MptMobileTextButton" } }),
            ("MptMobileBackButton", new Button { Classes = { "MptMobileBackButton" } }),
            ("MptMobileIconButton", new Button { Classes = { "MptMobileIconButton" } }),
            ("MptMobileCloseButton", new Button { Classes = { "MptMobileCloseButton" } }),
            ("MptMobileAvatar", new Button { Classes = { "MptMobileAvatar" } }),
            ("MptMobileAddDevice", new Button { Classes = { "MptMobileAddDevice" } }),
            ("MptMobileFilter", new Button { Classes = { "MptMobileFilter" } }),
            ("MptMobileChip", new Button { Classes = { "MptMobileChip" } }),
            ("MptMobileTabButton", new Button { Classes = { "MptMobileTabButton" } }),
            ("MptMobileListRow", new Button { Classes = { "MptMobileListRow" } }),
            ("MptMobileSettingRow", new Button { Classes = { "MptMobileSettingRow" } }),
            ("MptMobileTargetRow", new Button { Classes = { "MptMobileTargetRow" } }),
            ("MptMobileFileChoice", new Button { Classes = { "MptMobileFileChoice" } }),
            ("MptMobileSearch", new TextBox { Classes = { "MptMobileSearch" } }),
            ("MptMobileField", new TextBox { Classes = { "MptMobileField" } }),
            ("MptMobileSwitch", new ToggleSwitch { Classes = { "MptMobileSwitch" } })
        };

        var panel = new StackPanel();
        foreach (var (_, control) in controls) panel.Children.Add(control);
        var window = Show(new Border { Classes = { "MptMobileRoot" }, Child = panel });
        try
        {
            var failures = new List<string>();
            foreach (var (className, control) in controls)
            {
                if (control.MinHeight < 44) failures.Add($"{className}: MinHeight {control.MinHeight}");
                if (control is Button button && button.MinWidth < 0) failures.Add($"{className}: negative MinWidth");
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
            Assert.Equal(48d, controls[0].Control.MinHeight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Font_token_override_rescales_mobile_type_without_page_changes()
    {
        var title = new TextBlock { Text = "常用", Classes = { "MptMobilePageTitle" } };
        var body = new TextBlock { Text = "正文", Classes = { "MptMobileBody" } };
        var page = new StackPanel { Classes = { "MptMobilePage" }, Children = { title, body } };
        // A host that applies a system font scale only has to override the token on the page
        // root: every class-scoped size resolves through DynamicResource.
        page.Resources["MptMobileFontSizePageTitle"] = 34d;
        page.Resources["MptMobileFontSizeBody"] = 17d;

        var window = Show(new Border { Classes = { "MptMobileRoot" }, Child = page });
        try
        {
            Assert.Equal(34d, title.FontSize);
            Assert.Equal(17d, body.FontSize);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Text_and_glyph_contrast_meets_the_readability_floor()
    {
        var window = Show(new Border());
        try
        {
            var failures = new List<string>();
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                foreach (var (label, foreground, background, minimum) in ContrastPairs)
                {
                    var ratio = ContrastRatio(
                        BrushColor(window, foreground, variant),
                        BrushColor(window, background, variant));
                    if (ratio < minimum)
                        failures.Add($"{variant}: {label} = {ratio:F2}:1 (needs {minimum:F1}:1)");
                }
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Primary_button_lays_out_at_48dp_inside_a_phone_viewport()
    {
        var button = new Button { Content = "发送", Classes = { "MptMobilePrimary" } };
        var window = Show(new Border { Classes = { "MptMobileRoot" }, Child = new StackPanel { Classes = { "MptMobilePage" }, Children = { button } } });
        try
        {
            Assert.True(button.Bounds.Height >= 48, $"primary button height {button.Bounds.Height}");
            Assert.True(button.Bounds.Width <= 390 - 44, $"primary button width {button.Bounds.Width} exceeded the 356dp page column");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Icon_catalog_matches_the_prototype_glyphs_and_stays_inside_the_24dp_box()
    {
        string[] names =
        [
            "Home", "Grid", "Devices", "Activity", "Send", "Receive", "Arrow", "Chevron", "Back", "Close",
            "Plus", "Check", "Laptop", "Desktop", "Phone", "Cloud", "Bell", "Terminal", "Focus", "Clipboard",
            "Search", "Settings", "Sun", "Moon", "Shield", "Link", "Scan", "Image", "Keyboard", "Pulse",
            "Server", "Spark", "Fan", "Code", "Star", "File", "Refresh", "Clock", "Power", "Dots"
        ];

        var window = Show(new Border());
        try
        {
            var failures = new List<string>();
            foreach (var name in names)
            {
                var key = "MptMobileIcon" + name;
                var geometry = Assert.IsAssignableFrom<Geometry>(TryFind(window, key, ThemeVariant.Light));
                var bounds = geometry.Bounds;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    failures.Add($"{key}: empty geometry");
                else if (bounds.X < -0.5 || bounds.Y < -0.5 || bounds.Right > 24.5 || bounds.Bottom > 24.5)
                    failures.Add($"{key}: {bounds} escapes the 24dp design box");
            }

            Assert.True(failures.Count == 0, string.Join("\n", failures));

            // A stroke icon must keep the designed padding instead of being rescaled to fill.
            var icon = new Path { Data = (Geometry)TryFind(window, "MptMobileIconDots", ThemeVariant.Light)!, Classes = { "MptMobileIcon" } };
            var page = new Border { Classes = { "MptMobileRoot" }, Child = icon };
            window.Content = page;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(Stretch.None, icon.Stretch);
            Assert.Equal(24d, icon.Width);
            Assert.Equal(1.65d, icon.StrokeThickness);
            Assert.Equal(Color.Parse("#FF1C2025"), ((ISolidColorBrush)icon.Stroke!).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Desktop_controls_without_mobile_classes_keep_the_platform_theme()
    {
        var button = new Button { Content = "Save" };
        var text = new TextBlock { Text = "Desktop" };
        var border = new Border();
        var window = Show(new StackPanel { Children = { button, text, border } });
        try
        {
            Assert.Null(border.Background);
            Assert.Null(border.BorderBrush);
            Assert.Equal(new CornerRadius(0), border.CornerRadius);
            Assert.NotEqual(48d, button.MinHeight);
            Assert.NotEqual(Color.Parse("#FF0965EE"), ((ISolidColorBrush)button.Background!).Color);
            Assert.NotEqual(28d, text.FontSize);
            Assert.NotEqual(Color.Parse("#FF1C2025"), ((ISolidColorBrush)text.Foreground!).Color);
            Assert.NotEqual(Color.Parse("#FFF6F7F9"), ((ISolidColorBrush)window.Background!).Color);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed record ContractPage(
        Border Root,
        StackPanel Page,
        TextBlock PageTitle,
        TextBlock SectionTitle,
        TextBlock Body,
        TextBlock Caption,
        TextBlock Unstyled,
        Border Card,
        Button Primary,
        Button Secondary,
        Button IconButton,
        Button ListRow,
        TextBox Search,
        Border Divider,
        Border TabBar,
        Button TabButton,
        Border Sheet);

    private static ContractPage BuildContractPage()
    {
        var pageTitle = new TextBlock { Text = "常用", Classes = { "MptMobilePageTitle" } };
        var sectionTitle = new TextBlock { Text = "高频动作", Classes = { "MptMobileSectionTitle" } };
        var body = new TextBlock { Text = "正文", Classes = { "MptMobileBody" } };
        var caption = new TextBlock { Text = "说明", Classes = { "MptMobileCaption" } };
        // No class at all: it must pick up colour and body size from MptMobileRoot inheritance.
        var unstyled = new TextBlock { Text = "未分类文本" };
        var card = new Border { Classes = { "MptMobileCard" } };
        var primary = new Button { Content = "发送", Classes = { "MptMobilePrimary" } };
        var secondary = new Button { Content = "选择文件", Classes = { "MptMobileSecondary" } };
        var iconButton = new Button { Classes = { "MptMobileIconButton" } };
        var listRow = new Button { Classes = { "MptMobileListRow" } };
        var search = new TextBox { Classes = { "MptMobileSearch" } };
        var divider = new Border { Classes = { "MptMobileDivider" } };
        var tabBar = new Border { Classes = { "MptMobileTabBar" } };
        var tabButton = new Button { Classes = { "MptMobileTabButton" } };
        var sheet = new Border { Classes = { "MptMobileSheet" } };
        var page = new StackPanel
        {
            Classes = { "MptMobilePage" },
            Children = { pageTitle, sectionTitle, body, caption, unstyled, card, primary, secondary, iconButton, listRow, search, divider, tabBar, tabButton, sheet }
        };
        var root = new Border { Classes = { "MptMobileRoot" }, Child = page };
        return new ContractPage(root, page, pageTitle, sectionTitle, body, caption, unstyled, card, primary, secondary, iconButton, listRow, search, divider, tabBar, tabButton, sheet);
    }

    private static Window Show(Control content, ThemeVariant? variant = null)
    {
        Control root = variant is null
            ? content
            : new ThemeVariantScope { RequestedThemeVariant = variant, Child = content };
        var window = new Window { Width = 390, Height = 844, Content = root };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static object? TryFind(Window window, string key, ThemeVariant variant)
    {
        Assert.True(window.TryFindResource(key, variant, out var value), $"missing resource {key} for {variant}");
        return value;
    }

    private static Color BrushColor(Window window, string key, ThemeVariant variant) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(TryFind(window, key, variant)).Color;

    private static string ReadTokenFile() =>
        File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "MptMobileTokens.axaml"));

    /// <summary>
    /// Theme dictionaries hold only leaf resources, so the section between the variant marker
    /// and the first closing tag is exactly that variant's key list.
    /// </summary>
    private static List<string> KeysOfThemeDictionary(string xaml, string variant)
    {
        var marker = $"<ResourceDictionary x:Key=\"{variant}\">";
        var start = xaml.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {variant} theme dictionary in the token file");
        var end = xaml.IndexOf("</ResourceDictionary>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"unterminated {variant} theme dictionary");
        var section = xaml[(start + marker.Length)..end];
        Assert.DoesNotContain("<ResourceDictionary", section);
        return Regex.Matches(section, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255d;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }
}
