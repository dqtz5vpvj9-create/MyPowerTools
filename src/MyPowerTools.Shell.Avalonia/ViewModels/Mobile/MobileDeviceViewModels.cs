using System.Collections.ObjectModel;
using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

/// <summary>One paired device row/chip; state is exactly what the last real check reported.</summary>
public sealed class MobilePeerItemViewModel : ObservableViewModel
{
    private MobilePeerInfo _peer;

    public MobilePeerItemViewModel(MobilePeerInfo peer, MobileDevicesViewModel owner)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        OpenCommand = new AsyncRelayCommand(
            () => owner.Navigator.ShowDeviceDetailAsync(DeviceId),
            operationName: $"Open {peer.Name}");
        CheckCommand = new AsyncRelayCommand(
            () => owner.CheckPeerAsync(this),
            operationName: $"Check {peer.Name}");
        ShowActionsCommand = new AsyncRelayCommand(
            () => owner.Navigator.ShowSheetAsync(MobileSheetKeys.PeerActions, DeviceId),
            operationName: $"Show {peer.Name} actions");
    }

    public MobileDevicesViewModel Owner { get; }
    public string DeviceId => _peer.DeviceId;
    public string Name => string.IsNullOrWhiteSpace(_peer.Name) ? _peer.DeviceId : _peer.Name;
    public string Address => _peer.Address;
    public string IconGlyph => "\u25A3";

    public MobilePeerInfo Peer => _peer;

    public void Update(MobilePeerInfo peer)
    {
        _peer = peer;
        OnPropertyChanged(nameof(Peer));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(StateDetail));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsUnknown));
        OnPropertyChanged(nameof(IsOffline));
        OnPropertyChanged(nameof(SupportsToolControl));
        OnPropertyChanged(nameof(CapabilityLabel));
    }

    public bool IsOnline => _peer.ConnectionState == MobilePeerConnectionState.Online;
    public bool IsUnknown => _peer.ConnectionState == MobilePeerConnectionState.Unknown;
    public bool IsOffline => _peer.ConnectionState == MobilePeerConnectionState.Offline;
    public bool SupportsToolControl => _peer.SupportsToolControl;

    public string StateLabel => _peer.ConnectionState switch
    {
        MobilePeerConnectionState.Online => "在线",
        MobilePeerConnectionState.Offline => "离线",
        _ => "已配对 · 尚未检查"
    };

    public string StateDetail => _peer.Message is { Length: > 0 } message
        ? message
        : _peer.ConnectionState switch
        {
            MobilePeerConnectionState.Online => _peer.CheckedAt is { } online
                ? $"最近应答 · {MobileHomeViewModel.FormatTimestamp(online)}"
                : "已收到应答",
            MobilePeerConnectionState.Offline => _peer.CheckedAt is { } offline
                ? $"最近检查未应答 · {MobileHomeViewModel.FormatTimestamp(offline)}"
                : "最近检查未收到应答",
            _ => "只有设备地址时不会显示在线；点“检查连接”向设备发起一次真实请求。"
        };

    public string CapabilityLabel => _peer.SupportsToolControl ? "允许电脑工具控制" : "仅文件互传与通知";

    public ICommand OpenCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand ShowActionsCommand { get; }
}

/// <summary>One imported computer row on the phone; actions go through the control surface.</summary>
public sealed class MobileControlDeviceItemViewModel : ObservableViewModel
{
    public MobileControlDeviceItemViewModel(MobileControlDevice device, MobileDevicesViewModel owner)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        OpenCommand = new AsyncRelayCommand(() => owner.OpenControlDeviceAsync(device), operationName: $"Open {device.Name}");
    }

    public MobileControlDevice Device { get; }
    public MobileDevicesViewModel Owner { get; }
    public string DeviceId => Device.DeviceId;
    public string Name => Device.Name.Length > 0 ? Device.Name : "已导入的电脑";
    public string Subtitle => Device.Subtitle;
    public string StateLabel => Device.StateLabel;
    public bool IsReachable => Device.IsReachable;
    public bool NeverChecked => Device.NeverChecked;
    public string IconGlyph => "\u25A3";
    public ICommand OpenCommand { get; }
}

/// <summary>
/// 设备 tab. Peers, receiving state, relay state and transfer history all come from
/// <see cref="IMobileDeviceService"/>; an unreachable service produces an empty state, not examples.
/// </summary>
public sealed class MobileDevicesViewModel : ObservableViewModel
{
    private readonly MobileShellData _data;
    private readonly IMobileDeviceService _devices;
    private readonly IMobileControlDeviceService _controlDevices;
    private bool _isLoading;
    private string _errorMessage = "";
    private string _controlError = "";

    public MobileDevicesViewModel(
        MobileShellData data,
        IMobileDeviceService devices,
        IMobileControlDeviceService controlDevices,
        IMobileNavigator navigator)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _controlDevices = controlDevices ?? throw new ArgumentNullException(nameof(controlDevices));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync(force: true), operationName: "Refresh devices");
        PairCommand = new AsyncRelayCommand(() => navigator.ShowSheetAsync(MobileSheetKeys.PairDevice), operationName: "Pair a device");
        ShowPairingCodeCommand = new AsyncRelayCommand(() => navigator.ShowSheetAsync(MobileSheetKeys.LocalPairingCode), operationName: "Show pairing code");
        RelayCommand = new AsyncRelayCommand(() => navigator.ShowSheetAsync(MobileSheetKeys.Relay), operationName: "Show relay settings");
        OpenPermissionsCommand = new AsyncRelayCommand(() => navigator.ShowSheetAsync(MobileSheetKeys.Permissions), operationName: "Show device permissions");
        ConnectComputerCommand = new AsyncRelayCommand(OpenControlToolAsync, operationName: "Connect a computer");
        ReloadControlDevicesCommand = new AsyncRelayCommand(() => LoadControlDevicesAsync(force: true), operationName: "Refresh computers");
    }

    public IMobileNavigator Navigator { get; }
    public ObservableCollection<MobilePeerItemViewModel> Peers { get; } = [];

    /// <summary>Imported computers for phone-side tool control; the id is the desktop grant id.</summary>
    public ObservableCollection<MobileControlDeviceItemViewModel> ControlDevices { get; } = [];
    public ICommand ReloadCommand { get; }
    public ICommand PairCommand { get; }
    public ICommand ShowPairingCodeCommand { get; }
    public ICommand RelayCommand { get; }
    public ICommand OpenPermissionsCommand { get; }
    public ICommand ConnectComputerCommand { get; }
    public ICommand ReloadControlDevicesCommand { get; }

    public string Title => "你的设备";
    public string Subtitle => "各有所长，彼此相连。";

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

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

    public string LocalName => _data.Snapshot is { LocalDeviceName.Length: > 0 } snapshot
        ? snapshot.LocalDeviceName
        : "本机";
    public string LocalDetail => (_data.Snapshot?.Receiving ?? false)
        ? "正在接收文件"
        : "接收状态未知或未开启";
    public bool Receiving => _data.Snapshot?.Receiving ?? false;

    public bool RelayConfigured => _data.Snapshot?.RelayConfigured ?? false;
    public bool RelayRunning => _data.Snapshot?.RelayRunning ?? false;
    public string RelaySummary => RelayConfigured
        ? (RelayRunning ? "中转服务运行中" : RelayChecked ? "已配置 · 未运行" : "已配置 · 尚未检查")
        : "连接一个网盘";
    public string RelayDetail => _data.Snapshot?.RelayDescription
        ?? (RelayConfigured ? "设备离线时也能转交" : "给不在线的设备，留一份文件");

    /// <summary>True when the relay state is a measured result rather than "never checked".</summary>
    public bool RelayChecked => _data.Snapshot?.RelayChecked ?? false;

    public string Notice => _data.Snapshot?.Notice ?? "";
    public bool HasNotice => Notice.Length > 0;

    public bool HasControlDevices => ControlDevices.Count > 0;
    public string ControlError
    {
        get => _controlError;
        private set
        {
            if (SetProperty(ref _controlError, value))
            {
                OnPropertyChanged(nameof(HasControlError));
            }
        }
    }

    public bool HasControlError => ControlError.Length > 0;
    public bool ShowsControlEmpty => !HasControlDevices;
    public string ControlEmptyDetail => HasControlError
        ? ControlError
        : "连接一台电脑后，可在这里使用它的工具；电脑需要先在桌面端启用远程工具访问。";

    public bool HasPeers => Peers.Count > 0;
    public bool IsEmpty => !IsLoading && !HasError && !HasPeers;
    public string EmptyTitle => "还没有连接设备";
    public string EmptyDetail => "在文件助手里用连接码或扫码连接你的设备；发送前对方离线也能用。";

    public async Task LoadAsync(bool force = false)
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            await _data.GetSnapshotAsync(force).ConfigureAwait(true);
            if (_data.DeviceError.Length > 0)
            {
                ErrorMessage = _data.DeviceError;
            }

            RebuildPeers();
        }
        finally
        {
            IsLoading = false;
            RaiseSnapshotProperties();
        }
    }

    /// <summary>
    /// Reads the imported computers from the real control module. A module that is missing or not
    /// authorized produces an honest message plus the connect action, never an empty success.
    /// </summary>
    public async Task LoadControlDevicesAsync(bool force = false)
    {
        if (!force && ControlDevices.Count > 0)
        {
            return;
        }

        var snapshot = await _controlDevices.GetDevicesAsync().ConfigureAwait(true);
        ControlError = snapshot.Error;
        ControlDevices.Clear();
        foreach (var device in snapshot.Devices)
        {
            ControlDevices.Add(new MobileControlDeviceItemViewModel(device, this));
        }

        OnPropertyChanged(nameof(HasControlDevices));
        OnPropertyChanged(nameof(ShowsControlEmpty));
        OnPropertyChanged(nameof(ControlEmptyDetail));
    }

    /// <summary>Opens the control surface, which owns the import/permission flow for a computer.</summary>
    public Task OpenControlToolAsync() => Navigator.ActivateToolAsync(
        MobileControlDeviceService.ToolId,
        MobileControlDeviceService.FallbackRouteId,
        MobileControlDeviceService.BuildActivationUri(""));

    public Task OpenControlDeviceAsync(MobileControlDevice device, string? toolId = null) => Navigator.ActivateToolAsync(
        MobileControlDeviceService.ToolId,
        MobileControlDeviceService.FallbackRouteId,
        MobileControlDeviceService.BuildActivationUri(device.DeviceId, toolId));

    public MobilePeerItemViewModel? FindPeer(string deviceId) => Peers.FirstOrDefault(peer =>
        string.Equals(peer.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Explicit, user-triggered reachability check; there is no background polling.</summary>
    public async Task CheckPeerAsync(MobilePeerItemViewModel peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            var updated = await _devices.CheckPeerAsync(peer.DeviceId).ConfigureAwait(true);
            _data.InvalidateSnapshot();
            await LoadAsync(force: true).ConfigureAwait(true);
            Navigator.ShowToast(updated.ConnectionState switch
            {
                MobilePeerConnectionState.Online => $"{peer.Name} 已应答",
                MobilePeerConnectionState.Offline => $"{peer.Name} 没有应答",
                _ => $"{peer.Name} 状态未确认"
            });
        }
        catch (Exception ex)
        {
            Navigator.ShowToast(ex.Message);
        }
    }

    public async Task RemovePeerAsync(MobilePeerItemViewModel peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        try
        {
            await _devices.RemovePeerAsync(peer.DeviceId).ConfigureAwait(true);
            _data.InvalidateSnapshot();
            await LoadAsync(force: true).ConfigureAwait(true);
            await Navigator.CloseSheetAsync().ConfigureAwait(true);
            Navigator.ShowToast($"{peer.Name} 已解除连接");
        }
        catch (Exception ex)
        {
            Navigator.ShowToast(ex.Message);
        }
    }

    /// <summary>Imports the existing MPT connection code. Throws with the real reason on failure.</summary>
    public async Task ImportPairingAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("请输入电脑端显示的连接码。", nameof(code));
        }

        await _devices.ImportPairingAsync(code.Trim()).ConfigureAwait(true);
        _data.InvalidateSnapshot();
        await LoadAsync(force: true).ConfigureAwait(true);
        await Navigator.CloseSheetAsync().ConfigureAwait(true);
        Navigator.ShowToast("连接码已发送，请在电脑上确认这次配对");
    }

    /// <summary>The code this phone shows to the other device; the real secret-store value.</summary>
    public Task<string> GetPairingCodeAsync() => _devices.GetPairingCodeAsync();

    private void RebuildPeers()
    {
        var existing = Peers.ToDictionary(peer => peer.DeviceId, StringComparer.OrdinalIgnoreCase);
        Peers.Clear();
        if (_data.Snapshot is not { } snapshot)
        {
            return;
        }

        foreach (var peer in snapshot.Peers)
        {
            if (existing.TryGetValue(peer.DeviceId, out var item))
            {
                item.Update(peer);
                Peers.Add(item);
            }
            else
            {
                Peers.Add(new MobilePeerItemViewModel(peer, this));
            }
        }
    }

    private void RaiseSnapshotProperties()
    {
        OnPropertyChanged(nameof(HasPeers));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LocalName));
        OnPropertyChanged(nameof(LocalDetail));
        OnPropertyChanged(nameof(Receiving));
        OnPropertyChanged(nameof(RelayConfigured));
        OnPropertyChanged(nameof(RelayRunning));
        OnPropertyChanged(nameof(RelayChecked));
        OnPropertyChanged(nameof(RelaySummary));
        OnPropertyChanged(nameof(RelayDetail));
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    }
}

/// <summary>设备详情 page: the real peer record plus the actions the device service really offers.</summary>
public sealed class MobileDeviceDetailViewModel : ObservableViewModel
{
    private readonly MobilePeerItemViewModel _peer;

    public MobileDeviceDetailViewModel(MobilePeerItemViewModel peer, IMobileNavigator navigator)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        Navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        SendFileCommand = new AsyncRelayCommand(
            () => navigator.OpenToolSurfaceAsync("file-transfer"),
            operationName: "Open file transfer");
        CheckCommand = peer.CheckCommand;
        ShowActionsCommand = peer.ShowActionsCommand;
        _peer.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MobilePeerItemViewModel.StateLabel)
                or nameof(MobilePeerItemViewModel.StateDetail)
                or nameof(MobilePeerItemViewModel.IsOnline)
                or nameof(MobilePeerItemViewModel.IsUnknown)
                or nameof(MobilePeerItemViewModel.IsOffline)
                or nameof(MobilePeerItemViewModel.CapabilityLabel))
            {
                OnPropertyChanged(args.PropertyName);
            }
        };
    }

    public IMobileNavigator Navigator { get; }
    public MobilePeerItemViewModel Peer => _peer;
    public string DeviceId => _peer.DeviceId;
    public string Name => _peer.Name;
    public string Address => _peer.Address;
    public string StateLabel => _peer.StateLabel;
    public string StateDetail => _peer.StateDetail;
    public string CapabilityLabel => _peer.CapabilityLabel;
    public bool IsOnline => _peer.IsOnline;
    public bool IsUnknown => _peer.IsUnknown;
    public bool IsOffline => _peer.IsOffline;
    public bool SupportsToolControl => _peer.SupportsToolControl;
    public ICommand SendFileCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand ShowActionsCommand { get; }
}

/// <summary>A 动态 row: a real transfer event or a real recorded tool open.</summary>
public sealed record MobileActivityItemViewModel(
    string IconGlyph,
    string Title,
    string Detail,
    string Meta,
    bool IsRelay,
    ICommand? OpenCommand);

/// <summary>动态 tab: transfer history and tool opens the runtime actually recorded.</summary>
public sealed class MobileActivityViewModel : ObservableViewModel
{
    private readonly MobileShellData _data;
    private readonly MobileToolsViewModel _tools;
    private readonly IMobileNavigator _navigator;
    private bool _isLoading;
    private string _errorMessage = "";
    private string _notice = "";

    public MobileActivityViewModel(MobileShellData data, MobileToolsViewModel tools, IMobileNavigator navigator)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync(force: true), operationName: "Refresh activity");
    }

    public ObservableCollection<MobileActivityItemViewModel> Items { get; } = [];
    public ICommand ReloadCommand { get; }

    public string Title => "动态";
    public string Subtitle => "每件小事，都有着落。";

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

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

    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
            }
        }
    }

    public bool HasError => ErrorMessage.Length > 0;
    public bool HasNotice => Notice.Length > 0;
    public bool IsEmpty => !IsLoading && !HasError && Items.Count == 0;

    public async Task LoadAsync(bool force = false)
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            await _tools.LoadAsync(force).ConfigureAwait(true);
            var snapshot = await _data.GetSnapshotAsync(force).ConfigureAwait(true);
            Notice = snapshot?.Notice ?? "";
            if (_data.DeviceError.Length > 0)
            {
                ErrorMessage = _data.DeviceError;
            }

            Items.Clear();
            if (snapshot is not null)
            {
                foreach (var activity in snapshot.Activities)
                {
                    var relay = activity.State.Contains("relay", StringComparison.OrdinalIgnoreCase)
                        || activity.State.Contains("上传", StringComparison.Ordinal)
                        || activity.State.Contains("等待领取", StringComparison.Ordinal);
                    Items.Add(new MobileActivityItemViewModel(
                        relay ? "\u2601" : "\u21C4",
                        string.IsNullOrWhiteSpace(activity.Name) ? "文件" : activity.Name,
                        BuildTransferDetail(activity),
                        MobileHomeViewModel.FormatTimestamp(activity.Timestamp),
                        relay,
                        null));
                }
            }

            foreach (var entry in _data.RecentTools(5))
            {
                if (_tools.Find(entry.ToolId) is { } item)
                {
                    Items.Add(new MobileActivityItemViewModel(
                        "\u203A",
                        item.Title,
                        $"最近打开 · {item.PlatformLabel}",
                        "",
                        false,
                        item.ShowDetailCommand));
                }
            }
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNotice));
        }
    }

    private static string BuildTransferDetail(MobileTransferActivity activity)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(activity.Direction))
        {
            parts.Add(activity.Direction);
        }

        if (!string.IsNullOrWhiteSpace(activity.PeerName))
        {
            parts.Add(activity.PeerName!);
        }

        if (!string.IsNullOrWhiteSpace(activity.State))
        {
            parts.Add(activity.State);
        }

        if (!string.IsNullOrWhiteSpace(activity.Message))
        {
            parts.Add(activity.Message!);
        }

        return parts.Count == 0 ? "传输记录" : string.Join(" · ", parts);
    }
}
