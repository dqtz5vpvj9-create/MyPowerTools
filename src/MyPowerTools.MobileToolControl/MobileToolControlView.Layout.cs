using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace MyPowerTools.MobileToolControl;

/// <summary>
/// Layout builders for the 电脑工具 page. Every control is a standard Avalonia control carrying the
/// shipped <c>MptMobile*</c> class, and no colour, radius, font size or touch metric the theme owns is
/// restated here: spacing and alignment stay local, appearance comes from the SDK theme.
/// </summary>
internal sealed partial class MobileToolControlView
{
    private static StackPanel Stack(double spacing = 0) => new() { Spacing = spacing };

    private static StackPanel SectionHeader(string text)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Classes.Add(MobileToolControlTheme.SectionHeaderClass);
        header.Children.Add(MobileToolControlTheme.SectionTitle(text));
        return header;
    }

    /// <summary>Status pill: SDK shape and typography, with the SDK token pair for the real state.</summary>
    private static (Border Pill, TextBlock Text) Pill(string text, string tone)
    {
        var label = MobileToolControlTheme.Text(MobileToolControlTheme.PillTextClass, text);
        label.VerticalAlignment = VerticalAlignment.Center;
        var pill = new Border { Child = label, VerticalAlignment = VerticalAlignment.Center };
        pill.Classes.Add(MobileToolControlTheme.PillClass);
        MobileToolControlTheme.StylePill(pill, label, tone);
        return (pill, label);
    }

    private static Border Notice(string text, bool warning = false)
    {
        var border = new Border { Child = MobileToolControlTheme.Caption(text) };
        border.Classes.Add(MobileToolControlTheme.NoticeClass);
        MobileToolControlTheme.SetClass(border, MobileToolControlTheme.NoticeWarningClass, warning);
        return border;
    }

    private static Border IconBox(string glyph)
    {
        var text = new TextBlock
        {
            Text = glyph,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var border = new Border { Child = text, VerticalAlignment = VerticalAlignment.Center };
        border.Classes.Add(MobileToolControlTheme.IconBoxClass);
        return border;
    }

    /// <summary>A tappable row: the SDK list-row style, with an icon box, copy and optional trailing text.</summary>
    private static Button Row(
        string glyph,
        string title,
        string subtitle,
        string trailing,
        Action? onClick)
    {
        var copy = Stack(2);
        copy.Children.Add(MobileToolControlTheme.Text(MobileToolControlTheme.RowTitleClass, title));
        if (subtitle.Length > 0)
        {
            copy.Children.Add(MobileToolControlTheme.Text(MobileToolControlTheme.RowSubtitleClass, subtitle));
        }

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12
        };
        var icon = IconBox(glyph);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);

        if (trailing.Length > 0)
        {
            var trailingBlock = MobileToolControlTheme.Text(MobileToolControlTheme.RowMetaClass, trailing);
            trailingBlock.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(trailingBlock, 2);
            grid.Children.Add(trailingBlock);
        }

        var button = new Button { Content = grid };
        button.Classes.Add(MobileToolControlTheme.ListRowClass);
        if (onClick is not null)
        {
            button.Click += (_, _) => onClick();
        }
        else
        {
            // Informational row: the theme's disabled opacity keeps it visually quieter than a control
            // the user can act on, without inventing a second row style.
            button.IsEnabled = false;
        }

        return button;
    }

    private static TextBox Field(string placeholder, string text, bool multiline = false) =>
        MobileToolControlTheme.Field(placeholder, text, multiline);

    /// <summary>Inset panel: the SDK class owns the surface, radius and padding.</summary>
    private static Border Panel(Control child)
    {
        var border = new Border { Child = child };
        border.Classes.Add(MobileToolControlTheme.InsetPanelClass);
        return border;
    }

    /// <summary>The single bottom-sheet overlay: scrim, SDK sheet surface, grabber and title.</summary>
    private static (Border Overlay, StackPanel Body) Sheet(string title, Action onClose)
    {
        var titleBlock = MobileToolControlTheme.Text(MobileToolControlTheme.SheetTitleClass, title);
        var close = MobileToolControlTheme.IconButton("✕");
        close.Click += (_, _) => onClose();

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(titleBlock, 0);
        header.Children.Add(titleBlock);
        Grid.SetColumn(close, 1);
        header.Children.Add(close);

        var body = Stack(12);
        body.Children.Add(header);

        var grabber = new Border();
        grabber.Classes.Add(MobileToolControlTheme.SheetGrabberClass);

        var sheetBody = Stack(0);
        sheetBody.Children.Add(grabber);
        sheetBody.Children.Add(body);
        // Keep the sheet's overlay scrollbar clear of the field borders on a narrow phone.
        sheetBody.Margin = new Thickness(0, 0, 8, 0);

        var sheet = new Border
        {
            Child = new ScrollViewer
            {
                Content = sheetBody,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                // A sheet may scroll on a short phone; this is the only local layout limit here.
                MaxHeight = 620
            },
            VerticalAlignment = VerticalAlignment.Bottom,
            MaxWidth = 720,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        sheet.Classes.Add(MobileToolControlTheme.SheetClass);

        var overlay = new Border { Child = sheet };
        overlay.Classes.Add(MobileToolControlTheme.OverlayClass);
        return (overlay, body);
    }

    /// <summary>
    /// Appends one row per action to the given panel. The rows are created directly into their final
    /// parent: an Avalonia control can only have one parent, so building a temporary panel and moving
    /// its children would throw.
    /// </summary>
    private void AddActionRows(StackPanel panel, IReadOnlyList<MobileToolActionRow> rows)
    {
        foreach (var row in rows)
        {
            var (action, hint, intent) = MobileToolControlSemantics.Describe(row.Command);
            var glyph = intent == MobileToolIntent.Read ? "◉" : "▸";
            var trailing = row.Command.Allowed ? row.Command.SafetyLabel : "未授权";
            var command = row.Command;
            var button = Row(glyph, action, hint, trailing, () => OpenCommand(command));
            if (!command.Allowed)
            {
                button.IsEnabled = false;
            }

            panel.Children.Add(button);
        }
    }

    private static void AddFieldList(StackPanel panel, IReadOnlyList<MobileToolParameterField> fields)
    {
        foreach (var field in fields)
        {
            var block = Stack(6);
            block.Children.Add(MobileToolControlTheme.Text(MobileToolControlTheme.FieldLabelClass, field.Label));

            if (field.IsBoolean)
            {
                var toggle = new ToggleSwitch
                {
                    IsChecked = field.BooleanValue,
                    OnContent = "是",
                    OffContent = "否"
                };
                toggle.Classes.Add(MobileToolControlTheme.SwitchClass);
                toggle.IsCheckedChanged += (_, _) => field.BooleanValue = toggle.IsChecked == true;
                block.Children.Add(toggle);
            }
            else if (field.IsChoice && field.Choices.Count > 0)
            {
                var combo = new ComboBox
                {
                    ItemsSource = field.Choices,
                    SelectedItem = field.Choices.Contains(field.Value) ? field.Value : field.Choices[0],
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                combo.Classes.Add(MobileToolControlTheme.FieldClass);
                combo.SelectionChanged += (_, _) => field.Value = combo.SelectedItem as string ?? "";
                field.Value = combo.SelectedItem as string ?? "";
                field.OnValueChanged = () =>
                {
                    // A programmatic value change must be visible, otherwise the next edit would write
                    // the stale control text back into the field.
                    if (!string.Equals(combo.SelectedItem as string, field.Value, StringComparison.Ordinal) &&
                        field.Choices.Contains(field.Value))
                    {
                        combo.SelectedItem = field.Value;
                    }
                };
                block.Children.Add(combo);
            }
            else
            {
                // Integers and numbers keep their own validation; object/array parameters get an
                // advanced multiline JSON field instead of turning the whole page into a JSON editor.
                var box = Field(field.Placeholder, field.Value, multiline: field.IsJson);
                if (field.IsJson)
                {
                    box.MinHeight = 110;
                }

                box.TextChanged += (_, _) => field.Value = box.Text ?? "";
                field.OnValueChanged = () =>
                {
                    // A programmatic value change must be visible, otherwise the next edit would write
                    // the stale control text back into the field.
                    if (!string.Equals(box.Text, field.Value, StringComparison.Ordinal))
                    {
                        box.Text = field.Value;
                    }
                };
                block.Children.Add(box);
            }

            var error = MobileToolControlTheme.Text(
                MobileToolControlTheme.WarningTextClass,
                field.Error);
            error.IsVisible = field.HasError;
            field.OnErrorChanged = () =>
            {
                error.Text = field.Error;
                error.IsVisible = field.HasError;
            };
            block.Children.Add(error);


            panel.Children.Add(block);
        }
    }
}
