using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

public sealed record MobileStorageFileItemViewModel(
    string FilePath,
    string Name,
    string CategoryLabel,
    string SizeText,
    string MetaText,
    bool IsProtected,
    string ProtectedReason,
    ICommand DeleteCommand);

/// <summary>
/// ViewModel for the WeChat-style「存储空间」manager page: shows real app-private space breakdown,
/// device free space, external directory authorization status, safe one-tap cache cleanup, and
/// per-file management for locally cached transfer attachments.
/// </summary>
public sealed class MobileStorageViewModel : ObservableViewModel
{
    private readonly IMobileNavigator _navigator;
    private MobileStorageBreakdown? _breakdown;
    private bool _isLoading;
    private bool _isBusy;
    private string _statusMessage = "";

    public MobileStorageViewModel(IMobileNavigator navigator)
    {
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        RefreshCommand = new AsyncRelayCommand(() => LoadAsync(force: true), () => !IsBusy, operationName: "Refresh storage");
        PickExternalDirectoryCommand = new AsyncRelayCommand(PickExternalDirectoryAsync, () => !IsBusy, operationName: "Pick external directory");
        UseSystemDownloadsCommand = new AsyncRelayCommand(UseSystemDownloadsAsync, () => !IsBusy, operationName: "Use system downloads");
        ClearCacheCommand = new AsyncRelayCommand(ClearCacheAsync, () => !IsBusy && CanClearCache, operationName: "Clear storage cache");
        ClearTransferFilesCommand = new AsyncRelayCommand(ClearTransferFilesAsync, () => !IsBusy && CanClearTransferFiles, operationName: "Clear transfer file cache");
    }

    public ObservableCollection<MobileStorageFileItemViewModel> ManagedFiles { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand PickExternalDirectoryCommand { get; }
    public ICommand UseSystemDownloadsCommand { get; }
    public ICommand ClearCacheCommand { get; }
    public ICommand ClearTransferFilesCommand { get; }

    public MobileStorageBreakdown? Breakdown => _breakdown;
    public bool IsSupported => MobileStorageBridge.Current is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => StatusMessage.Length > 0;

    public string TotalAppSizeText => _breakdown is null ? (IsLoading ? "计算中…" : "0 B") : MobileByteFormatter.Format(_breakdown.TotalAppBytes);

    public string DeviceSpaceSummary
    {
        get
        {
            if (_breakdown is null)
            {
                return IsSupported ? "正在统计存储占用…" : "当前运行环境未接入手机存储管理器。";
            }

            if (_breakdown.DeviceTotalBytes <= 0)
            {
                return $"手机剩余可用 {MobileByteFormatter.Format(_breakdown.DeviceFreeBytes)}";
            }

            var pct = _breakdown.TotalAppBytes * 100.0 / _breakdown.DeviceTotalBytes;
            var pctText = pct < 0.1 && _breakdown.TotalAppBytes > 0 ? "<0.1%" : pct.ToString("0.0", CultureInfo.InvariantCulture) + "%";
            return $"占手机总空间 {pctText} · 剩余可用 {MobileByteFormatter.Format(_breakdown.DeviceFreeBytes)}";
        }
    }

    public string TransferSizeText => MobileByteFormatter.Format(_breakdown?.TransferFilesBytes ?? 0);
    public string CacheSizeText => MobileByteFormatter.Format(_breakdown?.CacheBytes ?? 0);
    public string LogsAndShadowSizeText => MobileByteFormatter.Format(_breakdown?.LogsAndShadowBytes ?? 0);
    public string TotalCleanableCacheText => MobileByteFormatter.Format(_breakdown?.TotalCleanableCacheBytes ?? 0);
    public string SystemDataSizeText => MobileByteFormatter.Format(_breakdown?.SystemDataBytes ?? 0);

    public string CacheDetailText => _breakdown is null
        ? "包含分享临时副本、传输分片、运行日志与旧模块缓存。"
        : $"含临时缓存 {MobileByteFormatter.Format(_breakdown.CacheBytes)}（{_breakdown.CacheFileCount} 个）与日志/旧模块缓存 {MobileByteFormatter.Format(_breakdown.LogsAndShadowBytes)}（{_breakdown.LogsAndShadowFileCount} 个）。清理不影响聊天记录和外部文件。";

    public string TransferDetailText => _breakdown is null
        ? "应用内保留的收发文件本地缓存（传输中文件自动保护）。"
        : _breakdown.TransferFilesCount == 0
            ? "当前没有堆积在应用私有目录的收发文件副本。"
            : $"共 {_breakdown.TransferFilesCount} 个本地副本，其中 {MobileByteFormatter.Format(_breakdown.CleanableTransferBytes)} 可安全释放；已保存到外部目录的文件与聊天记录不受影响。";

    public string ExternalDirectoryPath => _breakdown?.ExternalDirectory.DisplayPath
        ?? MobileStorageBridge.Current?.GetExternalDirectoryState().DisplayPath
        ?? "系统下载目录 / Download / MPT";

    public string ExternalDirectorySummary => _breakdown?.ExternalDirectory.StatusSummary
        ?? MobileStorageBridge.Current?.GetExternalDirectoryState().StatusSummary
        ?? "使用系统下载目录";

    public string ExternalDirectoryMeta
    {
        get
        {
            var ext = _breakdown?.ExternalDirectory;
            if (ext is null) return "";
            if (ext.NeedsReauthorization) return "授权已失效，新文件将暂回退到系统下载目录";
            return ext.ExternalFileCount == 0
                ? "外部目录内暂无已保存文件"
                : $"已保存 {ext.ExternalFileCount} 个文件 · 共 {MobileByteFormatter.Format(ext.ExternalBytes)}（外部文件管理器直接可管）";
        }
    }

    public bool ExternalDirectoryNeedsFix => _breakdown?.ExternalDirectory.NeedsReauthorization == true;
    public bool CanClearCache => (_breakdown?.TotalCleanableCacheBytes ?? 0) > 0;
    public bool CanClearTransferFiles => (_breakdown?.CleanableTransferBytes ?? 0) > 0;
    public bool HasManagedFiles => ManagedFiles.Count > 0;

    public event Action? BreakdownChanged;

    public async Task LoadAsync(bool force = false)
    {
        var manager = MobileStorageBridge.Current;
        if (manager is null)
        {
            RaiseAllProperties();
            return;
        }

        if (!force && _breakdown is not null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            _breakdown = await manager.InspectAsync().ConfigureAwait(true);
            RebuildManagedFiles();
            RaiseAllProperties();
            BreakdownChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = "统计存储空间失败：" + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PickExternalDirectoryAsync()
    {
        var manager = MobileStorageBridge.Current;
        if (manager is null) return;
        IsBusy = true;
        try
        {
            var result = await manager.PickExternalDirectoryAsync().ConfigureAwait(true);
            if (!result.Cancelled)
            {
                StatusMessage = result.Message;
                _navigator.ShowToast(result.Message);
            }

            await ReloadCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UseSystemDownloadsAsync()
    {
        var manager = MobileStorageBridge.Current;
        if (manager is null) return;
        IsBusy = true;
        try
        {
            manager.UseSystemDownloadsFallback();
            const string msg = "已切换为系统下载目录（Download/MPT）。";
            StatusMessage = msg;
            _navigator.ShowToast(msg);
            await ReloadCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearCacheAsync()
    {
        var manager = MobileStorageBridge.Current;
        if (manager is null) return;
        IsBusy = true;
        try
        {
            var result = await manager.ClearCacheAsync().ConfigureAwait(true);
            StatusMessage = result.Message;
            _navigator.ShowToast(result.Message);
            await ReloadCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ClearTransferFilesAsync()
    {
        var manager = MobileStorageBridge.Current;
        if (manager is null) return;
        IsBusy = true;
        try
        {
            var result = await manager.ClearManagedTransferFilesAsync().ConfigureAwait(true);
            StatusMessage = result.Message;
            _navigator.ShowToast(result.Message);
            await ReloadCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteSingleFileAsync(string filePath)
    {
        var manager = MobileStorageBridge.Current;
        if (manager is null) return;
        IsBusy = true;
        try
        {
            var result = await manager.DeleteFileAsync(filePath).ConfigureAwait(true);
            StatusMessage = result.Message;
            _navigator.ShowToast(result.Message);
            await ReloadCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadCoreAsync()
    {
        var manager = MobileStorageBridge.Current;
        if (manager is null) return;
        _breakdown = await manager.InspectAsync().ConfigureAwait(true);
        RebuildManagedFiles();
        RaiseAllProperties();
        BreakdownChanged?.Invoke();
    }

    private void RebuildManagedFiles()
    {
        ManagedFiles.Clear();
        if (_breakdown is null) return;
        foreach (var file in _breakdown.ManagedFiles)
        {
            var path = file.FilePath;
            var sizeText = MobileByteFormatter.Format(file.Bytes);
            var timeText = MobileHomeViewModel.FormatTimestamp(file.ModifiedAt);
            var meta = file.IsProtected
                ? $"{file.CategoryLabel} · {sizeText} · {file.ProtectedReason}"
                : $"{file.CategoryLabel} · {sizeText}" + (timeText.Length > 0 ? $" · {timeText}" : "");
            ManagedFiles.Add(new MobileStorageFileItemViewModel(
                FilePath: path,
                Name: file.Name,
                CategoryLabel: file.CategoryLabel,
                SizeText: sizeText,
                MetaText: meta,
                IsProtected: file.IsProtected,
                ProtectedReason: file.ProtectedReason,
                DeleteCommand: new AsyncRelayCommand(
                    () => DeleteSingleFileAsync(path),
                    () => !IsBusy && !file.IsProtected,
                    operationName: $"Delete {file.Name}")));
        }
    }

    private void RaiseAllProperties()
    {
        OnPropertyChanged(nameof(TotalAppSizeText));
        OnPropertyChanged(nameof(DeviceSpaceSummary));
        OnPropertyChanged(nameof(TransferSizeText));
        OnPropertyChanged(nameof(CacheSizeText));
        OnPropertyChanged(nameof(LogsAndShadowSizeText));
        OnPropertyChanged(nameof(TotalCleanableCacheText));
        OnPropertyChanged(nameof(SystemDataSizeText));
        OnPropertyChanged(nameof(CacheDetailText));
        OnPropertyChanged(nameof(TransferDetailText));
        OnPropertyChanged(nameof(ExternalDirectoryPath));
        OnPropertyChanged(nameof(ExternalDirectorySummary));
        OnPropertyChanged(nameof(ExternalDirectoryMeta));
        OnPropertyChanged(nameof(ExternalDirectoryNeedsFix));
        OnPropertyChanged(nameof(CanClearCache));
        OnPropertyChanged(nameof(CanClearTransferFiles));
        OnPropertyChanged(nameof(HasManagedFiles));
        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        (RefreshCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (PickExternalDirectoryCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (UseSystemDownloadsCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (ClearCacheCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (ClearTransferFilesCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        foreach (var item in ManagedFiles)
        {
            (item.DeleteCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
    }
}
