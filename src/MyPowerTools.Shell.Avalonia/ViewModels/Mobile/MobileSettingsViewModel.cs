using System.Reflection;
using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services;
using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

/// <summary>设置 page: only preferences this build can actually read and write.</summary>
public sealed class MobileSettingsViewModel : ObservableViewModel
{
    private readonly MobileShellData _data;
    private readonly ShellAppearanceService _appearance;
    private string _errorMessage = "";

    public MobileSettingsViewModel(MobileShellData data, MobileShellServices services, IMobileNavigator navigator)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _appearance = services?.Appearance ?? throw new ArgumentNullException(nameof(services));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        ShowPairingCodeCommand = new AsyncRelayCommand(
            () => navigator.ShowSheetAsync(MobileSheetKeys.LocalPairingCode),
            operationName: "Show pairing code");
        RelayCommand = new AsyncRelayCommand(
            () => navigator.ShowSheetAsync(MobileSheetKeys.Relay),
            operationName: "Show relay settings");
        PermissionsCommand = new AsyncRelayCommand(
            () => navigator.ShowSheetAsync(MobileSheetKeys.Permissions),
            operationName: "Show permissions");
        AppearanceCommand = new AsyncRelayCommand(
            () => navigator.ShowSheetAsync(MobileSheetKeys.Appearance),
            operationName: "Show appearance");
        AboutCommand = new AsyncRelayCommand(
            () => navigator.ShowSheetAsync(MobileSheetKeys.About),
            operationName: "Show about");
        ThemeOptions =
        [
            new MobileThemeOptionViewModel("跟随系统", ShellAppearanceService.SystemTheme, SetThemeAsync),
            new MobileThemeOptionViewModel("浅色", ShellAppearanceService.LightTheme, SetThemeAsync),
            new MobileThemeOptionViewModel("深色", ShellAppearanceService.DarkTheme, SetThemeAsync)
        ];
    }

    public IMobileNavigator Navigator { get; }
    public ICommand ShowPairingCodeCommand { get; }
    public ICommand RelayCommand { get; }
    public ICommand PermissionsCommand { get; }
    public ICommand AppearanceCommand { get; }
    public ICommand AboutCommand { get; }
    public IReadOnlyList<MobileThemeOptionViewModel> ThemeOptions { get; }

    public string Title => "设置";
    public string Subtitle => "按你的习惯。";

    public string LocalName => _data.Snapshot is { LocalDeviceName.Length: > 0 } snapshot
        ? snapshot.LocalDeviceName
        : "本机";
    public string LocalDetail => _data.Snapshot is null
        ? "设备信息尚未接入"
        : "Android · 当前设备";

    public string AppearanceLabel => _appearance.CurrentTheme switch
    {
        ShellAppearanceService.LightTheme => "浅色",
        ShellAppearanceService.DarkTheme => "深色",
        _ => "跟随系统"
    };

    public string RelaySummary => _data.Snapshot is { RelayConfigured: true } snapshot
        ? snapshot.RelayRunning
            ? "已连接 · 正在运行"
            : snapshot.RelayChecked ? "已连接 · 未运行" : "已连接 · 尚未检查"
        : "还未设置";

    public string DeviceNotice => _data.Snapshot?.Notice ?? "";
    public bool HasDeviceNotice => DeviceNotice.Length > 0;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>
    /// The assembly's informational version when the build stamped one. The Android package version
    /// lives with the platform layer (M7), so the About sheet never prints a placeholder number.
    /// </summary>
    public string VersionText
    {
        get
        {
            var informational = typeof(MobileSettingsViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            var trimmed = informational?.Split('+')[0].Trim();
            return string.IsNullOrWhiteSpace(trimmed) || trimmed == "1.0.0"
                ? "MyPowerTools"
                : $"MyPowerTools {trimmed}";
        }
    }

    public async Task LoadAsync(bool force = false)
    {
        ErrorMessage = "";
        await _data.GetSnapshotAsync(force).ConfigureAwait(true);
        if (_data.DeviceError.Length > 0)
        {
            ErrorMessage = _data.DeviceError;
        }

        RaiseSnapshotProperties();
    }

    /// <summary>Persists through the shared appearance preference (same file the desktop uses).</summary>
    public async Task SetThemeAsync(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
        {
            return;
        }

        await _appearance.SetThemeAsync(theme).ConfigureAwait(true);
        foreach (var option in ThemeOptions)
        {
            option.IsSelected = string.Equals(option.Value, _appearance.CurrentTheme, StringComparison.OrdinalIgnoreCase);
        }

        OnPropertyChanged(nameof(AppearanceLabel));
        Navigator.ShowToast($"外观已切换为{AppearanceLabel}");
    }

    private void RaiseSnapshotProperties()
    {
        OnPropertyChanged(nameof(LocalName));
        OnPropertyChanged(nameof(LocalDetail));
        OnPropertyChanged(nameof(AppearanceLabel));
        OnPropertyChanged(nameof(RelaySummary));
        OnPropertyChanged(nameof(DeviceNotice));
        OnPropertyChanged(nameof(HasDeviceNotice));
        foreach (var option in ThemeOptions)
        {
            option.IsSelected = string.Equals(option.Value, _appearance.CurrentTheme, StringComparison.OrdinalIgnoreCase);
        }
    }
}

/// <summary>One appearance choice; selection state reflects the persisted preference.</summary>
public sealed class MobileThemeOptionViewModel : ObservableViewModel
{
    private bool _isSelected;

    public MobileThemeOptionViewModel(string label, string value, Func<string?, Task> select)
    {
        Label = label;
        Value = value;
        SelectCommand = new AsyncRelayCommand(() => select(value), operationName: $"Set theme {value}");
    }

    public string Label { get; }
    public string Value { get; }
    public ICommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}
