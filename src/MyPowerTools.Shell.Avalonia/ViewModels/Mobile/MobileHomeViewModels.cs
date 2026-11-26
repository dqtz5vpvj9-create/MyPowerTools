using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using MyPowerTools.Shell.Avalonia.Services.Mobile;

namespace MyPowerTools.Shell.Avalonia.ViewModels.Mobile;

/// <summary>One of the four一级入口.</summary>
public sealed record MobileTabViewModel(string PageKey, string Label, string IconGlyph, ICommand NavigateCommand)
{
    public string AutomationName => Label;
}

/// <summary>A device chip on 常用; tapping opens the real device detail page.</summary>
public sealed record MobileDeviceChipViewModel(
    string DeviceId,
    string Name,
    string IconGlyph,
    string StateLabel,
    bool IsUnknown,
    bool IsOffline,
    ICommand OpenCommand);

/// <summary>A 最近 row: a real transfer activity or a really recorded tool open.</summary>
public sealed record MobileRecentItemViewModel(
    string IconGlyph,
    string Title,
    string Detail,
    string Meta,
    ICommand? OpenCommand);

/// <summary>
/// 常用 tab. Everything on it is either loaded from the live catalog/device snapshot or absent:
/// there is no example device, no example transfer and no example unread count.
/// </summary>
public sealed class MobileHomeViewModel : ObservableViewModel
{
    private readonly MobileShellData _data;
    private readonly MobileToolsViewModel _tools;
    private readonly IMobileNavigator _navigator;
    private string _greeting = "";
    private string _avatarText = "M";
    private bool _isLoading;
    private string _errorMessage = "";

    public MobileHomeViewModel(MobileShellData data, MobileToolsViewModel tools, IMobileNavigator navigator)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        PairDeviceCommand = new AsyncRelayCommand(() => _navigator.ShowSheetAsync(MobileSheetKeys.PairDevice), operationName: "Pair a device");
        BrowseToolsCommand = new AsyncRelayCommand(() => _navigator.ShowRootPageAsync(MobilePageKeys.Tools), operationName: "Browse tools");
        OpenActivityCommand = new AsyncRelayCommand(() => _navigator.ShowRootPageAsync(MobilePageKeys.Activity), operationName: "Open activity");
        RefreshCommand = new AsyncRelayCommand(LoadAsync, operationName: "Refresh home");
        SendFileCommand = new AsyncRelayCommand(
            () => _navigator.OpenToolSurfaceAsync("file-transfer"),
            () => HasHero,
            operationName: "Send a file");
        OpenSettingsCommand = new AsyncRelayCommand(
            () => _navigator.ShowPageAsync(MobilePageKeys.Settings),
            operationName: "Open settings");
    }

    public ObservableCollection<MobileDeviceChipViewModel> Devices { get; } = [];
    public ObservableCollection<MobileToolItemViewModel> FavoriteTools { get; } = [];
    public ObservableCollection<MobileRecentItemViewModel> Recent { get; } = [];

    public ICommand PairDeviceCommand { get; }
    public ICommand BrowseToolsCommand { get; }
    public ICommand OpenActivityCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SendFileCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public string Greeting
    {
        get => _greeting;
        private set => SetProperty(ref _greeting, value);
    }

    public string AvatarText
    {
        get => _avatarText;
        private set => SetProperty(ref _avatarText, value);
    }

    public string Headline => "你的随身工具箱。";
    public string HeroTitle => "文件传输助手";
    public string HeroDetail => "打开文件助手会话；从系统分享进来的内容会进入待发送区。";

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

    /// <summary>The real 文件互传 entry; the hero action opens its actual surface.</summary>
    public MobileToolItemViewModel? Hero => _tools.Find("file-transfer");
    public bool HasHero => Hero is not null;
    public string HeroStatus => Hero?.StatusLabel ?? "未注册";

    public bool HasFavorites => FavoriteTools.Count > 0;
    public bool ShowsFavoritesEmpty => !HasFavorites;
    public bool HasDevices => Devices.Count > 0;
    public bool HasRecent => Recent.Count > 0;
    public bool ShowsRecentEmpty => !HasRecent;
    public string DeviceNotice => _data.Snapshot?.Notice ?? _data.DeviceError;
    public bool HasDeviceNotice => DeviceNotice.Length > 0;
    public string FavoritesEmptyDetail => "打开工具库，点工具行上的星标就会出现在这里。";

    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = "";
        try
        {
            Greeting = BuildGreeting(DateTime.Now);
            await _tools.LoadAsync().ConfigureAwait(true);
            var snapshot = await _data.GetSnapshotAsync(force: false).ConfigureAwait(true);

            RebuildDevices(snapshot);
            RebuildFavorites();
            RebuildRecent(snapshot);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(Hero));
            OnPropertyChanged(nameof(HasHero));
            OnPropertyChanged(nameof(HeroStatus));
            OnPropertyChanged(nameof(HasFavorites));
            OnPropertyChanged(nameof(ShowsFavoritesEmpty));
            OnPropertyChanged(nameof(HasDevices));
            OnPropertyChanged(nameof(HasRecent));
            OnPropertyChanged(nameof(ShowsRecentEmpty));
            OnPropertyChanged(nameof(DeviceNotice));
            OnPropertyChanged(nameof(HasDeviceNotice));
            if (SendFileCommand is AsyncRelayCommand send)
            {
                send.NotifyCanExecuteChanged();
            }
        }
    }

    private void RebuildDevices(MobileDeviceSnapshot? snapshot)
    {
        Devices.Clear();
        if (snapshot is not null)
        {
            AvatarText = Initial(snapshot.LocalDeviceName);
            foreach (var peer in snapshot.Peers.Take(3))
            {
                var state = PeerState(peer);
                Devices.Add(new MobileDeviceChipViewModel(
                    peer.DeviceId,
                    peer.Name,
                    "\u25A3",
                    state.Label,
                    state.IsUnknown,
                    state.IsOffline,
                    new AsyncRelayCommand(() => _navigator.ShowDeviceDetailAsync(peer.DeviceId), operationName: $"Open {peer.Name}")));
            }
        }
    }

    private void RebuildFavorites()
    {
        FavoriteTools.Clear();
        foreach (var entry in _data.Favorites)
        {
            if (string.Equals(entry.ToolId, "file-transfer", StringComparison.OrdinalIgnoreCase))
            {
                // The hero card already is 文件互传.
                continue;
            }

            if (_tools.Find(entry.ToolId) is { } item)
            {
                FavoriteTools.Add(item);
            }
        }
    }

    private void RebuildRecent(MobileDeviceSnapshot? snapshot)
    {
        Recent.Clear();
        if (snapshot is not null)
        {
            foreach (var activity in snapshot.Activities.Take(2))
            {
                Recent.Add(new MobileRecentItemViewModel(
                    activity.Direction.Contains("receive", StringComparison.OrdinalIgnoreCase) ? "\u2193" : "\u2191",
                    string.IsNullOrWhiteSpace(activity.Name) ? "文件" : activity.Name,
                    TransferDetail(activity),
                    FormatTimestamp(activity.Timestamp),
                    null));
            }
        }

        foreach (var entry in _data.RecentTools(2))
        {
            if (Recent.Count >= 3)
            {
                break;
            }

            if (_tools.Find(entry.ToolId) is { } item)
            {
                Recent.Add(new MobileRecentItemViewModel("\u203A", item.Title, $"最近打开 · {item.PlatformLabel}", "", item.ShowDetailCommand));
            }
        }
    }

    private static string TransferDetail(MobileTransferActivity activity)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(activity.PeerName))
        {
            parts.Add(activity.PeerName!);
        }

        if (!string.IsNullOrWhiteSpace(activity.State))
        {
            parts.Add(MobileStatusText.TransferState(activity.State));
        }

        if (!string.IsNullOrWhiteSpace(activity.Message))
        {
            parts.Add(activity.Message!);
        }

        return parts.Count == 0 ? "传输记录" : string.Join(" · ", parts);
    }

    public static string FormatTimestamp(DateTimeOffset? timestamp)
    {
        if (timestamp is not { } value)
        {
            return "";
        }

        var local = value.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    public static (string Label, bool IsUnknown, bool IsOffline) PeerState(MobilePeerInfo peer) => peer.ConnectionState switch
    {
        MobilePeerConnectionState.Online => ("在线", false, false),
        MobilePeerConnectionState.Offline => ("离线", false, true),
        _ => ("尚未检查", true, false)
    };

    private static string Initial(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) ? "M" : char.ToUpperInvariant(trimmed[0]).ToString();
    }

    private static string BuildGreeting(DateTime now) => now.Hour switch
    {
        < 6 => "夜深了",
        < 12 => "早上好",
        < 14 => "中午好",
        < 18 => "下午好",
        _ => "晚上好"
    };
}
