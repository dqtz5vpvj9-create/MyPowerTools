using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface;

/// <summary>
/// The single file-transfer surface shared by Windows, macOS and Android. It owns layout and view
/// state only: sending, receiving, pairing and OpenList work all go through file-transfer module
/// commands, so the same page keeps working on a phone where no desktop service can run.
/// </summary>
public sealed partial class TransferView : UserControl, IMptAvaloniaSurfaceActivationHandler
{
    private const int MaximumFiles = 200;
    private readonly MptAvaloniaSurfaceContext _context;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _paths = [];
    private readonly List<string> _confirmedStaged = [];
    private readonly Dictionary<string, PendingRequest> _requests = new(StringComparer.Ordinal);
    private JsonArray _peers = [];
    private IDisposable? _events;
    private PendingRequest? _lastFailure;
    private bool _receiving;
    private bool _busy;
    private bool _busyQueued;
    private bool _openListRunning;
    private string _adminUrl = "";
    private string _localAuthority = "";
    private string _savedDirectory = "";

    public TransferView(MptAvaloniaSurfaceContext context)
    {
        _context = context;
        BuildUi();
        SizeChanged += (_, e) => ApplyDensity(e.NewSize.Width);
        AttachedToVisualTree += (_, _) =>
        {
            _events ??= _context.SubscribeEvents?.Invoke(OnEvent);
            _ = GuardAsync(RefreshAsync);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _events?.Dispose();
            _events = null;
            // The selection ends with this instance; reclaim the copies this page staged itself.
            if (!_busy) foreach (var path in _paths.ToArray()) DiscardStaged(path);
        };
    }

    // ---- commands ---------------------------------------------------------------------------

    private async Task<JsonNode> CallAsync(string command, JsonObject? args = null)
    {
        var response = await _context.ExecuteCommandAsync("file-transfer." + command, args, _lifetime.Token);
        if (!response.Success)
        {
            var message = response.Error?.Message;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? response.Output : message);
        }
        if (string.IsNullOrWhiteSpace(response.Output)) return new JsonObject();
        try { return JsonNode.Parse(response.Output) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus(ex.Message); }
    }

    private void SetStatus(string message) => _status.Text = message;

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
        : "保存到 " + ShortPath(_directory.Text);

    private static string ShortPath(string? path)
    {
        var value = (path ?? "").Trim();
        if (value.Length <= 36) return value;
        var parts = value.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? "…/" + string.Join('/', parts[^2..]) : "…" + value[^32..];
    }

    private static string? Str(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Flag(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static long Number(JsonNode? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    private static string Size(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:F2} GB"
        : bytes >= 1048576 ? $"{bytes / 1048576d:F1} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:F0} KB" : $"{bytes} B";

    private static string Authority(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : "";

    // ---- state ------------------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        var state = await CallAsync("inspect");
        var settings = state["settings"] as JsonObject;
        if (Str(settings, "receiveDirectory") is { Length: > 0 } directory) _directory.Text = directory;
        if (Str(settings, "deviceId") is { Length: > 0 } deviceId) _deviceId.Text = deviceId;
        if (Str(settings, "listenAddress") is { Length: > 0 } listen) _listen.Text = listen;
        if (Str(settings, "webDavUrl") is { Length: > 0 } webDav) _webDav.Text = webDav;
        if (Str(settings, "username") is { Length: > 0 } username) _username.Text = username;
        _savedDirectory = (Str(settings, "webDavUrl") ?? "").Trim();
        _openListRunning = Flag(state, "openListRunning");
        if (Str(state, "adminUrl") is { Length: > 0 } adminUrl) { _adminUrl = adminUrl; _localAuthority = Authority(adminUrl); }
        _receiving = Flag(state, "receiving");
        _busy = Flag(state, "busy");
        UpdatePeers(settings, Str(settings, "lastPeer") ?? "");
        UpdateReceive();
        UpdateSaveSummary();
        UpdateCloudState();
        UpdateSendButtons();
        if (state["progress"] is JsonObject progress && Str(progress, "state") is "sending" or "uploading" or "downloading" or "receiving")
            DisplayProgress(progress, addHistory: false);
        _history.Children.Clear();
        if (state["history"] is JsonArray history)
            foreach (var item in history.Reverse().Take(20))
                if (item is JsonObject entry) _history.Children.Add(HistoryRow(entry));
        if (_history.Children.Count == 0) _history.Children.Add(Text("还没有传输记录。", 13));
        SweepOutbox();
    }

    // ---- staged outbox cleanup ---------------------------------------------------------------

    private string OutboxRoot => Path.Combine(_context.DataDirectory, "outbox");

    /// <summary>True only for copies this surface staged under its own outbox. User-picked files are never deleted.</summary>
    private bool IsStaged(string path)
    {
        try
        {
            var root = Path.GetFullPath(OutboxRoot) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Deletes a staged copy only when no transfer can still be reading it.</summary>
    private void DiscardStaged(string path)
    {
        if (_busy || !IsStaged(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes staged copies older than a day once the module reports no transfer in flight.
    /// The age gate also covers a transfer started from a previous surface instance.</summary>
    private void SweepOutbox()
    {
        if (_busy || !Directory.Exists(OutboxRoot)) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(24);
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(OutboxRoot))
            {
                if (Directory.GetLastWriteTimeUtc(folder) >= cutoff) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                    if (!_paths.Contains(file, StringComparer.Ordinal)) File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Pairs one reported completion with one staged copy that was actually sent, so duplicate
    /// file names in one batch cannot reclaim a copy the batch still needs.</summary>
    private void NoteCompleted(string name)
    {
        var staged = _paths.FirstOrDefault(path => IsStaged(path)
            && !_confirmedStaged.Contains(path, StringComparer.Ordinal)
            && string.Equals(Path.GetFileName(path), name, StringComparison.Ordinal));
        if (staged is not null) _confirmedStaged.Add(staged);
    }

    /// <summary>Called once the module is idle again: the batch is over, so confirmed copies can go.</summary>
    private void RetireConfirmedStaged()
    {
        if (_confirmedStaged.Count == 0) return;
        foreach (var path in _confirmedStaged)
        {
            _paths.Remove(path);
            DiscardStaged(path);
        }
        _confirmedStaged.Clear();
        RefreshFileList();
    }

    private void UpdatePeers(JsonObject? settings, string lastPeer)
    {
        _peers = settings?["peers"] as JsonArray ?? [];
        var items = new List<string>();
        var selected = -1;
        for (var index = 0; index < _peers.Count; index++)
        {
            if (_peers[index] is not JsonObject peer) { items.Add("未命名设备"); continue; }
            var name = Str(peer, "name") ?? "未命名设备";
            var address = Str(peer, "address");
            var isLast = lastPeer.Length > 0 && string.Equals(Str(peer, "deviceId"), lastPeer, StringComparison.Ordinal);
            if (isLast) selected = index;
            var label = address is { Length: > 0 } ? $"{name} · {address}" : name;
            items.Add(isLast ? "✓ " + label : label);
        }
        _devices.ItemsSource = items;
        _devices.SelectedIndex = items.Count == 0 ? -1 : Math.Max(0, selected);
        _deviceHint.IsVisible = items.Count == 0;
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
        _send.IsEnabled = !_busy && _paths.Count > 0;
        _cancel.IsEnabled = _busy;
        _retryLast.IsVisible = _lastFailure is not null && !_busy;
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
        var start = !_receiving;
        await CallAsync(start ? "receive.start" : "receive.stop");
        _receiving = start;
        UpdateReceive();
        UpdateSendButtons();
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
        await CallAsync("configure", new JsonObject { ["receiveDirectory"] = path });
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
        var added = 0;
        foreach (var item in files)
        {
            if (item.TryGetLocalPath() is { Length: > 0 } path) { if (SelectFile(path)) added++; continue; }
            if (await StageAsync(item) is { } staged && SelectFile(staged)) added++;
        }
        SetStatus(added == 0 ? "没有添加新文件。" : $"已添加 {added} 个文件（共 {_paths.Count} 个），选择设备后点『发送』。");
    }

    private async Task<string?> StageAsync(IStorageFile item)
    {
        try
        {
            var name = string.IsNullOrWhiteSpace(item.Name) ? Guid.NewGuid().ToString("N") : Path.GetFileName(item.Name);
            var staging = Path.Combine(_context.DataDirectory, "outbox", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var local = Path.Combine(staging, name);
            await using var input = await item.OpenReadAsync();
            await using var output = File.Create(local);
            await input.CopyToAsync(output, _lifetime.Token);
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
            if (SelectFile(path)) added++;
        }
        SetStatus(folders > 0 && added == 0 ? "文件夹请先压缩后再发送。"
            : added == 0 ? "没有添加新文件。"
            : $"已添加 {added} 个文件（共 {_paths.Count} 个），选择设备后点『发送』。");
    }

    private bool SelectFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (_paths.Contains(path, StringComparer.Ordinal)) return false;
        if (_paths.Count >= MaximumFiles) { SetStatus($"一次最多发送 {MaximumFiles} 个文件。"); return false; }
        _paths.Add(path);
        RefreshFileList();
        return true;
    }

    private void RefreshFileList()
    {
        _fileList.Children.Clear();
        if (_paths.Count == 0)
        {
            _dropHint.Text = "把文件拖到这里，或点击选择文件（可多选）";
            UpdateSendButtons();
            return;
        }
        long total = 0;
        foreach (var path in _paths) total += Length(path);
        _dropHint.Text = $"已选择 {_paths.Count} 个文件 · {Size(total)}";
        foreach (var path in _paths)
        {
            var captured = path;
            _fileList.Children.Add(ItemRow(Text($"{Path.GetFileName(captured)} · {Size(Length(captured))}", 14),
                Button("移除", () => { RemoveFile(captured); return Task.CompletedTask; })));
        }
        UpdateSendButtons();
    }

    private static long Length(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    /// <summary>Drops one pending file and reclaims the staged copy it came from when it is safe.</summary>
    private void RemoveFile(string path)
    {
        _paths.Remove(path);
        DiscardStaged(path);
        RefreshFileList();
    }

    private Task ClearFilesAsync()
    {
        // Staged copies are only reclaimed when nothing can still be reading them.
        if (!_busy) foreach (var path in _paths.ToArray()) DiscardStaged(path);
        _paths.Clear();
        _confirmedStaged.Clear();
        RefreshFileList();
        SetStatus("已清空待发送文件。");
        return Task.CompletedTask;
    }

    private async Task SendAsync()
    {
        if (_busy) throw new InvalidOperationException("已有传输正在进行，请等待完成或点『取消传输』。");
        if (_paths.Count == 0) throw new InvalidOperationException("请先选择要发送的文件。");
        if (_devices.SelectedIndex < 0 || _devices.SelectedIndex >= _peers.Count)
            throw new InvalidOperationException("请先在『连接新设备』里导入接收设备的连接码。");
        var peerId = Str(_peers[_devices.SelectedIndex], "deviceId")
            ?? throw new InvalidOperationException("这台设备的连接码不完整，请重新导入。");
        var missing = _paths.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
        {
            foreach (var path in missing) _paths.Remove(path);
            RefreshFileList();
            throw new InvalidOperationException($"{missing.Length} 个文件已不在设备上，已从列表移除。");
        }
        var cloud = _method.SelectedIndex == 1;
        if (cloud && _savedDirectory.Length == 0)
            throw new InvalidOperationException("请先在『网盘中转设置』里点『保存并测试』连接网盘，再发送。");
        var args = new JsonObject
        {
            ["paths"] = new JsonArray(_paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()),
            ["peerId"] = peerId
        };
        var label = _paths.Count == 1 ? Path.GetFileName(_paths[0]) : $"{_paths.Count} 个文件";
        await StartAsync(cloud ? "send.cloud" : "send.direct", args, label);
    }

    private async Task StartAsync(string command, JsonObject args, string label)
    {
        // Register the request and show the pending state *before* the call. A small file can report
        // its terminal transfer.changed event while the command response is still in flight; a late
        // response must never overwrite that result with "正在传输" again.
        _requests[label] = new PendingRequest(command, args.DeepClone().AsObject(), label);
        _confirmedStaged.Clear();
        _lastFailure = null;
        _busy = true;
        _progress.IsVisible = true;
        _progress.Value = 0;
        _progressText.Text = "";
        UpdateSendButtons();
        SetStatus($"正在传输 {label}…");
        try
        {
            await CallAsync(command, args);
        }
        catch
        {
            // A rejected command must never leave a phantom transfer behind; re-derive the module's
            // real state, then still surface the rejection to the user.
            await GuardAsync(RollbackStartAsync);
            throw;
        }
        finally
        {
            // Runs after the click helper restored its own button, so state-dependent enablement wins.
            Dispatcher.UIThread.Post(UpdateSendButtons, DispatcherPriority.Background);
        }
    }

    /// <summary>Re-derives the module's real state after a rejected command instead of assuming idle.</summary>
    private async Task RollbackStartAsync()
    {
        var state = await CallAsync("inspect");
        _busy = Flag(state, "busy");
        if (!_busy)
        {
            _progress.IsVisible = false;
            _progressText.Text = "";
        }
        UpdateSendButtons();
    }

    private async Task CancelAsync()
    {
        await CallAsync("cancel");
        SetStatus("已请求取消当前传输。");
    }

    private async Task RetryLastAsync()
    {
        if (_lastFailure is not { } request) { SetStatus("没有可重试的传输。"); return; }
        await RetryAsync(request);
    }

    private async Task RetryAsync(PendingRequest request)
    {
        var args = request.Args.DeepClone().AsObject();
        if (args["paths"] is JsonArray paths)
        {
            var missing = paths.Select(path => path?.GetValue<string>() ?? "").Where(path => path.Length == 0 || !File.Exists(path)).ToArray();
            if (missing.Length > 0) throw new InvalidOperationException("原文件已不在设备上，请重新选择文件。");
        }
        await StartAsync(request.Command, args, request.Label);
    }

    // ---- pairing and cloud codes ------------------------------------------------------------

    private async Task CopyPairingAsync()
    {
        var response = await CallAsync("pairing");
        var code = Str(response, "code") ?? throw new InvalidOperationException("无法生成本机连接码，请先连接 Tailscale 网络。");
        await CopyTextAsync(code);
        SetStatus("连接码已复制。在另一台 MPT 的『连接新设备』里粘贴并点『添加设备』。");
    }

    private async Task ImportPairingAsync()
    {
        var code = (_pair.Text ?? "").Trim();
        if (code.Length == 0) throw new InvalidOperationException("请先粘贴或输入设备连接码。");
        if (code.StartsWith("mpt://cloud/", StringComparison.Ordinal))
            throw new InvalidOperationException("这是网盘连接码，请在『网盘中转设置』里导入。");
        var result = await CallAsync("pair.import", new JsonObject { ["code"] = code });
        var name = Str(result, "paired");
        _pair.Text = "";
        await RefreshAsync();
        SelectPeerByName(name);
        SetStatus($"设备 {name ?? "已保存"} 已添加，发送时直接选择它即可。");
    }

    private void SelectPeerByName(string? name)
    {
        if (_peers.Count == 0) return;
        var index = name is null ? -1 : _peers.ToList().FindIndex(peer => string.Equals(Str(peer, "name"), name, StringComparison.Ordinal));
        _devices.SelectedIndex = index >= 0 ? index : _peers.Count - 1;
    }

    private async Task ExportCloudAsync()
    {
        var response = await CallAsync("cloud.export");
        var code = Str(response, "code") ?? throw new InvalidOperationException("还没有网盘连接码，请先连接网盘。");
        await CopyTextAsync(code);
        SetStatus("网盘连接码已复制。在另一台 MPT 的『网盘中转设置』里粘贴并点『导入连接码』。");
    }

    private async Task ImportCloudAsync()
    {
        var code = (_cloudCode.Text ?? "").Trim();
        if (code.Length == 0) throw new InvalidOperationException("请先粘贴网盘连接码。");
        if (code.StartsWith("mpt://pair/", StringComparison.Ordinal))
            throw new InvalidOperationException("这是设备连接码，请在『连接新设备』里导入。");
        await CallAsync("cloud.import", new JsonObject { ["code"] = code });
        _cloudCode.Text = "";
        await RefreshAsync();
        SetStatus("网盘已连接，可以直接中转文件。");
    }

    private async Task PasteIntoAsync(TextBox target)
    {
        var text = await ReadClipboardAsync();
        if (string.IsNullOrWhiteSpace(text)) { SetStatus("剪贴板里没有文本，请先复制连接码或地址。"); return; }
        target.Text = text.Trim();
        SetStatus(target == _cloudCode ? "已粘贴网盘连接码，点『导入连接码』确认使用。"
            : target == _pair ? "已粘贴设备连接码，点『添加设备』确认添加。"
            : "已粘贴，确认后点『保存并测试』。");
    }

    // ---- OpenList ---------------------------------------------------------------------------

    private async Task StartOpenListAsync()
    {
        SetStatus("正在安装并启动 OpenList，首次需要下载，可能要几分钟…");
        var state = await CallAsync("openlist.start");
        _openListRunning = true;
        if (Str(state, "adminUrl") is { Length: > 0 } url) { _adminUrl = url; _localAuthority = Authority(url); }
        var password = Str(state, "password");
        if (password is { Length: > 0 }) await CopyTextAsync(password);
        if (((_webDav.Text ?? "").Trim().Length == 0) && _localAuthority.Length > 0) _webDav.Text = "http://" + _localAuthority + "/dav/";
        await RefreshAsync();
        SetStatus(password is { Length: > 0 }
            ? "OpenList 已启动，管理员密码已复制（用户名 admin）。请在打开的页面登录，挂载网盘并建好互传文件夹。"
            : "OpenList 已启动。请在打开的页面登录，挂载网盘并建好互传文件夹。");
        await OpenAdminAsync();
    }

    private async Task OpenAdminAsync()
    {
        if (!_openListRunning) { SetStatus("OpenList 还没有运行：请先点『一键安装并启用 OpenList』。"); return; }
        if (_adminUrl.Length == 0) { SetStatus("还没有拿到网盘管理地址，请重新启用一次 OpenList。"); return; }
        await OpenUrlAsync(_adminUrl);
    }

    private async Task CopyAdminPasswordAsync()
    {
        var state = await CallAsync("openlist.start");
        _openListRunning = true;
        if (Str(state, "adminUrl") is { Length: > 0 } url) { _adminUrl = url; _localAuthority = Authority(url); }
        if (Str(state, "password") is { Length: > 0 } password)
        {
            await CopyTextAsync(password);
            SetStatus("管理员密码已复制（用户名 admin）。");
        }
        else SetStatus("本机没有保存的管理员密码；如忘记可在网盘管理页重置。");
        await RefreshAsync();
    }

    private async Task StopOpenListAsync()
    {
        await CallAsync("openlist.stop");
        _openListRunning = false;
        UpdateCloudState();
        SetStatus("OpenList 已停止；已保存的账号和网盘连接仍然可用。");
        await RefreshAsync();
    }

    private async Task SaveCloudAsync()
    {
        var directory = DirectoryValue();
        if (directory.Length == 0)
            throw new InvalidOperationException("请填写互传目录（挂载目录），例如 /dav/网盘名/互传文件夹。");
        var uri = Uri.TryCreate(directory, UriKind.Absolute, out var parsed) ? parsed : null;
        if (uri is not null && !_openListRunning && !OperatingSystem.IsAndroid() && IsLocalAuthority(uri) &&
            uri.AbsolutePath.StartsWith("/dav/", StringComparison.Ordinal) && uri.AbsolutePath.TrimEnd('/') != "/dav")
            throw new InvalidOperationException("本机 OpenList 未运行，请先点『一键安装并启用 OpenList』。");
        if (uri is not null && IsLocal(uri) && !IsManualAccount())
        {
            // Idempotent: creates the relay account once, then re-scopes it whenever the mount changes.
            // The state comes from inspect, so reopening the page never asks for the password again.
            await CallAsync("openlist.connect", new JsonObject { ["url"] = directory });
            _password.Text = "";
            await RefreshAsync();
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
        await CallAsync("cloud.check");
        await RefreshAsync();
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
        await CallAsync("configure", values);
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
        await RefreshAsync();
        SetStatus("设置已保存。");
    }

    // ---- inbox ------------------------------------------------------------------------------

    private async Task RefreshInboxAsync()
    {
        var items = await CallAsync("cloud.list") as JsonArray ?? [];
        _inbox.Children.Clear();
        if (items.Count == 0)
        {
            _inbox.Children.Add(Text("还没有网盘来件。发送方上传完成后点『刷新收件箱』即可看到。", 13));
            return;
        }
        foreach (var item in items)
        {
            if (item is not JsonObject file) continue;
            var name = Str(file, "name") ?? "来件";
            var sender = Str(file, "sender");
            var copy = file.DeepClone().AsObject();
            var detail = sender is { Length: > 0 } ? $"{name} · {Size(Number(file, "size"))} · 来自 {sender}" : $"{name} · {Size(Number(file, "size"))}";
            _inbox.Children.Add(ItemRow(Text(detail, 14), Button("下载", () => DownloadAsync(copy, name))));
        }
    }

    private async Task DownloadAsync(JsonObject file, string name)
    {
        if (string.IsNullOrWhiteSpace(_directory.Text)) throw new InvalidOperationException("请先设置收件 / 下载保存路径。");
        await StartAsync("cloud.download", new JsonObject { ["file"] = file.DeepClone() }, name);
    }

    // ---- events and progress -----------------------------------------------------------------

    private void OnEvent(MptSurfaceEvent e)
    {
        if (e.SourceId != "file-transfer" || e.Type != "transfer.changed") return;
        var payload = (JsonObject)e.Payload.DeepClone();
        Dispatcher.UIThread.Post(() => DisplayProgress(payload, addHistory: true));
    }

    private void DisplayProgress(JsonObject item, bool addHistory)
    {
        var state = Str(item, "state") ?? "";
        var active = state is "sending" or "uploading" or "downloading" or "receiving";
        var total = Number(item, "total");
        var done = Number(item, "done");
        if (active)
        {
            _progress.IsVisible = true;
            _progress.Value = total > 0 ? Math.Clamp(done * 100d / total, 0, 100) : 0;
            _progressText.Text = total > 0 ? $"{done * 100d / total:F0}% · {Size(done)} / {Size(total)}" : "";
        }
        if (state is "sending" or "uploading" or "downloading")
        {
            _busy = true;
            UpdateSendButtons();
        }
        SetStatus(Describe(item));
        if (state is not ("completed" or "received" or "failed" or "cancelled")) return;
        _progress.IsVisible = false;
        _progressText.Text = "";
        if (state == "completed" && Str(item, "name") is { } sent) NoteCompleted(sent);
        if (state is "failed" or "cancelled" && Str(item, "name") is { } label && _requests.TryGetValue(label, out var request))
            _lastFailure = request;
        if (addHistory)
        {
            _history.Children.Insert(0, HistoryRow(item));
            while (_history.Children.Count > 30) _history.Children.RemoveAt(_history.Children.Count - 1);
        }
        ScheduleBusyRefresh();
    }

    private Control HistoryRow(JsonObject item)
    {
        var retry = Str(item, "state") is "failed" or "cancelled" && Str(item, "name") is { } label && _requests.TryGetValue(label, out var request)
            ? Button("重试", () => RetryAsync(request))
            : null;
        return retry is null ? ItemRow(Text(Describe(item), 13)) : ItemRow(Text(Describe(item), 13), retry);
    }

    private static string Describe(JsonObject item)
    {
        var state = Str(item, "state") switch
        {
            "completed" => "已完成", "received" => "已接收", "cancelled" => "已取消", "failed" => "失败",
            "uploading" => "上传中", "downloading" => "下载中", "receiving" => "接收中", _ => "发送中"
        };
        var name = Str(item, "name") ?? "文件";
        var message = Str(item, "message");
        return string.IsNullOrWhiteSpace(message) ? $"{name} · {state}" : $"{name} · {state} · {message}";
    }

    /// <summary>A multi-file send reports one file at a time; ask the module again shortly after an
    /// item finishes so the cancel button is only disabled when the whole batch is done.</summary>
    private void ScheduleBusyRefresh()
    {
        if (_busyQueued) return;
        _busyQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _busyQueued = false;
            _ = GuardAsync(async () =>
            {
                var state = await CallAsync("inspect");
                _busy = Flag(state, "busy");
                UpdateSendButtons();
                if (!_busy) RetireConfirmedStaged();
            });
        }, TimeSpan.FromMilliseconds(500));
    }

    // ---- activation --------------------------------------------------------------------------

    public ValueTask<bool> ActivateAsync(ToolActivationRequest request, CancellationToken cancellationToken = default)
    {
        var value = (request.ActivationUri ?? "").Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            // The Shell keeps one Surface per tool/route, so a multi-file share accumulates right here.
            var added = SelectFile(uri.LocalPath);
            SetStatus(added
                ? $"已从分享添加文件（共 {_paths.Count} 个），选择设备后点『发送』。"
                : $"分享的文件已在待发送列表里（共 {_paths.Count} 个）。");
            return ValueTask.FromResult(true);
        }
        if (value.StartsWith("mpt://pair/", StringComparison.Ordinal))
        {
            _pair.Text = value;
            _pairExpander.IsExpanded = true;
            SetStatus("已收到设备连接码：确认无误后点『添加设备』。");
            return ValueTask.FromResult(true);
        }
        if (value.StartsWith("mpt://cloud/", StringComparison.Ordinal))
        {
            _cloudCode.Text = value;
            _cloudExpander.IsExpanded = true;
            SetStatus("已收到网盘连接码：确认无误后点『导入连接码』。");
            return ValueTask.FromResult(true);
        }
        return ValueTask.FromResult(false);
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

    private sealed record PendingRequest(string Command, JsonObject Args, string Label);
}
