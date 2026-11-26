using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace FileTransfer.Surface;

/// <summary>
/// The phone/desktop widget vocabulary for the file-transfer tool, expressed entirely through the
/// shared mobile theme's <c>MptMobile*</c> classes.
///
/// Nothing here sets a colour: <c>MptMobileTheme.axaml</c> owns light and dark, so a page built from
/// these helpers follows the host theme automatically and cannot reintroduce the old local palette.
/// Only layout that is specific to this tool lives here.
/// </summary>
internal static class MobileUi
{
    /// <summary>The minimum touch target the plan requires for icon buttons.</summary>
    public const double IconTarget = 44;

    /// <summary>The minimum height the plan requires for a primary action.</summary>
    public const double PrimaryTarget = 48;

    internal static class Classes
    {
        public const string Root = "MptMobileRoot";
        public const string Page = "MptMobilePage";
        public const string PageNarrow = "MptMobilePageNarrow";
        public const string PageTitle = "MptMobilePageTitle";
        public const string PageSubtitle = "MptMobilePageSubtitle";
        public const string SectionTitle = "MptMobileSectionTitle";
        public const string CardTitle = "MptMobileCardTitle";
        public const string Body = "MptMobileBody";
        public const string Caption = "MptMobileCaption";
        public const string Note = "MptMobileNote";
        public const string Meta = "MptMobileMeta";
        public const string Micro = "MptMobileMicro";
        public const string FieldLabel = "MptMobileFieldLabel";
        public const string EmptyTitle = "MptMobileEmptyTitle";
        public const string ReceiptValue = "MptMobileReceiptValue";
        public const string CategoryTitle = "MptMobileCategoryTitle";
        public const string Card = "MptMobileCard";
        public const string ListCard = "MptMobileListCard";
        public const string InsetPanel = "MptMobileInsetPanel";
        public const string Notice = "MptMobileNotice";
        public const string Receipt = "MptMobileReceipt";
        public const string Divider = "MptMobileDivider";
        public const string Pill = "MptMobilePill";
        public const string PillText = "MptMobilePillText";
        public const string SuccessIcon = "MptMobileSuccessIcon";
        public const string ProgressTrack = "MptMobileProgressTrack";
        public const string ProgressFill = "MptMobileProgressFill";
        public const string Progress = "MptMobileProgress";
        public const string Primary = "MptMobilePrimary";
        public const string Secondary = "MptMobileSecondary";
        public const string QuietButton = "MptMobileQuietButton";
        public const string TextButton = "MptMobileTextButton";
        public const string IconButton = "MptMobileIconButton";
        public const string CloseButton = "MptMobileCloseButton";
        public const string BackButton = "MptMobileBackButton";
        public const string ListRow = "MptMobileListRow";
        public const string TargetRow = "MptMobileTargetRow";
        public const string FileChoice = "MptMobileFileChoice";
        public const string FileThumb = "MptMobileFileThumb";
        public const string Field = "MptMobileField";
        public const string Sheet = "MptMobileSheet";
        public const string SheetGrabber = "MptMobileSheetGrabber";
        public const string SheetTitle = "MptMobileSheetTitle";
        public const string Icon = "MptMobileIcon";
        public const string IconSecondary = "MptMobileIconSecondary";
        public const string IconAccent = "MptMobileIconAccent";
        public const string IconSuccessLarge = "MptMobileIconSuccessLarge";
        public const string IconDevice = "MptMobileIconDevice";
        public const string IconLarge = "MptMobileIconLarge";
        public const string IconBox = "MptMobileIconBox";
        public const string Tile = "MptMobileTile";
        public const string AddDevice = "MptMobileAddDevice";
        public const string DeviceTile = "MptMobileDeviceTile";
        public const string Check = "MptMobileCheck";
        public const string PageHeader = "MptMobilePageHeader";
    }

    /// <summary>
    /// The shared SDK mobile theme, as a resource the tool owns.
    ///
    /// A desktop host loads only its own desktop theme, so a page that depends on the mobile styles
    /// cannot assume the application already has them: on Windows every <c>MptMobile*</c> class went
    /// unstyled, which showed up as blank icons and default buttons. Adding the theme to the page
    /// root's own <see cref="StyledElement.Styles"/> applies it to that page and its children only —
    /// the application is never touched, and a host that already loaded the theme globally simply gets
    /// a second, harmless copy of the same selectors.
    /// </summary>
    public static void EnsureMobileTheme(StyledElement pageRoot)
    {
        var uri = new Uri("avares://MyPowerTools.AvaloniaSdk/Themes/MptMobileTheme.axaml");
        foreach (var style in pageRoot.Styles)
            if (style is StyleInclude include && include.Source == uri) return;
        pageRoot.Styles.Add(new StyleInclude(new Uri("avares://MyPowerTools.AvaloniaSdk/")) { Source = uri });
    }

    /// <summary>Applies the shared class contract to one control. An unknown class is inert.</summary>
    public static T With<T>(T control, params string[] classes) where T : Control
    {
        foreach (var name in classes)
            if (!control.Classes.Contains(name)) control.Classes.Add(name);
        return control;
    }

    // ---------------------------------------------------------------- text

    public static TextBlock PageTitle(string text) => With(new TextBlock { Text = text }, Classes.PageTitle);

    public static TextBlock PageSubtitle(string text) => With(new TextBlock { Text = text }, Classes.PageSubtitle);

    public static TextBlock SectionTitle(string text) => With(new TextBlock { Text = text }, Classes.SectionTitle);

    public static TextBlock CardTitle(string text) => With(new TextBlock { Text = text }, Classes.CardTitle);

    public static TextBlock Body(string text) => With(new TextBlock { Text = text }, Classes.Body);

    public static TextBlock Caption(string text) => With(new TextBlock { Text = text }, Classes.Caption);

    public static TextBlock Note(string text) => With(new TextBlock { Text = text }, Classes.Note);

    public static TextBlock Meta(string text) => With(new TextBlock { Text = text }, Classes.Meta);

    public static TextBlock FieldLabel(string text) => With(new TextBlock { Text = text }, Classes.FieldLabel);

    public static TextBlock EmptyTitle(string text) => With(new TextBlock { Text = text }, Classes.EmptyTitle);

    public static TextBlock ReceiptValue(string text) => With(new TextBlock
    {
        Text = text,
        HorizontalAlignment = HorizontalAlignment.Right,
        TextAlignment = TextAlignment.Right
    }, Classes.ReceiptValue);

    // ---------------------------------------------------------------- icons

    /// <summary>
    /// A stroke icon from the shared mobile icon dictionary. The theme owns stroke, size and colour
    /// through the classes; the geometry is resolved when the control reaches the tree, which is the
    /// first moment a theme resource exists. A key the host did not load leaves the icon blank rather
    /// than breaking the page.
    /// </summary>
    public static MobileIcon Icon(string iconKey, params string[] classes) =>
        With(new MobileIcon { IconKey = iconKey, IsHitTestVisible = false }, classes);

    /// <summary>Text that only decorates a tappable row, so the row keeps receiving the tap.</summary>
    internal static TextBlock Decorative(TextBlock text)
    {
        text.IsHitTestVisible = false;
        return text;
    }

    // ---------------------------------------------------------------- surfaces

    /// <summary>
    /// A compact pickable device tile. It carries its own surface so the tile reads as one control
    /// rather than inheriting whatever background happens to sit behind the sheet.
    /// </summary>
    public static Button DeviceTile(Control content, Func<Task> action)
    {
        var button = With(new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Command = new MptMobileAsyncCommand(action)
        }, Classes.DeviceTile);
        button.Bind(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("MptMobileCardBrush"));
        button.Bind(TemplatedControl.BorderBrushProperty, new DynamicResourceExtension("MptMobileDividerBrush"));
        button.BorderThickness = new Thickness(1);
        return button;
    }

    public static Border Card(Control child) => With(new Border { Child = child }, Classes.Card);

    public static Border ListCard(Control child) => With(new Border { Child = child }, Classes.ListCard);

    public static Border Inset(Control child) => With(new Border { Child = child }, Classes.InsetPanel);

    public static Border Notice(Control child) => With(new Border { Child = child }, Classes.Notice);

    public static Border ReceiptSurface(Control child) => With(new Border { Child = child }, Classes.Receipt);

    public static Border Divider() => With(new Border(), Classes.Divider);

    public static Border Pill(string text, bool offline = false) => With(new Border
    {
        Child = With(new TextBlock { Text = text }, offline ? [Classes.PillText, "offline"] : [Classes.PillText])
    }, offline ? [Classes.Pill, "offline"] : [Classes.Pill]);

    /// <summary>The prototype's thin progress bar: a themed track with a fill set from real bytes.</summary>
    public static (Border Track, Border Fill) ProgressTrack()
    {
        var fill = With(new Border { HorizontalAlignment = HorizontalAlignment.Left }, Classes.ProgressFill);
        var track = With(new Border { Child = fill }, Classes.ProgressTrack);
        return (track, fill);
    }

    public static Border Sheet(string title, Button close, ScrollViewer scroll)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var titleBlock = With(new TextBlock { Text = title }, Classes.SheetTitle);
        Grid.SetColumn(titleBlock, 0);
        row.Children.Add(titleBlock);
        Grid.SetColumn(close, 1);
        row.Children.Add(close);
        var grabber = With(new Border { HorizontalAlignment = HorizontalAlignment.Center }, Classes.SheetGrabber);
        return With(new Border { Child = Stack(10, grabber, row, scroll) }, Classes.Sheet);
    }

    // ---------------------------------------------------------------- controls

    public static Button PrimaryButton(string text) => With(new Button
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        MinHeight = PrimaryTarget
    }, Classes.Primary);

    public static Button SecondaryButton(string text) => With(new Button
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch
    }, Classes.Secondary);

    public static Button QuietButton(string text) => With(new Button
    {
        Content = text,
        HorizontalAlignment = HorizontalAlignment.Stretch
    }, Classes.QuietButton);

    public static Button TextButton(string text) => With(new Button { Content = text }, Classes.TextButton);

    public static Button IconButton(string iconKey, string automationName)
    {
        var button = With(new Button
        {
            Content = Icon(iconKey, Classes.Icon),
            MinWidth = IconTarget,
            MinHeight = IconTarget
        }, Classes.IconButton);
        AutomationProperties.SetName(button, automationName);
        ToolTip.SetTip(button, automationName);
        return button;
    }

    public static Button CloseButton()
    {
        var button = With(new Button
        {
            Content = Icon("MptMobileIconClose", Classes.Icon),
            MinWidth = IconTarget,
            MinHeight = IconTarget
        }, Classes.CloseButton);
        AutomationProperties.SetName(button, "关闭");
        return button;
    }

    public static Button BackButton(string text)
    {
        var button = With(new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    Icon("MptMobileIconBack", Classes.Icon),
                    new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
                }
            },
            MinHeight = IconTarget
        }, Classes.BackButton);
        AutomationProperties.SetName(button, text);
        return button;
    }

    public static TextBox FieldBox(string placeholder) =>
        With(new TextBox { PlaceholderText = placeholder }, Classes.Field);

    /// <summary>
    /// One tappable row: leading icon, title with its caption, optional trailing text or chevron.
    /// The theme owns padding, minimum height and the pressed state.
    /// </summary>
    public static Button ListRow(string iconKey, string title, string description,
        Func<Task> action, string? trailing = null)
    {
        var copy = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children = { CardTitle(title), Caption(description) }
        };
        // A transparent background makes the whole row the hit target: without one, a tap that lands
        // between the glyph and the text can fall through to the next row in the list.
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12,
            Background = Brushes.Transparent
        };
        var icon = Icon(iconKey, Classes.Icon, Classes.IconAccent);
        icon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        Control tail = trailing is null ? Icon("MptMobileIconChevron", Classes.Icon, Classes.IconSecondary) : Decorative(Meta(trailing));
        tail.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tail, 2);
        grid.Children.Add(tail);
        return With(new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // One tap starts one command; the command guards re-entry while it runs.
            Command = new MptMobileAsyncCommand(action)
        }, Classes.ListRow);
    }

    /// <summary>One pending file, with the action that removes it from this send.</summary>
    public static Button FileRow(string kind, string name, string detail, Action onRemove)
    {
        var thumb = With(new Border
        {
            Width = IconTarget,
            Height = IconTarget,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = kind,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        }, Classes.FileThumb);
        var copy = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Children = { CardTitle(name), Caption(detail) }
        };
        var remove = TextButton("移除");
        remove.VerticalAlignment = VerticalAlignment.Center;
        remove.MinWidth = IconTarget;
        remove.MinHeight = IconTarget;
        remove.Click += (_, _) => onRemove();
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12,
            Background = Brushes.Transparent
        };
        Grid.SetColumn(thumb, 0);
        grid.Children.Add(thumb);
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        return With(new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        }, Classes.FileChoice);
    }

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>Two-column receipt line: label left, value right.</summary>
    public static Control ReceiptRow(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        var key = Caption(label);
        Grid.SetColumn(key, 0);
        grid.Children.Add(key);
        var text = ReceiptValue(value);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }
}

/// <summary>
/// Re-entrancy-guarded async command for the tool's pages. It disables the owning button while the
/// action runs, so one tap cannot start two commands, and it reports faults instead of losing them in
/// an <c>async void</c>.
/// </summary>
internal sealed class MptMobileAsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : System.Windows.Input.ICommand
{
    private int _running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => Volatile.Read(ref _running) == 0 && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        try { await ExecuteAsync(); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine("File transfer command failed: " + ex.Message); }
    }

    public async Task ExecuteAsync()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); }
        finally
        {
            Volatile.Write(ref _running, 0);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
