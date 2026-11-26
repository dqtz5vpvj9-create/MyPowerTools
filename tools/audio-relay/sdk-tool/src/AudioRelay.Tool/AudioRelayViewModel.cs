using System.Text.Json.Nodes;
using MyPowerTools.AvaloniaSdk;

namespace AudioRelay.Tool;

public sealed class AudioRelayViewModel : MptObservableViewModel, IDisposable
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _isBusy;
    private bool _isInstalled;
    private bool _isRunning;
    private bool _isWindows;
    private string _statusText = "正在检查 AudioRelay…";
    private string _versionText = "—";
    private string _installPath = "—";
    private string _actionMessage = "";

    public AudioRelayViewModel(MptAvaloniaSurfaceContext context)
    {
        _context = context;
        RefreshCommand = Command(RefreshAsync, () => !IsBusy, "refresh");
        LaunchCommand = Command(LaunchAsync, () => !IsBusy && IsInstalled && IsWindows, "launch");
        DownloadsCommand = Command(() => ExecuteActionAsync("audio-relay.open-downloads", "已在浏览器中打开 AudioRelay 下载页。"), () => !IsBusy, "downloads");
        SendAudioGuideCommand = Command(() => ExecuteActionAsync("audio-relay.open-send-audio-guide", "已打开“电脑声音传到手机”官方向导。"), () => !IsBusy, "send-audio-guide");
        MicrophoneGuideCommand = Command(() => ExecuteActionAsync("audio-relay.open-microphone-guide", "已打开“手机作为电脑麦克风”官方向导。"), () => !IsBusy, "microphone-guide");
    }

    public MptAsyncRelayCommand RefreshCommand { get; }
    public MptAsyncRelayCommand LaunchCommand { get; }
    public MptAsyncRelayCommand DownloadsCommand { get; }
    public MptAsyncRelayCommand SendAudioGuideCommand { get; }
    public MptAsyncRelayCommand MicrophoneGuideCommand { get; }

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) NotifyCommandState(); } }
    public bool IsInstalled { get => _isInstalled; private set { if (SetProperty(ref _isInstalled, value)) NotifyCommandState(); } }
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public bool IsWindows { get => _isWindows; private set { if (SetProperty(ref _isWindows, value)) NotifyCommandState(); } }
    public bool ShowInstallAction => IsWindows && !IsInstalled;
    public bool ShowLaunchAction => IsWindows && IsInstalled;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string VersionText { get => _versionText; private set => SetProperty(ref _versionText, value); }
    public string InstallPath { get => _installPath; private set => SetProperty(ref _installPath, value); }
    public string ActionMessage { get => _actionMessage; private set => SetProperty(ref _actionMessage, value); }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var payload = await ExecutePayloadAsync("audio-relay.snapshot");
            IsWindows = string.Equals(payload["platform"]?.GetValue<string>(), "windows", StringComparison.OrdinalIgnoreCase);
            IsInstalled = payload["installed"]?.GetValue<bool>() == true;
            IsRunning = payload["running"]?.GetValue<bool>() == true;
            VersionText = payload["version"]?.GetValue<string>() ?? "—";
            InstallPath = payload["executablePath"]?.GetValue<string>() ?? "—";
            StatusText = !IsWindows
                ? "AudioRelay 桌面管理当前只在 Windows 开发版中可用。"
                : !IsInstalled
                    ? "未检测到 AudioRelay 桌面端。"
                    : IsRunning ? "AudioRelay 正在运行。" : "AudioRelay 已安装，当前未运行。";
            ActionMessage = IsInstalled
                ? "MyPowerTools 只负责检测和启动；音频源、手机连接与延迟参数仍由 AudioRelay 管理。"
                : "安装完成后回到这里点“刷新状态”，MyPowerTools 会自动识别官方安装。";
            _context.Log(new MptSurfaceLogEntry("info", $"AudioRelay status: installed={IsInstalled}; running={IsRunning}.", DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            StatusText = $"检查失败：{exception.Message}";
            ActionMessage = "AudioRelay Runtime 未能读取本机状态。";
            _context.Log(new MptSurfaceLogEntry("error", exception.Message, DateTimeOffset.UtcNow));
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ShowInstallAction));
            OnPropertyChanged(nameof(ShowLaunchAction));
        }
    }

    public void Dispose() => _lifetime.Cancel();

    private async Task LaunchAsync()
    {
        await ExecuteActionAsync("audio-relay.launch", "已启动 AudioRelay。请在 AudioRelay 中选择音频源和接收设备。");
        await Task.Delay(500, _lifetime.Token);
        await RefreshAsync();
    }

    private async Task ExecuteActionAsync(string commandId, string successMessage)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await ExecutePayloadAsync(commandId);
            ActionMessage = successMessage;
        }
        catch (Exception exception)
        {
            ActionMessage = $"操作失败：{exception.Message}";
            _context.Log(new MptSurfaceLogEntry("error", exception.Message, DateTimeOffset.UtcNow));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private MptAsyncRelayCommand Command(Func<Task> execute, Func<bool> canExecute, string name) =>
        new(execute, canExecute, $"audio-relay.{name}");

    private async Task<JsonObject> ExecutePayloadAsync(string commandId)
    {
        var command = await _context.ExecuteCommandAsync(commandId, null, _lifetime.Token);
        if (!command.Success)
        {
            throw new InvalidOperationException(command.Error?.Message ?? command.Output);
        }

        var response = JsonNode.Parse(command.Output)?.AsObject() ?? throw new InvalidDataException("Runtime 输出不是 JSON 对象。");
        var result = response["result"]?.AsObject() ?? throw new InvalidDataException("Runtime 输出缺少 result。");
        if (!string.Equals(result["state"]?.GetValue<string>(), "ready", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(result["error"]?["message"]?.GetValue<string>() ?? "Runtime 拒绝了该命令。");
        }

        return result["payload"] as JsonObject ?? [];
    }

    private void NotifyCommandState()
    {
        OnPropertyChanged(nameof(ShowInstallAction));
        OnPropertyChanged(nameof(ShowLaunchAction));
        RefreshCommand.NotifyCanExecuteChanged();
        LaunchCommand.NotifyCanExecuteChanged();
        DownloadsCommand.NotifyCanExecuteChanged();
        SendAudioGuideCommand.NotifyCanExecuteChanged();
        MicrophoneGuideCommand.NotifyCanExecuteChanged();
    }
}
