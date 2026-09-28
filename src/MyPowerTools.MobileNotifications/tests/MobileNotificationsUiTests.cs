using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MyPowerTools.MobileNotifications.Tests;

/// <summary>
/// UI + state tests for the rebuilt phone notification page: the real history list with day groups
/// and unread dots, event-driven refresh (no status polling), search, label filters, 全部已读, the
/// detail sheet with the full body and its quoted block, Back handling, the empty state and the
/// mobile style contract. They run the real view in a headless Avalonia host against the real store
/// file and a fake module.
/// </summary>
public sealed class MobileNotificationsUiTests
{
    /// <summary>Local wall-clock time, the shape the real server payload carries.</summary>
    private static DateTimeOffset LocalTime(DateTime local) => new(local);

    // ---------------------------------------------------------------- list + unread

    [AvaloniaFact]
    public void Inbox_renders_real_history_in_day_groups_with_read_state_and_missing_key_callout()
    {
        var module = new FakeNotificationsModule();
        module.Seed(
            TestRecords.Build("备份", "昨天的备份已完成", LocalTime(DateTime.Today.AddDays(-1).AddHours(18)), "older"),
            TestRecords.Build("构建", "新版本已构建完成", LocalTime(DateTime.Today.AddHours(9)), "newer"));
        using var host = MobileNotificationHost.Open(module);

        Assert.Equal(2, host.Rows().Count);

        // Real day buckets: today's message under 今天, yesterday's under 昨天.
        var texts = host.AllText();
        Assert.Contains(MobileNotificationDetailViewModel.FormatDayTitle(DateTime.Today, DateTime.Today), texts);
        Assert.Contains(MobileNotificationDetailViewModel.FormatDayTitle(DateTime.Today.AddDays(-1), DateTime.Today), texts);
        Assert.Contains("构建", texts);
        Assert.Contains("备份", texts);
        Assert.Contains("值得你看一眼。", texts);
        Assert.Contains("所有消息都已读完。", texts);
        Assert.Contains("MacBook Pro", texts);

        // The inbox header never prints the raw endpoint URL: connection details live in settings.
        var headerText = MobileNotificationHost.TextOf(host.Find<Border>("InboxHeader")!);
        Assert.DoesNotContain("message.lixinrui000.cn", headerText);
        Assert.DoesNotContain("https://", headerText);
        Assert.Contains("上次同步", headerText);
        Assert.Contains("共 2 条通知", headerText);

        // A missing signing key is offered as a configuration step, never as a silent success.
        var keyWarning = host.Find<Border>("KeyWarningCard");
        Assert.NotNull(keyWarning);
        Assert.True(keyWarning!.IsVisible);
        Assert.Equal("导入签名密钥", host.Find<Button>("KeySetupAction")!.Content);
    }

    [AvaloniaFact]
    public void Message_received_event_refreshes_the_list_without_polling_the_module()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build("构建", "第一次构建完成", DateTimeOffset.Now.AddMinutes(-20), "first"));
        using var host = MobileNotificationHost.Open(module);

        Assert.Single(host.Rows());
        var statusCalls = module.Commands.Count(command => command == FakeNotificationsModule.StatusCommand);

        module.Append(TestRecords.Build("部署", "部署已经完成", DateTimeOffset.Now, "second"));
        module.PublishEvent("message.received");
        host.Settle();

        Assert.Equal(2, host.Rows().Count);
        Assert.Contains("来自设备的 1 条新消息。", host.AllText());

        // The refresh came from the event: no extra status or sync command was issued.
        Assert.Equal(statusCalls, module.Commands.Count(command => command == FakeNotificationsModule.StatusCommand));
        Assert.DoesNotContain(FakeNotificationsModule.SyncCommand, module.Commands);
    }

    [AvaloniaFact]
    public void Mark_all_read_clears_unread_state_and_updates_the_heading()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build("构建", "构建完成", DateTimeOffset.Now, "one"));
        using var host = MobileNotificationHost.Open(module);

        module.Append(TestRecords.Build("部署", "部署完成", DateTimeOffset.Now, "two"));
        module.PublishEvent("message.received");
        host.Settle();
        Assert.Contains("来自设备的 1 条新消息。", host.AllText());

        var markRead = host.Find<Button>("MarkAllRead");
        Assert.NotNull(markRead);
        Assert.True(markRead!.IsVisible);
        host.Click(markRead);

        Assert.Contains("所有消息都已读完。", host.AllText());
        Assert.False(markRead.IsVisible);
        Assert.DoesNotContain(host.Rows(), row => host.UnreadDot(row)?.IsVisible == true);
    }

    [AvaloniaFact]
    public void Inbox_clear_event_empties_the_list()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build("构建", "构建完成", DateTimeOffset.Now, "one"));
        using var host = MobileNotificationHost.Open(module);

        Assert.Single(host.Rows());
        module.PublishEvent("inbox.cleared");
        host.Settle();

        Assert.Empty(host.Rows());
        Assert.True(host.Find<Border>("EmptyState")!.IsVisible);
    }

    // ---------------------------------------------------------------- search + filters

    [AvaloniaFact]
    public void Search_filters_rows_and_reports_a_real_empty_state_with_a_way_back()
    {
        var module = new FakeNotificationsModule();
        module.Seed(
            TestRecords.Build("构建", "构建完成", DateTimeOffset.Now.AddMinutes(-30), "build"),
            TestRecords.Build("备份", "备份完成", DateTimeOffset.Now.AddMinutes(-20), "backup"),
            TestRecords.Build("部署", "部署完成", DateTimeOffset.Now.AddMinutes(-10), "deploy"));
        using var host = MobileNotificationHost.Open(module);

        var searchPanel = host.Find<Border>("SearchPanel")!;
        Assert.False(searchPanel.IsVisible);

        host.Click(host.Find<Button>("SearchToggle")!);
        Assert.True(searchPanel.IsVisible);

        var searchBox = host.Find<TextBox>("SearchBox")!;
        searchBox.Text = "备份";
        host.Settle();
        Assert.Single(host.Rows());

        searchBox.Text = "没有这个词";
        host.Settle();
        Assert.Empty(host.Rows());
        Assert.True(host.Find<Border>("EmptyState")!.IsVisible);
        Assert.Contains("没有匹配“没有这个词”的通知。", host.AllText());

        // The empty state offers a real next step: clear the search and see everything again.
        host.Click(host.Find<Button>("EmptyAction")!);
        Assert.Equal(3, host.Rows().Count);
    }

    [AvaloniaFact]
    public void Label_chip_filters_the_list_including_the_claude_task_page()
    {
        var module = new FakeNotificationsModule();
        module.Seed(
            TestRecords.Build("构建", "构建完成", DateTimeOffset.Now.AddMinutes(-30), "build"),
            TestRecords.Build("Claude Task", "任务已完成", DateTimeOffset.Now.AddMinutes(-20), "task"),
            TestRecords.Build("备份", "备份完成", DateTimeOffset.Now.AddMinutes(-10), "backup"));
        using var host = MobileNotificationHost.Open(module);

        // Claude Task messages are hidden from the default inbox, like the desktop surface.
        Assert.Equal(2, host.Rows().Count);

        host.Click(host.Chips().Single(chip => Equals(chip.Content, "备份")));
        Assert.Single(host.Rows());
        Assert.Contains("备份完成", host.AllText());

        host.Click(host.Chips().Single(chip => Equals(chip.Content, "Claude Task")));
        Assert.Single(host.Rows());
        Assert.Contains("任务已完成", host.AllText());

        host.Click(host.Chips().Single(chip => Equals(chip.Content, "全部")));
        Assert.Equal(2, host.Rows().Count);
    }

    // ---------------------------------------------------------------- detail sheet

    [AvaloniaFact]
    public void Detail_sheet_shows_the_full_body_and_quoted_block_and_closes_on_back()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build(
            "构建",
            "新版本已构建完成",
            DateTimeOffset.Now.AddMinutes(-5),
            "with-quote",
            quotedRequest: "帮我修复构建失败的问题"));
        using var host = MobileNotificationHost.Open(module);

        host.Click(host.Rows().Single());
        host.Settle();

        Assert.True(host.Find<Border>("SheetOverlay")!.IsVisible);
        var sheet = host.Find<StackPanel>("DetailSheet")!;
        Assert.True(sheet.IsVisible);

        var body = MobileNotificationHost.TextOf(sheet);
        Assert.Contains("新版本已构建完成", body);
        // The shipped reference block is preserved verbatim, not re-formatted for the phone.
        Assert.Contains("For reference:", body);
        Assert.Contains("> 帮我修复构建失败的问题", body);
        Assert.Contains("MacBook Pro", body);

        // Back is consumed by the page while the sheet is open, so the Shell does not leave the tool.
        Assert.True(host.View.TryHandleBack());
        host.Pump();
        Assert.False(host.Find<Border>("SheetOverlay")!.IsVisible);
        Assert.False(host.View.TryHandleBack());

        Assert.Contains(MobileNotificationTheme.OverlayClass, host.Find<Border>("SheetOverlay")!.Classes);
        Assert.Contains(MobileNotificationTheme.SheetClass, host.Find<Border>("SheetCard")!.Classes);
    }

    [AvaloniaFact]
    public void Escape_follows_the_same_back_path_as_the_host_key()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build("构建", "构建完成", DateTimeOffset.Now, "one"));
        using var host = MobileNotificationHost.Open(module);

        host.Click(host.Rows().Single());
        host.Settle();
        Assert.True(host.Find<Border>("SheetOverlay")!.IsVisible);

        host.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
        host.Pump();
        Assert.False(host.Find<Border>("SheetOverlay")!.IsVisible);
    }

    [AvaloniaFact]
    public void Detail_sheet_marks_the_message_read_and_offers_session_navigation()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build("构建", "第一条", DateTimeOffset.Now.AddMinutes(-20), "one", sessionId: "s-1", sessionName: "构建会话"));
        using var host = MobileNotificationHost.Open(module);

        module.Append(TestRecords.Build("构建", "第二条", DateTimeOffset.Now, "two", sessionId: "s-1", sessionName: "构建会话"));
        module.PublishEvent("message.received");
        host.Settle();

        var unreadRow = host.Rows().Single(row => host.UnreadDot(row)?.IsVisible == true);
        host.Click(unreadRow);
        host.Settle();

        var sheet = host.Find<StackPanel>("DetailSheet")!;
        Assert.Contains("第二条", MobileNotificationHost.TextOf(sheet));

        var markRead = host.Find<Button>("DetailMarkRead")!;
        Assert.Equal("标为已读", markRead.Content);
        host.Click(markRead);
        Assert.Equal("已读", markRead.Content);
        Assert.Contains("所有消息都已读完。", host.AllText());

        // The session chain is resolved through the shipped RemoteNotificationSessionChain.
        Assert.Contains("2 / 2", MobileNotificationHost.TextOf(sheet));
        var previous = host.Find<Button>("DetailPrevious")!;
        Assert.True(previous.IsEnabled);
        host.Click(previous);
        host.Settle();
        Assert.Contains("第一条", MobileNotificationHost.TextOf(sheet));
        Assert.Contains("1 / 2", MobileNotificationHost.TextOf(sheet));
    }

    // ---------------------------------------------------------------- empty state

    [AvaloniaFact]
    public void Empty_inbox_syncs_on_demand_from_the_empty_state()
    {
        var module = new FakeNotificationsModule();
        using var host = MobileNotificationHost.Open(module);

        Assert.Empty(host.Rows());
        Assert.True(host.Find<Border>("EmptyState")!.IsVisible);
        Assert.Contains("还没有收到通知", host.AllText());
        Assert.Equal("立即同步", host.Find<Button>("EmptyAction")!.Content);

        host.Click(host.Find<Button>("EmptyAction")!);
        Assert.Contains(FakeNotificationsModule.SyncCommand, module.Commands);
        // The real result is reported on the page header (the settings feedback panel is not realised
        // while settings is closed).
        Assert.Contains("没有新通知", host.AllText());
    }

    // ---------------------------------------------------------------- style contract

    [AvaloniaFact]
    public void Page_uses_the_mobile_class_contract_and_touch_target_sizes()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build("构建", "构建完成", DateTimeOffset.Now, "one"));
        using var host = MobileNotificationHost.Open(module);

        Assert.Contains(MobileNotificationTheme.RootClass, host.Find<Border>("PageFrame")!.Child!.Classes);

        var rows = host.Rows();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.True(row.MinHeight >= Token<double>(MobileNotificationTheme.TouchTargetMinKey)));
        Assert.All(rows, row => Assert.Contains(MobileNotificationTheme.ListRowClass, row.Classes));

        Assert.Contains(MobileNotificationTheme.SearchClass, host.Find<TextBox>("SearchBox")!.Classes);
        Assert.Contains(MobileNotificationTheme.SearchBoxClass, host.Find<Border>("SearchPanel")!.Classes);
        Assert.Contains(MobileNotificationTheme.IconButtonClass, host.Find<Button>("SettingsToggle")!.Classes);
        Assert.True(host.Find<Button>("SettingsToggle")!.MinHeight >= Token<double>(MobileNotificationTheme.TouchTargetMinKey));
    }

    [AvaloniaFact]
    public void Page_is_styled_by_the_shipped_sdk_mobile_theme_not_a_private_palette()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build("构建", "构建完成", DateTimeOffset.Now, "one"));
        using var host = MobileNotificationHost.Open(module);

        // Metrics and typography must equal the SDK tokens, which only hold if the real
        // MptMobileTheme.axaml styles are loaded and this page only adds the agreed classes.
        var rowMinHeight = Token<double>(MobileNotificationTheme.ListRowMinHeightKey);
        Assert.Equal(rowMinHeight, host.Rows().Single().MinHeight);
        var iconButtonSize = Token<double>(MobileNotificationTheme.IconButtonSizeKey);
        Assert.Equal(iconButtonSize, host.Find<Button>("SettingsToggle")!.MinHeight);
        var pageTitleSize = Token<double>(MobileNotificationTheme.PageTitleFontSizeKey);
        Assert.Equal(pageTitleSize, host.Find<Border>("InboxHeader")!
            .GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "值得你看一眼。").FontSize);

        // The contract brushes really are SDK resources.
        Assert.True(MobileNotificationTheme.TryGetResource<IBrush>(MobileNotificationTheme.CardBrushKey, out _));
        Assert.True(MobileNotificationTheme.TryGetResource<IBrush>(MobileNotificationTheme.AccentBrushKey, out _));
    }

    [AvaloniaFact]
    public void Attached_page_follows_a_light_dark_light_theme_switch_in_place()
    {
        var module = new FakeNotificationsModule();
        module.Seed(TestRecords.Build("构建", "构建完成", DateTimeOffset.Now, "one"));
        using var host = MobileNotificationHost.Open(module);

        var frame = host.Find<Border>("PageFrame")!;
        var notice = host.Find<Border>("KeyWarningCard")!;
        var listCard = host.View.GetVisualDescendants().OfType<Border>()
            .First(border => border.Classes.Contains(MobileNotificationTheme.ListCardClass));

        SetVariant(ThemeVariant.Light);
        AssertBrushMatchesToken(frame.Background, MobileNotificationTheme.BackgroundBrushKey);
        AssertBrushMatchesToken(listCard.Background, MobileNotificationTheme.CardBrushKey);
        AssertBrushMatchesToken(notice.Background, MobileNotificationTheme.AccentSoftBrushKey);
        var lightBackground = Solid(frame.Background);
        var lightCard = Solid(listCard.Background);

        SetVariant(ThemeVariant.Dark);
        AssertBrushMatchesToken(frame.Background, MobileNotificationTheme.BackgroundBrushKey);
        AssertBrushMatchesToken(listCard.Background, MobileNotificationTheme.CardBrushKey);
        AssertBrushMatchesToken(notice.Background, MobileNotificationTheme.AccentSoftBrushKey);
        Assert.NotEqual(lightBackground, Solid(frame.Background));
        Assert.NotEqual(lightCard, Solid(listCard.Background));

        SetVariant(ThemeVariant.Light);
        AssertBrushMatchesToken(frame.Background, MobileNotificationTheme.BackgroundBrushKey);
        AssertBrushMatchesToken(listCard.Background, MobileNotificationTheme.CardBrushKey);
        Assert.Equal(lightBackground, Solid(frame.Background));
        Assert.Equal(lightCard, Solid(listCard.Background));

        void SetVariant(ThemeVariant variant)
        {
            Application.Current!.RequestedThemeVariant = variant;
            host.Window.RequestedThemeVariant = variant;
            host.Settle();
        }

        void AssertBrushMatchesToken(IBrush? actual, string tokenKey) =>
            Assert.Equal(Solid(Token<IBrush>(tokenKey, host.Window.ActualThemeVariant)), Solid(actual));
    }

    private static T Token<T>(string key, ThemeVariant? variant = null)
    {
        var application = Application.Current!;
        var effective = variant ?? application.ActualThemeVariant;
        Assert.True(application.TryFindResource(key, effective, out var value), $"missing SDK token {key}");
        return Assert.IsAssignableFrom<T>(value);
    }

    private static Color Solid(IBrush? brush) => Assert.IsType<SolidColorBrush>(brush).Color;


    [AvaloniaFact]
    public void Narrow_phone_width_switches_to_the_sdk_narrow_page_padding()
    {
        var wideModule = new FakeNotificationsModule();
        wideModule.Seed(TestRecords.Build("构建", "构建完成", DateTimeOffset.Now, "one"));
        using (var wide = MobileNotificationHost.Open(wideModule))
        {
            var frame = wide.Find<Border>("PageFrame")!;
            Assert.False(frame.Classes.Contains(MobileNotificationTheme.PageNarrowClass));
            Assert.Equal(Token<Thickness>(MobileNotificationTheme.PagePaddingKey).Left, frame.Padding.Left);
        }

        var narrowModule = new FakeNotificationsModule();
        narrowModule.Seed(TestRecords.Build("构建", "构建完成", DateTimeOffset.Now, "one"));
        using var narrow = MobileNotificationHost.Open(narrowModule, width: 320);
        var narrowFrame = narrow.Find<Border>("PageFrame")!;
        Assert.True(narrowFrame.Classes.Contains(MobileNotificationTheme.PageNarrowClass));
        Assert.Equal(Token<Thickness>(MobileNotificationTheme.PagePaddingNarrowKey).Left, narrowFrame.Padding.Left);
    }

    // ---------------------------------------------------------------- rendered evidence

    [AvaloniaFact]
    public void Renders_phone_frames_for_visual_review()
    {
        var module = new FakeNotificationsModule();
        module.Seed(
            TestRecords.Build("备份", "昨天的备份已完成", LocalTime(DateTime.Today.AddDays(-1).AddHours(18)), "older"),
            TestRecords.Build("构建", "新版本已构建完成", LocalTime(DateTime.Today.AddHours(9)), "newer", quotedRequest: "修复构建失败"));
        using var light = MobileNotificationHost.Open(module, "light");
        var inbox = light.Capture("inbox-light");
        light.Click(light.Rows().First());
        var detail = light.Capture("detail-light");
        light.View.TryHandleBack();
        light.Pump();
        light.Click(light.Find<Button>("SettingsToggle")!);
        var settings = light.Capture("settings-light");

        using var dark = MobileNotificationHost.Open(module, "dark");
        var darkInbox = dark.Capture("inbox-dark");

        foreach (var path in new[] { inbox, detail, settings, darkInbox })
        {
            Assert.True(File.Exists(path), path);
            Assert.True(new FileInfo(path).Length > 1024, path);
        }
    }
}
