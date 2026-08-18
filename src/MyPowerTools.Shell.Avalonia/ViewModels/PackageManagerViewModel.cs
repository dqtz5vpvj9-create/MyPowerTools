using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using MyPowerTools.Platform.Windows;
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
    private bool _isUpdateConsentVisible;
    private string _updateConsentTitle = "";
    private string _updateConsentIntro = "";
    private IReadOnlyList<OtaConsentItemViewModel> _updateConsentCloseItems = [];
    private string _updateConsentFootnote = "";
    private string _updateConsentConfirmText = "关闭并开始升级";
    private readonly Func<Action<OtaDownloadProgress>?, Task<string?>>? _applyUpdate;
    private readonly Func<OtaApplyConsent>? _createConsent;
    private readonly string? _otaStateRoot;
    private OtaApplyConsent? _pendingConsent;

    public PackageManagerViewModel(
        IReadOnlyList<PackageSummaryViewModel> packages,
        Func<string, Task>? installPackage = null,
        Func<string, Task>? rollbackPackage = null,
        Func<Task<string?>>? checkUpdate = null,
        Func<Action<OtaDownloadProgress>?, Task<string?>>? applyUpdate = null,
        string currentVersion = "-",
        string overlayVersion = "",
        Func<OtaApplyConsent>? createConsent = null,
        string? otaStateRoot = null)
        : base("Packages", $"{packages.Count} packages")
    {
        Packages = packages;
        CurrentVersion = currentVersion;
        OverlayVersion = overlayVersion;
        _applyUpdate = applyUpdate;
        _createConsent = createConsent;
        _otaStateRoot = otaStateRoot;
        if (!string.IsNullOrWhiteSpace(overlayVersion))
        {
            UpdateStatus = $"当前为开发覆盖（{overlayVersion}）。立即升级会换成 GitHub 发行版，不会自动加回本地 Debug 覆盖。";
        }
        InstallCommand = new AsyncRelayCommand(() => installPackage?.Invoke(InstallSourceDirectory) ?? Task.CompletedTask);
        RollbackCommand = new AsyncRelayCommand(() => rollbackPackage?.Invoke(RollbackPackageId) ?? Task.CompletedTask);
        CheckUpdateCommand = new AsyncRelayCommand(() => RunOtaCheckAsync(checkUpdate));
        ApplyUpdateCommand = new AsyncRelayCommand(RequestUpdateConsentAsync);
        ConfirmUpdateCommand = new AsyncRelayCommand(() => RunOtaApplyAsync(_applyUpdate));
        CancelUpdateConsentCommand = new AsyncRelayCommand(CancelUpdateConsentAsync);
    }

    public IReadOnlyList<PackageSummaryViewModel> Packages { get; }
    public bool IsEmpty => Packages.Count == 0;
    public ICommand InstallCommand { get; }
    public ICommand RollbackCommand { get; }
    public ICommand CheckUpdateCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public ICommand ConfirmUpdateCommand { get; }
    public ICommand CancelUpdateConsentCommand { get; }

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

    public string OverlayVersion { get; }

    public bool HasDevOverlay => !string.IsNullOrWhiteSpace(OverlayVersion);

    public string UpdateVersionText => HasDevOverlay
        ? $"安装底座 {CurrentVersion} · 开发覆盖 {OverlayVersion} · 最新版本 {LatestVersion}"
        : $"当前版本 {CurrentVersion} · 最新版本 {LatestVersion}";

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (SetProperty(ref _updateAvailable, value))
            {
                OnPropertyChanged(nameof(CanShowApplyButton));
            }
        }
    }

    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        private set
        {
            if (SetProperty(ref _isUpdateBusy, value))
            {
                OnPropertyChanged(nameof(CanShowApplyButton));
            }
        }
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

    public bool IsUpdateConsentVisible
    {
        get => _isUpdateConsentVisible;
        private set
        {
            if (SetProperty(ref _isUpdateConsentVisible, value))
            {
                OnPropertyChanged(nameof(CanShowApplyButton));
            }
        }
    }

    public bool CanShowApplyButton => UpdateAvailable && !IsUpdateConsentVisible && !IsUpdateBusy;

    public string UpdateConsentTitle
    {
        get => _updateConsentTitle;
        private set => SetProperty(ref _updateConsentTitle, value);
    }

    public string UpdateConsentIntro
    {
        get => _updateConsentIntro;
        private set => SetProperty(ref _updateConsentIntro, value);
    }

    public IReadOnlyList<OtaConsentItemViewModel> UpdateConsentCloseItems
    {
        get => _updateConsentCloseItems;
        private set
        {
            if (SetProperty(ref _updateConsentCloseItems, value))
            {
                OnPropertyChanged(nameof(HasUpdateConsentCloseItems));
            }
        }
    }

    public bool HasUpdateConsentCloseItems => UpdateConsentCloseItems.Count > 0;

    public string UpdateConsentFootnote
    {
        get => _updateConsentFootnote;
        private set
        {
            if (SetProperty(ref _updateConsentFootnote, value))
            {
                OnPropertyChanged(nameof(HasUpdateConsentFootnote));
            }
        }
    }

    public bool HasUpdateConsentFootnote => !string.IsNullOrWhiteSpace(UpdateConsentFootnote);

    public string UpdateConsentConfirmText
    {
        get => _updateConsentConfirmText;
        private set => SetProperty(ref _updateConsentConfirmText, value);
    }

    private Task RequestUpdateConsentAsync()
    {
        if (_applyUpdate is null || IsUpdateBusy)
        {
            return Task.CompletedTask;
        }

        var consent = _createConsent?.Invoke() ?? OtaApplyConsent.Create(HasDevOverlay, includeCurrentShell: true);
        _pendingConsent = consent;
        UpdateConsentTitle = consent.Title;
        UpdateConsentIntro = consent.Intro;
        UpdateConsentCloseItems = ToConsentItems(consent.Targets.Select(target => target.DisplayName));
        UpdateConsentFootnote = consent.Footnote;
        UpdateConsentConfirmText = consent.ConfirmButtonText;
        IsUpdateConsentVisible = true;
        UpdateStatus = consent.HasTargets
            ? "请确认将关闭的程序。同意后更新器会关闭它们，并在完成后重新打开。"
            : "没有需要关闭的程序，确认后开始更新。";
        return Task.CompletedTask;
    }

    private Task CancelUpdateConsentAsync()
    {
        if (IsUpdateBusy)
        {
            return Task.CompletedTask;
        }

        _pendingConsent = null;
        IsUpdateConsentVisible = false;
        UpdateStatus = UpdateAvailable
            ? $"发现新版本 {LatestVersion}。点击“立即升级”开始更新。"
            : "尚未检查更新。";
        return Task.CompletedTask;
    }

    private async Task RunOtaCheckAsync(Func<Task<string?>>? checkUpdate)
    {
        if (checkUpdate is null || IsUpdateBusy)
        {
            return;
        }

        IsUpdateConsentVisible = false;
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

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(output);
            }
            catch (JsonException)
            {
                UpdateStatus = $"检查更新失败：{CollapseOutput(output)}";
                return;
            }

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
                ? $"发现新版本 {latest}。点击“立即升级”后，更新器会列出需要关闭的程序。"
                : $"当前已是最新版本（{reason}）。";
            if (HasDevOverlay && available)
            {
                UpdateStatus += " 开发覆盖不会自动加回，升级后如需 Debug 请再运行开发版启动脚本。";
            }
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

        PersistReopenPlan();
        IsUpdateConsentVisible = false;
        IsUpdateBusy = true;
        UpdateProgressPercent = 0;
        UpdateProgressText = "准备下载…";
        IsUpdateProgressVisible = true;
        UpdateStatus = "正在下载并升级，占用文件的程序即将关闭并在完成后重新打开…";
        try
        {
            var output = await applyUpdate(OnOtaDownloadProgress);
            if (string.IsNullOrWhiteSpace(output))
            {
                UpdateStatus = "升级失败：更新器没有返回结果。";
                return;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(output);
            }
            catch (JsonException)
            {
                UpdateStatus = $"升级失败：{CollapseOutput(output)}";
                return;
            }

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

    private void PersistReopenPlan()
    {
        if (_pendingConsent is null)
        {
            return;
        }

        var stateRoot = _otaStateRoot;
        if (string.IsNullOrWhiteSpace(stateRoot))
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            stateRoot = OtaCloseTargetScanner.DefaultStateRoot();
        }

        OtaCloseTargetScanner.WriteReopenPlan(stateRoot, _pendingConsent.Targets);
    }

    private static IReadOnlyList<OtaConsentItemViewModel> ToConsentItems(IEnumerable<string> items)
    {
        return items.Select(static item => new OtaConsentItemViewModel(item)).ToArray();
    }
}
