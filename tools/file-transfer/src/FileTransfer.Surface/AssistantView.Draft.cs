using System.Text.Json.Nodes;
using Avalonia.Threading;

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

    private Task EnsureDraftLoadedAsync() => _draftLoad ??= LoadDraftAsync();

    private async Task LoadDraftAsync()
    {
        try
        {
            var revision = _draftRevision;
            var saved = await _core.InspectPreferencesAsync();
            _restoringDraft = true;
            var prior = saved["draftText"]?.GetValue<string>() ?? "";
            // A user can begin typing while disk/host inspection is in flight; keep both drafts.
            if (revision == _draftRevision) _input.Text = prior;
            else if (prior.Length > 0 && _input.Text != prior) _input.Text = prior + "\n" + _input.Text;
            foreach (var path in saved["attachmentPaths"] as JsonArray ?? [])
                if (path?.GetValue<string>() is { } value && File.Exists(value) && !_attachments.Contains(value)) _attachments.Add(value);
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
        catch (Exception)
        {
            _core.PublishOnUi(_core.Snapshot with { Status = "暂时无法恢复草稿，当前输入仍可发送。" });
        }
        finally
        {
            _restoringDraft = false;
            _draftLoaded = true;
            Sync();
        }
    }

    private void DraftChanged()
    {
        if (_restoringDraft) return;
        _draftRevision++;
        if (!_draftLoaded) return;
        _draftSaveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
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
            var args = new JsonObject
            {
                ["draftText"] = _input.Text,
                ["attachmentPaths"] = new JsonArray(_attachments.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["targetDeviceId"] = _targetDeviceId
            };
            try { await _core.SavePreferencesAsync(args); }
            catch (Exception)
            {
                _core.PublishOnUi(_core.Snapshot with { Status = "草稿尚未保存，请暂时保留此页面。" });
                return;
            }
            _savedDraftRevision = revision;
        }
    }
}
