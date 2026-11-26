using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface.Tests;

public sealed class ConversationDraftTests
{
    [AvaloniaFact]
    public async Task A_share_during_initial_inspect_uses_the_real_shared_key_and_keeps_the_composer_visible()
    {
        const string key = "shared:self-share-qa";
        var module = new FakeTransferModule { AssistantLinked = true };
        module.DraftPreferences["conversationKey"] = key;
        module.DraftPreferences["drafts"] = new JsonObject { [key] = new JsonObject() };
        var releaseInspect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = module.Context(Path.GetTempPath());
        var view = new TransferView(context with
        {
            ExecuteCommandAsync = async (command, args, token) =>
            {
                var session = command.EndsWith("assistant.inspect", StringComparison.Ordinal) || command.EndsWith("assistant.sync", StringComparison.Ordinal);
                if (session) await releaseInspect.Task;
                var result = await context.ExecuteCommandAsync(command, args, token);
                if (!session) return result;
                var json = JsonNode.Parse(result.Output)!.AsObject();
                json["identity"]!["conversationKey"] = key;
                return result with { Output = json.ToJsonString() };
            }
        });
        using var host = new Host(view);
        var activation = view.ActivateAsync(new ToolActivationRequest("file-transfer", "", "mypowertools://file-assistant?text=shared%20during%20startup")).AsTask();
        await SettleAsync();
        releaseInspect.TrySetResult();
        await activation;
        await SettleAsync();
        ChooseSharedConversation(view);
        await SettleAsync();
        Assert.True(Composer(view).IsEffectivelyVisible);
        Assert.Equal("shared during startup", Composer(view).Text);
        Assert.Equal(key, module.LastArgs("assistant.preferences.update")["conversationKey"]!.GetValue<string>());
        Assert.Equal(0, module.CountCalls("assistant.send"));
    }

    [AvaloniaFact]
    public async Task Shared_files_are_saved_after_choosing_a_conversation_without_sending()
    {
        var module = new FakeTransferModule();

        module.DraftPreferences["draftText"] = "原有草稿";
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        var folder = Path.Combine(Path.GetTempPath(), "mpt-share-draft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var paths = new[] { Path.Combine(folder, "beta.txt"), Path.Combine(folder, "alpha.txt") };
        try
        {
            foreach (var path in paths)
            {
                File.WriteAllText(path, "shared file");
                await view.ActivateAsync(new ToolActivationRequest("file-transfer", "", new Uri(path).AbsoluteUri));
                ChooseSharedConversation(view);
                await SettleAsync();
                Assert.Contains(path, module.DraftPreferences["attachmentPaths"]!.AsArray().Select(n => n!.GetValue<string>()));
                Assert.Null(module.DraftPreferences["targetDeviceId"]);
            }
            Assert.Equal(2, module.DraftPreferences["attachmentPaths"]!.AsArray().Count);
            Assert.Equal("原有草稿", module.DraftPreferences["draftText"]!.GetValue<string>());
            Assert.Equal(0, module.CountCalls("assistant.send"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [AvaloniaFact]
    public async Task Shared_text_is_saved_once_after_choosing_a_conversation()
    {
        var module = new FakeTransferModule();
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        await view.ActivateAsync(new ToolActivationRequest("file-transfer", "", "mypowertools://file-assistant?text=shared%20draft"));
        ChooseSharedConversation(view);
        await SettleAsync();
        Assert.Equal("shared draft", module.DraftPreferences["draftText"]!.GetValue<string>());
        Assert.Equal(1, module.CountCalls("assistant.preferences.update"));
        await SettleAsync();
        Assert.Equal(1, module.CountCalls("assistant.preferences.update"));
        Assert.Equal(0, module.CountCalls("assistant.send"));
    }

    [AvaloniaFact]
    public async Task A_shared_draft_save_failure_keeps_the_content_and_warning_visible()
    {
        var module = new FakeTransferModule();
        var context = module.Context(Path.GetTempPath());
        var view = new TransferView(context with
        {
            ExecuteCommandAsync = (command, args, token) => command.EndsWith("preferences.update", StringComparison.Ordinal)
                ? Task.FromException<CommandExecutionResult>(new IOException(PrivateFailure))
                : context.ExecuteCommandAsync(command, args, token)
        });
        using var host = new Host(view);
        await view.ActivateAsync(new ToolActivationRequest("file-transfer", "", "mypowertools://file-assistant?text=unsaved"));
        ChooseSharedConversation(view);
        await SettleAsync();
        Assert.Equal("unsaved", Composer(view).Text);
        Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(),
            text => text.Text == "草稿尚未保存，请暂时保留此页面。");
        Assert.Equal(0, module.CountCalls("assistant.send"));
    }

    [AvaloniaTheory]
    [InlineData("")]
    [InlineData("上次保存的草稿")]
    public async Task Loading_and_refreshing_an_unchanged_draft_does_not_save_it(string text)
    {
        var module = new FakeTransferModule();
        module.DraftPreferences["draftText"] = text;
        var view = new TransferView(module.Context(Path.GetTempPath()));
        var window = new Window { Width = 390, Height = 844, Content = view };
        try
        {
            window.Show();
            await SettleAsync();
            Assert.Equal(text, Composer(view).Text);
            Assert.Equal(0, module.CountCalls("assistant.preferences.update"));
            module.EmitAssistantChanged();
            await SettleAsync();
            Assert.Equal(0, module.CountCalls("assistant.preferences.update"));
        }
        finally { window.Close(); }
        Assert.Equal(0, module.CountCalls("assistant.preferences.update"));
    }

    [AvaloniaFact]
    public async Task Clearing_a_saved_draft_is_saved_once_and_empty_focus_changes_are_ignored()
    {
        var module = new FakeTransferModule();
        module.DraftPreferences["draftText"] = "要清空的旧草稿";
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        await SettleAsync();
        Assert.Equal(0, module.CountCalls("assistant.preferences.update"));
        Composer(view).Text = "";
        await SettleAsync();
        Assert.Equal(1, module.CountCalls("assistant.preferences.update"));
        Assert.Equal("", module.DraftPreferences["draftText"]!.GetValue<string>());
        Composer(view).Focus();
        Composer(view).Text = null;
        await SettleAsync();
        Assert.Equal(1, module.CountCalls("assistant.preferences.update"));
    }

    [AvaloniaFact]
    public async Task Editing_then_reverting_before_the_timer_does_not_save_an_unchanged_snapshot()
    {
        var module = new FakeTransferModule();
        module.DraftPreferences["draftText"] = "已保存";
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        Composer(view).Text = "修改";
        Dispatcher.UIThread.RunJobs();
        Composer(view).Text = "已保存";
        await SettleAsync();
        Assert.Equal(0, module.CountCalls("assistant.preferences.update"));
    }

    [AvaloniaFact]
    public async Task Typing_while_the_initial_read_is_pending_preserves_and_saves_both_drafts()
    {
        var module = new FakeTransferModule();
        module.DraftPreferences["draftText"] = "原有草稿";
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = module.Context(Path.GetTempPath());
        var view = new TransferView(context with
        {
            ExecuteCommandAsync = async (command, args, token) =>
            {
                if (command.EndsWith("preferences.inspect", StringComparison.Ordinal)) await ready.Task;
                return await context.ExecuteCommandAsync(command, args, token);
            }
        });
        using var host = new Host(view);
        Composer(view).Text = "刚输入的草稿";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, module.CountCalls("assistant.preferences.update"));
        ready.SetResult();
        await SettleAsync();
        Assert.Equal("原有草稿\n刚输入的草稿", Composer(view).Text);
        Assert.Equal(Composer(view).Text, module.DraftPreferences["draftText"]!.GetValue<string>());
        Assert.Equal(1, module.CountCalls("assistant.preferences.update"));
    }

    [AvaloniaFact]
    public async Task An_unready_host_read_is_logged_without_writing_an_untouched_empty_composer()
    {
        var module = new FakeTransferModule();
        var logs = new List<MptSurfaceLogEntry>();
        var context = module.Context(Path.GetTempPath());
        var view = new TransferView(context with
        {
            Log = logs.Add,
            ExecuteCommandAsync = (command, args, token) => command.EndsWith("preferences.inspect", StringComparison.Ordinal)
                ? Task.FromException<CommandExecutionResult>(new InvalidOperationException(PrivateFailure))
                : context.ExecuteCommandAsync(command, args, token)
        });
        using var host = new Host(view);
        Composer(view).Focus();
        Composer(view).Text = "";
        await SettleAsync();
        Assert.Equal(0, module.CountCalls("assistant.preferences.update"));
        AssertSafeDiagnostic(Assert.Single(logs), "load", "InvalidOperationException");
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<TextBlock>(),
            text => text.Text == "草稿尚未保存，请暂时保留此页面。");
    }

    [AvaloniaFact]
    public async Task A_real_save_failure_keeps_the_warning_and_draft_and_logs_only_safe_diagnostics()
    {
        var module = new FakeTransferModule();
        var logs = new List<MptSurfaceLogEntry>();
        var context = module.Context(Path.GetTempPath());
        var failing = true;
        var view = new TransferView(context with
        {
            Log = logs.Add,
            ExecuteCommandAsync = (command, args, token) => failing && command.EndsWith("preferences.update", StringComparison.Ordinal)
                ? Task.FromException<CommandExecutionResult>(new IOException(PrivateFailure))
                : context.ExecuteCommandAsync(command, args, token)
        });
        using var host = new Host(view);
        Composer(view).Text = "不能写进诊断的草稿正文";
        await SettleAsync();
        AssertSafeDiagnostic(Assert.Single(logs), "save", "IOException");
        Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(),
            text => text.Text == "草稿尚未保存，请暂时保留此页面。");
        Assert.Equal("不能写进诊断的草稿正文", Composer(view).Text);
        failing = false;
        Composer(view).Text += "；继续编辑";
        await SettleAsync();
        Assert.Equal(Composer(view).Text, module.DraftPreferences["draftText"]!.GetValue<string>());
        Assert.Single(logs);
    }

    [AvaloniaFact]
    public async Task Edits_made_while_a_save_is_pending_are_not_mistaken_for_its_saved_snapshot()
    {
        var module = new FakeTransferModule();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = module.Context(Path.GetTempPath());
        var writes = 0;
        var view = new TransferView(context with
        {
            ExecuteCommandAsync = async (command, args, token) =>
            {
                if (command.EndsWith("preferences.update", StringComparison.Ordinal) && ++writes == 1) await ready.Task;
                return await context.ExecuteCommandAsync(command, args, token);
            }
        });
        using var host = new Host(view);
        Composer(view).Text = "第一版";
        await SettleAsync();
        Assert.Equal(1, writes);
        Composer(view).Text = "第二版";
        Dispatcher.UIThread.RunJobs();
        ready.SetResult();
        await SettleAsync();
        Assert.Equal(2, writes);
        Assert.Equal("第二版", module.DraftPreferences["draftText"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public async Task Conversation_drafts_survive_view_restart_without_crossing_destinations()
    {
        var module = new FakeTransferModule();
        module.DraftPreferences["conversationKey"] = "shared";
        module.DraftPreferences["drafts"] = new JsonObject();
        module.AddPeer("laptop", "笔记本");
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using (var host = new Host(view))
        {
            OpenConversation(view, "文件传输助手");
            Composer(view).Text = "共享草稿";
            await SettleAsync();
            view.Conversation.TryHandleBack();
            OpenConversation(view, "笔记本");
            Assert.Equal("", Composer(view).Text ?? "");
            Composer(view).Text = "私聊草稿";
            await SettleAsync();
        }
        var reopened = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var second = new Host(reopened);
        await SettleAsync();
        OpenConversation(reopened, "笔记本");
        Assert.Equal("私聊草稿", Composer(reopened).Text);
        reopened.Conversation.TryHandleBack();
        OpenConversation(reopened, "文件传输助手");
        Assert.Equal("共享草稿", Composer(reopened).Text);
        Assert.Equal(0, module.CountCalls("assistant.send"));
    }

    [AvaloniaTheory]
    [InlineData("共享已发送")]
    [InlineData("私聊还未发送")]
    public async Task Send_completion_clears_only_the_origin_conversation_after_switching(string privateText)
    {
        var path = Path.Combine(Path.GetTempPath(), "mpt-draft-switch-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "同一个文件分别发给两个会话");
        try
        {
            var module = new FakeTransferModule();
            module.DraftPreferences["conversationKey"] = "shared";
            module.DraftPreferences["drafts"] = new JsonObject();
            module.AddPeer("laptop", "笔记本");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            module.BeforeAnswer = command => command == "assistant.send" ? ready.Task : null;
            var view = new TransferView(module.Context(Path.GetTempPath()));
            using var host = new Host(view);
            OpenConversation(view, "文件传输助手");
            Composer(view).Text = "共享已发送";
            view.Conversation.AddAttachment(path);
            var sending = view.Conversation.SendFromComposerAsync();
            Assert.False(sending.IsCompleted);

            view.Conversation.TryHandleBack();
            OpenConversation(view, "笔记本");
            Composer(view).Text = privateText;
            view.Conversation.AddAttachment(path);
            ready.SetResult();
            await sending;
            await SettleAsync();

            Assert.Equal(privateText, Composer(view).Text);
            Assert.Equal(1, view.Conversation.AttachmentCount);
            Assert.Equal("device:laptop", module.DraftPreferences["conversationKey"]!.GetValue<string>());
            view.Conversation.TryHandleBack();
            OpenConversation(view, "文件传输助手");
            Assert.Equal("", Composer(view).Text ?? "");
            Assert.Equal(0, view.Conversation.AttachmentCount);
            Assert.Null(module.LastArgs("assistant.send")["targetDeviceId"]);
        }
        finally { File.Delete(path); }
    }

    [AvaloniaFact]
    public void Switching_conversations_clears_the_previous_conversations_action_error()
    {
        var module = new FakeTransferModule();
        module.DraftPreferences["conversationKey"] = "shared";
        module.DraftPreferences["drafts"] = new JsonObject();
        module.AddPeer("laptop", "笔记本");
        var view = new TransferView(module.Context(Path.GetTempPath()));
        using var host = new Host(view);
        OpenConversation(view, "文件传输助手");
        const string error = "文件已不在设备上：/private/shared/report.pdf";
        view.Assistant.PublishOnUi(view.Assistant.Snapshot with { Status = error });
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == error);

        view.Conversation.TryHandleBack();
        OpenConversation(view, "笔记本");

        Assert.Empty(view.Assistant.Snapshot.Status);
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == error);
    }

    private static void OpenConversation(TransferView view, string name)
    {
        Dispatcher.UIThread.RunJobs();
        var button = view.GetLogicalDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "打开会话 " + name);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void ChooseSharedConversation(TransferView view)
    {
        Dispatcher.UIThread.RunJobs();
        var button = view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>()
            .Single(b => AutomationProperties.GetName(b) == "打开会话 文件传输助手");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private const string PrivateFailure = "不能写进诊断的草稿正文 /private/document.txt token=DO_NOT_LOG";

    private static void AssertSafeDiagnostic(MptSurfaceLogEntry log, string operation, string type)
    {
        Assert.Equal("error", log.Level);
        Assert.Contains($"file-transfer.draft.{operation}", log.Message);
        Assert.Contains(type, log.Message);
        Assert.Contains("hresult=0x", log.Message);
        Assert.Contains("methods=", log.Message);
        Assert.DoesNotContain("草稿正文", log.Message);
        Assert.DoesNotContain("/private", log.Message);
        Assert.DoesNotContain("DO_NOT_LOG", log.Message);
        Assert.Null(log.Properties);
    }

    private static TextBox Composer(Control view) => view.GetLogicalDescendants().OfType<TextBox>()
        .Single(box => box.PlaceholderText == "写点文字，或添加文件…");

    private static async Task SettleAsync()
    {
        // TextChanged is dispatched after Text assignment; also let the real 250ms save timer fire.
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(350);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Host : IDisposable
    {
        private readonly Window _window;
        public Host(Control view)
        {
            _window = new Window { Width = 390, Height = 844, Content = view };
            _window.Show();
            Dispatcher.UIThread.RunJobs();
        }
        public void Dispose() => _window.Close();
    }
}
