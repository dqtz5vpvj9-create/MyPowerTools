using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MyPowerTools.Abstractions;

namespace FileTransfer.Surface.Tests;

public sealed class ConversationNavigationV3Tests
{
    [AvaloniaTheory]
    [InlineData(390)]
    [InlineData(1000)]
    public void PublicAuthorizationCanBeRequestedOnThisDeviceWithoutConnectionCode(int width)
    {
        var module = new FakeTransferModule();
        module.RawResponses["assistant.inspect"] = """{"identity":{"id":"fresh-phone","name":"Phone","linked":false,"publicRoomState":"public-denied","authorizationError":"服务器暂未开放公屏授权"},"items":[],"members":[]}""";
        module.RawResponses["assistant.sync"] = module.RawResponses["assistant.inspect"];
        module.RawResponses["assistant.public.join"] = """{"joined":true}""";
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, width);
        view.Conversation.OpenSetup(); host.Settle();
        Button Label(string label) => view.GetLogicalDescendants().OfType<Button>().First(b =>
            b.IsEffectivelyVisible && (Equals(b.Content, label) || Text(b).Contains(label)));
        Click(host.Window, Label("公屏与授权"));
        Assert.Contains("服务器暂未开放公屏授权", Text(view));
        Assert.Contains("服务 · proxy.lixinrui000.cn", Text(view));
        module.RawResponses["assistant.inspect"] = """{"identity":{"id":"fresh-phone","name":"Phone","linked":true,"publicRoomState":"public","conversationKey":"shared:public-room"},"items":[{"id":"public-message","conversationKey":"shared:public-room","kind":"text","text":"加入后的公屏消息","senderDeviceId":"peer","state":"available","receipts":[]}],"members":[]}""";
        module.RawResponses["assistant.sync"] = module.RawResponses["assistant.inspect"];
        Click(host.Window, Label("申请加入公屏"));
        Assert.Contains("加入后的公屏消息", Text(view.Conversation.ThreadPanel));
        Assert.Equal(1, module.CountCalls("assistant.public.join"));
        Assert.Equal(0, module.CountCalls("assistant.link.import"));
        Assert.Equal(0, module.CountCalls("assistant.link.export"));
    }

    [AvaloniaTheory]
    [InlineData(390)]
    [InlineData(1000)]
    public void StorageListsSelectsAndConfirmsOnlyChosenDownloads(int width)
    {
        var module = Data();
        module.RawResponses["assistant.storage.inspect"] = """{"files":[{"itemId":"owned-download","name":"验收.pdf","bytes":1048576}]}""";
        module.RawResponses["assistant.storage.clean"] = """{"freedBytes":1048576,"removed":["owned-download"],"errors":[]}""";
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, width);
        view.Conversation.OpenSetup(); host.Settle();
        Button Label(string label) => view.GetLogicalDescendants().OfType<Button>().First(b =>
            b.IsEffectivelyVisible && (Equals(b.Content, label) || Text(b).Contains(label)));
        Click(host.Window, Label("空间管理"));
        Assert.Equal("空间管理", view.Conversation.SheetTitle);
        Assert.Contains("已下载 1 个文件 · 1 MB", Text(view));
        Click(host.Window, view.Conversation.SheetHost.GetLogicalDescendants().OfType<CheckBox>().Single());
        Click(host.Window, Label("清理 1 个文件 · 1 MB"));
        Assert.Equal(0, module.CountCalls("assistant.storage.clean"));
        Click(host.Window, Label("确认清理本机副本"));
        Assert.Equal("owned-download", module.LastArgs("assistant.storage.clean")["itemIds"]![0]!.GetValue<string>());
        Assert.Contains("已释放 1 MB", Text(view));
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(390)]
    [InlineData(1000)]
    public void Conversations_are_isolated_and_mobile_back_returns_to_list(int width)
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, width);
        Save(host.Window, "list-" + width);
        Click(host.Window, Named(view, "打开会话 LIS-IMAC"));
        Assert.Contains(Text(view.Conversation.ThreadPanel), t => t == "私聊独有内容");
        Assert.DoesNotContain(Text(view.Conversation.ThreadPanel), t => t == "公屏独有内容");
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "选择发送目标");
        Assert.Equal("laptop", view.Conversation.SelectedTargetDeviceId);
        if (width < 640)
        {
            var back = Named(view, "返回会话列表");
            Assert.True(back.Bounds.Width >= 44 && back.Bounds.Height >= 44);
        }
        Save(host.Window, "private-" + width);
        if (width < 640) { Assert.True(view.Conversation.TryHandleBack()); host.Settle(); }
        Click(host.Window, Named(view, "打开会话 文件传输助手"));
        Assert.Contains(Text(view.Conversation.ThreadPanel), t => t == "公屏独有内容");
        Assert.DoesNotContain(Text(view.Conversation.ThreadPanel), t => t == "私聊独有内容");
        Save(host.Window, "shared-" + width);
        if (width < 640) { view.Conversation.TryHandleBack(); host.Settle(); }
        Click(host.Window, Named(view, "打开会话 历史记录"));
        Assert.Contains(Text(view.Conversation.ThreadPanel), t => t == "旧消息来源不明");
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "发送"));
    }

    [AvaloniaFact]
    public async Task System_share_requires_conversation_choice_and_never_sends()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, 390);
        view.ShowAdvanced(true); host.Settle();
        await view.ActivateAsync(new ToolActivationRequest("file-transfer", "", "mypowertools://file-assistant?text=分享测试"));
        host.Settle();
        Assert.True(view.IsConversationVisible);
        Assert.Equal("", Input(view).Text ?? "");
        Assert.Contains(Text(view), t => t == "分享到会话");
        Click(host.Window, Named(view.Conversation.SheetHost, "打开会话 LIS-IMAC"));
        Assert.Equal("分享测试", Input(view).Text);
        Assert.Equal("laptop", view.Conversation.SelectedTargetDeviceId);
        Assert.Equal(0, module.CountCalls("assistant.send"));
        Save(host.Window, "share-selected-390");
    }

    [AvaloniaFact]
    public void Contacts_show_remembered_offline_devices_and_card_before_chat()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, 390);
        Click(host.Window, view.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "通讯录")));
        Click(host.Window, Named(view, "打开会话 LIS-IMAC"));
        Assert.Contains(Text(view), t => t == "设备名片");
        Assert.Contains(Text(view), t => t == "设备标识 · laptop");
        Save(host.Window, "contact-390");
        Click(host.Window, view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "发消息")));
        Assert.Equal("laptop", view.Conversation.SelectedTargetDeviceId);
        Assert.Equal(0, module.CountCalls("assistant.send"));
    }

    [AvaloniaFact]
    public async Task Switching_preserves_independent_drafts_and_share_merges_only_after_selection()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, 390);
        Click(host.Window, Named(view, "打开会话 文件传输助手"));
        Input(view).Text = "共享草稿"; host.Settle();
        view.Conversation.TryHandleBack(); host.Settle();
        Click(host.Window, Named(view, "打开会话 LIS-IMAC"));
        Assert.Equal("", Input(view).Text ?? "");
        Input(view).Text = "私聊草稿"; host.Settle();
        await view.Conversation.ActivateAsync(new ToolActivationRequest("file-transfer", "", "mypowertools://file-assistant?text=新分享"));
        host.Settle();
        Assert.Equal("私聊草稿", Input(view).Text);
        Click(host.Window, Named(view.Conversation.SheetHost, "打开会话 文件传输助手"));
        Assert.Equal("共享草稿\n新分享", Input(view).Text);
        view.Conversation.TryHandleBack(); host.Settle();
        Click(host.Window, Named(view, "打开会话 LIS-IMAC"));
        Assert.Equal("私聊草稿", Input(view).Text);
        Assert.Equal(0, module.CountCalls("assistant.send"));
    }

    [AvaloniaFact]
    public void Forward_requires_confirmation_and_does_not_send_the_destination_draft()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, 390);
        Click(host.Window, Named(view, "打开会话 LIS-IMAC"));
        Click(host.Window, Named(view.Conversation.ThreadPanel, "消息操作 私聊独有内容"));
        Click(host.Window, view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "转发")));
        Click(host.Window, Named(view.Conversation.SheetHost, "打开会话 文件传输助手"));
        Assert.Equal(0, module.CountCalls("assistant.send"));
        Assert.Equal("转发给 文件传输助手", view.Conversation.SheetTitle);
        Click(host.Window, view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "确认转发")));
        Assert.Equal(1, module.CountCalls("assistant.send"));
        Assert.Equal("私聊独有内容", module.LastArgs("assistant.send")["text"]!.GetValue<string>());
    }

    [AvaloniaFact]
    public void Real_shared_key_does_not_merge_an_older_shared_conversation()
    {
        var module = Data();
        module.AssistantItems[0]["conversationKey"] = "shared:current-session";
        module.AssistantItems.Add(new JsonObject
        {
            ["id"] = "old-shared", ["conversationKey"] = "shared:previous-session", ["kind"] = "text",
            ["text"] = "以前共享会话的内容", ["senderDeviceId"] = "laptop", ["senderName"] = "LIS-IMAC",
            ["state"] = "available", ["createdAt"] = DateTimeOffset.UtcNow.ToString("O"), ["receipts"] = new JsonArray()
        });
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, 390);
        view.Assistant.PublishOnUi(view.Assistant.Snapshot with
        { Identity = view.Assistant.Snapshot.Identity with { ConversationKey = "shared:current-session" } });
        host.Settle();
        Click(host.Window, Named(view, "打开会话 文件传输助手"));
        Assert.Contains(Text(view.Conversation.ThreadPanel), t => t == "公屏独有内容");
        Assert.DoesNotContain(Text(view.Conversation.ThreadPanel), t => t == "以前共享会话的内容");
        Assert.DoesNotContain(Text(view.Conversation.ThreadPanel), t => t == "私聊独有内容");
        view.Conversation.TryHandleBack(); host.Settle();
        Click(host.Window, Named(view, "打开会话 以前的共享会话"));
        Assert.Contains(Text(view.Conversation.ThreadPanel), t => t == "以前共享会话的内容");
        Assert.DoesNotContain(view.GetLogicalDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && Equals(b.Content, "发送"));
    }

    [AvaloniaFact]
    public void Shared_member_without_private_permission_opens_pair_preview_instead_of_sending()
    {
        var module = Data();
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, 390);
        view.Assistant.PublishOnUi(view.Assistant.Snapshot with
        {
            Members = [new AssistantDevice("member-only", "共享成员", "", "android", false, false)
                { CanPrivateMessage = false, RequiresPairing = true }]
        });
        host.Settle();
        Click(host.Window, Named(view, "打开会话 文件传输助手"));
        Click(host.Window, Named(view, "会话详情"));
        Click(host.Window, Named(view.Conversation.SheetHost, "打开会话 共享成员"));
        Click(host.Window, view.Conversation.SheetHost.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "发消息")));
        Assert.Equal("添加设备", view.Conversation.SheetTitle);
        Assert.Equal(0, module.CountCalls("assistant.send"));
        Assert.Equal(0, module.CountCalls("pair.import"));
        Assert.Null(view.Conversation.SelectedTargetDeviceId);
    }

    [AvaloniaFact]
    public void Restoring_a_short_conversation_does_not_show_a_new_message_cue()
    {
        var module = Data();
        for (var restart = 0; restart < 2; restart++)
        {
            var view = new TransferView(module.Context("/mnt/cache/data-cache"));
            using var host = new Host(view, 390);
            Click(host.Window, Named(view, "打开会话 文件传输助手"));
            host.Settle();
            Assert.DoesNotContain(view.GetLogicalDescendants().OfType<Button>(),
                b => b.IsEffectivelyVisible && (b.Content as string)?.Contains("新消息") == true);
        }
    }

    [AvaloniaFact]
    public void Mobile_managed_cloud_start_and_stop_use_runtime_commands_without_reconfiguring_cloud()
    {
        var module = Data();
        module.BeforeAnswer = name =>
        {
            if (name == "openlist.start") module.OpenListRunning = true;
            if (name == "openlist.stop") module.OpenListRunning = false;
            return null;
        };
        var view = new TransferView(module.Context("/mnt/cache/data-cache"));
        using var host = new Host(view, 390);
        view.ShowAdvanced(true); host.Settle();
        var choose = view.GetLogicalDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, "选择文件"));
        var originalPosition = choose.TranslatePoint(default, host.Window);
        Click(host.Window, Named(view, "连接与接收设置"));
        Button Label(string label) => view.GetLogicalDescendants().OfType<Button>().First(b =>
            b.IsEffectivelyVisible && (Equals(b.Content, label) || Text(b).Contains(label)));
        Click(host.Window, Label("网盘中转设置"));
        Assert.Equal(originalPosition, choose.TranslatePoint(default, host.Window));
        Save(host.Window, "cloud-settings-390");
        Click(host.Window, Label("启用本机网盘服务"));
        Assert.Equal(1, module.CountCalls("openlist.start"));
        Assert.Contains("本机网盘服务正在运行", Text(view));
        Assert.True(Label("打开网盘管理").IsEnabled);
        Click(host.Window, Label("停止本机网盘服务"));
        Assert.Equal(1, module.CountCalls("openlist.stop"));
        Assert.Contains("本机网盘服务未启动", Text(view));
        Assert.Equal(0, module.CountCalls("configure"));
        module.FailCommands.Add("openlist.start");
        Click(host.Window, Label("启用本机网盘服务"));
        Assert.Contains("本机网盘服务启动失败，请重试。", Text(view));
        Assert.True(Label("启用本机网盘服务").IsEnabled);
    }

    private static FakeTransferModule Data()
    {
        var module = new FakeTransferModule { AssistantLinked = true };
        module.AddPeer("laptop", "LIS-IMAC"); module.AddPeer("phone2", "我的手机");
        void Add(string key, string text, string kind = "text") => module.AssistantItems.Add(new JsonObject
        {
            ["id"] = "row" + module.AssistantItems.Count, ["conversationKey"] = key, ["kind"] = kind, ["text"] = text,
            ["name"] = kind == "file" ? "项目资料 · 修订版.pdf" : null, ["size"] = 24000,
            ["senderDeviceId"] = module.AssistantItems.Count % 2 == 0 ? "mpt-phone" : "laptop", ["senderName"] = "LIS-IMAC",
            ["state"] = "available", ["createdAt"] = DateTimeOffset.UtcNow.AddMinutes(-module.AssistantItems.Count).ToString("O"), ["receipts"] = new JsonArray()
        });
        Add("shared", "公屏独有内容"); Add("device:laptop", "私聊独有内容");
        Add("device:laptop", "这里是下一版的项目资料"); Add("device:laptop", "", "file"); Add("history", "旧消息来源不明");
        return module;
    }
    private static IEnumerable<string?> Text(Control view) => view.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text);
    private static TextBox Input(Control view) => view.GetLogicalDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "消息内容");
    private static Button Named(Control view, string name) => view.GetLogicalDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == name);
    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left);
        for (var i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
    }
    private static void Save(Window window, string name)
    {
        var dir = Environment.GetEnvironmentVariable("MPT_V3_SCREENSHOTS");
        if (dir is null) return;
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(dir, name + ".png"));
    }
    private sealed class Host : IDisposable
    {
        public Window Window { get; }
        public Host(Control view, int width) { Window = new Window { Width = width, Height = 844, Content = view }; Window.Show(); Settle(); }
        public void Settle() { for (var i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); } }
        public void Dispose() => Window.Close();
    }
}
