using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface;

internal sealed partial class AssistantView
{
    private Task? _draftLoad;
    private Task? _draftSave;
    private DispatcherTimer? _draftSaveTimer;
    private bool _restoringDraft;
    private bool _draftLoaded;
    private bool _targetUsable = true;
    private bool _conversationPreferences;
    private long _draftRevision;
    private long _savedDraftRevision;
    private JsonObject _observedDraft = DraftValues("", [], null);
    private readonly Dictionary<string, JsonObject> _conversationDrafts = [];
    private readonly Dictionary<string, string> _conversationDraftNames = [];
    private IEnumerable<string> ConversationDraftKeys => _conversationDrafts.Keys;
    private string? ConversationDraftTargetName(string key) => _conversationDraftNames.GetValueOrDefault(key);
    private readonly Dictionary<string, JsonObject> _pendingDrafts = [];
    private readonly Dictionary<string, JsonObject> _persistedDrafts = [];
    private readonly SemaphoreSlim _draftSwitchGate = new(1, 1);
    private double? _restoreScroll;
    private string ActiveConversationKey { get; set; } = "";

    private string SharedConversationKey => _core.Snapshot.Identity.ConversationKey is { Length: > 0 } key ? key : "shared";
    private Task EnsureDraftLoadedAsync() => _draftLoad ??= LoadDraftAsync();

    private async Task LoadDraftAsync()
    {
        try
        {
            var revision = _draftRevision;
            var saved = await _core.InspectPreferencesAsync();
            _conversationPreferences = saved["drafts"] is JsonObject;
            if (saved["drafts"] is JsonObject drafts)
                foreach (var entry in drafts)
                    if (entry.Value is JsonObject value)
                    {
                        _conversationDrafts[entry.Key] = value.DeepClone().AsObject();
                        if (value["targetName"]?.GetValue<string>() is { Length: > 0 } name) _conversationDraftNames[entry.Key] = name;
                    }
            ActiveConversationKey = saved["conversationKey"]?.GetValue<string>()
                ?? (saved["targetDeviceId"]?.GetValue<string>() is { Length: > 0 } target ? "device:" + target : SharedConversationKey);
            var prior = _conversationDrafts.GetValueOrDefault(ActiveConversationKey) ?? saved.AsObject();
            _restoringDraft = true;
            RestoreDraft(prior, revision != _draftRevision);
            _conversationDrafts[ActiveConversationKey] = CurrentDraft();
            _conversationDraftNames[ActiveConversationKey] = _targetName;
            if (revision != _draftRevision) _pendingDrafts[ActiveConversationKey] = CurrentDraft();
            else _persistedDrafts[ActiveConversationKey] = CurrentDraft().DeepClone().AsObject();
        }
        catch (Exception ex)
        {
            LogDraftFailure("load", ex);
            _core.PublishOnUi(_core.Snapshot with { Status = "暂时无法恢复草稿，当前输入仍可发送。" });
        }
        finally
        {
            if (ActiveConversationKey.Length == 0) ActiveConversationKey = SharedConversationKey;
            _observedDraft = CurrentDraft();
            _restoringDraft = false;
            _draftLoaded = true;
            Sync();
            RefreshConversationNavigation();
            RestoreConversationScroll();
            if (_pendingDrafts.Count > 0) _ = SaveDraftAsync();
        }
    }

    private void RestoreDraft(JsonObject saved, bool merge)
    {
        var prior = saved["draftText"]?.GetValue<string>() ?? "";
        if (!merge) { _input.Text = prior; _attachments.Clear(); }
        else if (prior.Length > 0 && _input.Text != prior) _input.Text = prior + "\n" + _input.Text;
        foreach (var path in (saved["attachmentPaths"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>().Where(File.Exists))
            if (!_attachments.Contains(path)) _attachments.Add(path);
        if (!merge)
        {
            _targetDeviceId = saved["targetDeviceId"]?.GetValue<string>();
            _targetName = _targetDeviceId is null ? "文件传输助手" : saved["targetName"]?.GetValue<string>() ?? RememberedDevices().FirstOrDefault(d => d.DeviceId == _targetDeviceId)?.Name ?? "已添加设备";
            _targetUsable = saved["targetUsable"]?.GetValue<bool>() ?? true;
        }
        _restoreScroll = saved["scrollOffset"]?.GetValue<double>();
        var missing = saved["missingAttachments"] as JsonArray;
        if (!_targetUsable) _core.PublishOnUi(_core.Snapshot with { Status = "原来的接收设备已移除，请重新添加后发送。" });
        else if (missing is { Count: > 0 }) _core.PublishOnUi(_core.Snapshot with { Status = "部分附件已不可用，请重新添加：" + string.Join("、", missing.Select(n => n?["name"]?.GetValue<string>())) });
    }

    private async Task SwitchConversationAsync(string key, string? targetDeviceId, string name)
    {
        await EnsureDraftLoadedAsync();
        await _draftSwitchGate.WaitAsync();
        try
        {
            if (ActiveConversationKey == key) return;
            CaptureConversationScroll();
            DraftChanged();
            await SaveDraftAsync();
            _restoringDraft = true;
            ActiveConversationKey = key;
            var saved = _conversationDrafts.GetValueOrDefault(key) ?? DraftValues("", [], targetDeviceId);
            RestoreDraft(saved, false);
            _targetDeviceId = targetDeviceId;
            _targetName = name;
            _conversationDraftNames[key] = name;
            _targetUsable = (targetDeviceId is null || RememberedDevices().Any(d => d.DeviceId == targetDeviceId && d.CanPrivateMessage))
                && key != "history" && !key.StartsWith("history:", StringComparison.Ordinal);
            _observedDraft = CurrentDraft();
            _conversationDrafts[key] = _observedDraft.DeepClone().AsObject();
            // Persist the selected conversation even when its draft is empty.
            if (_conversationPreferences) { _pendingDrafts[key] = _observedDraft.DeepClone().AsObject(); _draftRevision++; }
            _restoringDraft = false;
            SyncComposer();
            RefreshConversationNavigation();
            await SaveDraftAsync();
        }
        finally { _restoringDraft = false; _draftSwitchGate.Release(); }
    }

    private string ConversationDraftText(string key) => key == ActiveConversationKey ? _input.Text ?? "" : _conversationDrafts.GetValueOrDefault(key)?["draftText"]?.GetValue<string>() ?? "";
    private DateTimeOffset? ConversationReadAt(string key) => DateTimeOffset.TryParse(_conversationDrafts.GetValueOrDefault(key)?["lastReadAt"]?.GetValue<string>(), out var at) ? at : null;

    private Task MarkConversationReadAsync(string key, DateTimeOffset at)
    {
        if (key != ActiveConversationKey || !_draftLoaded || !_conversationPreferences || (ConversationReadAt(key) is { } old && old >= at)) return Task.CompletedTask;
        var value = key == ActiveConversationKey ? CurrentDraft() : _conversationDrafts.GetValueOrDefault(key)?.DeepClone().AsObject();
        if (value is null) return Task.CompletedTask;
        value["lastReadAt"] = at.ToString("O");
        _conversationDrafts[key] = value;
        _pendingDrafts[key] = value.DeepClone().AsObject();
        _draftRevision++;
        if (key == ActiveConversationKey) _observedDraft = CurrentDraft();
        return SaveDraftAsync();
    }

    private void CaptureConversationScroll()
    {
        if (!_draftLoaded || _restoringDraft || !_conversationPreferences) return;
        var value = _conversationDrafts.GetValueOrDefault(ActiveConversationKey) ?? CurrentDraft();
        value["scrollOffset"] = Math.Max(0, _threadScroll.Offset.Y);
        _conversationDrafts[ActiveConversationKey] = value;
    }

    private void ConversationScrollChanged()
    {
        // A scroll position is not shown in the conversation list, and rebuilding its rows from a
        // ScrollChanged notification would invalidate the layout pass that raised it -- and the rows
        // are the thread's siblings in the same page. The draft is still captured and persisted.
        CaptureConversationScroll();
        DraftChanged(refreshNavigation: false);
    }

    private void RestoreConversationScroll()
    {
        if (_restoreScroll is not { } offset) { _followThreadEnd = true; return; }
        _restoreScroll = null;
        _followThreadEnd = false;
        var key = ActiveConversationKey;
        Dispatcher.UIThread.Post(() =>
        {
            if (key != ActiveConversationKey) return;
            _threadScroll.Offset = new Vector(0, offset);
            _followThreadEnd = _threadScroll.Offset.Y >= _threadScroll.Extent.Height - _threadScroll.Viewport.Height - 1;
            if (_followThreadEnd) _newMessages.IsVisible = false;
        }, DispatcherPriority.Loaded);
    }

    private void DraftChanged(bool refreshNavigation = true)
    {
        if (_restoringDraft) return;
        var current = CurrentDraft();
        if (JsonNode.DeepEquals(current, _observedDraft)) return;
        _observedDraft = current;
        _draftRevision++;
        if (!_draftLoaded) return;
        _conversationDrafts[ActiveConversationKey] = current.DeepClone().AsObject();
        if (_draftSave is not { IsCompleted: false } && JsonNode.DeepEquals(current, _persistedDrafts.GetValueOrDefault(ActiveConversationKey)))
            _pendingDrafts.Remove(ActiveConversationKey);
        else _pendingDrafts[ActiveConversationKey] = current;
        if (refreshNavigation) RefreshConversationNavigation();
        _draftSaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        { _draftSaveTimer!.Stop(); _ = SaveDraftAsync(); });
        _draftSaveTimer.Stop();
        _draftSaveTimer.Start();
    }

    private Task SaveDraftAsync()
    {
        if (!_draftLoaded || _restoringDraft) return Task.CompletedTask;
        if (_draftSave is { IsCompleted: false }) return _draftSave;
        return _draftSave = WriteDraftAsync();
    }

    private async Task WriteDraftAsync()
    {
        while (_pendingDrafts.Count > 0)
        {
            var pair = _pendingDrafts.First();
            var args = pair.Value.DeepClone().AsObject();
            try { await _core.SavePreferencesAsync(args); }
            catch (Exception ex)
            {
                LogDraftFailure("save", ex);
                _core.PublishOnUi(_core.Snapshot with { Status = "草稿尚未保存，请暂时保留此页面。" });
                return;
            }
            _persistedDrafts[pair.Key] = pair.Value.DeepClone().AsObject();
            if (_pendingDrafts.TryGetValue(pair.Key, out var latest) && JsonNode.DeepEquals(latest, pair.Value)) _pendingDrafts.Remove(pair.Key);
        }
        _savedDraftRevision = _draftRevision;
    }

    private JsonObject CurrentDraft()
    {
        var value = DraftValues(_input.Text ?? "", _attachments, _targetDeviceId);
        if (_conversationPreferences)
        {
            value["conversationKey"] = ActiveConversationKey;
            var prior = _conversationDrafts.GetValueOrDefault(ActiveConversationKey);
            if (prior?["scrollOffset"] is { } scroll) value["scrollOffset"] = scroll.DeepClone();
            if (prior?["lastReadAt"] is { } read) value["lastReadAt"] = read.DeepClone();
        }
        return value;
    }

    private static JsonObject DraftValues(string text, IEnumerable<string> paths, string? target) => new()
    {
        ["draftText"] = text,
        ["attachmentPaths"] = new JsonArray(paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()),
        ["targetDeviceId"] = target
    };

    private void LogDraftFailure(string operation, Exception exception)
    {
        // Exception messages, Data and source paths may contain the draft or credentials. Keep
        // only runtime types, error numbers and method names on the existing Surface log channel.
        var cause = exception.GetBaseException();
        var methods = new System.Diagnostics.StackTrace(cause, false).GetFrames()
            .Select(frame => frame.GetMethod())
            .Where(method => method is not null)
            .Take(8)
            .Select(method => $"{method!.DeclaringType?.FullName}.{method.Name}");
        _context.Log(new MptSurfaceLogEntry("error",
            $"file-transfer.draft.{operation} failed: {exception.GetType().FullName}; " +
            $"cause={cause.GetType().FullName}; hresult=0x{cause.HResult:X8}; " +
            $"attached={_attached}; loaded={_draftLoaded}; " +
            $"pending={_draftRevision != _savedDraftRevision}; methods={string.Join(" > ", methods)}",
            DateTimeOffset.UtcNow));
    }
}
