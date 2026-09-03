using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace PasteImage.Surface.ViewModels;

public sealed class PasteImageViewModel : ToolSurfacePageViewModel, IDisposable
{
    private static readonly IBrush Accent = Brush.Parse("#0F6CBD");
    private static readonly IBrush AccentSoft = Brush.Parse("#EFF6FC");
    private static readonly IBrush Success = Brush.Parse("#107C10");
    private static readonly IBrush SuccessSoft = Brush.Parse("#F1FAF1");
    private static readonly IBrush Warning = Brush.Parse("#9D5D00");
    private static readonly IBrush Error = Brush.Parse("#C42B1C");
    private static readonly IBrush ErrorSoft = Brush.Parse("#FDF3F2");

    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable? _eventSubscription;
    private readonly MptAsyncRelayCommand _uploadCommand;
    private readonly MptAsyncRelayCommand _refreshCommand;
    private string _destination = "正在读取…";
    private string _health = "正在检查 Windows OpenSSH…";
    private string _connectionStatus = "检查中";
    private IBrush _statusBrush = Warning;
    private string _messageIcon = "i";
    private string _message = "复制图片后按 Ctrl+Alt+V，或点击上传按钮。";
    private IBrush _messageForeground = Accent;
    private IBrush _messageBackground = AccentSoft;
    private Bitmap? _preview;
    private string _previewMeta = "";
    private bool _isBusy;
    private int _disposed;

    public PasteImageViewModel(MptAvaloniaSurfaceContext context)
        : base("Paste Image", "上传剪贴板图片并将远端路径写回剪贴板", ToolSurfaceState.Loading)
    {
        _context = context;
        _uploadCommand = new MptAsyncRelayCommand(UploadAsync, () => CanUpload, "PasteImageUpload");
        _refreshCommand = new MptAsyncRelayCommand(RefreshAllAsync, () => !IsBusy, "PasteImageRefresh");
        UploadCommand = _uploadCommand;
        RefreshCommand = _refreshCommand;
        _eventSubscription = context.SubscribeEvents?.Invoke(OnSurfaceEvent);
    }

    public string Destination { get => _destination; private set => SetProperty(ref _destination, value); }
    public string Health { get => _health; private set => SetProperty(ref _health, value); }
    public string ConnectionStatus { get => _connectionStatus; private set => SetProperty(ref _connectionStatus, value); }
    public IBrush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }
    public string MessageIcon { get => _messageIcon; private set => SetProperty(ref _messageIcon, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public IBrush MessageForeground { get => _messageForeground; private set => SetProperty(ref _messageForeground, value); }
    public IBrush MessageBackground { get => _messageBackground; private set => SetProperty(ref _messageBackground, value); }
    public Bitmap? Preview { get => _preview; private set => SetProperty(ref _preview, value); }
    public string PreviewMeta { get => _previewMeta; private set => SetProperty(ref _previewMeta, value); }
    public ObservableCollection<UploadHistoryRow> History { get; } = [];

    /// <summary>Set by the view once it is attached; writes text to the system clipboard.</summary>
    public Func<string, Task>? ClipboardWriter { get; set; }
    public ICommand UploadCommand { get; }
    public ICommand RefreshCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanUpload));
            _uploadCommand.NotifyCanExecuteChanged();
            _refreshCommand.NotifyCanExecuteChanged();
        }
    }

    public bool CanUpload => !IsBusy && ProductState != ToolSurfaceState.Failed;
    public bool HasPreview => Preview is not null;
    public bool IsPreviewEmpty => Preview is null;
    public bool HasHistory => History.Count > 0;
    public bool IsHistoryEmpty => History.Count == 0;

    public async Task InitializeAsync()
    {
        await RefreshAllAsync().ConfigureAwait(false);
    }

    private async Task RefreshAllAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var statusTask = LoadStatusAsync(_lifetime.Token);
        var historyTask = LoadHistoryAsync(_lifetime.Token);
        await Task.WhenAll(statusTask, historyTask).ConfigureAwait(false);
    }

    private async Task LoadStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteAsync("paste-image.inspect", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!response.Success) throw new InvalidOperationException(response.Error?.Message ?? "读取状态失败。");
            using var document = ParseJsonPayload(response.Output);
            var root = document.RootElement;
            var ready = string.Equals(root.GetProperty("State").GetString(), "running", StringComparison.OrdinalIgnoreCase);
            var destination = $"{root.GetProperty("remoteHost").GetString()}:{root.GetProperty("remoteDirectory").GetString()}";
            var health = root.GetProperty("Summary").GetString() ?? "状态未知";
            await RunOnUiAsync(() =>
            {
                Destination = destination;
                Health = health;
                StatusBrush = ready ? Success : Warning;
                ConnectionStatus = ready ? "已就绪" : "需要检查";
                SetProductState(ready ? ToolSurfaceState.Ready : ToolSurfaceState.Empty);
                OnPropertyChanged(nameof(CanUpload));
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() =>
            {
                Destination = "无法读取目标";
                Health = exception.Message;
                StatusBrush = Error;
                ConnectionStatus = "连接失败";
                SetProductState(ToolSurfaceState.Failed, exception.Message);
                OnPropertyChanged(nameof(CanUpload));
            }).ConfigureAwait(false);
        }
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await ExecuteAsync("paste-image.history", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            if (!response.Success) throw new InvalidOperationException(response.Error?.Message ?? "读取上传记录失败。");
            using var document = ParseJsonPayload(response.Output);
            var rows = document.RootElement.GetProperty("items")
                .EnumerateArray()
                .Take(5)
                .Select(item => UploadHistoryRow.FromJson(item).WithCopySupport(CopyPathToClipboardAsync))
                .ToArray();
            var preview = rows.Length == 0 ? null : await LoadBitmapAsync(rows[0].LocalPreviewPath, cancellationToken).ConfigureAwait(false);
            await RunOnUiAsync(() => ReplaceHistory(rows, preview)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _context.Log(new MptSurfaceLogEntry("error", $"读取 Paste Image 历史失败：{exception.Message}", DateTimeOffset.Now));
        }
    }

    private async Task UploadAsync()
    {
        if (IsBusy || Volatile.Read(ref _disposed) != 0) return;
        IsBusy = true;
        SetMessage("↑", "正在读取剪贴板并上传…", Accent, AccentSoft);
        try
        {
            var response = await ExecuteAsync("paste-image.upload", TimeSpan.FromSeconds(65), _lifetime.Token).ConfigureAwait(false);
            await RunOnUiAsync(() =>
            {
                if (response.Success)
                {
                    SetMessage("✓", $"上传成功，路径已复制：{NormalizeOutput(response.Output)}", Success, SuccessSoft);
                }
                else
                {
                    SetMessage("!", FriendlyError(response.Error?.Message ?? response.Output), Error, ErrorSoft);
                }
            }).ConfigureAwait(false);

            if (response.Success)
            {
                // Reload even when the live event subscription is active: the event
                // stream is best-effort (it can lag or miss events across Runner
                // restarts), and InsertHistory deduplicates by remote path when the
                // upload.alert event arrives as well.
                await LoadHistoryAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() => SetMessage("!", FriendlyError(exception.Message), Error, ErrorSoft)).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    private void OnSurfaceEvent(MptSurfaceEvent surfaceEvent)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _ = ApplySurfaceEventAsync(surfaceEvent, DateTimeOffset.UtcNow);
    }

    private async Task ApplySurfaceEventAsync(MptSurfaceEvent surfaceEvent, DateTimeOffset surfaceReceivedUtc)
    {
        try
        {
            if (string.Equals(surfaceEvent.Type, "upload.alert", StringComparison.OrdinalIgnoreCase))
            {
                var row = UploadHistoryRow.FromEvent(surfaceEvent.Payload).WithCopySupport(CopyPathToClipboardAsync);
                var previewDecodeStartedUtc = DateTimeOffset.UtcNow;
                var preview = await LoadBitmapAsync(row.LocalPreviewPath, _lifetime.Token).ConfigureAwait(false);
                var previewDecodedUtc = DateTimeOffset.UtcNow;
                var timing = surfaceEvent.Payload["totalMilliseconds"]?.GetValue<double>() ?? 0;
                var uiAppliedUtc = DateTimeOffset.MinValue;
                await RunOnUiAsync(() =>
                {
                    InsertHistory(row, preview);
                    var timingText = timing > 0 ? $"（{timing:F0} ms）" : "";
                    SetMessage("✓", $"上传成功{timingText}，路径已复制：{row.RemotePath}", Success, SuccessSoft);
                    uiAppliedUtc = DateTimeOffset.UtcNow;
                }).ConfigureAwait(false);
                await PersistProfileAsync(
                    surfaceEvent,
                    surfaceReceivedUtc,
                    previewDecodeStartedUtc,
                    previewDecodedUtc,
                    uiAppliedUtc,
                    _lifetime.Token).ConfigureAwait(false);
                return;
            }

            if (string.Equals(surfaceEvent.Type, "upload.failed", StringComparison.OrdinalIgnoreCase))
            {
                var message = surfaceEvent.Payload["message"]?.GetValue<string>() ?? "上传失败。";
                await RunOnUiAsync(() => SetMessage("!", FriendlyError(message), Error, ErrorSoft)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _context.Log(new MptSurfaceLogEntry("error", $"处理 Paste Image 实时事件失败：{exception.Message}", DateTimeOffset.Now));
        }
    }

    private async Task<CommandExecutionResult> ExecuteAsync(string commandId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return await _context.ExecuteCommandAsync(commandId, new JsonObject(), deadline.Token).ConfigureAwait(false);
    }

    private void ReplaceHistory(IReadOnlyList<UploadHistoryRow> rows, Bitmap? preview)
    {
        History.Clear();
        foreach (var row in rows) History.Add(row);
        SetPreview(preview, rows.FirstOrDefault());
        RaiseHistoryState();
    }

    private void InsertHistory(UploadHistoryRow row, Bitmap? preview)
    {
        var existing = History.FirstOrDefault(item => string.Equals(item.RemotePath, row.RemotePath, StringComparison.Ordinal));
        if (existing is not null) History.Remove(existing);
        History.Insert(0, row);
        while (History.Count > 5) History.RemoveAt(History.Count - 1);
        SetPreview(preview, row);
        RaiseHistoryState();
    }

    private void SetPreview(Bitmap? preview, UploadHistoryRow? row)
    {
        var previous = Preview;
        Preview = preview;
        PreviewMeta = row is null ? "" : $"{row.Width} × {row.Height}  ·  {FormatBytes(row.SizeBytes)}\n{row.RemotePath}";
        OnPropertyChanged(nameof(HasPreview));
        OnPropertyChanged(nameof(IsPreviewEmpty));
        if (!ReferenceEquals(previous, preview)) previous?.Dispose();
    }

    private void RaiseHistoryState()
    {
        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(IsHistoryEmpty));
    }

    private void SetMessage(string icon, string message, IBrush foreground, IBrush background)
    {
        MessageIcon = icon;
        Message = message;
        MessageForeground = foreground;
        MessageBackground = background;
    }

    private static async Task<Bitmap?> LoadBitmapAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new Bitmap(stream);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PersistProfileAsync(
        MptSurfaceEvent surfaceEvent,
        DateTimeOffset surfaceReceivedUtc,
        DateTimeOffset previewDecodeStartedUtc,
        DateTimeOffset previewDecodedUtc,
        DateTimeOffset uiAppliedUtc,
        CancellationToken cancellationToken)
    {
        var profile = (surfaceEvent.Payload.DeepClone() as JsonObject) ?? new JsonObject();
        profile["hostEventSequence"] = surfaceEvent.Sequence;
        profile["hostEventTimeUtc"] = surfaceEvent.Time.ToString("O");
        profile["surfaceEventReceivedUtc"] = surfaceReceivedUtc.ToString("O");
        profile["previewDecodeStartedUtc"] = previewDecodeStartedUtc.ToString("O");
        profile["previewDecodedUtc"] = previewDecodedUtc.ToString("O");
        profile["uiAppliedUtc"] = uiAppliedUtc.ToString("O");

        var triggerPath = Path.Combine(_context.DataDirectory, "profile-trigger.json");
        if (File.Exists(triggerPath))
        {
            try
            {
                if (JsonNode.Parse(await File.ReadAllTextAsync(triggerPath, cancellationToken).ConfigureAwait(false)) is JsonObject trigger)
                {
                    foreach (var pair in trigger) profile[pair.Key] = pair.Value?.DeepClone();
                }
            }
            catch (JsonException)
            {
            }
        }

        profile["profileTraceWrittenUtc"] = DateTimeOffset.UtcNow.ToString("O");
        Directory.CreateDirectory(_context.DataDirectory);
        var profilePath = Path.Combine(_context.DataDirectory, "latest-upload-profile.json");
        var temporaryPath = profilePath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, profilePath, overwrite: true);
        _context.Log(new MptSurfaceLogEntry(
            "info",
            $"Paste Image profile {profile["profileId"]?.GetValue<string>() ?? ""} written.",
            DateTimeOffset.Now,
            profile));
    }

    private static async Task RunOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(action);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _eventSubscription?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        Preview?.Dispose();
        Preview = null;
    }

    private static JsonDocument ParseJsonPayload(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start) throw new JsonException("命令返回了无法识别的数据。");
        return JsonDocument.Parse(output[start..(end + 1)]);
    }

    private async Task CopyPathToClipboardAsync(string path)
    {
        var writer = ClipboardWriter;
        if (writer is null)
        {
            await RunOnUiAsync(() => SetMessage("!", "剪贴板不可用，无法复制路径。", Error, ErrorSoft)).ConfigureAwait(false);
            return;
        }

        try
        {
            await writer(path).ConfigureAwait(false);
            await RunOnUiAsync(() => SetMessage("✓", $"路径已复制：{path}", Success, SuccessSoft)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() => SetMessage("!", $"复制失败：{exception.Message}", Error, ErrorSoft)).ConfigureAwait(false);
        }
    }

    private static string NormalizeOutput(string output)
    {
        const string prefix = "succeeded:";
        var value = output.Trim();
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..].Trim() : value;
    }

    private static string FriendlyError(string message)
    {
        var value = NormalizeOutput(message);
        if (value.StartsWith("failed:", StringComparison.OrdinalIgnoreCase)) value = value["failed:".Length..].Trim();
        if (value.Contains("No image is available in the clipboard", StringComparison.OrdinalIgnoreCase))
            return "剪贴板中没有可上传的图片。请先复制一张图片。";
        if (value.Contains("already running", StringComparison.OrdinalIgnoreCase))
            return "已有一次上传正在进行。";
        return value;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024d:F1} MB",
        >= 1024 => $"{bytes / 1024d:F0} KB",
        _ => $"{bytes} B"
    };
}

public sealed record UploadHistoryRow(
    string RemotePath,
    string LocalPreviewPath,
    DateTimeOffset UploadedAt,
    int Width,
    int Height,
    long SizeBytes)
{
    public string UploadedAtText => UploadedAt.ToLocalTime().ToString("MM-dd HH:mm:ss");

    public ICommand CopyCommand { get; init; } = new MptAsyncRelayCommand(
        () => Task.CompletedTask, operationName: "CopyHistoryPath");

    public UploadHistoryRow WithCopySupport(Func<string, Task> copyAction)
    {
        var path = RemotePath;
        return this with
        {
            CopyCommand = new MptAsyncRelayCommand(
                () => copyAction(path),
                operationName: "CopyHistoryPath")
        };
    }

    public static UploadHistoryRow FromJson(JsonElement item) => new(
        item.GetProperty("RemotePath").GetString() ?? "",
        item.GetProperty("LocalPreviewPath").GetString() ?? "",
        item.GetProperty("UploadedAt").GetDateTimeOffset(),
        item.GetProperty("Width").GetInt32(),
        item.GetProperty("Height").GetInt32(),
        item.GetProperty("SizeBytes").GetInt64());

    public static UploadHistoryRow FromEvent(JsonObject payload) => new(
        payload["remotePath"]?.GetValue<string>() ?? "",
        payload["localPreviewPath"]?.GetValue<string>() ?? "",
        DateTimeOffset.Parse(payload["uploadedAt"]?.GetValue<string>() ?? DateTimeOffset.UtcNow.ToString("O")),
        payload["width"]?.GetValue<int>() ?? 0,
        payload["height"]?.GetValue<int>() ?? 0,
        payload["sizeBytes"]?.GetValue<long>() ?? 0);
}
