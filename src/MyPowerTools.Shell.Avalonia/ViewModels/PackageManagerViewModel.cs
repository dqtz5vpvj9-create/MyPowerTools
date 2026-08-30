using System.Windows.Input;
using MyPowerTools.Platform.Windows;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Shell.Avalonia.ViewModels;

public sealed partial class PackageManagerViewModel : ShellPageViewModel
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

}
