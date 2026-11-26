using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace FileTransfer.Surface.Tests;

public sealed class CloudAccountsTests
{
    [AvaloniaFact]
    public async Task Native_authorization_forwards_credential_once_and_only_backend_completion_makes_account_ready()
    {
        var fake = new Module();
        var login = new TaskCompletionSource<MptCloudAuthorizationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Authorize = (_, _) => login.Task;
        using var host = new Host(fake);
        await host.Ready();
        await host.Click("添加网盘");
        await host.Click("夸克网盘");
        host.StartClick("登录并连接");
        await Until(() => fake.Calls.Any(c => c.Command == "authorize.begin"));
        Assert.Empty(fake.Accounts);
        Assert.Contains("等待你确认登录", host.Text);
        Assert.True(fake.Last("authorize.begin")["nativeAuthorizationAvailable"]!.GetValue<bool>());
        login.SetResult(new("quark", "cookie", "secret-cookie-not-for-ui"));
        await Until(() => host.Text.Contains("网盘已连接"));
        Assert.Single(fake.Accounts);
        Assert.Equal("secret-cookie-not-for-ui", fake.Last("authorize.complete")["credential"]!.GetValue<string>());
        Assert.Equal(1, fake.Calls.Count(c => c.Command == "authorize.complete"));
        Assert.DoesNotContain("secret-cookie", host.Text);
        Assert.Equal("ready", fake.Accounts[0]!["status"]!.GetValue<string>());
        Assert.True(fake.Last("inspect")["nativeAuthorizationAvailable"]!.GetValue<bool>());
    }

    [AvaloniaFact]
    public async Task Back_cancels_pending_native_login_and_discards_a_late_credential()
    {
        var fake = new Module();
        var login = new TaskCompletionSource<MptCloudAuthorizationResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Authorize = (_, _) => login.Task;
        using var host = new Host(fake);
        await host.Ready(); await host.Click("添加网盘"); await host.Click("夸克网盘");
        host.StartClick("登录并连接");
        await Until(() => fake.Calls.Any(c => c.Command == "authorize.begin"));
        Assert.True(host.View.TryHandleBack());
        login.SetResult(new("quark", "cookie", "late-secret"));
        await Until(() => fake.Calls.Any(c => c.Command == "authorize.cancel"));
        await Pump();
        Assert.Equal("home", host.View.CurrentPage);
        Assert.DoesNotContain(fake.Calls, c => c.Command == "authorize.complete");
        Assert.Empty(fake.Accounts);
        Assert.DoesNotContain("late-secret", host.Text);
        Assert.False(host.View.TryHandleBack());
    }

    [AvaloniaFact]
    public async Task Blocked_provider_shows_reason_without_fabricating_a_login_or_account()
    {
        var fake = new Module { Blocked = true };
        using var host = new Host(fake);
        await host.Ready(); await host.Click("添加网盘"); await host.Click("百度网盘");
        await host.Click("检查连接条件");
        Assert.Contains("当前平台尚未提供百度登录入口", host.Text);
        Assert.Contains("还不能连接这个网盘", host.Text);
        Assert.Empty(fake.Accounts);
        Assert.DoesNotContain(fake.Calls, c => c.Command == "authorize.complete");
        Assert.DoesNotContain(host.View.GetLogicalDescendants().OfType<TextBox>(), _ => true);
    }

    [AvaloniaFact]
    public async Task Default_requires_ready_account_and_cloud_only_pause_disconnect_send_exact_commands()
    {
        var fake = new Module(); fake.AddAccount("ready"); fake.AddAccount("paused", "second");
        using var host = new Host(fake);
        await host.Ready(); await host.Click("默认中转盘");
        Assert.Single(host.View.GetLogicalDescendants().OfType<RadioButton>());
        await host.Click("返回我的网盘");
        await host.Click("使用方式");
        var cloudOnly = host.View.GetLogicalDescendants().OfType<RadioButton>().Single(r => AutomationProperties.GetName(r) == "仅经网盘");
        cloudOnly.IsChecked = true;
        await host.Click("保存使用方式");
        Assert.Equal("cloudOnly", fake.Preferences["mode"]!.GetValue<string>());
        await host.Click("管理夸克网盘 · 本机账号");
        await host.Click("暂停使用"); await host.Click("确认暂停");
        Assert.True(fake.Last("pause")["paused"]!.GetValue<bool>());
        await host.Click("管理夸克网盘 · 本机账号"); await host.Click("恢复使用");
        Assert.False(fake.Last("pause")["paused"]!.GetValue<bool>());
        await host.Click("管理夸克网盘 · 本机账号"); await host.Click("断开连接");
        Assert.Contains("网盘里的文件会保留", host.Text);
        Assert.DoesNotContain(fake.Calls, c => c.Command == "disconnect");
        await host.Click("确认断开，保留网盘文件");
        Assert.Equal("account", fake.Last("disconnect")["accountId"]!.GetValue<string>());
        Assert.Equal("cloudOnly", fake.Preferences["mode"]!.GetValue<string>());
        Assert.DoesNotContain(fake.Calls, c => c.Command.Contains("delete") || c.Command.Contains("send"));
    }

    [AvaloniaFact]
    public async Task Folder_picker_uses_module_ids_and_unavailable_cleanup_never_deletes()
    {
        var fake = new Module(); fake.AddAccount("ready");
        using var host = new Host(fake);
        await host.Ready(); await host.Click("管理夸克网盘 · 本机账号"); await host.Click("选择存放位置");
        await host.Click("工作资料"); await host.Click("使用此位置");
        Assert.Equal("provider-folder-17", fake.Last("directory")["folderId"]!.GetValue<string>());
        Assert.False(fake.Last("directory").ContainsKey("path"));
        await host.Click("清理中转文件");
        Assert.Contains("还不能安全清理", host.Text);
        Assert.DoesNotContain(host.View.GetLogicalDescendants().OfType<Button>(), b => (AutomationProperties.GetName(b) ?? "").Contains("确认清理"));
        Assert.DoesNotContain(fake.Calls, c => c.Command.Contains("delete") || c.Command.Contains("cleanup"));
    }

    [AvaloniaFact]
    public async Task Raw_backend_error_and_unknown_capacity_are_never_presented_as_credentials_or_success()
    {
        var fake = new Module(); fake.AddAccount("ready"); fake.Fail = "pause";
        using var host = new Host(fake);
        await host.Ready();
        Assert.Contains("空间信息暂不可用", host.Text);
        Assert.DoesNotContain("1 TB", host.Text);
        await host.Click("管理夸克网盘 · 本机账号"); await host.Click("暂停使用"); await host.Click("确认暂停");
        Assert.Contains("操作暂未完成", host.Text);
        Assert.DoesNotContain("private-cookie", host.Text);
        Assert.Equal("ready", fake.Accounts[0]!["status"]!.GetValue<string>());
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(390)]
    [InlineData(768)]
    public async Task Account_views_fit_narrow_width_and_touch_targets(int width)
    {
        var fake = new Module(); fake.AddAccount("ready");
        using var host = new Host(fake, width);
        await host.Ready();
        host.AssertLayout(); host.Save("accounts-" + width);
        await host.Click("使用方式"); host.AssertLayout(); host.Save("policy-" + width);
        await host.Click("返回我的网盘"); await host.Click("添加另一个网盘");
        host.AssertLayout(); host.Save("brands-" + width);
    }

    [AvaloniaFact]
    public async Task Detach_unsubscribes_and_idle_does_not_poll_or_start_runtime()
    {
        var fake = new Module();
        using var host = new Host(fake); await host.Ready();
        var count = fake.Calls.Count;
        await Task.Delay(60); await Pump();
        Assert.Equal(count, fake.Calls.Count);
        Assert.Equal(1, fake.Subscriptions);
        host.View.Deactivate();
        Assert.Equal(0, fake.Subscriptions);
        Assert.All(fake.Calls, c => Assert.Equal("inspect", c.Command));
    }

    [AvaloniaFact]
    public async Task Preparing_event_updates_busy_login_without_waiting_for_account_writer()
    {
        var fake = new Module();
        fake.Authorize = (_, _) => Task.FromResult<MptCloudAuthorizationResult?>(new("quark", "cookie", "private-session"));
        fake.CompleteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new Host(fake);
        await host.Ready(); await host.Click("添加网盘"); await host.Click("夸克网盘");
        host.StartClick("登录并连接");
        await Until(() => host.Text.Contains("正在准备 MPT 文件夹"));
        Assert.Empty(fake.Accounts);
        Assert.Contains(fake.Calls, c => c.Command == "authorize.status");
        Assert.DoesNotContain("private-session", host.Text);
        fake.CompleteGate.SetResult();
        await Until(() => host.Text.Contains("网盘已连接"));
        Assert.Single(fake.Accounts);
        Assert.Contains("尚未接入网盘发送", host.Text);
    }

    [AvaloniaFact]
    public async Task Assistant_entry_and_back_preserve_the_same_composer_and_unsubscribe_accounts()
    {
        var fake = new Module();
        var transfer = new TransferView(fake.Context());
        var window = new Window { Width = 390, Height = 800, Content = transfer };
        try
        {
            window.Show(); await Pump(); window.UpdateLayout();
            var input = transfer.Conversation.GetLogicalDescendants().OfType<TextBox>()
                .Single(t => AutomationProperties.GetName(t) == "消息内容");
            input.Text = "保留正在编辑的草稿";
            transfer.Conversation.OpenCloudAccounts();
            await Until(() => fake.Calls.Any(c => c.Command == "inspect"));
            Assert.True(transfer.Conversation.IsSheetOpen);
            var cloud = transfer.Conversation.GetLogicalDescendants().OfType<CloudAccountsView>().Single();
            var add = cloud.GetLogicalDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "添加网盘");
            await ((MptMobileAsyncCommand)add.Command!).ExecuteAsync();
            Assert.Equal("providers", cloud.CurrentPage);
            Assert.True(transfer.Conversation.TryHandleBack());
            Assert.Equal("home", cloud.CurrentPage);
            var count = fake.Subscriptions;
            Assert.True(transfer.Conversation.TryHandleBack());
            Assert.False(transfer.Conversation.IsSheetOpen);
            Assert.Equal(count - 1, fake.Subscriptions);
            Assert.Equal("保留正在编辑的草稿", input.Text);
            Assert.True(transfer.IsConversationVisible);
        }
        finally { window.Close(); }
    }

    private static async Task Pump() { await Task.Yield(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 150; i++) { await Pump(); if (condition()) return; await Task.Delay(5); }
        Assert.True(condition(), "Expected UI state did not arrive.");
    }

    private sealed class Host : IDisposable
    {
        public CloudAccountsView View { get; }
        public Window Window { get; }
        private readonly ScrollViewer _scroll;
        public string Text => string.Join("\n", View.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
        public Host(Module fake, int width = 390)
        {
            View = new(fake.Context());
            _scroll = new ScrollViewer { Content = View, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Window = new() { Width = width, Height = 780, Content = new Border { Padding = new Thickness(16), Child = _scroll } };
            Window.Show();
        }
        public Task Ready() => Until(() => View.Core.Snapshot.Providers.Count == 2);
        public Button FindButton(string label) => View.GetLogicalDescendants().OfType<Button>()
            .Single(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == label);
        public void StartClick(string label)
        {
            var button = FindButton(label); button.BringIntoView(); Settle();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), Window)!.Value;
            Window.MouseDown(point, MouseButton.Left); Window.MouseUp(point, MouseButton.Left); Settle();
        }
        public async Task Click(string label) { StartClick(label); await Pump(); Settle(); }
        private void Settle() { for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); Window.UpdateLayout(); } }
        public void AssertLayout()
        {
            Settle();
            foreach (var control in View.GetVisualDescendants().OfType<Control>().Where(c => c is Button or RadioButton))
            {
                if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0) continue;
                var point = control.TranslatePoint(default, Window)!.Value;
                Assert.True(point.X >= -1 && point.X + control.Bounds.Width <= Window.Bounds.Width + 1,
                    $"{AutomationProperties.GetName(control)} overflows at {point.X} + {control.Bounds.Width} / {Window.Bounds.Width}");
                Assert.True(control.Bounds.Height >= 44, $"Touch target under 44: {AutomationProperties.GetName(control)}");
            }
        }
        public void Save(string name)
        {
            var dir = Environment.GetEnvironmentVariable("MPT_CLOUD_SHOTS");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir); _scroll.Offset = default; Settle();
            using var frame = Window.CaptureRenderedFrame(); frame?.Save(Path.Combine(dir, name + ".png"));
        }
        public void Dispose() { Window.Close(); View.Deactivate(); }
    }

    private sealed class Module
    {
        public readonly List<(string Command, JsonObject Args)> Calls = [];
        public JsonArray Accounts { get; } = [];
        public JsonObject Preferences { get; } = new() { ["mode"] = "auto", ["defaultAccountId"] = "account", ["cleanupAvailable"] = false, ["cleanupUnavailableReason"] = "还不能安全清理，中转副本会保留。" };
        public bool Blocked;
        public string? Fail;
        public int Subscriptions;
        public Func<string, CancellationToken, Task<MptCloudAuthorizationResult?>>? Authorize;
        public TaskCompletionSource? CompleteGate;
        private Action<MptSurfaceEvent>? _events;
        private JsonObject _operation = new();
        public JsonObject Last(string command) => Calls.Last(c => c.Command == command).Args;
        public void AddAccount(string status, string id = "account") => Accounts.Add(new JsonObject
        {
            ["id"] = id, ["providerId"] = "quark", ["displayName"] = id == "account" ? "本机账号" : "已暂停的账号",
            ["status"] = status, ["folderId"] = "mpt-folder", ["folderName"] = "MPT", ["capacity"] = null
        });
        private JsonObject Inspect() => new()
        {
            ["accounts"] = Accounts.DeepClone(), ["preferences"] = Preferences.DeepClone(),
            ["providers"] = new JsonArray(new JsonObject { ["id"] = "quark", ["name"] = "夸克网盘", ["authorizationAvailable"] = !Blocked },
                new JsonObject { ["id"] = "baidu", ["name"] = "百度网盘", ["authorizationAvailable"] = !Blocked, ["unavailableReason"] = Blocked ? "当前平台尚未提供百度登录入口" : "" })
        };
        public MptAvaloniaSurfaceContext Context() => new("file-transfer", "main", "/mnt/cache/data-cache", "light", Execute,
            (_, _, _) => Task.CompletedTask, null!, _ => { }, events => { _events = events; Subscriptions++; return new Subscription(() => Subscriptions--); })
        { AuthorizeCloudAccountAsync = Authorize };
        private async Task<CommandExecutionResult> Execute(string full, JsonObject? args, CancellationToken token)
        {
            if (!full.StartsWith("file-transfer.cloud.accounts.", StringComparison.Ordinal))
                return new CommandExecutionResult("test", full, "succeeded", true, "{}");
            var command = full["file-transfer.cloud.accounts.".Length..];
            args ??= new(); Calls.Add((command, args.DeepClone().AsObject()));
            if (command == Fail) return new CommandExecutionResult("test", full, "failed", false,
                "private-cookie: do-not-display", new("upstream_failed", "private-cookie: do-not-display"));
            JsonObject answer;
            switch (command)
            {
                case "authorize.begin":
                    _operation = new() { ["operationId"] = "op-1", ["providerId"] = args["providerId"]?.DeepClone(),
                        ["state"] = Blocked ? "blocked" : "waiting", ["recovery"] = Blocked ? "当前平台尚未提供百度登录入口" : "", ["authorizationUrl"] = null };
                    answer = _operation; break;
                case "authorize.complete":
                    if (CompleteGate is not null)
                    {
                        _operation["state"] = "preparing";
                        _events?.Invoke(new(1, "file-transfer", "file-transfer.cloud.accounts.changed", DateTimeOffset.UtcNow, new() { ["reason"] = "preparing" }));
                        await CompleteGate.Task.WaitAsync(token);
                    }
                    AddAccount("ready"); _operation["state"] = "ready"; answer = _operation; break;
                case "authorize.status": answer = _operation; break;
                case "authorize.cancel": _operation["state"] = "cancelled"; answer = _operation; break;
                case "preferences": Preferences["mode"] = args["mode"]?.DeepClone(); answer = Inspect(); break;
                case "default": Preferences["defaultAccountId"] = args["accountId"]?.DeepClone(); answer = Inspect(); break;
                case "pause": Accounts.First(a => a!["id"]!.GetValue<string>() == args["accountId"]!.GetValue<string>())!["status"] = args["paused"]!.GetValue<bool>() ? "paused" : "ready"; answer = Inspect(); break;
                case "disconnect": var row = Accounts.First(a => a!["id"]!.GetValue<string>() == args["accountId"]!.GetValue<string>()); Accounts.Remove(row); answer = Inspect(); break;
                case "folders": answer = new() { ["available"] = true, ["folders"] = args["parentId"] is null ? new JsonArray(new JsonObject { ["id"] = "provider-folder-17", ["name"] = "工作资料" }) : new JsonArray() }; break;
                default: answer = Inspect(); break;
            }
            return new CommandExecutionResult("test", full, "succeeded", true, answer.ToJsonString());
        }
        private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
    }
}
