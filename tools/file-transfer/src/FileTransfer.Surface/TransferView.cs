using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface;

/// <summary>
/// The single file-transfer surface shared by Windows, macOS and Android. It owns layout and view
/// state only: sending, receiving, pairing and OpenList work all go through file-transfer module
/// commands, so the same page keeps working on a phone where no desktop service can run.
///
/// The surface has two presentations over one <see cref="TransferCore"/>: the wide form
/// (Windows / macOS / tablet) and the phone step flow in <see cref="TransferMobileView"/>. Only one
/// of them is attached at a time, so neither keeps a second copy of the transfer state.
/// </summary>
public sealed partial class TransferView : UserControl, IMptAvaloniaSurfaceActivationHandler, IMptAvaloniaSurfaceBackHandler
{
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly TransferCore _core;
    private readonly AssistantCore _assistantCore;
    private readonly AssistantView _assistant;
    private readonly TransferMobileView _mobile;
    private readonly ScrollViewer _desktopScroller;
    private bool _mobileShown;
    private bool _activated;
    private bool _advancedShown;

    // Presentation-only mirrors of module state owned by the core. They exist so the wide form's
    // cloud/OpenList wording does not have to re-derive it on every re-render.
    private bool _receiving;
    private bool _openListRunning;
    private string _savedDirectory = "";
    private string _adminUrl = "";
    private string _localAuthority = "";

    public TransferView(MptAvaloniaSurfaceContext context)
    {
        // The conversation pins its composer and scrolls its own history. Ask desktop hosts for a
        // finite viewport; the advanced presentations also provide their own scroll viewers.
        Classes.Add("MptViewportSurface");
        _context = context;
        _core = new TransferCore(context);
        _assistantCore = new AssistantCore(context);
        BuildDesktopUi();
        _desktopScroller = new ScrollViewer { Content = _desktopContent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _mobile = new TransferMobileView(context, _core);
        // The conversation is the tool's default screen on every platform. The classic form stays
        // reachable from it as an advanced page rather than greeting the user with a settings table.
        _assistant = new AssistantView(context, _assistantCore, _core);
        _assistant.AdvancedRequested += (_, _) => ShowAdvanced(true);
        Content = _assistant;
        SizeChanged += (_, e) => ApplyDensity(e.NewSize.Width);
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    /// <summary>The shared transfer state, exposed for the phone page and for headless layout tests.</summary>
    internal TransferCore Core => _core;

    /// <summary>The conversation state, exposed for headless behaviour tests.</summary>
    internal AssistantCore Assistant => _assistantCore;

    /// <summary>The conversation page, exposed for headless behaviour tests.</summary>
    internal AssistantView Conversation => _assistant;

    /// <summary>True while the conversation is the visible screen (the default).</summary>
    public bool IsConversationVisible => !_advancedShown;

    /// <summary>Switches between the conversation and the classic transfer form.</summary>
    public void ShowAdvanced(bool advanced)
    {
        if (advanced == _advancedShown) return;
        _advancedShown = advanced;
        ApplyPresentation(Bounds.Width > 0 ? Bounds.Width : _lastWidth);
    }

    /// <summary>The phone presentation. It is only attached below phone width.</summary>
    internal TransferMobileView Mobile => _mobile;

    /// <summary>True while the phone layout owns the surface. Used by tests to assert the breakpoint.</summary>
    public bool IsMobileLayout => _mobileShown;

    private void Attach()
    {
        if (_activated) { _mobile.Attach(); _assistant.Attach(); return; }
        _activated = true;
        _core.Changed += SyncFromCore;
        _core.Attach();
        // The conversation reads both cores: the assistant contract for the session, and the legacy
        // commands for receive settings and the advanced form it links to.
        _assistantCore.Attach();
        _mobile.Attach();
        _assistant.Attach();
        _ = GuardAsync(async () =>
        {
            await _core.RefreshAsync();
            await _assistantCore.RefreshAsync();
            // Entering the page is a real event, so this is where the session is pulled once. There
            // is no polling: further updates arrive on file-transfer.assistant.changed.
            await _assistantCore.SyncAsync();
        });
    }

    private void Detach()
    {
        if (!_activated) return;
        _activated = false;
        _core.Changed -= SyncFromCore;
        _desktopContent = _desktopScroller.Content as StackPanel;
        _core.Detach();
        _assistantCore.Detach();
        _mobile.Detach();
        _assistant.Detach();
    }

    /// <summary>Selects the phone presentation for phone widths and the wide form everywhere else.</summary>
    private void ApplyDensity(double width)
    {
        if (width > 0) _lastWidth = width;
        ApplyPresentation(_lastWidth);
    }

    private double _lastWidth;

    /// <summary>Shows either the conversation or the advanced form for the current width.</summary>
    private void ApplyPresentation(double width)
    {
        var mobile = width > 0 && width < NarrowWidth;
        ApplyDesktopDensity(width);
        _mobile.ApplyViewport(width);
        _assistant.ApplyViewport(width);
        if (!_advancedShown)
        {
            // The conversation is one layout at both widths: a phone column at 320 and a wider, still
            // single-column thread on a desktop window.
            _mobileShown = false;
            Content = _assistant;
            return;
        }
        _mobileShown = mobile;
        Content = mobile ? _mobile : _desktopScroller;
        if (mobile) _mobile.Sync();
    }

    // ---- commands ---------------------------------------------------------------------------

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { SetStatus(ex.Message); }
    }

    private void SetStatus(string message) => _core.PublishOnUi(_core.Snapshot with { Status = message });

    /// <summary>Names the target of a pending import so a code from an external app is never applied blind.</summary>
    private void UpdateCodePreview(TextBox box, TextBlock preview)
    {
        var description = ConnectionCodePreview.Describe(box.Text);
        preview.Text = description ?? "";
        preview.IsVisible = description is not null;
    }

    /// <summary>One short line for the first screen: the full path stays editable in 更多设置.</summary>
    private void UpdateSaveSummary() => _saveSummary.Text = OperatingSystem.IsAndroid()
        ? "保存到系统下载 / MPT"
        : "保存到 " + TransferCore.ShortPath(_directory.Text);

    // ---- state ------------------------------------------------------------------------------

    /// <summary>Projects the shared snapshot onto the wide form. The phone page reads the same snapshot.</summary>
    private void SyncFromCore()
    {
        var state = _core.Snapshot;
        _status.Text = state.Status;
        _receiving = state.Receiving;
        _openListRunning = state.OpenListRunning;
        _savedDirectory = state.RelayDescription;
        SyncDevices(state);
        UpdateReceive();
        UpdateCloudState();
        UpdateSendButtons();
        if (state.Busy)
        {
            _progress.IsVisible = true;
            _progress.Value = state.Progress;
            _progressText.Text = state.ProgressText;
        }
        else
        {
            _progress.IsVisible = false;
            _progressText.Text = "";
        }
        RefreshFileList(state);
        RefreshHistory(state);
    }

    private bool _syncingDevices;

    private void SyncDevices(TransferSnapshot state)
    {
        _syncingDevices = true;
        try
        {
            var items = state.Peers
                .Select(peer => peer.Address.Length > 0 ? $"{peer.Name} · {peer.Address}" : peer.Name)
                .ToArray();
            if (!items.SequenceEqual(_devices.ItemsSource?.Cast<string>() ?? []))
                _devices.ItemsSource = items;
            var index = state.PeerId is null ? -1 : state.Peers.ToList().FindIndex(peer => peer.DeviceId == state.PeerId);
            if (_devices.SelectedIndex != index) _devices.SelectedIndex = index;
            _deviceHint.IsVisible = items.Length == 0;
        }
        finally { _syncingDevices = false; }
    }

    private void OnDeviceSelectionChanged()
    {
        if (_syncingDevices) return;
        var state = _core.Snapshot;
        var index = _devices.SelectedIndex;
        _core.SelectPeer(index >= 0 && index < state.Peers.Count ? state.Peers[index].DeviceId : null);
    }

    private void OnMethodSelectionChanged()
    {
        var relay = _method.SelectedIndex == 1;
        if (relay && !_core.Snapshot.RelayConfigured)
        {
            // Refusing here keeps the visible method switch and the core route from disagreeing.
            _method.SelectedIndex = 0;
            SetStatus("请先在『网盘中转设置』里点『保存并测试』连接网盘，再选择网盘中转。");
            return;
        }
        _core.SelectRoute(relay ? TransferRoute.Relay : TransferRoute.Direct);
    }

    private void UpdateReceive()
    {
        _receive.Content = _receiving ? "停止接收" : "开启接收";
        _receiveStatus.Text = _receiving
            ? OperatingSystem.IsAndroid() ? "接收已开启，收到的文件会发布到系统下载目录" : "接收已开启，文件会保存到收件文件夹"
            : "接收未开启";
    }

    private void UpdateSendButtons()
    {
        var state = _core.Snapshot;
        _send.IsEnabled = !state.Busy && state.Files.Count > 0;
        _cancel.IsEnabled = state.Busy;
        _retryLast.IsVisible = _core.HasFailure && !state.Busy;
    }

    private void UpdateCloudState()
    {
        _openListStart.IsVisible = !_openListRunning;
        _openListAdmin.IsEnabled = _openListRunning;
        _openListStop.IsEnabled = _openListRunning;
        if (_savedDirectory.Length == 0) { _cloudState.Text = "网盘未配置。"; return; }
        if (!Uri.TryCreate(_savedDirectory, UriKind.Absolute, out var uri)) { _cloudState.Text = "网盘目录已保存。"; return; }
        _cloudState.Text = (_openListRunning && IsLocal(uri) ? "已连接本机 OpenList：" : "已配置：") + uri.AbsolutePath.TrimEnd('/');
    }

    private bool IsLocal(Uri uri) =>
        _openListRunning && uri.AbsolutePath.StartsWith("/dav/", StringComparison.Ordinal) && IsLocalAuthority(uri);

    private bool IsLocalAuthority(Uri uri)
    {
        if (_localAuthority.Length > 0 && string.Equals(uri.Authority, _localAuthority, StringComparison.OrdinalIgnoreCase)) return true;
        var listen = (_listen.Text ?? "").Trim();
        return listen.Length > 0 && string.Equals(uri.Authority, listen + ":15244", StringComparison.OrdinalIgnoreCase);
    }

    // ---- receiving --------------------------------------------------------------------------

    private async Task ToggleReceiveAsync()
    {
        var start = !_core.Snapshot.Receiving;
        await _core.CallAsync(start ? "receive.start" : "receive.stop");
        await _core.RefreshAsync();
        SetStatus(start ? "接收已开启：保持 MPT 运行即可收到直传文件。" : "接收已停止。");
    }

    private async Task PickDirectoryAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择接收文件夹", AllowMultiple = false });
        if (folders.Count == 0) return;
        if (folders[0].TryGetLocalPath() is not { Length: > 0 } path)
        {
            SetStatus("该位置无法直接写入，请选择设备上的本地文件夹，例如 Download/MyPowerTools。");
            return;
        }
        _directory.Text = path;
        await _core.CallAsync("configure", new JsonObject { ["receiveDirectory"] = path });
        UpdateSaveSummary();
        SetStatus("收件 / 下载保存路径已更新：" + path);
    }

    private async Task OpenFolderAsync()
    {
        var path = (_directory.Text ?? "").Trim();
        if (path.Length == 0) { SetStatus("请先设置收件 / 下载保存路径。"); return; }
        if (OperatingSystem.IsAndroid())
        {
            SetStatus("Android 收到和下载的文件会发布到系统『下载』目录，请在系统文件管理器里查看。");
            return;
        }
        try
        {
            Directory.CreateDirectory(path);
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is not null && await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path))) return;
            SetStatus("无法打开文件夹：" + path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus(ex.Message);
        }
    }

    // ---- sending ----------------------------------------------------------------------------

    private async Task PickAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择要发送的文件", AllowMultiple = true });
        if (files.Count == 0) return;
        await AddPickedAsync(files);
    }

    /// <summary>Shared by the wide form and the phone page. Content-URI files are copied into the
    /// page's own outbox first, so a share that only grants a temporary read still sends.</summary>
    private async Task<int> AddPickedAsync(IReadOnlyList<IStorageFile> files)
    {
        var added = 0;
        foreach (var item in files)
        {
            if (item.TryGetLocalPath() is { Length: > 0 } path) { if (_core.AddFile(path)) added++; continue; }
            if (await StageAsync(item) is { } staged && _core.AddStage(staged)) added++;
        }
        SetStatus(added == 0
            ? "没有添加新文件。"
            : $"已添加 {added} 个文件（共 {_core.Files.Count} 个），选择设备后点『发送』。");
        return added;
    }

    private async Task<string?> StageAsync(IStorageFile item)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(item.Name) ? Guid.NewGuid().ToString("N") : Path.GetFileName(item.Name);
            var staging = Path.Combine(_core.OutboxRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var local = Path.Combine(staging, name);
            await using var input = await item.OpenReadAsync();
            await using var output = File.Create(local);
            await input.CopyToAsync(output);
            return local;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetStatus("无法读取所选文件：" + ex.Message);
            return null;
        }
    }

    private void OnDrop(DragEventArgs e)
    {
        var added = 0;
        var folders = 0;
        foreach (var item in e.DataTransfer.TryGetFiles() ?? [])
        {
            if (item.TryGetLocalPath() is not { Length: > 0 } path) continue;
            if (Directory.Exists(path)) { folders++; continue; }
            if (_core.AddFile(path)) added++;
        }
        SetStatus(folders > 0 && added == 0 ? "文件夹请先压缩后再发送。"
            : added == 0 ? "没有添加新文件。"
            : $"已添加 {added} 个文件（共 {_core.Files.Count} 个），选择设备后点『发送』。");
    }

    private void RefreshFileList() => RefreshFileList(_core.Snapshot);

    private void RefreshFileList(TransferSnapshot state)
    {
        _fileList.Children.Clear();
        if (state.Files.Count == 0)
        {
            _dropHint.Text = "把文件拖到这里，或点击选择文件（可多选）";
            return;
        }
        _dropHint.Text = $"已选择 {state.Files.Count} 个文件 · {TransferCore.Size(state.TotalBytes)}";
        foreach (var file in state.Files)
        {
            var captured = file.Path;
            _fileList.Children.Add(ItemRow(
                Text($"{file.Name} · {TransferCore.Size(file.Length)}", 14),
                Button("移除", () => { _core.RemoveFile(captured); return Task.CompletedTask; })));
        }
    }

    private Task ClearFilesAsync()
    {
        _core.ClearFiles();
        SetStatus("已清空待发送文件。");
        return Task.CompletedTask;
    }

    private Task SendAsync() => _core.SendAsync();

    private async Task CancelAsync()
    {
        await _core.CancelAsync();
    }

    private async Task RetryLastAsync()
    {
        if (!_core.HasFailure) { SetStatus("没有可重试的传输。"); return; }
        await _core.RetryAsync();
    }

    /// <summary>Keeps the wide form's history panel aligned with the module-reported entries.</summary>
    private void RefreshHistory(TransferSnapshot state)
    {
        _history.Children.Clear();
        if (state.History.Count == 0)
        {
            _history.Children.Add(Text("还没有传输记录。", 13));
            return;
        }
        for (var index = 0; index < state.History.Count; index++)
        {
            var entry = state.History[index];
            var retry = _core.HasFailure && index == 0
                ? Button("重试", RetryLastAsync)
                : null;
            _history.Children.Add(retry is null
                ? ItemRow(Text(TransferCore.Describe(entry), 13))
                : ItemRow(Text(TransferCore.Describe(entry), 13), retry));
        }
    }

    // ---- pairing and cloud codes ------------------------------------------------------------

    private async Task CopyPairingAsync()
    {
        var response = await _core.CallAsync("pairing");
        var code = TransferCore.Str(response, "code") ?? throw new InvalidOperationException("未能生成本机连接码，请重试。");
        await CopyTextAsync(code);
        SetStatus("连接码已复制。在另一台 MPT 的『连接新设备』里粘贴并点『添加设备』。");
    }

    private async Task ImportPairingAsync()
    {
        var code = (_pair.Text ?? "").Trim();
        if (code.Length == 0) throw new InvalidOperationException("请先粘贴或输入设备连接码。");
        if (code.StartsWith("mpt://cloud/", StringComparison.Ordinal))
            throw new InvalidOperationException("这是网盘连接码，请在『网盘中转设置』里导入。");
        var result = await _core.CallAsync("pair.import", new JsonObject { ["code"] = code });
        var name = TransferCore.Str(result, "paired");
        _pair.Text = "";
        await _core.RefreshAsync();
        SelectPeerByName(name);
        SetStatus($"设备 {name ?? "已保存"} 已添加，发送时直接选择它即可。");
    }

    private void SelectPeerByName(string? name)
    {
        var state = _core.Snapshot;
        if (state.Peers.Count == 0) return;
        var match = name is null ? null : state.Peers.FirstOrDefault(peer => string.Equals(peer.Name, name, StringComparison.Ordinal));
        _core.SelectPeer(match?.DeviceId ?? state.Peers[^1].DeviceId);
    }

    private async Task ExportCloudAsync()
    {
        var response = await _core.CallAsync("cloud.export");
        var code = TransferCore.Str(response, "code") ?? throw new InvalidOperationException("还没有网盘连接码，请先连接网盘。");
        await CopyTextAsync(code);
        SetStatus("网盘连接码已复制。在另一台 MPT 的『网盘中转设置』里粘贴并点『导入连接码』。");
    }

    private async Task ImportCloudAsync()
    {
        var code = (_cloudCode.Text ?? "").Trim();
        if (code.Length == 0) throw new InvalidOperationException("请先粘贴网盘连接码。");
        if (code.StartsWith("mpt://pair/", StringComparison.Ordinal))
            throw new InvalidOperationException("这是设备连接码，请在『连接新设备』里导入。");
        await _core.CallAsync("cloud.import", new JsonObject { ["code"] = code });
        _cloudCode.Text = "";
        await _core.RefreshAsync();
        SetStatus("网盘已连接，可以直接中转文件。");
    }

    private async Task PasteIntoAsync(TextBox target)
    {
        var text = await ReadClipboardAsync();
        if (string.IsNullOrWhiteSpace(text)) { SetStatus("剪贴板里没有文本，请先复制连接码或地址。"); return; }
        target.Text = text.Trim();
        SetStatus(target == _cloudCode ? "已粘贴网盘连接码，点『导入连接码』确认使用。"
            : target == _pair ? "已粘贴设备连接码，确认后点『添加设备』。"
            : "已粘贴，确认后点『保存并测试』。");
    }

    // ---- OpenList ---------------------------------------------------------------------------

    private async Task StartOpenListAsync()
    {
        SetStatus("正在安装并启动 OpenList，首次需要下载，可能要几分钟…");
        var state = await _core.CallAsync("openlist.start");
        _openListRunning = true;
        if (TransferCore.Str(state, "adminUrl") is { Length: > 0 } url) { _adminUrl = url; _localAuthority = TransferCore.Authority(url); }
        var password = TransferCore.Str(state, "password");
        if (password is { Length: > 0 }) await CopyTextAsync(password);
        if (((_webDav.Text ?? "").Trim().Length == 0) && _localAuthority.Length > 0) _webDav.Text = "http://" + _localAuthority + "/dav/";
        await _core.RefreshAsync();
        SetStatus(password is { Length: > 0 }
            ? "OpenList 已启动，管理员密码已复制（用户名 admin）。请在打开的页面登录，挂载网盘并建好互传文件夹。"
            : "OpenList 已启动。请在打开的页面登录，挂载网盘并建好互传文件夹。");
        await OpenAdminAsync();
    }

    private async Task OpenAdminAsync()
    {
        if (!_core.Snapshot.OpenListRunning) { SetStatus("OpenList 还没有运行：请先点『启用本机网盘服务』。"); return; }
        if (_adminUrl.Length == 0) { SetStatus("还没有拿到网盘管理地址，请重新启用一次 OpenList。"); return; }
        await OpenUrlAsync(_adminUrl);
    }

    private async Task CopyAdminPasswordAsync()
    {
        var state = await _core.CallAsync("openlist.start");
        _openListRunning = true;
        if (TransferCore.Str(state, "adminUrl") is { Length: > 0 } url) { _adminUrl = url; _localAuthority = TransferCore.Authority(url); }
        if (TransferCore.Str(state, "password") is { Length: > 0 } password)
        {
            await CopyTextAsync(password);
            SetStatus("管理员密码已复制（用户名 admin）。");
        }
        else SetStatus("本机没有保存的管理员密码；如忘记可在网盘管理页重置。");
        await _core.RefreshAsync();
    }

    private async Task StopOpenListAsync()
    {
        await _core.CallAsync("openlist.stop");
        _openListRunning = false;
        UpdateCloudState();
        SetStatus("OpenList 已停止；已保存的账号和网盘连接仍然可用。");
        await _core.RefreshAsync();
    }

    private async Task SaveCloudAsync()
    {
        var directory = DirectoryValue();
        if (directory.Length == 0)
            throw new InvalidOperationException("请填写互传目录（挂载目录），例如 /dav/网盘名/互传文件夹。");
        var uri = Uri.TryCreate(directory, UriKind.Absolute, out var parsed) ? parsed : null;
        if (uri is not null && !_openListRunning && !OperatingSystem.IsAndroid() && IsLocalAuthority(uri) &&
            uri.AbsolutePath.StartsWith("/dav/", StringComparison.Ordinal) && uri.AbsolutePath.TrimEnd('/') != "/dav")
            throw new InvalidOperationException("本机 OpenList 未运行，请先点『启用本机网盘服务』。");
        if (uri is not null && IsLocal(uri) && !IsManualAccount())
        {
            // Idempotent: creates the relay account once, then re-scopes it whenever the mount changes.
            // The state comes from inspect, so reopening the page never asks for the password again.
            await _core.CallAsync("openlist.connect", new JsonObject { ["url"] = directory });
            _password.Text = "";
            await _core.RefreshAsync();
            SetStatus("网盘已连接：已在这台 OpenList 的挂载目录上建立专用账号，可以直接中转文件。");
            return;
        }
        if (IsManualAccount())
        {
            var values = new JsonObject { ["webDavUrl"] = directory, ["username"] = (_username.Text ?? "").Trim() };
            // An empty box keeps the stored password instead of clearing it.
            if ((_password.Text ?? "").Length > 0) values["password"] = _password.Text;
            await ConfigureAsync(values);
        }
        else if (!string.Equals(directory, _savedDirectory, StringComparison.Ordinal))
            await ConfigureAsync(new JsonObject { ["webDavUrl"] = directory });
        await _core.CallAsync("cloud.check");
        await _core.RefreshAsync();
        SetStatus("网盘连接正常，可以中转文件。");
    }

    private string DirectoryValue()
    {
        var value = (_webDav.Text ?? "").Trim();
        if (value.Length == 0) return "";
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is not ("http" or "https")) throw new InvalidOperationException("互传目录请填写 http(s) 地址。");
            return absolute.AbsoluteUri.TrimEnd('/');
        }
        if (_localAuthority.Length == 0)
            throw new InvalidOperationException("请填写完整的 WebDAV 地址，或先启用本机 OpenList 后只填挂载目录名（例如 网盘名/互传文件夹）。");
        return "http://" + _localAuthority + "/dav/" + value.Trim('/');
    }

    private bool IsManualAccount()
    {
        if ((_password.Text ?? "").Length > 0) return true;
        var username = (_username.Text ?? "").Trim();
        return username.Length > 0 && !username.StartsWith("mpt-", StringComparison.Ordinal);
    }

    private async Task ConfigureAsync(JsonObject values)
    {
        await _core.CallAsync("configure", values);
        _password.Text = "";
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_deviceId.Text)) throw new InvalidOperationException("本机收件箱名称不能为空。");
        var values = new JsonObject
        {
            ["deviceId"] = (_deviceId.Text ?? "").Trim(),
            ["receiveDirectory"] = (_directory.Text ?? "").Trim(),
            ["listenAddress"] = (_listen.Text ?? "").Trim()
        };
        // Empty credential boxes keep the stored secret instead of clearing it.
        if ((_username.Text ?? "").Trim() is { Length: > 0 } username) values["username"] = username;
        if ((_password.Text ?? "").Length > 0) values["password"] = _password.Text;
        if ((_webDav.Text ?? "").Trim() is { Length: > 0 }) values["webDavUrl"] = DirectoryValue();
        await ConfigureAsync(values);
        await _core.RefreshAsync();
        SetStatus("设置已保存。");
    }

    // ---- inbox ------------------------------------------------------------------------------

    private async Task RefreshInboxAsync()
    {
        var items = await _core.CallAsync("cloud.list") as JsonArray ?? [];
        _inbox.Children.Clear();
        if (items.Count == 0)
        {
            _inbox.Children.Add(Text("还没有网盘来件。发送方上传完成后点『刷新收件箱』即可看到。", 13));
            return;
        }
        foreach (var item in items)
        {
            if (item is not JsonObject file) continue;
            var name = TransferCore.Str(file, "name") ?? "来件";
            var sender = TransferCore.Str(file, "sender");
            var copy = file.DeepClone().AsObject();
            var detail = sender is { Length: > 0 } ? $"{name} · {TransferCore.Size(TransferCore.Number(file, "size"))} · 来自 {sender}" : $"{name} · {TransferCore.Size(TransferCore.Number(file, "size"))}";
            _inbox.Children.Add(ItemRow(Text(detail, 14), Button("下载", () => DownloadAsync(copy, name))));
        }
    }

    private async Task DownloadAsync(JsonObject file, string name)
    {
        if (string.IsNullOrWhiteSpace(_directory.Text)) throw new InvalidOperationException("请先设置收件 / 下载保存路径。");
        await _core.StartDownloadAsync(file, name);
    }

    /// <summary>
    /// The host's back request for this tool, in the order the plan requires: close an open sheet
    /// first, then leave the advanced page for the conversation, then step the phone flow back, and
    /// finally decline so the host can leave the tool. This is the public contract, so the Shell never
    /// reaches into the page's internals.
    /// </summary>
    public bool TryHandleBack()
    {
        if (_assistant.TryHandleBack()) return true;
        if (_advancedShown)
        {
            ShowAdvanced(false);
            return true;
        }
        return _mobileShown && _mobile.TryHandleBack();
    }

    /// <summary>True while a sheet owns the page, so the host can restore focus after closing it.</summary>
    public bool IsSheetOpen => _assistant.IsSheetOpen
        || (_mobileShown && _mobile.IsSheetOpen)
        || (_advancedShown && _mobile.IsSheetOpen);

    /// <summary>True while the phone page has a sheet open. Exposed for host focus and test assertions.</summary>
    public bool IsMobileSheetOpen => _mobile.IsSheetOpen;

    // ---- activation --------------------------------------------------------------------------

    public async ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
    {
        // The conversation is this tool's main screen, so it gets the activation first: a system share
        // becomes a pending attachment in the composer, not an entry in the classic send list.
        if (await _assistant.ActivateAsync(request, cancellationToken))
        {
            ShowAdvanced(false);
            return true;
        }

        var value = (request.ActivationUri ?? "").Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            var added = _core.AddFile(uri.LocalPath);
            SetStatus(added
                ? $"已从分享添加文件（共 {_core.Files.Count} 个），选择设备后点『发送』。"
                : $"分享的文件已在待发送列表里（共 {_core.Files.Count} 个）。");
            return true;
        }
        if (value.StartsWith("mpt://pair/", StringComparison.Ordinal))
        {
            ShowAdvanced(true);
            if (_mobileShown) return await _mobile.ActivateAsync(request, cancellationToken);
            _pair.Text = value;
            _pairExpander.IsExpanded = true;
            SetStatus("已收到设备连接码：确认无误后点『添加设备』。");
            return true;
        }
        if (value.StartsWith("mpt://cloud/", StringComparison.Ordinal))
        {
            ShowAdvanced(true);
            if (_mobileShown) return await _mobile.ActivateAsync(request, cancellationToken);
            _cloudCode.Text = value;
            _cloudExpander.IsExpanded = true;
            SetStatus("已收到网盘连接码：确认无误后点『导入连接码』。");
            return true;
        }
        return await _mobile.ActivateAsync(request, cancellationToken);
    }

    // ---- clipboard and links ------------------------------------------------------------------

    private async Task<string?> ReadClipboardAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return null;
        IAsyncDataTransfer? data = null;
        try
        {
            data = await clipboard.TryGetDataAsync();
            return data is null ? null : await data.TryGetTextAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (data is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
            else if (data is IDisposable disposable) disposable.Dispose();
        }
    }

    private async Task CopyTextAsync(string value)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.Clipboard is not { } clipboard) throw new InvalidOperationException("当前环境不支持剪贴板。");
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(value));
        await clipboard.SetDataAsync(data);
    }

    private async Task OpenUrlAsync(string target)
    {
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is null || !Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            SetStatus("地址无效，请手动访问：" + target);
            return;
        }
        if (!await launcher.LaunchUriAsync(uri)) SetStatus("无法自动打开，请手动访问：" + target);
    }
}
