using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Threading;
using InputMonitor.Surface.Controls;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace InputMonitor.Surface.ViewModels;

public sealed class InputMonitorViewModel : ToolSurfacePageViewModel, IDisposable
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer _refreshTimer;
    private readonly ParamCommand _selectGrain;
    private readonly ParamCommand _selectDimension;
    private string _grain = "day";
    private string _dimension = "mouse";
    private DateTime _selectedDate = DateTime.Today;
    private string _statusText = "正在读取…";
    private string _fatigueText = "疲劳 0%";
    private string _frontAppText = "前台应用：—";
    private string _keyboardPrimary = "0";
    private string _keyboardSecondary = "次按键 · 按住 0秒";
    private string _mousePrimary = "0";
    private string _mouseSecondary = "次操作 · 移动 0px";
    private string _windowPrimary = "0分钟";
    private string _interactionPrimary = "0分钟";
    private string _heatmapTitle = "近 7 天分小时热力 · 鼠标";
    private string _trackTitle = "今日鼠标轨迹热力";
    private string _trackCaption = "暂无轨迹数据";
    private string _frequencyUnit = "次按键";
    private string _heatValueKind = "count";
    private bool _paused;
    private bool _showKeyboard;
    private bool _showMouse = true;
    private bool _showApps;
    private bool _showRangeHeat;
    private bool _showDayHeat = true;
    private int _refreshGeneration;
    private int _disposed;
    private int[] _trackCounts = [];
    private double[] _activityValues = new double[24];
    private string[] _activityLabels = Enumerable.Range(0, 24).Select(hour => hour.ToString(CultureInfo.InvariantCulture)).ToArray();
    private double[] _frequencyValues = [];
    private string[] _frequencyLabels = [];
    private string[] _heatDays = [];
    private double[] _heatValues = [];
    private double[] _weekValues = [];
    private string _weekAlignedStart = "";
    private CategoryOption? _selectedCategory;

    public InputMonitorViewModel(MptAvaloniaSurfaceContext context)
        : base("Input Monitor", "本机键盘、鼠标与前台窗口活动统计，并按连续活动提醒休息", ToolSurfaceState.Loading)
    {
        _context = context;
        Categories =
        [
            new CategoryOption("", "全部类型"),
            new CategoryOption("development", "开发"),
            new CategoryOption("browser", "浏览器"),
            new CategoryOption("office", "办公"),
            new CategoryOption("design", "设计"),
            new CategoryOption("social", "社交"),
            new CategoryOption("media", "影音"),
            new CategoryOption("other", "其他")
        ];
        _selectedCategory = Categories[0];
        _selectGrain = new ParamCommand(value =>
        {
            Grain = value;
            _ = RefreshAsync(_lifetime.Token);
        });
        _selectDimension = new ParamCommand(value =>
        {
            Dimension = value;
            _ = RefreshAsync(_lifetime.Token);
        });
        RestCommand = new MptAsyncRelayCommand(() => RunAsync("input-monitor.rest"), null, "InputMonitorRest");
        PauseCommand = new MptAsyncRelayCommand(TogglePauseAsync, null, "InputMonitorPause");
        RefreshCommand = new MptAsyncRelayCommand(() => RefreshAsync(_lifetime.Token), null, "InputMonitorRefresh");
        PreviousDayCommand = new ParamCommand(_ => ShiftDay(-1));
        NextDayCommand = new ParamCommand(_ => ShiftDay(1));
        TodayCommand = new ParamCommand(unused =>
        {
            _selectedDate = DateTime.Today;
            NotifyDate();
            _ = RefreshAsync(_lifetime.Token);
        });
        KeyHeat = [];
        Apps = [];
        CategoryRows = [];
        TrackScreens = [];
        CategorySlices = [];
        _refreshTimer = new Timer(
            _ => Dispatcher.UIThread.Post(() => _ = RefreshAsync(_lifetime.Token)),
            null,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30));
    }

    public ObservableCollection<KeyHeatRow> KeyHeat { get; }
    public ObservableCollection<AppUsageRow> Apps { get; }
    public ObservableCollection<CategoryBreakdownRow> CategoryRows { get; }
    public ObservableCollection<TrackScreenRow> TrackScreens { get; }
    public IReadOnlyList<CategorySlice> CategorySlices { get; private set; }
    public IReadOnlyList<CategoryOption> Categories { get; }
    public ICommand SelectGrainCommand => _selectGrain;
    public ICommand SelectDimensionCommand => _selectDimension;
    public ICommand RestCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand PreviousDayCommand { get; }
    public ICommand NextDayCommand { get; }
    public ICommand TodayCommand { get; }

    public string Grain
    {
        get => _grain;
        private set
        {
            if (SetProperty(ref _grain, value))
            {
                OnPropertyChanged(nameof(GrainIsDay));
                OnPropertyChanged(nameof(GrainIsMonth));
                OnPropertyChanged(nameof(GrainIsQuarter));
                OnPropertyChanged(nameof(GrainIsYear));
                OnPropertyChanged(nameof(ShowBackToToday));
            }
        }
    }

    public string Dimension
    {
        get => _dimension;
        private set
        {
            if (SetProperty(ref _dimension, value))
            {
                OnPropertyChanged(nameof(DimensionIsAll));
                OnPropertyChanged(nameof(DimensionIsKeyboard));
                OnPropertyChanged(nameof(DimensionIsMouse));
                OnPropertyChanged(nameof(DimensionIsApp));
            }
        }
    }

    public bool GrainIsDay => Grain == "day";
    public bool GrainIsMonth => Grain == "month";
    public bool GrainIsQuarter => Grain == "quarter";
    public bool GrainIsYear => Grain == "year";
    public bool DimensionIsAll => Dimension == "all";
    public bool DimensionIsKeyboard => Dimension == "keyboard";
    public bool DimensionIsMouse => Dimension == "mouse";
    public bool DimensionIsApp => Dimension == "app";
    public bool IsSelectedToday => _selectedDate.Date == DateTime.Today;
    public bool ShowBackToToday => GrainIsDay && !IsSelectedToday;
    public bool CanGoForward => _selectedDate.Date < DateTime.Today;
    public string SelectedDateText => _selectedDate.ToString("yyyy/M/d", CultureInfo.CurrentCulture);

    public CategoryOption? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                _ = RefreshAsync(_lifetime.Token);
            }
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string FatigueText { get => _fatigueText; private set => SetProperty(ref _fatigueText, value); }
    public string FrontAppText { get => _frontAppText; private set => SetProperty(ref _frontAppText, value); }
    public string KeyboardPrimary { get => _keyboardPrimary; private set => SetProperty(ref _keyboardPrimary, value); }
    public string KeyboardSecondary { get => _keyboardSecondary; private set => SetProperty(ref _keyboardSecondary, value); }
    public string MousePrimary { get => _mousePrimary; private set => SetProperty(ref _mousePrimary, value); }
    public string MouseSecondary { get => _mouseSecondary; private set => SetProperty(ref _mouseSecondary, value); }
    public string WindowPrimary { get => _windowPrimary; private set => SetProperty(ref _windowPrimary, value); }
    public string InteractionPrimary { get => _interactionPrimary; private set => SetProperty(ref _interactionPrimary, value); }
    public string HeatmapTitle { get => _heatmapTitle; private set => SetProperty(ref _heatmapTitle, value); }
    public string TrackTitle { get => _trackTitle; private set => SetProperty(ref _trackTitle, value); }
    public string TrackCaption { get => _trackCaption; private set => SetProperty(ref _trackCaption, value); }
    public string FrequencyUnit { get => _frequencyUnit; private set => SetProperty(ref _frequencyUnit, value); }
    public string HeatValueKind { get => _heatValueKind; private set => SetProperty(ref _heatValueKind, value); }
    public string PauseLabel => _paused ? "恢复提醒" : "暂停提醒";
    public bool IsReminderPaused => _paused;
    public bool ShowKeyboard { get => _showKeyboard; private set => SetProperty(ref _showKeyboard, value); }
    public bool ShowMouse { get => _showMouse; private set => SetProperty(ref _showMouse, value); }
    public bool ShowApps { get => _showApps; private set => SetProperty(ref _showApps, value); }
    public bool ShowRangeHeat { get => _showRangeHeat; private set => SetProperty(ref _showRangeHeat, value); }
    public bool ShowDayHeat { get => _showDayHeat; private set => SetProperty(ref _showDayHeat, value); }
    public bool ShowTrackSummary => TrackScreens.Count > 1;
    public bool ActivityUseHours => !GrainIsDay;
    public IReadOnlyList<double> ActivityValues { get => _activityValues; private set => SetProperty(ref _activityValues, value.ToArray()); }
    public IReadOnlyList<string> ActivityLabels { get => _activityLabels; private set => SetProperty(ref _activityLabels, value.ToArray()); }
    public IReadOnlyList<double> FrequencyValues { get => _frequencyValues; private set => SetProperty(ref _frequencyValues, value.ToArray()); }
    public IReadOnlyList<string> FrequencyLabels { get => _frequencyLabels; private set => SetProperty(ref _frequencyLabels, value.ToArray()); }
    public IReadOnlyList<string> HeatDays { get => _heatDays; private set => SetProperty(ref _heatDays, value.ToArray()); }
    public IReadOnlyList<double> HeatValues { get => _heatValues; private set => SetProperty(ref _heatValues, value.ToArray()); }
    public IReadOnlyList<double> WeekValues { get => _weekValues; private set => SetProperty(ref _weekValues, value.ToArray()); }
    public string WeekAlignedStart { get => _weekAlignedStart; private set => SetProperty(ref _weekAlignedStart, value); }
    public IReadOnlyList<int> TrackCounts { get => _trackCounts; private set => SetProperty(ref _trackCounts, value.ToArray()); }

    public Task InitializeAsync() => RefreshAsync(_lifetime.Token);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _refreshTimer.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void ShiftDay(int delta)
    {
        var next = _selectedDate.Date.AddDays(delta);
        if (next > DateTime.Today)
        {
            return;
        }

        _selectedDate = next;
        NotifyDate();
        _ = RefreshAsync(_lifetime.Token);
    }

    private void NotifyDate()
    {
        OnPropertyChanged(nameof(SelectedDateText));
        OnPropertyChanged(nameof(IsSelectedToday));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(ShowBackToToday));
    }

    private async Task TogglePauseAsync()
    {
        var next = !_paused;
        await RunOnUiAsync(() => SetPausedUi(next, next ? "已暂停休息提醒，采集仍在继续" : "已恢复休息提醒"))
            .ConfigureAwait(false);

        try
        {
            var response = await ExecuteAsync(
                    "input-monitor.pause",
                    new JsonObject { ["paused"] = next },
                    _lifetime.Token)
                .ConfigureAwait(false);
            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error?.Message ?? "切换提醒失败。");
            }

            var confirmed = ReadPausedFlag(response.Output) ?? next;
            await RunOnUiAsync(() => SetPausedUi(
                    confirmed,
                    confirmed ? "已暂停休息提醒，采集仍在继续" : "已恢复休息提醒"))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() => SetPausedUi(!next, exception.Message)).ConfigureAwait(false);
        }
    }

    private void SetPausedUi(bool paused, string status)
    {
        _paused = paused;
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(IsReminderPaused));
        StatusText = status;
    }

    private async Task RunAsync(string commandId, JsonObject? args = null)
    {
        try
        {
            var response = await ExecuteAsync(commandId, args ?? new JsonObject(), _lifetime.Token).ConfigureAwait(false);
            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error?.Message ?? "命令失败。");
            }

            await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() => StatusText = exception.Message).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var generation = Interlocked.Increment(ref _refreshGeneration);
        try
        {
            var args = new JsonObject
            {
                ["day"] = _selectedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["grain"] = Grain,
                ["dimension"] = Dimension
            };
            if (!string.IsNullOrWhiteSpace(SelectedCategory?.Id))
            {
                args["category"] = SelectedCategory.Id;
            }

            var response = await ExecuteAsync("input-monitor.stats", args, cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _refreshGeneration))
            {
                return;
            }

            if (!response.Success)
            {
                throw new InvalidOperationException(response.Error?.Message ?? "读取统计失败。");
            }

            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(response.Output) ? "{}" : response.Output);
            var root = document.RootElement.Clone();
            await RunOnUiAsync(() => Apply(root)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (generation != Volatile.Read(ref _refreshGeneration))
            {
                return;
            }

            await RunOnUiAsync(() =>
            {
                SetProductState(ToolSurfaceState.Ready, exception.Message);
            }).ConfigureAwait(false);
        }
    }

    private void Apply(JsonElement root)
    {
        var snapshot = root.GetProperty("snapshot");
        var metrics = snapshot.GetProperty("metrics");
        var fatigue = snapshot.GetProperty("fatigue");
        var overview = root.TryGetProperty("overview", out var overviewNode) ? overviewNode : metrics;
        _paused = fatigue.GetProperty("isPaused").GetBoolean();
        OnPropertyChanged(nameof(PauseLabel));
        OnPropertyChanged(nameof(IsReminderPaused));
        FatigueText = $"疲劳 {fatigue.GetProperty("percentage").GetInt32()}%";
        FrontAppText = $"前台应用：{snapshot.GetProperty("frontAppName").GetString() ?? "—"}";
        var capturing = snapshot.GetProperty("captureRunning").GetBoolean();
        StatusText = capturing
            ? (_paused ? "正在采集本机输入 · 提醒已暂停" : "正在采集本机输入")
            : "采集未运行";

        KeyboardPrimary = DashboardFormat.Count(overview.GetProperty("keyCount").GetInt32());
        KeyboardSecondary = $"次按键 · 按住 {DashboardFormat.Hold(overview.GetProperty("keyDurationMs").GetInt64())}";
        var mouseOps = overview.GetProperty("clickCount").GetInt32() + overview.GetProperty("scrollCount").GetInt32();
        MousePrimary = DashboardFormat.Count(mouseOps);
        MouseSecondary = $"次操作 · 移动 {DashboardFormat.Distance(overview.GetProperty("moveDistance").GetDouble())}";
        var windowSeconds = root.TryGetProperty("selectedDayAppSeconds", out var appSeconds)
            ? appSeconds.GetDouble()
            : overview.GetProperty("activeAppSeconds").GetDouble();
        WindowPrimary = DashboardFormat.CompactDuration(windowSeconds);
        var liveInteraction = overview.GetProperty("interactionSeconds").GetDouble();
        var storedInteraction = root.TryGetProperty("selectedDayInteraction", out var stored) ? stored.GetDouble() : liveInteraction;
        var useLive = !GrainIsDay || IsSelectedToday;
        InteractionPrimary = DashboardFormat.CompactDuration(useLive ? liveInteraction : storedInteraction);

        FrequencyUnit = Dimension switch
        {
            "keyboard" => "次按键",
            "app" => "次窗口切换",
            _ => "次操作"
        };
        HeatValueKind = Dimension is "app" or "all" ? "duration" : "count";
        var dimensionLabel = Dimension switch
        {
            "keyboard" => "键盘",
            "mouse" => "鼠标",
            "app" => "应用",
            _ => "所有"
        };
        HeatmapTitle = GrainIsDay
            ? $"近 7 天分小时热力 · {dimensionLabel}"
            : $"{GrainLabel(Grain)}度热力 · {dimensionLabel}";
        TrackTitle = $"{(IsSelectedToday ? "今日" : $"{_selectedDate.Month}月{_selectedDate.Day}日")}鼠标轨迹热力";
        ShowDayHeat = GrainIsDay;
        ShowRangeHeat = !GrainIsDay;
        ShowKeyboard = GrainIsDay && Dimension == "keyboard";
        ShowMouse = GrainIsDay;
        ShowApps = Dimension is "app" or "all" || !GrainIsDay;

        if (GrainIsDay)
        {
            ActivityValues = ReadDoubleArray(root, "hourlyActivity", 24);
            ActivityLabels = Enumerable.Range(0, 24).Select(hour => hour.ToString(CultureInfo.InvariantCulture)).ToArray();
            FrequencyValues = BucketMinutes(root);
            FrequencyLabels = [];
        }
        else
        {
            var perDay = ReadNamedDoubles(root, "perDayActivity", "seconds");
            ActivityValues = perDay.Select(item => item.Value).ToArray();
            ActivityLabels = perDay.Select(item => item.Key).ToArray();
            var frequency = ReadNamedInts(root, "perDayFrequency", "count");
            FrequencyValues = frequency.Select(item => (double)item.Value).ToArray();
            FrequencyLabels = frequency.Select(item => item.Key).ToArray();
        }

        OnPropertyChanged(nameof(ActivityUseHours));

        var heatDays = new List<string>();
        var heatValues = new List<double>();
        if (root.TryGetProperty("recent7Hourly", out var recent) && recent.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in recent.EnumerateArray())
            {
                var day = row.GetProperty("day").GetString() ?? "";
                heatDays.Add(HeatDayLabels.For(day, DateTime.Today));
                heatValues.AddRange(ReadHourly(row.GetProperty("hourly")));
            }
        }

        HeatDays = heatDays;
        HeatValues = heatValues;

        var weekDays = root.TryGetProperty("weekDays", out var weekDaysNode) ? weekDaysNode.GetInt32() : 0;
        WeekAlignedStart = root.TryGetProperty("weekAlignedStart", out var aligned) ? aligned.GetString() ?? "" : "";
        var heatMap = new Dictionary<string, double>(StringComparer.Ordinal);
        if (root.TryGetProperty("heatDayValues", out var heatDayValues) && heatDayValues.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in heatDayValues.EnumerateObject())
            {
                heatMap[property.Name] = property.Value.GetDouble();
            }
        }

        if (weekDays > 0 && DateTime.TryParseExact(WeekAlignedStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            WeekValues = Enumerable.Range(0, weekDays)
                .Select(offset => heatMap.GetValueOrDefault(start.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
                .ToArray();
        }
        else
        {
            WeekValues = [];
        }

        KeyHeat.Clear();
        if (root.TryGetProperty("keyHeat", out var keyHeat) && keyHeat.ValueKind == JsonValueKind.Array)
        {
            var items = keyHeat.EnumerateArray().Select(item => (
                Label: KeyDisplay.Label(item.GetProperty("label").GetString() ?? ""),
                Count: item.GetProperty("count").GetInt32())).ToArray();
            var max = Math.Max(1, items.Select(item => item.Count).DefaultIfEmpty(1).Max());
            foreach (var item in items)
            {
                KeyHeat.Add(new KeyHeatRow(item.Label, item.Count, item.Count / (double)max));
            }
        }

        TrackScreens.Clear();
        if (root.TryGetProperty("trackScreens", out var screensNode) && screensNode.ValueKind == JsonValueKind.Array && screensNode.GetArrayLength() > 0)
        {
            foreach (var screen in screensNode.EnumerateArray())
            {
                TrackScreens.Add(ReadTrackScreen(screen));
            }
        }
        else
        {
            var sampleCount = ReadInt(root, "trackSampleCount")
                ?? ReadInt(root, "TrackSampleCount");
            var trackCounts = ReadIntArray(root, "trackCounts") ?? ReadIntArray(root, "TrackCounts");
            if (root.TryGetProperty("trackHeat", out var trackHeat) && trackHeat.ValueKind == JsonValueKind.Object)
            {
                sampleCount ??= ReadInt(trackHeat, "sampleCount") ?? ReadInt(trackHeat, "SampleCount");
                trackCounts ??= ReadIntArray(trackHeat, "counts") ?? ReadIntArray(trackHeat, "Counts");
            }

            TrackScreens.Add(new TrackScreenRow(
                "主屏",
                sampleCount is > 0 ? $"采样 {sampleCount.Value:N0} 个点 · 颜色越深表示经过越频繁" : "暂无轨迹数据",
                trackCounts ?? [],
                48,
                27,
                1920,
                1080,
                sampleCount ?? 0));
        }

        var totalSamples = TrackScreens.Sum(screen => screen.SampleCount);
        TrackCaption = TrackScreens.Count <= 1
            ? (totalSamples == 0 ? "暂无轨迹数据" : TrackScreens[0].Caption)
            : $"共 {totalSamples:N0} 个点 · 分 {TrackScreens.Count} 台显示器显示";
        TrackCounts = TrackScreens.Count > 0 ? TrackScreens[0].Counts : [];
        OnPropertyChanged(nameof(ShowTrackSummary));

        Apps.Clear();
        CategoryRows.Clear();
        var slices = new List<CategorySlice>();
        if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Array)
        {
            var rows = apps.EnumerateArray().Select(item => new
            {
                Name = item.GetProperty("appName").GetString() ?? "",
                Category = item.GetProperty("categoryLabel").GetString() ?? "",
                Seconds = item.GetProperty("totalSeconds").GetDouble()
            }).ToArray();
            var max = Math.Max(1, rows.Select(item => item.Seconds).DefaultIfEmpty(1).Max());
            foreach (var row in rows.Take(15))
            {
                Apps.Add(new AppUsageRow(
                    row.Name,
                    row.Category,
                    DashboardFormat.CompactDuration(row.Seconds),
                    row.Seconds / max,
                    DashboardPalette.AppBarBrush));
            }

            var grouped = rows.GroupBy(row => row.Category).Select(group => (Category: group.Key, Seconds: group.Sum(item => item.Seconds)))
                .OrderByDescending(item => item.Seconds)
                .ToArray();
            var total = Math.Max(1, grouped.Sum(item => item.Seconds));
            foreach (var item in grouped)
            {
                slices.Add(new CategorySlice(item.Category, item.Seconds));
                CategoryRows.Add(new CategoryBreakdownRow(
                    item.Category,
                    DashboardFormat.CompactDuration(item.Seconds),
                    $"{item.Seconds / total * 100:0}%",
                    DashboardPalette.CategoryBrush(item.Category)));
            }
        }

        CategorySlices = slices;
        OnPropertyChanged(nameof(CategorySlices));
        SetProductState(ToolSurfaceState.Ready);
    }

    private static TrackScreenRow ReadTrackScreen(JsonElement screen)
    {
        var name = ReadString(screen, "name") ?? ReadString(screen, "Name") ?? "显示器";
        var sampleCount = ReadInt(screen, "sampleCount") ?? ReadInt(screen, "SampleCount") ?? 0;
        var width = ReadInt(screen, "width") ?? ReadInt(screen, "Width") ?? 1920;
        var height = ReadInt(screen, "height") ?? ReadInt(screen, "Height") ?? 1080;
        var columns = ReadInt(screen, "cols") ?? ReadInt(screen, "Cols") ?? 48;
        var rows = ReadInt(screen, "rows") ?? ReadInt(screen, "Rows") ?? 27;
        var counts = ReadIntArray(screen, "counts") ?? ReadIntArray(screen, "Counts") ?? [];
        return new TrackScreenRow(
            $"{name} · {width}×{height}",
            sampleCount > 0 ? $"采样 {sampleCount:N0} 个点 · 颜色越深表示经过越频繁" : "暂无轨迹数据",
            counts,
            columns,
            rows,
            width,
            height,
            sampleCount);
    }

    private static string? ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return node.GetString();
    }

    private static bool? ReadPausedFlag(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (root.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (root.TryGetProperty("paused", out var paused) && paused.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return paused.GetBoolean();
            }

            if (root.TryGetProperty("snapshot", out var snapshot) &&
                snapshot.TryGetProperty("fatigue", out var nested) &&
                nested.TryGetProperty("isPaused", out var nestedPaused) &&
                nestedPaused.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return nestedPaused.GetBoolean();
            }

            if (root.TryGetProperty("fatigue", out var fatigue) &&
                fatigue.TryGetProperty("isPaused", out var flag) &&
                flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return flag.GetBoolean();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static int? ReadInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return node.TryGetInt32(out var value) ? value : (int)node.GetInt64();
    }

    private static int[]? ReadIntArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return node.EnumerateArray().Select(item => item.TryGetInt32(out var value) ? value : (int)item.GetInt64()).ToArray();
    }

    private static string GrainLabel(string grain) => grain switch
    {
        "month" => "月",
        "quarter" => "季",
        "year" => "年",
        _ => "日"
    };

    private static double[] BucketMinutes(JsonElement root)
    {
        var buckets = new double[144];
        if (!root.TryGetProperty("perMinute", out var perMinute) || perMinute.ValueKind != JsonValueKind.Array)
        {
            return buckets;
        }

        foreach (var item in perMinute.EnumerateArray())
        {
            var minute = item.GetProperty("minute").GetInt32();
            var count = item.GetProperty("count").GetInt32();
            var index = Math.Clamp(minute / 10, 0, 143);
            buckets[index] += count;
        }

        return buckets;
    }

    private static double[] ReadDoubleArray(JsonElement root, string name, int length)
    {
        var values = new double[length];
        if (!root.TryGetProperty(name, out var node))
        {
            return values;
        }

        if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
            {
                if (index >= length)
                {
                    break;
                }

                values[index++] = item.GetDouble();
            }

            return values;
        }

        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (int.TryParse(property.Name, out var hour) && hour >= 0 && hour < length)
                {
                    values[hour] = property.Value.GetDouble();
                }
            }
        }

        return values;
    }

    private static double[] ReadHourly(JsonElement node)
    {
        var values = new double[24];
        if (node.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in node.EnumerateArray())
            {
                if (index >= 24)
                {
                    break;
                }

                values[index++] = item.GetDouble();
            }
        }
        else if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (int.TryParse(property.Name, out var hour) && hour is >= 0 and < 24)
                {
                    values[hour] = property.Value.GetDouble();
                }
            }
        }

        return values;
    }

    private static List<KeyValuePair<string, double>> ReadNamedDoubles(JsonElement root, string name, string valueName)
    {
        var result = new List<KeyValuePair<string, double>>();
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in node.EnumerateArray())
        {
            result.Add(new KeyValuePair<string, double>(
                item.GetProperty("day").GetString() ?? "",
                item.GetProperty(valueName).GetDouble()));
        }

        return result;
    }

    private static List<KeyValuePair<string, int>> ReadNamedInts(JsonElement root, string name, string valueName)
    {
        var result = new List<KeyValuePair<string, int>>();
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in node.EnumerateArray())
        {
            result.Add(new KeyValuePair<string, int>(
                item.GetProperty("day").GetString() ?? "",
                item.GetProperty(valueName).GetInt32()));
        }

        return result;
    }

    private async Task<CommandExecutionResult> ExecuteAsync(string commandId, JsonObject args, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        return await _context.ExecuteCommandAsync(commandId, args, deadline.Token).ConfigureAwait(false);
    }

    private static async Task RunOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, DispatcherPriority.Send);
        await completion.Task.ConfigureAwait(false);
    }

    private sealed class ParamCommand : ICommand
    {
        private readonly Action<string> _execute;

        public ParamCommand(Action<string> execute) => _execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => _execute(parameter?.ToString() ?? "");
    }
}
