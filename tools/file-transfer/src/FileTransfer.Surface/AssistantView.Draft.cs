using System.Text.Json.Nodes;
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
    private long _draftRevision;
    private long _savedDraftRevision;
    private JsonObject _observedDraft = DraftValues("", [], null);
    private JsonObject? _savedDraft;

    private Task EnsureDraftLoadedAsync() => _draftLoad ??= LoadDraftAsync();

    private async Task LoadDraftAsync()
    {
        try
        {
            var revision = _draftRevision;
            var saved = await _core.InspectPreferencesAsync();
            _restoringDraft = true;
            var prior = saved["draftText"]?.GetValue<string>() ?? "";
            var paths = (saved["attachmentPaths"] as JsonArray ?? [])
                .Select(node => node?.GetValue<string>()).OfType<string>().Where(File.Exists).ToArray();
            _savedDraft = DraftValues(prior, paths, saved["targetDeviceId"]?.GetValue<string>());
            // A user can begin typing while disk/host inspection is in flight; keep both drafts.
            if (revision == _draftRevision) _input.Text = prior;
            else if (prior.Length > 0 && _input.Text != prior) _input.Text = prior + "\n" + _input.Text;
            foreach (var path in paths)
                if (!_attachments.Contains(path)) _attachments.Add(path);
            if (revision == _draftRevision)
            {
                _targetDeviceId = saved["targetDeviceId"]?.GetValue<string>();
                _targetName = _targetDeviceId is null ? "文件传输助手" : saved["targetName"]?.GetValue<string>() ?? "已添加设备";
                _targetUsable = saved["targetUsable"]?.GetValue<bool>() ?? true;
            }
            var missing = saved["missingAttachments"] as JsonArray;
            if (!_targetUsable) _core.PublishOnUi(_core.Snapshot with { Status = "原来的接收设备已移除，请重新选择发送目标。" });
            else if (missing is { Count: > 0 })
                _core.PublishOnUi(_core.Snapshot with { Status = "部分附件已不可用，请重新添加：" + string.Join("、", missing.Select(n => n?["name"]?.GetValue<string>())) });
        }
        catch (Exception ex)
        {
            LogDraftFailure("load", ex);
            _core.PublishOnUi(_core.Snapshot with { Status = "暂时无法恢复草稿，当前输入仍可发送。" });
        }
        finally
        {
            // Avalonia queues TextChanged after the Text setter returns. Its later notification
            // must not turn a restored value (including null -> empty) into a user edit.
            _observedDraft = CurrentDraft();
            _restoringDraft = false;
            _draftLoaded = true;
            Sync();
            // Input entered while the host read was pending still needs its first save.
            if (_draftRevision != _savedDraftRevision) _ = SaveDraftAsync();
        }
    }

    private void DraftChanged()
    {
        if (_restoringDraft) return;
        var current = CurrentDraft();
        if (JsonNode.DeepEquals(current, _observedDraft)) return;
        _observedDraft = current;
        _draftRevision++;
        if (!_draftLoaded) return;
        // Android may keep its native input queue non-idle while the page is visible. A draft
        // save is required work: Background can starve indefinitely, so debounce at Normal.
        _draftSaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            _draftSaveTimer!.Stop();
            _ = SaveDraftAsync();
        });
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
        while (_savedDraftRevision != _draftRevision)
        {
            var revision = _draftRevision;
            var args = CurrentDraft();
            if (JsonNode.DeepEquals(args, _savedDraft))
            {
                _savedDraftRevision = revision;
                continue;
            }
            try { await _core.SavePreferencesAsync(args); }
            catch (Exception ex)
            {
                LogDraftFailure("save", ex);
                _core.PublishOnUi(_core.Snapshot with { Status = "草稿尚未保存，请暂时保留此页面。" });
                return;
            }
            _savedDraft = args;
            _savedDraftRevision = revision;
        }
    }

    private JsonObject CurrentDraft() => DraftValues(_input.Text ?? "", _attachments, _targetDeviceId);

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
