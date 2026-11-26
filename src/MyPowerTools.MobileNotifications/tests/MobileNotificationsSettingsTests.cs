using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;

namespace MyPowerTools.MobileNotifications.Tests;

/// <summary>
/// On-demand settings, the background switch and the signing-key paths.
///
/// These tests pin the honesty rules of the phone page: the background switch only follows state the
/// module confirmed, a denied notification permission or a missing signing key produces an actionable
/// configuration step instead of a simulated success, and the server/key commands carry exactly the
/// payload the user typed.
/// </summary>
public sealed class MobileNotificationsSettingsTests
{
    [AvaloniaFact]
    public void Settings_stay_hidden_until_requested_and_open_the_key_section_from_the_callout()
    {
        var module = new FakeNotificationsModule();
        using var host = MobileNotificationHost.Open(module);
        var settings = host.Find<Border>("SettingsPanel")!;
        Assert.False(settings.IsVisible);

        host.Click(host.Find<Button>("SettingsToggle")!);
        Assert.True(settings.IsVisible);
        Assert.False(host.Find<Border>("InboxPanel")!.IsVisible);
        Assert.Contains("服务器", MobileNotificationHost.TextOf(settings));
        Assert.Contains("签名密钥", MobileNotificationHost.TextOf(settings));
        Assert.Contains("后台接收", MobileNotificationHost.TextOf(settings));
        Assert.Contains("通知历史", MobileNotificationHost.TextOf(settings));

        // The connection settings page is where the real endpoint is shown.
        Assert.Contains("message.lixinrui000.cn", MobileNotificationHost.TextOf(settings));

        // Android Back closes settings before the Shell leaves the tool page.
        Assert.True(host.View.TryHandleBack());
        Assert.False(settings.IsVisible);
        Assert.False(host.View.TryHandleBack());

        // The inbox callout jumps straight into the key card.
        host.Click(host.Find<Button>("KeySetupAction")!);
        Assert.True(settings.IsVisible);
        Assert.NotNull(host.Find<TextBox>("SigningKeyBox"));
    }

    [AvaloniaFact]
    public void Enabling_background_receiving_requests_permission_and_never_fakes_success()
    {
        var module = new FakeNotificationsModule();
        module.Failures[FakeNotificationsModule.PollingStartCommand] =
            (MobileNotificationsViewModel.PermissionRequiredCode, "请允许通知，以便在后台接收文件时显示状态和停止按钮。");
        using var host = ShowWithSettings(module);
        var toggle = host.Find<ToggleSwitch>("BackgroundSwitch")!;
        toggle.IsChecked = true;
        host.Pump();

        Assert.Contains(FakeNotificationsModule.PollingStartCommand, module.Commands);

        // The module refused, so the switch is back off and the page explains what to do next.
        Assert.NotEqual(true, toggle.IsChecked);
        Assert.Contains("请允许通知", host.AllText());
        Assert.Equal("去配置签名密钥", host.Find<Button>("ErrorAction")!.Content);
        Assert.DoesNotContain("后台接收已开启。", host.AllText());
        Assert.Contains("后台接收未开启", host.AllText());
    }

    [AvaloniaFact]
    public void Background_switch_follows_the_state_the_module_confirms()
    {
        var module = new FakeNotificationsModule();
        module.State["keyConfigured"] = true;
        using var host = ShowWithSettings(module);
        var toggle = host.Find<ToggleSwitch>("BackgroundSwitch")!;
        Assert.NotEqual(true, toggle.IsChecked);

        toggle.IsChecked = true;
        host.Pump();

        Assert.Contains(FakeNotificationsModule.PollingStartCommand, module.Commands);
        Assert.True(toggle.IsChecked);
        Assert.Contains("后台接收已开启。", host.AllText());
        Assert.True(module.State["backgroundActive"]!.GetValue<bool>());

        toggle.IsChecked = false;
        host.Pump();

        Assert.Contains(FakeNotificationsModule.PollingStopCommand, module.Commands);
        Assert.NotEqual(true, toggle.IsChecked);
        Assert.Contains("后台接收已停止。", host.AllText());
        Assert.False(module.State["backgroundActive"]!.GetValue<bool>());
    }

    [AvaloniaFact]
    public void Background_switch_is_disabled_when_the_host_has_no_background_capability()
    {
        var module = new FakeNotificationsModule();
        module.State["backgroundAvailable"] = false;
        using var host = ShowWithSettings(module);
        var toggle = host.Find<ToggleSwitch>("BackgroundSwitch")!;
        Assert.False(toggle.IsEnabled);
        Assert.Contains("没有 background.activity 能力", host.AllText());
    }

    [AvaloniaFact]
    public void Server_settings_send_the_typed_payload_and_block_invalid_numbers()
    {
        var module = new FakeNotificationsModule();
        using var host = ShowWithSettings(module);
        // Selected by placeholder: the editable ComboBox template also contains a TextBox.
        var boxes = host.Find<Border>("SettingsPanel")!.GetVisualDescendants().OfType<TextBox>().ToList();
        TextBox Box(string watermark) => boxes.Single(box => box.PlaceholderText == watermark);
        Box("message.example.com").Text = "notify.example.com";
        Box("8888").Text = "9443";
        Box("default").Text = "ops";
        Box("5").Text = "30";

        host.Click(host.Find<Button>("SaveServerSettings")!);

        Assert.Contains(FakeNotificationsModule.ConfigureCommand, module.Commands);
        var payload = module.Payloads[module.Commands.IndexOf(FakeNotificationsModule.ConfigureCommand)]!;
        Assert.Equal("https", payload["protocol"]!.GetValue<string>());
        Assert.Equal("notify.example.com", payload["host"]!.GetValue<string>());
        Assert.Equal(9443, payload["port"]!.GetValue<int>());
        Assert.Equal("ops", payload["channel"]!.GetValue<string>());
        Assert.Equal(30, payload["pollIntervalSeconds"]!.GetValue<int>());
        Assert.Contains("服务器设置已保存。", host.AllText());

        // A non-numeric port must not reach the module.
        var callsBefore = module.Commands.Count;
        Box("8888").Text = "abc";
        host.Click(host.Find<Button>("SaveServerSettings")!);
        Assert.Equal(callsBefore, module.Commands.Count);
        Assert.Contains("端口必须是数字。", host.AllText());
    }

    [AvaloniaFact]
    public void Signing_key_import_and_clear_report_the_real_key_state()
    {
        var module = new FakeNotificationsModule();
        using var host = ShowWithSettings(module);
        var keyBox = host.Find<TextBox>("SigningKeyBox")!;
        Assert.Contains("尚未导入签名密钥", host.AllText());

        keyBox.Text = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----";
        host.Click(host.Find<Button>("ImportKey")!);

        Assert.Contains(FakeNotificationsModule.KeyImportCommand, module.Commands);
        var payload = module.Payloads[module.Commands.IndexOf(FakeNotificationsModule.KeyImportCommand)]!;
        Assert.Contains("BEGIN OPENSSH PRIVATE KEY", payload["privateKey"]!.GetValue<string>());
        Assert.Contains("签名密钥已保存到系统凭据库", host.AllText());
        Assert.Equal("", keyBox.Text);

        host.Click(host.Find<Button>("ClearKey")!);

        Assert.Contains(FakeNotificationsModule.KeyClearCommand, module.Commands);
        Assert.Contains("签名密钥已清除", host.AllText());
        Assert.Contains("尚未导入签名密钥", host.AllText());
    }

    [AvaloniaFact]
    public void A_rejected_signing_key_is_reported_as_a_failure()
    {
        var module = new FakeNotificationsModule();
        module.Failures[FakeNotificationsModule.KeyImportCommand] =
            ("MPT_VALIDATION_FAILED", "私钥格式无法识别。");
        using var host = ShowWithSettings(module);
        host.Find<TextBox>("SigningKeyBox")!.Text = "not-a-key";
        host.Click(host.Find<Button>("ImportKey")!);

        Assert.Contains("私钥格式无法识别。", host.AllText());
        Assert.DoesNotContain("签名密钥已保存到系统凭据库", host.AllText());
    }

    [AvaloniaFact]
    public void Clearing_history_needs_confirmation_before_the_module_is_called()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build("构建", "构建完成", DateTimeOffset.Now, "one"));
        using var host = ShowWithSettings(module);
        host.Click(host.Find<Button>("ClearHistory")!);
        host.Settle();

        Assert.True(host.Find<StackPanel>("ClearSheet")!.IsVisible);
        Assert.DoesNotContain(FakeNotificationsModule.InboxClearCommand, module.Commands);

        // Back closes the confirmation without clearing anything.
        Assert.True(host.View.TryHandleBack());
        host.Pump();
        Assert.False(host.Find<StackPanel>("ClearSheet")!.IsVisible);
        Assert.DoesNotContain(FakeNotificationsModule.InboxClearCommand, module.Commands);

        host.Click(host.Find<Button>("ClearHistory")!);
        host.Settle();
        host.Click(host.Find<Button>("ConfirmClearHistory")!);
        host.Settle();

        Assert.Contains(FakeNotificationsModule.InboxClearCommand, module.Commands);
        Assert.Empty(module.History());
        Assert.Contains("通知历史已清空。", host.AllText());
        Assert.False(host.Find<StackPanel>("ClearSheet")!.IsVisible);
    }

    [AvaloniaFact]
    public void Activation_link_opens_the_message_it_points_at()
    {
        var module = new FakeNotificationsModule();
        module.Seed(
            TestRecords.Build("构建", "第一个完成", DateTimeOffset.Now.AddMinutes(-10), "first"),
            TestRecords.Build("部署", "第二个完成", DateTimeOffset.Now, "second"));
        using var host = MobileNotificationHost.Open(module);
        var activated = host.View.ActivateAsync(new MyPowerTools.Abstractions.ToolActivationRequest(
            "remote-notifications-android",
            "main",
            "mypowertools://remote-notification?id=first")).AsTask().GetAwaiter().GetResult();
        host.Settle();

        Assert.True(activated);
        Assert.True(host.Find<Border>("SheetOverlay")!.IsVisible);
        Assert.Contains("第一个完成", MobileNotificationHost.TextOf(host.Find<StackPanel>("DetailSheet")!));
        Assert.False(host.View.ActivateAsync(new MyPowerTools.Abstractions.ToolActivationRequest(
            "remote-notifications-android",
            "main",
            "mypowertools://remote-notification?id=missing")).AsTask().GetAwaiter().GetResult());
    }

    // ---------------------------------------------------------------- helpers

    private static MobileNotificationHost ShowWithSettings(FakeNotificationsModule module)
    {
        var host = MobileNotificationHost.Open(module);
        host.Click(host.Find<Button>("SettingsToggle")!);
        return host;
    }
}
