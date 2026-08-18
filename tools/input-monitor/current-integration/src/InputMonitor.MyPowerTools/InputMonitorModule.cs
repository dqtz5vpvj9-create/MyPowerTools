using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using InputMonitor.Core;
using MyPowerTools.Abstractions;
using MyPowerTools.Protocol;

namespace InputMonitor.MyPowerTools;

public sealed class InputMonitorModule : IMptModule, IMptModuleLifecycle
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly Channel<MptModuleEvent> _events = Channel.CreateUnbounded<MptModuleEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private InputMonitorHost? _host;
    private long _eventSequence;

    public string Id => "input-monitor";
    public string PackageId => "input-monitor";
    public Version Version => new(0, 1, 3);

    public ValueTask<InitializeResult> InitializeAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(context.DataDirectory);
        Directory.CreateDirectory(context.LogDirectory);
        var categories = new AppCategoryMap();
        IInputCapture? capture = null;
        IFrontAppTracker? tracker = null;
        IRestOverlay? overlay = null;
        if (OperatingSystem.IsWindows() && !SuppressLiveCapture())
        {
            (capture, tracker, overlay) = CreateWindowsCapture(categories);
        }
        _host?.Dispose();
        _host = new InputMonitorHost(context.DataDirectory, capture, tracker, overlay, categories);
        _host.Start();
        return ValueTask.FromResult(new InitializeResult(true, context.ProtocolVersion, ["status", "commands", "settings", "logs"]));
    }

    public ValueTask StartAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _host?.Start();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(ModuleContext context, CancellationToken cancellationToken)
    {
        _host?.Stop();
        return ValueTask.CompletedTask;
    }

    public ValueTask<ModuleStatusSnapshot> GetStatusAsync(CancellationToken cancellationToken)
    {
        var host = RequireHost();
        var snapshot = host.Snapshot();
        var windows = OperatingSystem.IsWindows();
        var checks = new[]
        {
            new HealthCheckSnapshot("platform.windows", "Windows capture", windows, windows ? "Low-level hooks are available." : "Input capture currently runs on Windows only."),
            new HealthCheckSnapshot("capture.running", "Collector", snapshot.CaptureRunning || !windows, snapshot.CaptureRunning ? "Collecting keyboard, mouse, and foreground app activity." : "Collector is idle.")
        };
        var ready = checks.All(check => check.Ok);
        var summary = windows
            ? $"疲劳 {snapshot.Fatigue.Percentage}% · 今日互动 {FormatDuration(snapshot.Metrics.InteractionSeconds)}"
            : "Input Monitor is installed, but live capture is Windows-only.";
        return ValueTask.FromResult(new ModuleStatusSnapshot(
            Id,
            ready ? "running" : "degraded",
            summary,
            DateTimeOffset.UtcNow,
            checks,
            (ulong)Math.Max(0, Interlocked.Read(ref _eventSequence))));
    }

    public ValueTask<IReadOnlyList<MptCommandDescriptor>> ListCommandsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MptCommandDescriptor> commands =
        [
            Command("input-monitor.snapshot", "Today's Input Monitor snapshot", "Read live fatigue and activity counters"),
            Command("input-monitor.stats", "Input Monitor statistics", "Query daily or range statistics for the stats workspace"),
            Command("input-monitor.rest", "Start a rest break", "Open the rest overlay using the current fatigue value"),
            Command("input-monitor.pause", "Pause or resume reminders", "Toggle rest reminders without stopping collection"),
            Command("input-monitor.skip", "Skip the current rest", "Dismiss the rest overlay and raise the next threshold"),
            Command("input-monitor.set-category", "Override an app category", "Remap a process to a statistics category")
        ];
        return ValueTask.FromResult(commands);
    }

    public ValueTask<CommandExecutionResult> ExecuteCommandAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        var host = RequireHost();
        try
        {
            if (string.Equals(request.CommandId, "input-monitor.snapshot", StringComparison.OrdinalIgnoreCase))
            {
                return Ok(request, JsonSerializer.Serialize(host.Snapshot(), JsonOptions));
            }

            if (string.Equals(request.CommandId, "input-monitor.stats", StringComparison.OrdinalIgnoreCase))
            {
                var day = ReadString(request.Args, "day");
                var grain = ReadString(request.Args, "grain") ?? "day";
                var dimension = ReadString(request.Args, "dimension") ?? "keyboard";
                var category = ReadString(request.Args, "category");
                var screens = WindowsDisplays.Enumerate();
                var chosen = screens.FirstOrDefault(screen => screen.IsPrimary);
                if (chosen.Width <= 0)
                {
                    chosen = screens[0];
                }

                return Ok(request, JsonSerializer.Serialize(host.BuildStatsPayload(new StatsQuery
                {
                    Day = day,
                    Grain = grain,
                    Dimension = dimension,
                    Category = category,
                    ScreenWidth = chosen.Width,
                    ScreenHeight = chosen.Height,
                    ScreenOriginX = chosen.OriginX,
                    ScreenOriginY = chosen.OriginY,
                    Screens = screens
                }), JsonOptions));
            }

            if (string.Equals(request.CommandId, "input-monitor.rest", StringComparison.OrdinalIgnoreCase))
            {
                host.Fatigue.ManualRest();
                return Ok(request, JsonSerializer.Serialize(host.Snapshot(), JsonOptions));
            }

            if (string.Equals(request.CommandId, "input-monitor.pause", StringComparison.OrdinalIgnoreCase))
            {
                var paused = ReadBool(request.Args, "paused") ?? !host.Fatigue.Snapshot().IsPaused;
                host.Fatigue.SetPaused(paused);
                var snapshot = host.Snapshot();
                return Ok(request, JsonSerializer.Serialize(new { paused = snapshot.Fatigue.IsPaused, snapshot }, JsonOptions));
            }

            if (string.Equals(request.CommandId, "input-monitor.skip", StringComparison.OrdinalIgnoreCase))
            {
                host.Fatigue.Skip();
                return Ok(request, JsonSerializer.Serialize(host.Snapshot(), JsonOptions));
            }

            if (string.Equals(request.CommandId, "input-monitor.set-category", StringComparison.OrdinalIgnoreCase))
            {
                var bundleId = ReadString(request.Args, "bundleId");
                var categoryName = ReadString(request.Args, "category");
                if (string.IsNullOrWhiteSpace(bundleId) || !AppCategories.TryParse(categoryName, out var category))
                {
                    return ValueTask.FromResult(Failed(request, MptErrorCodes.ValidationFailed, "bundleId and a valid category are required."));
                }

                host.Categories.SetOverride(bundleId, category);
                Interlocked.Increment(ref _eventSequence);
                return Ok(request, JsonSerializer.Serialize(new { bundleId, category = AppCategories.ToStorage(category) }, JsonOptions));
            }

            return ValueTask.FromResult(Failed(request, MptErrorCodes.NotFound, $"Command '{request.CommandId}' is not implemented by Input Monitor."));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Failed(request, MptErrorCodes.RuntimeUnavailable, exception.Message));
        }
    }

    public async IAsyncEnumerable<MptModuleEvent> SubscribeEventsAsync(
        EventCursor cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var moduleEvent in _events.Reader.ReadAllAsync(cancellationToken))
        {
            if (moduleEvent.Seq > cursor.LastEventSeq)
            {
                yield return moduleEvent;
            }
        }
    }

    public ValueTask<SettingsSchemaDocument> GetSettingsSchemaAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(new SettingsSchemaDocument(Id, """
        {
          "type": "object",
          "properties": {
            "remindIntervalMinutes": { "type": "number", "title": "疲劳提醒间隔（分钟）", "minimum": 5, "maximum": 120, "default": 20 },
            "restDurationSeconds": { "type": "integer", "title": "休息时长（秒）", "minimum": 10, "maximum": 7200, "default": 300 },
            "fatigueFromKeyboard": { "type": "boolean", "title": "键盘计入疲劳", "default": true },
            "fatigueFromMouse": { "type": "boolean", "title": "鼠标计入疲劳", "default": true },
            "fatigueFromApp": { "type": "boolean", "title": "前台窗口计入疲劳", "default": true },
            "privacyMode": { "type": "boolean", "title": "隐私模式（不记录按键字符）", "default": false },
            "trackSampleDistance": { "type": "number", "title": "轨迹采样最小距离（像素）", "minimum": 5, "maximum": 200, "default": 30 },
            "appHeartbeatSeconds": { "type": "number", "title": "前台窗口心跳（秒）", "minimum": 5, "maximum": 120, "default": 30 },
            "remindAfterResume": { "type": "boolean", "title": "恢复提醒后若已超阈值立即提醒", "default": true },
            "dataRetentionDays": { "type": "integer", "title": "数据保留天数", "minimum": 1, "maximum": 36500, "default": 365 }
          }
        }
        """));
    }

    public ValueTask<SettingsSnapshotDocument> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = RequireHost().Settings;
        var values = new JsonObject
        {
            ["remindIntervalMinutes"] = settings.RemindIntervalMinutes,
            ["restDurationSeconds"] = settings.RestDurationSeconds,
            ["fatigueFromKeyboard"] = settings.FatigueFromKeyboard,
            ["fatigueFromMouse"] = settings.FatigueFromMouse,
            ["fatigueFromApp"] = settings.FatigueFromApp,
            ["privacyMode"] = settings.PrivacyMode,
            ["trackSampleDistance"] = settings.TrackSampleDistance,
            ["appHeartbeatSeconds"] = settings.AppHeartbeatSeconds,
            ["remindAfterResume"] = settings.RemindAfterResume,
            ["dataRetentionDays"] = settings.DataRetentionDays
        };
        return ValueTask.FromResult(new SettingsSnapshotDocument(Id, 1, values, DateTimeOffset.UtcNow));
    }

    public ValueTask<SettingsValidationResult> ValidateSettingsAsync(SettingsPatch patch, CancellationToken cancellationToken)
    {
        var current = ToJson(RequireHost().Settings);
        var merged = SettingsJson.Merge(current, patch.Patch);
        var next = FromJson(merged);
        next.Clamp();
        return ValueTask.FromResult(new SettingsValidationResult(true, []));
    }

    public ValueTask<SettingsSnapshotDocument> ApplySettingsAsync(SettingsSnapshotDocument snapshot, CancellationToken cancellationToken)
    {
        var host = RequireHost();
        var next = FromJson(SettingsJson.Merge(ToJson(host.Settings), snapshot.Values));
        host.ApplySettings(next);
        return GetSettingsAsync(cancellationToken);
    }

    public ValueTask<IReadOnlyList<UiSurfaceDescriptor>> ListSurfacesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<UiSurfaceDescriptor> surfaces =
        [
            new("input-monitor.dashboard", "dashboard-card", "Input Monitor", new JsonObject { ["state"] = "ready" }),
            new("input-monitor.stats", "detail-page", "Input Monitor", new JsonObject { ["moduleId"] = Id })
        ];
        return ValueTask.FromResult(surfaces);
    }

    public ValueTask DisposeAsync(CancellationToken cancellationToken)
    {
        _host?.Dispose();
        _host = null;
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private InputMonitorHost RequireHost() =>
        _host ?? throw new InvalidOperationException("Input Monitor has not been initialized.");

    [SupportedOSPlatform("windows")]
    private static (IInputCapture Capture, IFrontAppTracker Tracker, IRestOverlay Overlay) CreateWindowsCapture(
        AppCategoryMap categories) =>
        (new WindowsInputCapture(), new WindowsFrontAppTracker(categories), new WindowsRestOverlay());

    private static bool SuppressLiveCapture()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MPT_DISABLE_INPUT_CAPTURE")))
        {
            return true;
        }

        var processName = Process.GetCurrentProcess().ProcessName;
        return processName.Contains("testhost", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("vstest", StringComparison.OrdinalIgnoreCase);
    }

    private static MptCommandDescriptor Command(string id, string title, string subtitle) =>
        new(id, "input-monitor", title, subtitle, "action", Category: "Wellbeing", TimeoutMs: 10000, SupportsCancellation: true);

    private static ValueTask<CommandExecutionResult> Ok(CommandRequest request, string output) =>
        ValueTask.FromResult(new CommandExecutionResult(request.InvocationId, request.CommandId, "succeeded", true, output));

    private static CommandExecutionResult Failed(CommandRequest request, string code, string message) =>
        new(request.InvocationId, request.CommandId, "failed", false, "", new MptRuntimeError(code, message));

    private static string? ReadString(JsonObject args, string name) => SettingsJson.ReadString(args, name);

    private static bool? ReadBool(JsonObject args, string name) => SettingsJson.ReadBool(args, name);

    private static JsonObject ToJson(MonitorSettings settings) => new()
    {
        ["remindIntervalMinutes"] = settings.RemindIntervalMinutes,
        ["restDurationSeconds"] = settings.RestDurationSeconds,
        ["fatigueFromKeyboard"] = settings.FatigueFromKeyboard,
        ["fatigueFromMouse"] = settings.FatigueFromMouse,
        ["fatigueFromApp"] = settings.FatigueFromApp,
        ["privacyMode"] = settings.PrivacyMode,
        ["trackSampleDistance"] = settings.TrackSampleDistance,
        ["appHeartbeatSeconds"] = settings.AppHeartbeatSeconds,
        ["remindAfterResume"] = settings.RemindAfterResume,
        ["dataRetentionDays"] = settings.DataRetentionDays
    };

    private static MonitorSettings FromJson(JsonObject values)
    {
        var settings = new MonitorSettings();
        settings.RemindIntervalMinutes = SettingsJson.ReadDouble(values, "remindIntervalMinutes") ?? settings.RemindIntervalMinutes;
        settings.RestDurationSeconds = SettingsJson.ReadInt(values, "restDurationSeconds") ?? settings.RestDurationSeconds;
        settings.FatigueFromKeyboard = SettingsJson.ReadBool(values, "fatigueFromKeyboard") ?? settings.FatigueFromKeyboard;
        settings.FatigueFromMouse = SettingsJson.ReadBool(values, "fatigueFromMouse") ?? settings.FatigueFromMouse;
        settings.FatigueFromApp = SettingsJson.ReadBool(values, "fatigueFromApp") ?? settings.FatigueFromApp;
        settings.PrivacyMode = SettingsJson.ReadBool(values, "privacyMode") ?? settings.PrivacyMode;
        settings.TrackSampleDistance = SettingsJson.ReadDouble(values, "trackSampleDistance") ?? settings.TrackSampleDistance;
        settings.AppHeartbeatSeconds = SettingsJson.ReadDouble(values, "appHeartbeatSeconds") ?? settings.AppHeartbeatSeconds;
        settings.RemindAfterResume = SettingsJson.ReadBool(values, "remindAfterResume") ?? settings.RemindAfterResume;
        settings.DataRetentionDays = SettingsJson.ReadInt(values, "dataRetentionDays") ?? settings.DataRetentionDays;
        settings.Clamp();
        return settings;
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}小时{span.Minutes}分"
            : $"{span.Minutes}分{span.Seconds}秒";
    }
}
