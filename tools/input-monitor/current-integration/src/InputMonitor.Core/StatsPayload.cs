using System.Globalization;

namespace InputMonitor.Core;

public sealed class StatsQuery
{
    public string? Day { get; init; }
    public string Grain { get; init; } = "day";
    public string Dimension { get; init; } = "keyboard";
    public string? Category { get; init; }
    public int ScreenWidth { get; init; } = 1920;
    public int ScreenHeight { get; init; } = 1080;
    public int ScreenOriginX { get; init; }
    public int ScreenOriginY { get; init; }
    public IReadOnlyList<ScreenBounds> Screens { get; init; } = [];
}

public readonly record struct ScreenBounds(
    int OriginX,
    int OriginY,
    int Width,
    int Height,
    bool IsPrimary,
    string Name)
{
    public bool Contains(double x, double y) =>
        x >= OriginX && x < OriginX + Math.Max(1, Width) &&
        y >= OriginY && y < OriginY + Math.Max(1, Height);
}

public sealed class TrackHeatMap
{
    public const int LongSide = 48;

    public string Name { get; init; } = "";
    public bool IsPrimary { get; init; }
    public int ColsCount { get; init; } = LongSide;
    public int RowsCount { get; init; } = 27;
    public int SampleCount { get; init; }
    public int[] Counts { get; init; } = [];
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }

    public static (int Cols, int Rows) GridSize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width >= height)
        {
            return (LongSide, Math.Clamp((int)Math.Round(LongSide * (double)height / width), 12, 64));
        }

        return (Math.Clamp((int)Math.Round(LongSide * (double)width / height), 12, 64), LongSide);
    }

    public static TrackHeatMap FromPoints(
        IReadOnlyList<(double X, double Y)> points,
        int screenWidth,
        int screenHeight,
        int originX,
        int originY,
        string name = "主屏",
        bool isPrimary = true) =>
        FromPoints(points, new ScreenBounds(originX, originY, screenWidth, screenHeight, isPrimary, name));

    public static TrackHeatMap FromPoints(
        IReadOnlyList<(double X, double Y)> points,
        ScreenBounds screen)
    {
        var width = Math.Max(1, screen.Width);
        var height = Math.Max(1, screen.Height);
        var (cols, rows) = GridSize(width, height);
        var counts = new int[cols * rows];
        var sampleCount = 0;
        foreach (var (x, y) in points)
        {
            if (!screen.Contains(x, y))
            {
                continue;
            }

            var col = Math.Clamp((int)((x - screen.OriginX) / width * cols), 0, cols - 1);
            var row = Math.Clamp((int)((y - screen.OriginY) / height * rows), 0, rows - 1);
            counts[row * cols + col]++;
            sampleCount++;
        }

        return new TrackHeatMap
        {
            Name = string.IsNullOrWhiteSpace(screen.Name) ? "显示器" : screen.Name,
            IsPrimary = screen.IsPrimary,
            ColsCount = cols,
            RowsCount = rows,
            SampleCount = sampleCount,
            Counts = counts,
            ScreenWidth = width,
            ScreenHeight = height
        };
    }

    public static IReadOnlyList<ScreenBounds> FallbackScreens(StatsQuery query)
    {
        if (query.Screens.Count > 0)
        {
            return query.Screens;
        }

        var width = query.ScreenWidth > 0 ? query.ScreenWidth : 1920;
        var height = query.ScreenHeight > 0 ? query.ScreenHeight : 1080;
        return [new ScreenBounds(query.ScreenOriginX, query.ScreenOriginY, width, height, true, "主屏")];
    }
}

public static class StatsPayloadBuilder
{
    private static readonly string[] KeyboardOpKinds = [InputEventKinds.KeyDown];
    private static readonly string[] KeyboardActiveKinds =
        [InputEventKinds.KeyDown, InputEventKinds.KeyUp, InputEventKinds.FlagsChanged];
    private static readonly string[] MouseKinds =
        [InputEventKinds.LeftClick, InputEventKinds.RightClick, InputEventKinds.Scroll];
    private static readonly string[] AllOpKinds =
        [InputEventKinds.KeyDown, InputEventKinds.LeftClick, InputEventKinds.RightClick, InputEventKinds.Scroll];

    public static object Build(
        EventRepository repository,
        LiveSnapshot live,
        MonitorSettings settings,
        Func<DaySummary, double, MetricsSnapshot> toMetrics,
        StatsQuery query)
    {
        var today = EventRepository.DayString(DateTimeOffset.Now);
        var selectedDay = string.IsNullOrWhiteSpace(query.Day) ? today : query.Day;
        var grain = NormalizeGrain(query.Grain);
        var dimension = NormalizeDimension(query.Dimension);
        AppCategories.TryParse(query.Category, out var parsedCategory);
        AppCategory? categoryFilter = string.IsNullOrWhiteSpace(query.Category) ? null : parsedCategory;

        var (rangeStart, rangeEnd, startDay, endDay) = RangeForGrain(selectedDay, grain);
        var (alignedStart, weekDays) = WeekGrid(startDay, endDay, grain);
        var gridStartDay = grain == "day" ? startDay : alignedStart;

        var headerDay = grain == "day" ? selectedDay : today;
        var appSeconds = repository.AppSecondsByDay(gridStartDay, endDay);
        var overview = headerDay == live.Metrics.Day
            ? live.Metrics
            : toMetrics(repository.DaySummaryFor(headerDay), repository.InteractionSeconds(headerDay));

        var (opKinds, activeKinds, includeTrack) = DimensionKinds(dimension);
        double[] hourlyActivity;
        IReadOnlyList<object> perDayActivity;
        if (grain == "day")
        {
            hourlyActivity = Hours(ActivityHours(repository, selectedDay, dimension, activeKinds, includeTrack, categoryFilter));
            perDayActivity = [];
        }
        else
        {
            hourlyActivity = new double[24];
            var byDay = ActivityDays(repository, startDay, endDay, dimension, activeKinds, includeTrack, categoryFilter);
            perDayActivity = FillDays(startDay, endDay)
                .Select(day => (object)new { day, seconds = byDay.GetValueOrDefault(day) })
                .ToArray();
        }

        var recent7 = new List<object>();
        if (grain == "day")
        {
            var selectedDate = ParseDay(selectedDay) ?? DateTime.Now.Date;
            for (var offset = 6; offset >= 0; offset--)
            {
                var day = selectedDate.AddDays(-offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                recent7.Add(new
                {
                    day,
                    hourly = Hours(RecentHourly(repository, day, dimension, categoryFilter))
                });
            }
        }

        Dictionary<string, double> heatDayValues = [];
        if (grain != "day")
        {
            heatDayValues = dimension switch
            {
                "app" => repository.PerDayAppSeconds(gridStartDay, endDay, categoryFilter),
                "all" => repository.PerDayInteractionSeconds(gridStartDay, endDay, categoryFilter),
                _ => HeatFromSummaries(repository.DaySummaries(gridStartDay, endDay), dimension)
            };
        }

        IReadOnlyList<(int Minute, int Count)> perMinute = [];
        IReadOnlyList<object> perDayFrequency = [];
        if (grain == "day")
        {
            perMinute = MinuteSeries(repository, selectedDay, dimension, opKinds);
        }
        else
        {
            var counts = dimension == "app"
                ? repository.PerDaySessionStarts(startDay, endDay)
                : repository.PerDayOperationCounts(startDay, endDay, opKinds);
            if (dimension == "all")
            {
                foreach (var (day, count) in repository.PerDaySessionStarts(startDay, endDay))
                {
                    counts[day] = counts.GetValueOrDefault(day) + count;
                }
            }

            perDayFrequency = FillDays(startDay, endDay)
                .Select(day => (object)new { day, count = counts.GetValueOrDefault(day) })
                .ToArray();
        }

        var apps = repository.AppUsage(rangeStart, rangeEnd)
            .Where(app => categoryFilter is null || app.Category == categoryFilter)
            .Select(app => new
            {
                app.AppName,
                app.BundleId,
                category = AppCategories.ToStorage(app.Category),
                categoryLabel = AppCategories.DisplayName(app.Category),
                app.TotalSeconds
            })
            .ToArray();

        var keyHeat = grain == "day" && dimension == "keyboard" ? repository.KeyHeat(selectedDay) : [];
        var points = grain == "day" ? repository.TrackPoints(selectedDay) : [];
        var screens = TrackHeatMap.FallbackScreens(query);
        var trackScreens = screens.Select(screen => TrackHeatMap.FromPoints(points, screen)).ToArray();
        var trackHeat = trackScreens.FirstOrDefault(map => map.IsPrimary) ?? trackScreens.FirstOrDefault() ?? new TrackHeatMap();

        return new
        {
            snapshot = live,
            selectedDay,
            grain,
            dimension,
            category = query.Category,
            overview,
            selectedDayAppSeconds = appSeconds.GetValueOrDefault(headerDay),
            todayAppSeconds = appSeconds.GetValueOrDefault(today),
            selectedDayInteraction = grain == "day" ? repository.InteractionSeconds(selectedDay) : 0d,
            hourlyActivity,
            perDayActivity,
            recent7Hourly = recent7,
            keyHeat,
            trackHeat = ToTrackDto(trackHeat),
            trackSampleCount = trackScreens.Sum(map => map.SampleCount),
            trackCounts = trackHeat.Counts,
            trackScreens = trackScreens.Select(ToTrackDto).ToArray(),
            apps,
            perMinute = perMinute.Select(item => new { minute = item.Minute, count = item.Count }).ToArray(),
            perDayFrequency,
            heatDayValues,
            weekAlignedStart = alignedStart,
            weekDays,
            daySummaries = repository.DaySummaries(gridStartDay, endDay),
            settings
        };
    }

    private static object ToTrackDto(TrackHeatMap map) => new
    {
        name = map.Name,
        isPrimary = map.IsPrimary,
        sampleCount = map.SampleCount,
        counts = map.Counts,
        cols = map.ColsCount,
        rows = map.RowsCount,
        width = map.ScreenWidth,
        height = map.ScreenHeight
    };

    public static IReadOnlyList<string> FillDays(string startDay, string endDay)
    {
        var start = ParseDay(startDay);
        var end = ParseDay(endDay);
        if (start is null || end is null || end < start)
        {
            return [];
        }

        var days = new List<string>();
        for (var cursor = start.Value; cursor <= end.Value; cursor = cursor.AddDays(1))
        {
            days.Add(cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        return days;
    }

    private static Dictionary<int, double> ActivityHours(
        EventRepository repository,
        string day,
        string dimension,
        IReadOnlyList<string> activeKinds,
        bool includeTrack,
        AppCategory? categoryFilter) =>
        dimension switch
        {
            "keyboard" or "mouse" => repository.HourlyEventActiveSeconds(day, activeKinds, includeTrack),
            "app" => repository.HourlyAppSeconds(day, categoryFilter),
            _ => repository.HourlyInteractionSeconds(day, categoryFilter)
        };

    private static Dictionary<string, double> ActivityDays(
        EventRepository repository,
        string startDay,
        string endDay,
        string dimension,
        IReadOnlyList<string> activeKinds,
        bool includeTrack,
        AppCategory? categoryFilter) =>
        dimension switch
        {
            "keyboard" or "mouse" => repository.PerDayEventActiveSeconds(startDay, endDay, activeKinds, includeTrack),
            "app" => repository.PerDayAppSeconds(startDay, endDay, categoryFilter),
            _ => repository.PerDayInteractionSeconds(startDay, endDay, categoryFilter)
        };

    private static Dictionary<int, double> RecentHourly(
        EventRepository repository,
        string day,
        string dimension,
        AppCategory? categoryFilter) =>
        dimension switch
        {
            "keyboard" => ToDouble(repository.HourlyCounts(day, KeyboardOpKinds)),
            "mouse" => ToDouble(repository.HourlyCounts(day, MouseKinds)),
            "app" => repository.HourlyAppSeconds(day, categoryFilter),
            _ => repository.HourlyInteractionSeconds(day, categoryFilter)
        };

    private static IReadOnlyList<(int Minute, int Count)> MinuteSeries(
        EventRepository repository,
        string day,
        string dimension,
        IReadOnlyList<string> opKinds)
    {
        if (dimension == "app")
        {
            return repository.PerMinuteSessionStarts(day);
        }

        var merged = new Dictionary<int, int>();
        foreach (var (minute, count) in repository.PerMinuteCounts(day, opKinds))
        {
            merged[minute] = merged.GetValueOrDefault(minute) + count;
        }

        if (dimension == "all")
        {
            foreach (var (minute, count) in repository.PerMinuteSessionStarts(day))
            {
                merged[minute] = merged.GetValueOrDefault(minute) + count;
            }
        }

        return merged.Select(pair => (pair.Key, pair.Value)).OrderBy(item => item.Key).ToArray();
    }

    private static Dictionary<string, double> HeatFromSummaries(Dictionary<string, DaySummary> summaries, string dimension)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (day, summary) in summaries)
        {
            result[day] = dimension == "keyboard"
                ? summary.KeyCount
                : summary.ClickCount + summary.ScrollCount;
        }

        return result;
    }

    private static (IReadOnlyList<string> OpKinds, IReadOnlyList<string> ActiveKinds, bool IncludeTrack) DimensionKinds(string dimension) =>
        dimension switch
        {
            "keyboard" => (KeyboardOpKinds, KeyboardActiveKinds, false),
            "mouse" => (MouseKinds, MouseKinds, true),
            "app" => ([], [], false),
            _ => (AllOpKinds, [], false)
        };

    private static double[] Hours(Dictionary<int, double> map)
    {
        var values = new double[24];
        foreach (var (hour, value) in map)
        {
            if ((uint)hour < 24)
            {
                values[hour] = value;
            }
        }

        return values;
    }

    private static Dictionary<int, double> ToDouble(Dictionary<int, int> map)
    {
        var result = new Dictionary<int, double>(map.Count);
        foreach (var (key, value) in map)
        {
            result[key] = value;
        }

        return result;
    }

    private static string NormalizeGrain(string? grain) =>
        grain?.Trim().ToLowerInvariant() switch
        {
            "month" or "月" => "month",
            "quarter" or "季" => "quarter",
            "year" or "年" => "year",
            _ => "day"
        };

    private static string NormalizeDimension(string? dimension) =>
        dimension?.Trim().ToLowerInvariant() switch
        {
            "keyboard" or "键盘" => "keyboard",
            "mouse" or "鼠标" => "mouse",
            "app" or "应用" => "app",
            _ => "all"
        };

    internal static (double Start, double End, string StartDay, string EndDay) RangeForGrain(string selectedDay, string grain)
    {
        var today = DateTime.Now.Date;
        var selected = ParseDay(selectedDay) ?? today;
        DateTime startDate;
        DateTime endDate;
        switch (grain)
        {
            case "month":
                startDate = new DateTime(today.Year, today.Month, 1);
                endDate = startDate.AddMonths(1).AddDays(-1);
                break;
            case "quarter":
                var quarter = ((today.Month - 1) / 3) * 3 + 1;
                startDate = new DateTime(today.Year, quarter, 1);
                endDate = startDate.AddMonths(3).AddDays(-1);
                break;
            case "year":
                startDate = new DateTime(today.Year, 1, 1);
                endDate = new DateTime(today.Year, 12, 31);
                break;
            default:
                startDate = selected;
                endDate = selected;
                break;
        }

        var start = new DateTimeOffset(startDate, TimeZoneInfo.Local.GetUtcOffset(startDate)).ToUnixTimeSeconds();
        var end = new DateTimeOffset(endDate.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(endDate.AddDays(1))).ToUnixTimeSeconds();
        return (start, end, startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    internal static (string AlignedStart, int Days) WeekGrid(string startDay, string endDay, string grain)
    {
        var start = ParseDay(startDay) ?? DateTime.Now.Date;
        var end = ParseDay(endDay) ?? start;
        var offsetFromMonday = ((int)start.DayOfWeek + 6) % 7;
        var aligned = start.AddDays(-offsetFromMonday);
        if (grain == "day")
        {
            aligned = start;
        }

        return (aligned.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (end - aligned).Days + 1);
    }

    private static DateTime? ParseDay(string? day)
    {
        if (DateTime.TryParseExact(
                day,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed.Date;
        }

        return null;
    }
}
