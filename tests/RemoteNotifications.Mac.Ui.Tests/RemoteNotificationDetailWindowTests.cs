using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using RemoteNotifications.Surface.Services;
using RemoteNotifications.Surface.ViewModels;
using RemoteNotifications.Surface.Views;
using Xunit;

namespace RemoteNotifications.Mac.Ui.Tests;

public sealed class RemoteNotificationDetailWindowTests
{
    [AvaloniaFact]
    public void Construction_injects_store_before_lookup_and_does_not_create_a_renderer()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider();
        var window = Create(store, provider);
        try
        {
            Assert.Same(store, window.SessionStore);
            Assert.Equal(1, store.LoadCount);
            Assert.Empty(provider.Requests);
            Assert.Null(window.FindControl<ContentControl>("DocumentHost")!.Content);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Open_uses_window_scoped_host_and_renders_markdown()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider();
        var window = Create(store, provider);
        try
        {
            window.Show();
            var request = Assert.Single(provider.Requests);
            Assert.Equal(0, provider.PageSessionCalls);
            Assert.True(request.Source.IsFile);
            Assert.Contains("<strong>message 0</strong>", File.ReadAllText(request.Source.LocalPath));
            Assert.Same(provider.Sessions[0].View, window.FindControl<ContentControl>("DocumentHost")!.Content);
            provider.Sessions[0].Raise(MptWebSurfaceState.Ready);
            Pump();
            Assert.False(window.FindControl<ScrollViewer>("FallbackViewer")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Creation_failure_removes_temp_document_and_preserves_readable_message()
    {
        var provider = new FakeProvider { ThrowOnCreate = true };
        var window = Create(new MemoryStore(), provider);
        try
        {
            window.Show();
            Pump();
            Assert.True(window.IsVisible);
            Assert.True(window.FindControl<ScrollViewer>("FallbackViewer")!.IsVisible);
            Assert.Null(window.FindControl<ContentControl>("DocumentHost")!.Content);
            Assert.False(File.Exists(Assert.Single(provider.Requests).Source.LocalPath));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Asynchronous_failure_detaches_disposes_and_removes_document()
    {
        var provider = new FakeProvider();
        var window = Create(new MemoryStore(), provider);
        try
        {
            window.Show();
            provider.Sessions[0].Raise(MptWebSurfaceState.Failed);
            Pump();
            Assert.True(window.IsVisible);
            Assert.Null(window.FindControl<ContentControl>("DocumentHost")!.Content);
            Assert.True(window.FindControl<ScrollViewer>("FallbackViewer")!.IsVisible);
            Assert.Equal(1, provider.Sessions[0].DisposeCount);
            Assert.False(File.Exists(provider.Requests[0].Source.LocalPath));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Close_before_ready_ignores_queued_callbacks_and_disposes_once()
    {
        var provider = new FakeProvider();
        var window = Create(new MemoryStore(), provider);
        window.Show();
        provider.Sessions[0].Raise(MptWebSurfaceState.Ready);
        window.Close();
        Pump();
        Assert.False(window.IsVisible);
        Assert.Equal(1, provider.Sessions[0].DisposeCount);
        Assert.False(File.Exists(provider.Requests[0].Source.LocalPath));
    }

    [AvaloniaFact]
    public void Session_navigation_uses_injected_store_and_releases_previous_render()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider();
        var window = Create(store, provider);
        try
        {
            window.Show();
            window.NavigateNext();
            Pump();
            Assert.Equal("message-1", Assert.IsType<RemoteNotificationMessageViewModel>(window.DataContext).Id);
            Assert.Equal(1, provider.Sessions[0].DisposeCount);
            Assert.False(File.Exists(provider.Requests[0].Source.LocalPath));
            window.NavigatePrevious();
            Assert.Equal("message-0", Assert.IsType<RemoteNotificationMessageViewModel>(window.DataContext).Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Json_string_bridge_navigates_once_and_stale_commands_are_ignored()
    {
        var provider = new FakeProvider();
        var window = Create(new MemoryStore(), provider);
        try
        {
            window.Show();
            var previous = provider.Requests[0];
            await previous.HandleBridgeRequestAsync!(JsonSerializer.Serialize("next"), default);
            Pump();
            Assert.Equal("message-1", Assert.IsType<RemoteNotificationMessageViewModel>(window.DataContext).Id);
            await previous.HandleBridgeRequestAsync!(JsonSerializer.Serialize("next"), default);
            Pump();
            Assert.Equal("message-1", Assert.IsType<RemoteNotificationMessageViewModel>(window.DataContext).Id);
            await provider.Requests[^1].HandleBridgeRequestAsync!(JsonSerializer.Serialize("close"), default);
            Pump();
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Queued_command_from_replaced_document_cannot_close_new_document()
    {
        var provider = new FakeProvider();
        var window = Create(new MemoryStore(), provider);
        try
        {
            window.Show();
            // The callback returns synchronously after enqueueing the UI operation.
            var task = provider.Requests[0].HandleBridgeRequestAsync!("\"close\"", default);
            Assert.True(task.IsCompletedSuccessfully);
            window.NavigateNext();
            Pump();
            Assert.True(window.IsVisible);
            Assert.Equal("message-1", Assert.IsType<RemoteNotificationMessageViewModel>(window.DataContext).Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Theme_change_rebuilds_the_document_and_releases_the_old_session()
    {
        var provider = new FakeProvider();
        var window = Create(new MemoryStore(), provider);
        try
        {
            window.RequestedThemeVariant = ThemeVariant.Light;
            window.Show();
            window.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();
            Assert.True(provider.Requests.Count >= 2);
            Assert.Equal(1, provider.Sessions[0].DisposeCount);
            Assert.Contains("data-theme=\"dark\"", File.ReadAllText(provider.Requests[^1].Source.LocalPath));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Unreadable_session_history_does_not_prevent_detail_opening()
    {
        var store = new MemoryStore { ThrowOnLoad = true };
        var provider = new FakeProvider();
        var window = Create(store, provider);
        try
        {
            window.Show();
            Assert.True(window.IsVisible);
            Assert.Single(provider.Sessions);
            Assert.False(Assert.IsType<RemoteNotificationMessageViewModel>(window.DataContext).HasSessionPosition);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Tool_disposal_closes_owned_details_and_allows_repeated_open_close()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider();
        var service = new RemoteNotificationDetailWindowService(store, provider);
        var view = new RemoteNotificationsView(service);
        try
        {
            for (var i = 0; i < 5; i++)
            {
                Assert.True(service.Open(new RemoteNotificationMessageViewModel(store.Records[0])));
                Assert.Single(service.OpenWindows).Close();
                Assert.Empty(service.OpenWindows);
            }
            service.Open(new RemoteNotificationMessageViewModel(store.Records[0]));
            view.Dispose();
            Pump();
            Assert.Empty(service.OpenWindows);
            Assert.All(provider.Sessions, session => Assert.Equal(1, session.DisposeCount));
            Assert.All(provider.Requests, request => Assert.False(File.Exists(request.Source.LocalPath)));
        }
        finally { view.Dispose(); service.Dispose(); }
    }

    [AvaloniaFact]
    public void Reopening_original_message_resets_a_reused_navigated_window()
    {
        var store = new MemoryStore();
        var provider = new FakeProvider();
        using var service = new RemoteNotificationDetailWindowService(store, provider);
        service.Open(new RemoteNotificationMessageViewModel(store.Records[0]));
        var window = Assert.Single(service.OpenWindows);
        window.NavigateNext();
        service.Open(new RemoteNotificationMessageViewModel(store.Records[0]));
        Assert.Same(window, Assert.Single(service.OpenWindows));
        Assert.Equal("message-0", Assert.IsType<RemoteNotificationMessageViewModel>(window.DataContext).Id);
    }

    [Fact]
    public void Markdown_preserves_formatting_and_encodes_label_and_raw_html()
    {
        var html = RemoteNotificationHtmlDocument.Build("<label>",
            "# Heading\n\n**bold**\n\n```text\ncode\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |\n\n<script>alert(1)</script>", false);
        Assert.Contains("&lt;label&gt;", html);
        Assert.Contains("<strong>bold</strong>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<pre><code", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.DoesNotContain("messageHandlers.close", html);
        Assert.Contains("messageHandlers.mptBridge", html);
    }

    [AvaloniaFact]
    public void Remote_markdown_image_origin_is_retained_in_host_policy()
    {
        var provider = new FakeProvider();
        using var document = new RemoteNotificationHostedDocument(provider,
            RemoteNotificationHtmlDocument.Build("", "![image](https://images.example.test/a.png)", false), _ => { });
        Assert.Contains(new Uri("https://images.example.test/"), Assert.Single(provider.Requests).AllowedOrigins);
    }

    private static RemoteNotificationDetailWindow Create(MemoryStore store, FakeProvider provider) =>
        new(new RemoteNotificationMessageViewModel(store.Records[0]), store, provider);
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private sealed class FakeProvider : IMptWindowWebSurfaceService
    {
        public List<MptWebSurfaceRequest> Requests { get; } = [];
        public List<FakeSession> Sessions { get; } = [];
        public bool ThrowOnCreate { get; init; }
        public int PageSessionCalls { get; private set; }
        public IMptWebSurfaceSession CreateSession(MptWebSurfaceRequest request)
        {
            PageSessionCalls++;
            return CreateWindowSession(request);
        }
        public IMptWebSurfaceSession CreateWindowSession(MptWebSurfaceRequest request)
        {
            Requests.Add(request);
            if (ThrowOnCreate) throw new InvalidOperationException("Simulated native creation failure.");
            var session = new FakeSession();
            Sessions.Add(session);
            return session;
        }
    }
    private sealed class FakeSession : IMptWebSurfaceSession
    {
        public Control View { get; } = new Border();
        public MptWebSurfaceState State { get; private set; } = MptWebSurfaceState.Loading;
        public event EventHandler<MptWebSurfaceStateChangedEventArgs>? StateChanged;
        public int DisposeCount { get; private set; }
        public void Reload() { }
        public void Dispose() => DisposeCount++;
        public void Raise(MptWebSurfaceState state)
        {
            State = state;
            StateChanged?.Invoke(this, new MptWebSurfaceStateChangedEventArgs(state, "test"));
        }
    }
    private sealed class MemoryStore : IRemoteNotificationsStore
    {
        public RemoteNotificationRecord[] Records { get; } = Enumerable.Range(0, 3).Select(index =>
            new RemoteNotificationRecord($"message-{index}", "default", $"[project] **message {index}**", "info",
                DateTimeOffset.Parse("2026-09-13T00:00:00Z").AddMinutes(index).ToString("O"),
                DateTimeOffset.Parse("2026-09-13T00:00:00Z").AddMinutes(index).ToString("O"),
                "session", "project", "detail-test")).ToArray();
        public int LoadCount { get; private set; }
        public bool ThrowOnLoad { get; init; }
        public RemoteNotificationsSnapshot Load()
        {
            LoadCount++;
            if (ThrowOnLoad) throw new IOException("Simulated store failure.");
            return new RemoteNotificationsSnapshot(Records, ["project"], null, false, []);
        }
        public void SaveMessages(IReadOnlyList<RemoteNotificationRecord> messagesOldestFirst) { }
        public void SaveFilter(string? label) { }
        public void SaveKnownLabels(IReadOnlyList<string> labels) { }
        public void SavePersistentWindowsToasts(bool enabled) { }
        public void SaveSeenMessageIds(IReadOnlyList<string> messageIdsOldestFirst) { }
        public void ClearMessages() { }
    }
}
