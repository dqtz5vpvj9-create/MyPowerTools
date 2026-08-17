using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed class PackageManagerViewModel : ShellPageViewModel
{
    private string _installSourceDirectory = "";
    private string _rollbackPackageId = "";
    private string _currentVersion = "-";
    private string _latestVersion = "-";
    private string _updateStatus = "尚未检查更新。";
    private bool _updateAvailable;
    private bool _isUpdateBusy;
    private double _updateProgressPercent;
    private string _updateProgressText = "";
    private bool _isUpdateProgressVisible;

    public PackageManagerViewModel(
        IReadOnlyList<PackageSummaryViewModel> packages,
        Func<string, Task>? installPackage = null,
        Func<string, Task>? rollbackPackage = null,
        Func<Task<string?>>? checkUpdate = null,
        Func<Action<OtaDownloadProgress>?, Task<string?>>? applyUpdate = null,
        string currentVersion = "-")
        : base("Packages", $"{packages.Count} packages")
    {
        Packages = packages;
        CurrentVersion = currentVersion;
        InstallCommand = new AsyncRelayCommand(() => installPackage?.Invoke(InstallSourceDirectory) ?? Task.CompletedTask);
        RollbackCommand = new AsyncRelayCommand(() => rollbackPackage?.Invoke(RollbackPackageId) ?? Task.CompletedTask);
        CheckUpdateCommand = new AsyncRelayCommand(() => RunOtaCheckAsync(checkUpdate));
        ApplyUpdateCommand = new AsyncRelayCommand(() => RunOtaApplyAsync(applyUpdate));
    }

    public IReadOnlyList<PackageSummaryViewModel> Packages { get; }
    public bool IsEmpty => Packages.Count == 0;
    public ICommand InstallCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }

    public string InstallSourceDirectory
    {
        get => _installSourceDirectory;
        set => SetProperty(ref _installSourceDirectory, value);
    }

    public string RollbackPackageId
    {
        get => _rollbackPackageId;
        set => SetProperty(ref _rollbackPackageId, value);
    }

    public string CurrentVersion
    {
        get => _currentVersion;
        private set
        {
            if (SetProperty(ref _currentVersion, value))
            {
                OnPropertyChanged(nameof(UpdateVersionText));
            }
        }
    }

    public string LatestVersion
    {
        get => _latestVersion;
        private set
        {
            if (SetProperty(ref _latestVersion, value))
            {
                OnPropertyChanged(nameof(UpdateVersionText));
            }
        }
    }

    public string UpdateVersionText => $"当前版本 {CurrentVersion} · 最新版本 {LatestVersion}";

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => SetProperty(ref _updateAvailable, value);
    }

    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        private set => SetProperty(ref _isUpdateBusy, value);
    }

    public double UpdateProgressPercent
    {
        get => _updateProgressPercent;
        private set => SetProperty(ref _updateProgressPercent, value);
    }

    public string UpdateProgressText
    {
        get => _updateProgressText;
        private set => SetProperty(ref _updateProgressText, value);
    }

    public bool IsUpdateProgressVisible
    {
        get => _isUpdateProgressVisible;
        private set => SetProperty(ref _isUpdateProgressVisible, value);
    }

    private async Task RunOtaCheckAsync(Func<Task<string?>>? checkUpdate)
    {
        if (checkUpdate is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        UpdateStatus = "正在检查更新…";
        try
        {
            var output = await checkUpdate();
            if (string.IsNullOrWhiteSpace(output))
            {
                UpdateStatus = "检查更新失败：更新器没有返回结果。";
                return;
            }

            var node = JsonNode.Parse(output);
            if (node is null)
            {
                UpdateStatus = $"检查更新失败：{CollapseOutput(output)}";
                return;
            }

            var error = node["error"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(error))
            {
                UpdateStatus = $"检查更新失败：{error}";
                return;
            }

            var current = node["currentVersion"]?.GetValue<string>() ?? CurrentVersion;
            var latest = node["latestVersion"]?.GetValue<string>() ?? "-";
            var available = node["available"]?.GetValue<bool>() ?? false;
            var reason = node["reason"]?.GetValue<string>() ?? "";
            CurrentVersion = current;
            LatestVersion = latest;
            UpdateAvailable = available;
            UpdateStatus = available
                ? $"发现新版本 {latest}。点击“立即升级”开始更新（更新期间界面会关闭并自动重启）。"
                : $"当前已是最新版本（{reason}）。";
        }
        catch (Exception ex)
        {
            UpdateStatus = $"检查更新失败：{ex.Message}";
        }
        finally
        {
            IsUpdateBusy = false;
        }
    }

    private async Task RunOtaApplyAsync(Func<Action<OtaDownloadProgress>?, Task<string?>>? applyUpdate)
    {
        if (applyUpdate is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateBusy = true;
        UpdateProgressPercent = 0;
        UpdateProgressText = "准备下载…";
        IsUpdateProgressVisible = true;
        UpdateStatus = "正在下载并升级，界面即将关闭并自动重启…";
        try
        {
            var output = await applyUpdate(OnOtaDownloadProgress);
            if (string.IsNullOrWhiteSpace(output))
            {
                UpdateStatus = "升级失败：更新器没有返回结果。";
                return;
            }

            var node = JsonNode.Parse(output);
            if (node is null)
            {
                UpdateStatus = $"升级失败：{CollapseOutput(output)}";
                return;
            }

            var error = node["error"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(error))
            {
                UpdateStatus = $"升级失败：{error}";
                return;
            }

            var success = node["success"]?.GetValue<bool>() ?? false;
            var toVersion = node["toVersion"]?.GetValue<string>();
            var healthOk = node["health"]?["ok"]?.GetValue<bool>() ?? false;
            if (success && !string.IsNullOrWhiteSpace(toVersion))
            {
                CurrentVersion = toVersion;
                LatestVersion = toVersion;
                UpdateAvailable = false;
                UpdateStatus = healthOk
                    ? $"已升级到 {toVersion}，健康检查通过。"
                    : $"已升级到 {toVersion}，但健康检查未完全通过，请查看 OTA 日志。";
            }
            else
            {
                UpdateStatus = "升级失败，请查看 %LOCALAPPDATA%\\MyPowerTools\\ota-state\\last-update.json 与日志。";
            }
        }
        catch (Exception ex)
        {
            UpdateStatus = $"升级失败：{ex.Message}";
        }
        finally
        {
            IsUpdateProgressVisible = false;
            IsUpdateBusy = false;
        }
    }

    private void OnOtaDownloadProgress(OtaDownloadProgress progress)
    {
        UpdateProgressPercent = progress.PercentValue;
        UpdateProgressText = progress.Text;
        UpdateStatus = $"正在下载 {progress.File}：{progress.Text}…";
    }

    private static string CollapseOutput(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length <= 400)
        {
            return trimmed.Replace("\r", " ").Replace("\n", " ");
        }

        return trimmed[..400].Replace("\r", " ").Replace("\n", " ") + "…";
    }
}
