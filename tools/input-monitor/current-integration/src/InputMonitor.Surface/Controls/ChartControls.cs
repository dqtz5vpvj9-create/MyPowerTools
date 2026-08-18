using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace InputMonitor.Surface.Controls;

public sealed class RatioBar : Control
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<RatioBar, double>(nameof(Ratio));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<RatioBar, IBrush?>(nameof(Fill));

    static RatioBar()
    {
        AffectsRender<RatioBar>(RatioProperty, FillProperty);
    }

    public double Ratio
    {
        get => GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var width = Math.Max(2, Bounds.Width * Math.Clamp(Ratio, 0, 1));
        context.DrawRectangle(
            Fill ?? DashboardPalette.DefaultBarFill,
            null,
            new RoundedRect(new Rect(0, 0, width, Bounds.Height), 3));
    }
}

public sealed class CategoryStrip : Control
{
    public static readonly StyledProperty<IReadOnlyList<CategorySlice>?> SlicesProperty =
        AvaloniaProperty.Register<CategoryStrip, IReadOnlyList<CategorySlice>?>(nameof(Slices));

    static CategoryStrip()
    {
        AffectsRender<CategoryStrip>(SlicesProperty);
    }

    public IReadOnlyList<CategorySlice>? Slices
    {
        get => GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var slices = Slices;
        if (slices is null || slices.Count == 0 || Bounds.Width <= 0)
        {
            context.DrawRectangle(DashboardPalette.HeatEmptyBrush, null, new RoundedRect(new Rect(Bounds.Size), 2));
            return;
        }

        var total = Math.Max(1, slices.Sum(slice => slice.Seconds));
        var x = 0d;
        foreach (var slice in slices)
        {
            var width = Bounds.Width * slice.Seconds / total;
            context.DrawRectangle(
                DashboardPalette.CategoryBrush(slice.Category),
                null,
                new RoundedRect(new Rect(x, 0, Math.Max(1, width), Bounds.Height), 2));
            x += width;
        }
    }
}

public sealed record CategorySlice(string Category, double Seconds);

public sealed class ActivityBarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<ActivityBarChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<ActivityBarChart, IReadOnlyList<string>?>(nameof(Labels));

    public static readonly StyledProperty<bool> UseHoursProperty =
        AvaloniaProperty.Register<ActivityBarChart, bool>(nameof(UseHours));

    static ActivityBarChart()
    {
        AffectsRender<ActivityBarChart>(ValuesProperty, LabelsProperty, UseHoursProperty);
    }

    private int _hover = -1;

    public ActivityBarChart()
    {
        ClipToBounds = true;
        MinHeight = 140;
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    public bool UseHours
    {
        get => GetValue(UseHoursProperty);
        set => SetValue(UseHoursProperty, value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var values = Values;
        if (values is null || values.Count == 0)
        {
            return;
        }

        var plot = PlotRect();
        var x = e.GetPosition(this).X;
        var index = (int)((x - plot.X) / plot.Width * values.Count);
        _hover = index >= 0 && index < values.Count ? index : -1;
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
        var plot = PlotRect();
        if (values.Count == 0 || !values.Any(value => value > 0))
        {
            DrawEmpty(context, "暂无数据");
            return;
        }

        var max = Math.Max(values.Max(), 1);
        var gap = values.Count > 40 ? 1d : 3d;
        var barWidth = Math.Max(2, (plot.Width - gap * (values.Count - 1)) / values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var height = values[index] / max * plot.Height;
            var x = plot.X + index * (barWidth + gap);
            var rect = new Rect(x, plot.Bottom - height, barWidth, Math.Max(1, height));
            var brush = index == _hover ? DashboardPalette.BlueBrush : DashboardPalette.BarFillBrush;
            context.DrawRectangle(brush, null, new RoundedRect(rect, 3));
        }

        DrawAxisLabels(context, plot, values.Count);
        if ((uint)_hover < (uint)values.Count)
        {
            var title = LabelAt(_hover);
            var text = DashboardFormat.Duration(values[_hover]);
            DrawBadge(context, plot, title, text, _hover / (double)Math.Max(1, values.Count - 1));
        }
    }

    private Rect PlotRect() => new(8, 28, Math.Max(8, Bounds.Width - 16), Math.Max(8, Bounds.Height - 48));

    private string LabelAt(int index)
    {
        var labels = Labels;
        if (labels is not null && index < labels.Count)
        {
            return labels[index];
        }

        return UseHours ? index.ToString(CultureInfo.InvariantCulture) : $"{index}:00";
    }

    private void DrawAxisLabels(DrawingContext context, Rect plot, int count)
    {
        var stride = Math.Max(1, count / 8);
        for (var index = 0; index < count; index += stride)
        {
            var text = LabelAt(index);
            if (text.Length > 5)
            {
                text = text[^5..];
            }

            DrawText(context, text, 9, DashboardPalette.AxisBrush, new Point(plot.X + index * plot.Width / count, plot.Bottom + 4));
        }
    }

    private static void DrawEmpty(DrawingContext context, string text)
    {
        DrawText(context, text, 12, DashboardPalette.SecondaryBrush, new Point(16, 60));
    }

    internal static void DrawBadge(DrawingContext context, Rect plot, string title, string value, double t)
    {
        var x = plot.X + Math.Clamp(t, 0, 1) * plot.Width;
        var body = $"{title}\n{value}";
        var formatted = ChartText.Create(body, 10, DashboardPalette.SecondaryBrush);
        var width = Math.Max(72, formatted.Width + 16);
        var rect = new Rect(Math.Clamp(x - width / 2, 8, Math.Max(8, plot.Right - width)), 4, width, 36);
        context.DrawRectangle(DashboardPalette.CardBrush, new Pen(DashboardPalette.HairlineBrush), new RoundedRect(rect, 6));
        DrawText(context, title, 9, DashboardPalette.SecondaryBrush, new Point(rect.X + 8, rect.Y + 4));
        DrawText(context, value, 10, Brushes.Black, new Point(rect.X + 8, rect.Y + 16));
    }

    internal static void DrawText(DrawingContext context, string text, double size, IBrush brush, Point origin)
    {
        context.DrawText(ChartText.Create(text, size, brush), origin);
    }
}

public sealed class FrequencyChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<FrequencyChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<FrequencyChart, IReadOnlyList<string>?>(nameof(Labels));

    public static readonly StyledProperty<string> UnitTextProperty =
        AvaloniaProperty.Register<FrequencyChart, string>(nameof(UnitText), "次操作");

    static FrequencyChart()
    {
        AffectsRender<FrequencyChart>(ValuesProperty, LabelsProperty, UnitTextProperty);
    }

    private int _hover = -1;

    public FrequencyChart()
    {
        ClipToBounds = true;
        MinHeight = 160;
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    public string UnitText
    {
        get => GetValue(UnitTextProperty);
        set => SetValue(UnitTextProperty, value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var values = Values;
        if (values is null || values.Count == 0)
        {
            return;
        }

        var plot = PlotRect();
        var t = (e.GetPosition(this).X - plot.X) / plot.Width;
        _hover = (int)Math.Round(Math.Clamp(t, 0, 1) * (values.Count - 1));
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
        var plot = PlotRect();
        if (values.Count == 0 || !values.Any(value => value > 0))
        {
            ActivityBarChart.DrawText(context, "暂无数据", 12, DashboardPalette.SecondaryBrush, new Point(16, 70));
            return;
        }

        var max = Math.Max(values.Max(), 1);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(plot.X, plot.Bottom), true);
            for (var index = 0; index < values.Count; index++)
            {
                var point = PointAt(plot, index, values.Count, values[index], max);
                if (index == 0)
                {
                    g.LineTo(new Point(point.X, plot.Bottom));
                }

                g.LineTo(point);
            }

            g.LineTo(new Point(plot.Right, plot.Bottom));
            g.EndFigure(true);
        }

        context.DrawGeometry(DashboardPalette.FrequencyFill, null, geometry);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            for (var index = 0; index < values.Count; index++)
            {
                var point = PointAt(plot, index, values.Count, values[index], max);
                if (index == 0)
                {
                    g.BeginFigure(point, false);
                }
                else
                {
                    g.LineTo(point);
                }
            }

            g.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(DashboardPalette.BlueBrush, 1.5), line);

        var ticks = new[] { 0, values.Count / 4, values.Count / 2, values.Count * 3 / 4, values.Count - 1 };
        foreach (var tick in ticks.Distinct())
        {
            if (tick < 0 || tick >= values.Count)
            {
                continue;
            }

            var x = PointAt(plot, tick, values.Count, 0, 1).X;
            ActivityBarChart.DrawText(context, LabelAt(tick), 9, DashboardPalette.AxisBrush, new Point(x - 10, plot.Bottom + 4));
        }

        if ((uint)_hover < (uint)values.Count)
        {
            var point = PointAt(plot, _hover, values.Count, values[_hover], max);
            context.DrawLine(
                new Pen(DashboardPalette.SecondaryBrush, 1) { DashStyle = DashStyle.Dash },
                new Point(point.X, plot.Y),
                new Point(point.X, plot.Bottom));
            ActivityBarChart.DrawBadge(
                context,
                plot,
                LabelAt(_hover),
                $"{values[_hover]:0} {UnitText}",
                _hover / (double)Math.Max(1, values.Count - 1));
        }
    }

    private Rect PlotRect() => new(8, 28, Math.Max(8, Bounds.Width - 16), Math.Max(8, Bounds.Height - 48));

    private string LabelAt(int index)
    {
        var labels = Labels;
        if (labels is not null && index < labels.Count && !string.IsNullOrWhiteSpace(labels[index]))
        {
            var label = labels[index];
            return label.Length > 5 ? label[^5..] : label;
        }

        var minute = index * 10;
        return $"{minute / 60:00}:00";
    }

    private static Point PointAt(Rect plot, int index, int count, double value, double max)
    {
        var x = plot.X + (count <= 1 ? 0 : index / (double)(count - 1)) * plot.Width;
        var y = plot.Bottom - value / max * plot.Height;
        return new Point(x, y);
    }
}

internal static class ChartText
{
    public static FormattedText Create(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush);
}

internal static class DashboardFormat
{
    public static string Duration(double seconds)
    {
        var total = Math.Max(0, (int)seconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var rest = total % 60;
        if (hours > 0)
        {
            return $"{hours} 小时 {minutes} 分";
        }

        if (minutes == 0)
        {
            return $"{rest} 秒";
        }

        return rest == 0 ? $"{minutes} 分钟" : $"{minutes} 分 {rest} 秒";
    }

    public static string CompactDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}小时{span.Minutes}分"
            : $"{span.Minutes}分钟";
    }

    public static string Hold(long milliseconds)
    {
        var seconds = Math.Max(0, milliseconds / 1000);
        return seconds < 60 ? $"{seconds}秒" : $"{seconds / 60}分{seconds % 60}秒";
    }

    public static string Distance(double pixels)
    {
        if (pixels < 1000)
        {
            return $"{pixels:0}px";
        }

        if (pixels < 1_000_000)
        {
            return $"{pixels / 1000:0.1}k px";
        }

        return $"{pixels / 1_000_000:0.00}M px";
    }

    public static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
