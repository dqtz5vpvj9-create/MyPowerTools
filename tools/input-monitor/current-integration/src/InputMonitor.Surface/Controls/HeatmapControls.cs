using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace InputMonitor.Surface.Controls;

public sealed class HourlyHeatmapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<string>?> DayLabelsProperty =
        AvaloniaProperty.Register<HourlyHeatmapControl, IReadOnlyList<string>?>(nameof(DayLabels));

    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<HourlyHeatmapControl, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<string> ValueKindProperty =
        AvaloniaProperty.Register<HourlyHeatmapControl, string>(nameof(ValueKind), "count");

    static HourlyHeatmapControl()
    {
        AffectsRender<HourlyHeatmapControl>(DayLabelsProperty, ValuesProperty, ValueKindProperty);
    }

    private (int Row, int Hour)? _hover;

    public HourlyHeatmapControl()
    {
        ClipToBounds = false;
        MinHeight = 180;
    }

    public IReadOnlyList<string>? DayLabels
    {
        get => GetValue(DayLabelsProperty);
        set => SetValue(DayLabelsProperty, value);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public string ValueKind
    {
        get => GetValue(ValueKindProperty);
        set => SetValue(ValueKindProperty, value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var days = DayLabels?.Count ?? 0;
        if (days == 0)
        {
            return;
        }

        var layout = Layout();
        var position = e.GetPosition(this);
        var row = (int)((position.Y - layout.Grid.Y) / (layout.CellHeight + layout.Gap));
        var hour = (int)((position.X - layout.Grid.X) / (layout.CellWidth + layout.Gap));
        _hover = row >= 0 && row < days && hour is >= 0 and < 24 ? (row, hour) : null;
        InvalidateVisual();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    public override void Render(DrawingContext context)
    {
        var days = DayLabels ?? [];
        var values = Values ?? [];
        var layout = Layout();
        var max = values.Count == 0 ? 1 : Math.Max(values.Max(), 1);
        for (var hour = 0; hour < 24; hour++)
        {
            if (hour % 3 != 0)
            {
                continue;
            }

            var x = layout.Grid.X + hour * (layout.CellWidth + layout.Gap);
            ActivityBarChart.DrawText(context, hour.ToString(CultureInfo.InvariantCulture), 8, DashboardPalette.AxisBrush, new Point(x, 0));
        }

        for (var row = 0; row < days.Count; row++)
        {
            var y = layout.Grid.Y + row * (layout.CellHeight + layout.Gap);
            ActivityBarChart.DrawText(context, days[row], 10, DashboardPalette.SecondaryBrush, new Point(0, y + 4));
            for (var hour = 0; hour < 24; hour++)
            {
                var value = ValueAt(values, row, hour);
                var rect = new Rect(
                    layout.Grid.X + hour * (layout.CellWidth + layout.Gap),
                    y,
                    layout.CellWidth,
                    layout.CellHeight);
                context.DrawRectangle(DashboardPalette.Fill(DashboardPalette.Heat(value, max)), null, new RoundedRect(rect, 3));
            }
        }

        if (_hover is { } hover && hover.Row < days.Count)
        {
            var value = ValueAt(values, hover.Row, hover.Hour);
            var x = layout.Grid.X + hover.Hour * (layout.CellWidth + layout.Gap);
            var y = layout.Grid.Y + hover.Row * (layout.CellHeight + layout.Gap) - 28;
            DrawHoverBadge(context, $"{days[hover.Row]} {hover.Hour}:00", FormatValue(value), new Point(x, y));
        }
    }

    private (Rect Grid, double CellWidth, double CellHeight, double Gap) Layout()
    {
        const double labelWidth = 44;
        const double gap = 2;
        const double cellHeight = 22;
        var rows = Math.Max(1, DayLabels?.Count ?? 7);
        var grid = new Rect(labelWidth, 14, Math.Max(24, Bounds.Width - labelWidth), rows * (cellHeight + gap));
        var cellWidth = Math.Max(6, (grid.Width - gap * 23) / 24);
        return (grid, cellWidth, cellHeight, gap);
    }

    private static double ValueAt(IReadOnlyList<double> values, int row, int hour)
    {
        var index = row * 24 + hour;
        return index >= 0 && index < values.Count ? values[index] : 0;
    }

    private string FormatValue(double value) =>
        ValueKind == "duration" ? DashboardFormat.CompactDuration(value) : $"{value:0} 次";

    internal static void DrawHoverBadge(DrawingContext context, string title, string value, Point origin)
    {
        var width = 108;
        var rect = new Rect(Math.Clamp(origin.X - 20, 0, Math.Max(0, 400)), Math.Max(0, origin.Y), width, 32);
        context.DrawRectangle(
            DashboardPalette.CardBrush,
            new Pen(DashboardPalette.HairlineBrush),
            new RoundedRect(rect, 6));
        ActivityBarChart.DrawText(context, title, 9, DashboardPalette.SecondaryBrush, new Point(rect.X + 8, rect.Y + 3));
        ActivityBarChart.DrawText(context, value, 10, Brushes.Black, new Point(rect.X + 8, rect.Y + 15));
    }
}

public sealed class WeekGridHeatmapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<WeekGridHeatmapControl, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<string?> AlignedStartProperty =
        AvaloniaProperty.Register<WeekGridHeatmapControl, string?>(nameof(AlignedStart));

    public static readonly StyledProperty<string> ValueKindProperty =
        AvaloniaProperty.Register<WeekGridHeatmapControl, string>(nameof(ValueKind), "count");

    static WeekGridHeatmapControl()
    {
        AffectsRender<WeekGridHeatmapControl>(ValuesProperty, AlignedStartProperty, ValueKindProperty);
    }

    private int _hover = -1;

    public WeekGridHeatmapControl()
    {
        MinHeight = 140;
        ClipToBounds = false;
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public string? AlignedStart
    {
        get => GetValue(AlignedStartProperty);
        set => SetValue(AlignedStartProperty, value);
    }

    public string ValueKind
    {
        get => GetValue(ValueKindProperty);
        set => SetValue(ValueKindProperty, value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var values = Values ?? [];
        var layout = Layout(values.Count);
        var position = e.GetPosition(this);
        var week = (int)((position.X - layout.Grid.X) / (layout.Cell + layout.Gap));
        var weekday = (int)((position.Y - layout.Grid.Y) / (layout.Cell + layout.Gap));
        var index = week * 7 + weekday;
        _hover = week >= 0 && weekday is >= 0 and < 7 && index < values.Count ? index : -1;
        InvalidateVisual();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = -1;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values ?? [];
        var layout = Layout(values.Count);
        var max = values.Count == 0 ? 1 : Math.Max(values.Max(), 1);
        var labels = new[] { "一", "", "三", "", "五", "", "日" };
        for (var weekday = 0; weekday < 7; weekday++)
        {
            ActivityBarChart.DrawText(
                context,
                labels[weekday],
                9,
                DashboardPalette.AxisBrush,
                new Point(0, layout.Grid.Y + weekday * (layout.Cell + layout.Gap)));
        }

        var weeks = Math.Max(1, (int)Math.Ceiling(values.Count / 7d));
        for (var week = 0; week < weeks; week++)
        {
            for (var weekday = 0; weekday < 7; weekday++)
            {
                var index = week * 7 + weekday;
                if (index >= values.Count)
                {
                    continue;
                }

                var rect = new Rect(
                    layout.Grid.X + week * (layout.Cell + layout.Gap),
                    layout.Grid.Y + weekday * (layout.Cell + layout.Gap),
                    layout.Cell,
                    layout.Cell);
                context.DrawRectangle(DashboardPalette.Fill(DashboardPalette.Heat(values[index], max)), null, new RoundedRect(rect, 3));
            }
        }

        if ((uint)_hover < (uint)values.Count)
        {
            var day = DayAt(_hover);
            var text = ValueKind == "duration" ? DashboardFormat.CompactDuration(values[_hover]) : $"{values[_hover]:0} 次";
            HourlyHeatmapControl.DrawHoverBadge(context, day, text, new Point(layout.Grid.X + 8, 0));
        }
    }

    private (Rect Grid, double Cell, double Gap) Layout(int days)
    {
        const double gap = 3;
        const double label = 14;
        var weeks = Math.Max(1, (int)Math.Ceiling(Math.Max(days, 1) / 7d));
        var available = Math.Max(40, Bounds.Width - label);
        var cell = Math.Clamp(Math.Floor((available - gap * (weeks - 1)) / weeks), 6, 13);
        return (new Rect(label, 28, available, 7 * (cell + gap)), cell, gap);
    }

    private string DayAt(int index)
    {
        if (!DateTime.TryParseExact(AlignedStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            return "";
        }

        return start.AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}

public sealed class TrackHeatmapControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<int>?> CountsProperty =
        AvaloniaProperty.Register<TrackHeatmapControl, IReadOnlyList<int>?>(nameof(Counts));

    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<TrackHeatmapControl, int>(nameof(Columns), 48);

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<TrackHeatmapControl, int>(nameof(Rows), 27);

    public static readonly StyledProperty<int> PixelWidthProperty =
        AvaloniaProperty.Register<TrackHeatmapControl, int>(nameof(PixelWidth), 1920);

    public static readonly StyledProperty<int> PixelHeightProperty =
        AvaloniaProperty.Register<TrackHeatmapControl, int>(nameof(PixelHeight), 1080);

    static TrackHeatmapControl()
    {
        AffectsRender<TrackHeatmapControl>(CountsProperty, ColumnsProperty, RowsProperty);
        AffectsMeasure<TrackHeatmapControl>(PixelWidthProperty, PixelHeightProperty, ColumnsProperty, RowsProperty);
    }

    private (int Col, int Row)? _hover;

    public TrackHeatmapControl()
    {
        MinWidth = 96;
        MinHeight = 72;
        ClipToBounds = true;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
    }

    public IReadOnlyList<int>? Counts
    {
        get => GetValue(CountsProperty);
        set => SetValue(CountsProperty, value);
    }

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public int PixelWidth
    {
        get => GetValue(PixelWidthProperty);
        set => SetValue(PixelWidthProperty, value);
    }

    public int PixelHeight
    {
        get => GetValue(PixelHeightProperty);
        set => SetValue(PixelHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var pixelW = Math.Max(1, PixelWidth);
        var pixelH = Math.Max(1, PixelHeight);
        var maxW = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : 640;
        var maxH = 320d;
        if (double.IsFinite(availableSize.Height) && availableSize.Height > 0)
        {
            maxH = Math.Min(maxH, availableSize.Height);
        }

        var scale = Math.Min(maxW / pixelW, maxH / pixelH);
        return new Size(Math.Max(96, Math.Round(pixelW * scale)), Math.Max(72, Math.Round(pixelH * scale)));
    }

    protected override Size ArrangeOverride(Size finalSize) => MeasureOverride(finalSize);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var position = e.GetPosition(this);
        var col = (int)(position.X / Bounds.Width * Columns);
        var row = (int)(position.Y / Bounds.Height * Rows);
        _hover = (uint)col < Columns && (uint)row < Rows ? (col, row) : null;
        InvalidateVisual();
        base.OnPointerMoved(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
        base.OnPointerExited(e);
    }

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(DashboardPalette.InsetBrush, null, new RoundedRect(new Rect(Bounds.Size), 8));
        var counts = Counts ?? [];
        var cols = Math.Max(1, Columns);
        var rows = Math.Max(1, Rows);
        if (counts.Count == 0 || counts.All(count => count == 0))
        {
            ActivityBarChart.DrawText(context, "暂无轨迹数据", 12, DashboardPalette.SecondaryBrush, new Point(16, Bounds.Height / 2 - 8));
            return;
        }

        var max = Math.Max(1, counts.Max());
        var cellW = Bounds.Width / cols;
        var cellH = Bounds.Height / rows;
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                var index = row * cols + col;
                var count = index < counts.Count ? counts[index] : 0;
                if (count <= 0)
                {
                    continue;
                }

                var t = Math.Min(1, count / (double)max);
                var rect = new Rect(col * cellW, row * cellH, cellW, cellH);
                context.DrawRectangle(
                    DashboardPalette.Fill(DashboardPalette.AccentIndigo, 0.12 + 0.75 * t),
                    null,
                    new RoundedRect(rect, 2));
            }
        }

        if (_hover is { } hover)
        {
            var index = hover.Row * cols + hover.Col;
            var count = index < counts.Count ? counts[index] : 0;
            if (count > 0)
            {
                HourlyHeatmapControl.DrawHoverBadge(context, $"{count} 次", "经过次数", new Point(hover.Col * cellW, Math.Max(0, hover.Row * cellH - 28)));
            }
        }
    }
}
