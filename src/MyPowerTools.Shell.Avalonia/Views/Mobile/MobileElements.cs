using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace MyPowerTools.Shell.Avalonia.Views.Mobile;

/// <summary>
/// Builders for the phone page vocabulary (cards, rows, chips, sheets). They apply the agreed
/// <c>MptMobile*</c> classes and bind colours to the shared <c>MptMobile*Brush</c> keys so the phone
/// pages stay on the one mobile theme instead of restating colours per page.
/// </summary>
internal static class MobileElements
{
    public static TextBlock PageTitle(string text) => Text(text, "MptMobilePageTitle");

    public static TextBlock SectionTitle(string text) => Text(text, "MptMobileSectionTitle");

    public static TextBlock Body(string text) => Text(text, "MptMobileBody");

    public static TextBlock Caption(string text) => Text(text, "MptMobileCaption");

    public static TextBlock Text(string text, string classes)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        foreach (var name in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            block.Classes.Add(name);
        }

        return block;
    }

    /// <summary>A single surface card.</summary>
    public static Border Card(Control child, string extraClasses = "")
    {
        var border = new Border { Child = child };
        border.Classes.Add("MptMobileCard");
        foreach (var name in extraClasses.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            border.Classes.Add(name);
        }

        border.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileCardBrush"));
        border.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("MptMobileDividerBrush"));
        return border;
    }

    /// <summary>A card whose children stack vertically and are separated by hairlines.</summary>
    public static Border ListCard(params Control[] children)
    {
        var stack = new StackPanel();
        for (var index = 0; index < children.Length; index++)
        {
            if (index > 0)
            {
                var divider = new Border { Height = 1 };
                divider.Classes.Add("MptMobileDivider");
                divider.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileDividerBrush"));
                stack.Children.Add(divider);
            }

            stack.Children.Add(children[index]);
        }

        return Card(stack, "MptMobileListCard");
    }

    public static Button Primary(string text, ICommand? command, string? automationName = null)
    {
        var button = new Button
        {
            Content = text,
            Command = command,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Classes.Add("MptMobilePrimary");
        AutomationProperties.SetName(button, automationName ?? text);
        return button;
    }

    public static Button Secondary(string text, ICommand? command, string? automationName = null)
    {
        var button = new Button
        {
            Content = text,
            Command = command,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        button.Classes.Add("MptMobileSecondary");
        AutomationProperties.SetName(button, automationName ?? text);
        return button;
    }

    public static Button TextButton(string text, ICommand? command, string? automationName = null)
    {
        var button = new Button { Content = text, Command = command };
        button.Classes.Add("MptMobileTextButton");
        AutomationProperties.SetName(button, automationName ?? text);
        return button;
    }

    public static Button IconButton(string glyph, string automationName, ICommand? command)
    {
        var button = new Button { Content = MobileIcons.Icon(glyph, 18, 1.9), Command = command };
        button.Classes.Add("MptMobileIconButton");
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    public static Button BackButton(ICommand command, string label = "返回")
    {
        var icon = MobileIcons.Icon("back", 18, 2.1);
        icon.Bind(global::Avalonia.Controls.Shapes.Shape.StrokeProperty, new DynamicResourceExtension("MptMobileAccentBrush"));
        var text = new TextBlock { Text = label, FontSize = 15, FontWeight = FontWeight.Medium, VerticalAlignment = VerticalAlignment.Center };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileAccentBrush"));
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(text);
        var button = new Button { Content = content, Command = command, HorizontalContentAlignment = HorizontalAlignment.Left };
        button.Classes.Add("MptMobileBack");
        button.Classes.Add("MptMobileBackButton");
        AutomationProperties.SetName(button, label);
        return button;
    }

    /// <summary>The tinted vector icon badge used by every list row, tile and hero.</summary>
    public static Border IconBox(string glyph, string extraClasses = "") =>
        MobileIcons.Badge(glyph, boxSize: 40, iconSize: 19, extraClasses: extraClasses);

    public static Border StatusPill(string text)
    {
        var (lightBg, lightFg, darkBg, darkFg) = text switch
        {
            "运行中" or "已连接" or "在线" => ("#ECFDF5", "#047857", "#123529", "#34D399"),
            "可用" or "当前" or "连接码" or "检查" => ("#EBF3FF", "#1D4ED8", "#1B2E4F", "#60A5FA"),
            "异常" or "故障" or "离线" => ("#FEF2F2", "#B91C1C", "#3D1A1E", "#F87171"),
            _ => ("#F1F5F9", "#475569", "#1E2638", "#94A3B8")
        };
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Foreground = SolidColorBrush.Parse(lightFg),
            VerticalAlignment = VerticalAlignment.Center
        };
        var pill = new Border
        {
            Child = label,
            Background = SolidColorBrush.Parse(lightBg),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(9, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        pill.Classes.Add("MptMobileStatusPill");
        void ApplyTheme()
        {
            var isDark = pill.ActualThemeVariant == global::Avalonia.Styling.ThemeVariant.Dark;
            pill.Background = SolidColorBrush.Parse(isDark ? darkBg : lightBg);
            label.Foreground = SolidColorBrush.Parse(isDark ? darkFg : lightFg);
        }
        pill.ActualThemeVariantChanged += (_, _) => ApplyTheme();
        pill.AttachedToVisualTree += (_, _) => ApplyTheme();
        return pill;
    }

    /// <summary>A full-width tappable row: icon, title, subtitle, optional trailing text.</summary>
    public static Button ListRow(
        string? glyph,
        string title,
        string subtitle,
        string? meta,
        ICommand? command,
        string? automationName = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        if (!string.IsNullOrEmpty(glyph))
        {
            var box = IconBox(glyph!);
            Grid.SetColumn(box, 0);
            grid.Children.Add(box);
        }

        var copy = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Text(title, "MptMobileRowTitle"));
        if (!string.IsNullOrEmpty(subtitle))
        {
            copy.Children.Add(Text(subtitle, "MptMobileRowSubtitle"));
        }

        copy.Margin = string.IsNullOrEmpty(glyph) ? default : new Thickness(12, 0, 0, 0);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        var trailing = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        if (!string.IsNullOrEmpty(meta))
        {
            if (meta!.Length <= 6 && !meta.Contains(':'))
            {
                trailing.Children.Add(StatusPill(meta));
            }
            else
            {
                var tail = Text(meta, "MptMobileRowMeta");
                tail.VerticalAlignment = VerticalAlignment.Center;
                trailing.Children.Add(tail);
            }
        }
        if (command is not null)
        {
            var chev = MobileIcons.Icon("chevron", 15, 1.8);
            chev.Bind(global::Avalonia.Controls.Shapes.Shape.StrokeProperty, new DynamicResourceExtension("MptMobileChevronBrush"));
            trailing.Children.Add(chev);
        }
        if (trailing.Children.Count > 0)
        {
            Grid.SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }

        var button = new Button
        {
            Content = grid,
            Command = command,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        button.Classes.Add("MptMobileListRow");
        AutomationProperties.SetName(button, automationName ?? $"{title} {subtitle}".Trim());
        return button;
    }

    public static TextBox SearchBox(string watermark, string? text, Action<string> onChanged)
    {
        var box = new TextBox { PlaceholderText = watermark, Text = text ?? "" };
        box.Classes.Add("MptMobileSearch");
        AutomationProperties.SetName(box, watermark);
        box.TextChanged += (_, _) => onChanged(box.Text ?? "");
        return box;
    }

    public static Button Chip(string label, bool selected, ICommand? command)
    {
        var chip = new Button { Content = label, Command = command };
        chip.Classes.Add("MptMobileChip");
        chip.Classes.Set("selected", selected);
        AutomationProperties.SetName(chip, label);
        return chip;
    }

    /// <summary>A notice strip; used for real explanations, never for fabricated success.</summary>
    public static Border Banner(string message, string classes = "")
    {
        var border = new Border { Child = Text(message, "MptMobileBody") };
        border.Classes.Add("MptMobileBanner");
        foreach (var name in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            border.Classes.Add(name);
        }

        return border;
    }

    /// <summary>Empty state with the next real step.</summary>
    public static Control EmptyState(string title, string detail, string? actionLabel, ICommand? action)
    {
        var stack = EmptyContainer();
        stack.Children.Add(EmptyTitle(title));
        stack.Children.Add(EmptyDetail(detail));
        if (!string.IsNullOrWhiteSpace(actionLabel))
        {
            var button = Secondary(actionLabel!, action);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            button.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(button);
        }

        return stack;
    }

    public static StackPanel EmptyContainer()
    {
        var stack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Classes.Add("MptMobileEmpty");
        return stack;
    }

    public static TextBlock EmptyTitle(string text = "") => Text(text, "MptMobileEmptyTitle");

    public static TextBlock EmptyDetail(string text = "") => Text(text, "MptMobileEmptyDetail");

    public static ScrollViewer Scroller(Control content)
    {
        var scroller = new ScrollViewer
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden
        };
        scroller.Classes.Add("MptMobileScroller");
        return scroller;
    }

    /// <summary>
    /// Page body: a scroller with the phone page class applied. Below 340 dp the page uses the
    /// tighter 18 dp margin from the design contract instead of 22 dp.
    /// </summary>
    public static Control Page(params Control[] children)
    {
        var stack = new StackPanel();
        stack.Classes.Add("MptMobilePage");
        foreach (var child in children)
        {
            stack.Children.Add(child);
        }

        var scroller = Scroller(stack);
        scroller.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width > 0 && e.NewSize.Width < 340;
            stack.Classes.Set("narrow", narrow);
            stack.Classes.Set("MptMobilePageNarrow", narrow);
        };
        return scroller;
    }

    public static Control Section(string title, Control content)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Classes.Add("MptMobileSection");
        stack.Children.Add(SectionTitle(title));
        stack.Children.Add(content);
        return stack;
    }

    public static Control SectionHeader(string title, Control? action = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var heading = SectionTitle(title);
        heading.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(heading, 0);
        grid.Children.Add(heading);
        if (action is not null)
        {
            Grid.SetColumn(action, 1);
            grid.Children.Add(action);
        }

        grid.Classes.Add("MptMobileSectionHeader");
        return grid;
    }

    /// <summary>A two-value detail pair from the prototype's detail grid.</summary>
    public static Control DetailStat(string label, string value, string? unit = null)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Classes.Add("MptMobileDetailStat");
        stack.Children.Add(Text(label, "MptMobileCaption"));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        line.Children.Add(Text(value, "MptMobileDetailValue"));
        if (!string.IsNullOrEmpty(unit))
        {
            line.Children.Add(Text(unit!, "MptMobileCaption"));
        }

        stack.Children.Add(line);
        return stack;
    }

    /// <summary>A label/value receipt line.</summary>
    public static Control ReceiptRow(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Classes.Add("MptMobileReceiptRow");
        grid.Children.Add(Text(label, "MptMobileCaption"));
        var right = Text(value, "MptMobileRowTitle");
        right.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    public static ItemsControl Items<T>(IEnumerable<T> items, Func<T, Control> template)
    {
        var control = new ItemsControl { ItemsSource = items };
        control.ItemTemplate = new FuncDataTemplate<T>((item, _) => template(item));
        control.Classes.Add("MptMobileItems");
        return control;
    }

    /// <summary>Binds a control property to a view-model path without a XAML data context.</summary>
    public static T Bind<T>(this T control, AvaloniaProperty property, object source, string path)
        where T : Control
    {
        control.Bind(property, new Binding(path) { Source = source });
        return control;
    }

    public static T WithClasses<T>(this T control, params string[] classes)
        where T : Control
    {
        foreach (var name in classes)
        {
            control.Classes.Add(name);
        }

        return control;
    }
}
