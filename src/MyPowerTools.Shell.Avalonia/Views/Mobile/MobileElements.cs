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
        var button = new Button { Content = glyph, Command = command };
        button.Classes.Add("MptMobileIconButton");
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    public static Button BackButton(ICommand command, string label = "返回")
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new TextBlock { Text = "\u2039", FontSize = 22, VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, Command = command };
        button.Classes.Add("MptMobileBack");
        button.Classes.Add("MptMobileBackButton");
        AutomationProperties.SetName(button, label);
        return button;
    }

    /// <summary>The icon square used by every list row, tile and hero.</summary>
    public static Border IconBox(string glyph, string extraClasses = "")
    {
        var text = new TextBlock
        {
            Text = glyph,
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var box = new Border { Child = text, Width = 40, Height = 40 };
        text.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("MptMobileAccentBrush"));
        box.Classes.Add("MptMobileIconBox");
        foreach (var name in extraClasses.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            box.Classes.Add(name);
        }

        box.Bind(Border.BackgroundProperty, new DynamicResourceExtension("MptMobileSoftFillBrush"));
        return box;
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

        if (!string.IsNullOrEmpty(meta))
        {
            var tail = Text(meta!, "MptMobileRowMeta");
            tail.VerticalAlignment = VerticalAlignment.Center;
            tail.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(tail, 2);
            grid.Children.Add(tail);
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
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
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
