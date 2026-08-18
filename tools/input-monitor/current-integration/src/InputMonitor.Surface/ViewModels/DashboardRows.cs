using Avalonia.Media;
using InputMonitor.Surface.Controls;
using MyPowerTools.AvaloniaSdk;

namespace InputMonitor.Surface.ViewModels;

public sealed record CategoryOption(string Id, string Label);

public sealed class KeyHeatRow : MptObservableViewModel
{
    public KeyHeatRow(string label, int count, double ratio)
    {
        Label = label;
        Count = count;
        Ratio = ratio;
    }

    public string Label { get; }
    public int Count { get; }
    public double Ratio { get; }
}

public sealed class AppUsageRow : MptObservableViewModel
{
    public AppUsageRow(string name, string category, string duration, double ratio, IBrush barBrush)
    {
        Name = name;
        Category = category;
        Duration = duration;
        Ratio = ratio;
        BarBrush = barBrush;
    }

    public string Name { get; }
    public string Category { get; }
    public string Duration { get; }
    public double Ratio { get; }
    public IBrush BarBrush { get; }
}

public sealed class CategoryBreakdownRow
{
    public CategoryBreakdownRow(string category, string duration, string percent, IBrush swatch)
    {
        Category = category;
        Duration = duration;
        Percent = percent;
        Swatch = swatch;
    }

    public string Category { get; }
    public string Duration { get; }
    public string Percent { get; }
    public IBrush Swatch { get; }
}

public sealed class TrackScreenRow
{
    public TrackScreenRow(
        string title,
        string caption,
        IReadOnlyList<int> counts,
        int columns,
        int rows,
        int pixelWidth,
        int pixelHeight,
        int sampleCount)
    {
        Title = title;
        Caption = caption;
        Counts = counts;
        Columns = columns;
        Rows = rows;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        SampleCount = sampleCount;
    }

    public string Title { get; }
    public string Caption { get; }
    public IReadOnlyList<int> Counts { get; }
    public int Columns { get; }
    public int Rows { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int SampleCount { get; }
}

internal static class KeyDisplay
{
    public static string Label(string raw)
    {
        if (raw.StartsWith("key:", StringComparison.Ordinal) && int.TryParse(raw.AsSpan(4), out var code))
        {
            return WindowsVkNames.GetValueOrDefault(code, "其他按键");
        }

        return raw switch
        {
            " " => "空格",
            "\t" => "Tab",
            _ => raw
        };
    }

    private static readonly Dictionary<int, string> WindowsVkNames = new()
    {
        [8] = "退格",
        [9] = "Tab",
        [13] = "回车",
        [16] = "Shift",
        [17] = "Ctrl",
        [18] = "Alt",
        [20] = "Caps Lock",
        [27] = "Esc",
        [32] = "空格",
        [33] = "PgUp",
        [34] = "PgDn",
        [35] = "End",
        [36] = "Home",
        [37] = "←",
        [38] = "↑",
        [39] = "→",
        [40] = "↓",
        [45] = "Insert",
        [46] = "Delete",
        [91] = "Win",
        [92] = "Win",
        [112] = "F1",
        [113] = "F2",
        [114] = "F3",
        [115] = "F4",
        [116] = "F5",
        [117] = "F6",
        [118] = "F7",
        [119] = "F8",
        [120] = "F9",
        [121] = "F10",
        [122] = "F11",
        [123] = "F12"
    };
}

internal static class HeatDayLabels
{
    public static string For(string day, DateTime today)
    {
        if (day == today.ToString("yyyy-MM-dd"))
        {
            return "今天";
        }

        if (day == today.AddDays(-1).ToString("yyyy-MM-dd"))
        {
            return "昨天";
        }

        return day.Length >= 5 ? day[^5..] : day;
    }
}
