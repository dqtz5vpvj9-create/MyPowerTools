using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface;

// Layout half of the shared file-transfer surface. Every row stacks below phone width through
// MptAdaptiveLayout, and list rows use a star column so long file names wrap instead of pushing
// their buttons off screen.
//
// This is the wide layout, kept for Windows and macOS. It is built on the same TransferCore as the
// phone layout in TransferMobileView, so no transfer state exists twice.
public sealed partial class TransferView
{
    private const double NarrowWidth = 640;
    private const double TouchTarget = 48;

    private readonly TextBlock _status = Text("选择文件和设备，即可发送。", 15, "StatusLine");
    private readonly TextBlock _receiveStatus = Text("接收未开启", 14);
    private readonly TextBlock _saveSummary = Text("", 13, "SaveLocationSummary");
    private readonly TextBlock _deviceHint = Text("还没有接收设备：展开下方『连接新设备』导入连接码。", 13, "DeviceHint");
    private readonly TextBlock _dropHint = Text("把文件拖到这里（可多选）", 16, "FileSelectionHint");
    private readonly TextBlock _progressText = Text("", 13);
    private readonly TextBlock _cloudState = Text("网盘未配置。", 13);
    private readonly TextBlock _pairPreview = Text("", 13, "PairPreview");
    private readonly TextBlock _cloudPreview = Text("", 13, "CloudPreview");
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 6, IsVisible = false, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _devices = new() { PlaceholderText = "选择设备", HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 420, MinHeight = TouchTarget, Name = "DevicePicker" };
    private readonly ComboBox _method = new() { ItemsSource = new[] { "Tailscale 直传", "网盘中转" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, MaxWidth = 260, MinHeight = TouchTarget };
    private readonly StackPanel _fileList = new() { Spacing = 6 };
    private readonly StackPanel _inbox = new() { Spacing = 8 };
    private readonly StackPanel _history = new() { Spacing = 6 };
    private readonly TextBox _pair = new() { PlaceholderText = "粘贴另一台设备的连接码", MinHeight = TouchTarget, Name = "PairCodeBox" };
    private readonly TextBox _cloudCode = new() { PlaceholderText = "粘贴另一台设备的网盘连接码", MinHeight = TouchTarget, Name = "CloudCodeBox" };
    private readonly TextBox _directory = new() { MinHeight = TouchTarget };
    private readonly TextBox _deviceId = new() { MinHeight = TouchTarget };
    private readonly TextBox _listen = new() { MinHeight = TouchTarget };
    private readonly TextBox _webDav = new() { PlaceholderText = "网盘名/互传文件夹，或完整 https://…/dav/网盘名/互传文件夹", MinHeight = TouchTarget };
    private readonly TextBox _username = new() { PlaceholderText = "留空由本机 OpenList 自动创建专用账号", MinHeight = TouchTarget };
    private readonly TextBox _password = new() { PasswordChar = '●', PlaceholderText = "留空保留已保存密码", MinHeight = TouchTarget };
    private Button _receive = null!;
    private Button _send = null!;
    private Button _cancel = null!;
    private Button _retryLast = null!;
    private Button _openListStart = null!;
    private Button _openListAdmin = null!;
    private Button _openListPassword = null!;
    private Button _openListStop = null!;
    private Button _cloudSave = null!;
    private Expander _pairExpander = null!;
    private Expander _cloudExpander = null!;

    private void CreateButtons()
    {
        // The wide form's actions are cheap controls and every one of them is a state projection
        // target, so they exist from the start: the form's own tree is what stays lazy.
        if (_send is not null) return;
        _receive = Button("开启接收", ToggleReceiveAsync);
        _send = Button("发送", SendAsync);
        _cancel = Button("取消传输", CancelAsync);
        _retryLast = Button("重试上次", RetryLastAsync);
        _openListStart = Button("启用本机网盘服务", StartOpenListAsync);
        _openListAdmin = Button("打开网盘管理", OpenAdminAsync);
        _openListPassword = Button("复制管理员密码", CopyAdminPasswordAsync);
        _openListStop = Button("停止本机网盘服务", StopOpenListAsync);
        _cloudSave = Button("保存并测试", SaveCloudAsync);
    }

    private void BuildDesktopUi()
    {
        CreateButtons();
        // A code can arrive from an external app; the page must name the target before import.
        _pair.TextChanged += (_, _) => UpdateCodePreview(_pair, _pairPreview);
        _cloudCode.TextChanged += (_, _) => UpdateCodePreview(_cloudCode, _cloudPreview);
        _directory.TextChanged += (_, _) => UpdateSaveSummary();
        _devices.SelectionChanged += (_, _) => OnDeviceSelectionChanged();
        _method.SelectionChanged += (_, _) => OnMethodSelectionChanged();
        var content = new StackPanel { Spacing = 16, Margin = new Thickness(28), MaxWidth = 1000, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(Text("文件互传", 28));
        content.Children.Add(Text("电脑与手机，随手互传。", 15));
        content.Children.Add(_status);
        // Send flow first: on a phone the first screen must cover choose files, choose device, send.
        content.Children.Add(Section("发送"));
        content.Children.Add(DropZone());
        content.Children.Add(Row(_devices, _method));
        content.Children.Add(_deviceHint);
        content.Children.Add(Row(_send, _cancel, _retryLast));
        content.Children.Add(_progress);
        content.Children.Add(_progressText);
        content.Children.Add(Section("接收"));
        content.Children.Add(Row(_receive, _receiveStatus));
        if (OperatingSystem.IsAndroid())
            content.Children.Add(_saveSummary);
        else
            content.Children.Add(Row(Button("打开保存位置", OpenFolderAsync), _saveSummary));
        content.Children.Add(PairingSection());
        content.Children.Add(CloudSection());
        content.Children.Add(SettingsSection());
        content.Children.Add(Row(Section("网盘收件箱"), Button("刷新收件箱", RefreshInboxAsync)));
        content.Children.Add(_inbox);
        content.Children.Add(Section("最近传输"));
        content.Children.Add(_history);
        _desktopContent = content;
        _retryLast.IsVisible = false;
        _deviceHint.IsVisible = false;
    }

    private StackPanel? _desktopContent;

    private static TextBlock Text(string text, double size, string? name = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, LineHeight = size * 1.4, TextWrapping = TextWrapping.Wrap };
        if (name is not null) block.Name = name;
        return block;
    }

    private static TextBlock Section(string title) => Text(title, 20);

    private static StackPanel Field(string title, Control input) => new() { Spacing = 5, Children = { Text(title, 13), input } };

    private static StackPanel Row(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 }.WithChildren(children);
        MptAdaptiveLayout.SetStackBelow(panel, NarrowWidth);
        return panel;
    }

    /// <summary>Row whose leading text wraps inside the remaining width instead of pushing actions off screen.</summary>
    private static Grid ItemRow(Control content, params Control[] actions)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(content, 0);
        grid.Children.Add(content);
        var panel = Row(actions);
        panel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(panel, 1);
        grid.Children.Add(panel);
        return grid;
    }

    private Button Button(string title, Func<Task> action)
    {
        var button = new Button { Content = title, Padding = new Thickness(15, 9), MinHeight = TouchTarget };
        button.Click += async (_, _) => { button.IsEnabled = false; try { await GuardAsync(action); } finally { button.IsEnabled = true; } };
        return button;
    }

    private Control DropZone()
    {
        // The picker keeps the full row and 清空 stays a compact trailing action, so both buttons
        // remain on one line at 320 wide and the send flow fits the first screen.
        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        var pick = Button("选择文件（可多选）", PickAsync);
        Grid.SetColumn(pick, 0);
        actions.Children.Add(pick);
        var clear = Button("清空", ClearFilesAsync);
        Grid.SetColumn(clear, 1);
        actions.Children.Add(clear);
        var panel = new StackPanel { Spacing = 8, Children = { _dropHint, _fileList, actions } };
        var border = new Border
        {
            Padding = new Thickness(12), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            BorderBrush = SubtleBrush(), Child = panel
        };
        DragDrop.SetAllowDrop(border, true);
        border.AddHandler(DragDrop.DragOverEvent, (_, e) => { e.DragEffects = DragDropEffects.Copy; e.Handled = true; });
        border.AddHandler(DragDrop.DropEvent, (_, e) => { OnDrop(e); e.Handled = true; });
        return border;
    }

    private Expander PairingSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text("两台设备各复制一次连接码并互相导入，之后直接选择设备即可发送。连接码包含接收权限，请只分享给自己的设备。", 13));
        panel.Children.Add(Row(Button("复制本机连接码", CopyPairingAsync)));
        panel.Children.Add(Field("设备连接码", _pair));
        panel.Children.Add(_pairPreview);
        panel.Children.Add(Row(Button("粘贴", () => PasteIntoAsync(_pair)), Button("添加设备", ImportPairingAsync)));
        _pairExpander = new Expander { Header = "连接新设备", Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
        return _pairExpander;
    }

    private Expander CloudSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(Text(OperatingSystem.IsAndroid()
            ? "OpenList 已集成在 MPT 中，无需另装。启用后只在本机提供网盘管理，绑定网盘后再选择互传目录。"
            : "启用本机 OpenList 后，在管理页绑定网盘，再选择互传目录。", 13));
        panel.Children.Add(Row(_openListStart, _openListAdmin, _openListPassword, _openListStop));
        panel.Children.Add(Text("已有网盘配置会保留。仅在你保存新的互传目录时更新连接。", 13));
        panel.Children.Add(Field("互传目录（挂载目录）", _webDav));
        panel.Children.Add(Row(Button("粘贴", () => PasteIntoAsync(_webDav)), Button("打开网盘管理", OpenAdminAsync)));
        panel.Children.Add(Field("OpenList 用户名（留空自动创建）", _username));
        panel.Children.Add(Field("OpenList 密码（留空保留已保存密码）", _password));
        panel.Children.Add(Row(_cloudSave, _cloudState));
        panel.Children.Add(Text("其他设备只需导入一次网盘连接码，不用重复填写服务器和账号。连接码包含网盘收发权限，请只分享给自己的设备。", 13));
        panel.Children.Add(Field("网盘连接码", _cloudCode));
        panel.Children.Add(_cloudPreview);
        panel.Children.Add(Row(Button("粘贴", () => PasteIntoAsync(_cloudCode)), Button("导入连接码", ImportCloudAsync), Button("复制本机网盘连接码", ExportCloudAsync)));
        _cloudExpander = new Expander { Header = "网盘中转设置", Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
        return _cloudExpander;
    }

    private Expander SettingsSection()
    {
        var panel = new StackPanel { Spacing = 10 };
        // The receive path input lives here so the first screen stays focused on sending; the
        // receive area keeps only a compact "open location" action plus a one-line summary.
        panel.Children.Add(Field("收件 / 下载保存路径", _directory));
        panel.Children.Add(Row(Button("选择文件夹", PickDirectoryAsync)));
        panel.Children.Add(Field("本机收件箱名称", _deviceId));
        panel.Children.Add(Field("本机 Tailscale IP（自动填写）", _listen));
        panel.Children.Add(Row(Button("保存设置", SaveAsync), Button("重新读取", () => Core.RefreshAsync())));
        return new Expander { Header = "更多设置", Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private void ApplyDesktopDensity(double width)
    {
        if (_desktopContent is null) return;
        _desktopContent.Margin = new Thickness(width < NarrowWidth ? 16 : 28);
        _cloudState.MaxWidth = width < NarrowWidth ? double.PositiveInfinity : 420;
    }

    private IBrush SubtleBrush() =>
        this.TryFindResource("MptBrushBorder", out var value) && value is IBrush brush ? brush : Brushes.SlateGray;
}

internal static class LayoutExtensions
{
    public static StackPanel WithChildren(this StackPanel panel, IEnumerable<Control> children)
    { foreach (var child in children) panel.Children.Add(child); return panel; }
}
