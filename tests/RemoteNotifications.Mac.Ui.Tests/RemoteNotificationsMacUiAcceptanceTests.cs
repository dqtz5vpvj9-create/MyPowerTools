using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RemoteNotifications.Surface.Services;
using RemoteNotifications.Surface.ViewModels;
using RemoteNotifications.Surface.Views;
using Xunit;

namespace RemoteNotifications.Mac.Ui.Tests;

public sealed class RemoteNotificationsMacUiAcceptanceTests
{
    [AvaloniaFact]
    public void Narrow_inbox_uses_hamburger_actions_and_one_horizontal_project_strip()
    {
        var labels = Enumerable.Range(1, 24)
            .Select(index => $"project-{index:00}-long-session-name")
            .ToArray();
        var messages = labels
            .Select((label, index) => new RemoteNotificationRecord(
                $"message-{index:00}",
                "default",
                $"[{label}] production notification {index:00}",
                "info",
                DateTimeOffset.UtcNow.AddMinutes(-index).ToString("O"),
                DateTimeOffset.UtcNow.AddMinutes(-index).ToString("O"),
                $"session-{index:00}",
                label,
                "mac-ui-test"))
            .Reverse()
            .ToArray();
        var snapshot = new RemoteNotificationsSnapshot(messages, labels, null, false, []);
        var store = new MemoryStore(snapshot);
        var viewModel = new RemoteNotificationsViewModel(
            snapshot,
            store,
            new RemoteNotificationNoopPoller());
        var view = new RemoteNotificationsView
        {
            DataContext = viewModel
        };
        var window = new Window
        {
            Width = 640,
            Height = 720,
            Content = view
        };

        try
        {
            window.Show();
            RunLayout(window);

            var headerActions = Assert.IsAssignableFrom<Control>(
                view.FindControl<Control>("HeaderActions"));
            var connection = Assert.IsAssignableFrom<Control>(
                view.FindControl<Control>("ConnectionExpander"));
            var overflow = Assert.IsAssignableFrom<Control>(
                view.FindControl<Control>("OverflowMenuButton"));
            Assert.False(headerActions.IsVisible);
            Assert.False(connection.IsVisible);
            Assert.True(overflow.IsVisible);

            var scroller = Assert.IsType<ScrollViewer>(
                view.FindControl<ScrollViewer>("LabelScroller"));
            Assert.Equal(ScrollBarVisibility.Hidden, scroller.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.VerticalScrollBarVisibility);
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width,
                $"Expected horizontal overflow, extent={scroller.Extent.Width}, viewport={scroller.Viewport.Width}.");
            Assert.InRange(scroller.Bounds.Height, 1, 64);

            var items = Assert.IsType<ItemsControl>(scroller.Content);
            var horizontalPanel = items.GetVisualDescendants()
                .OfType<StackPanel>()
                .FirstOrDefault(panel => panel.Orientation == Orientation.Horizontal);
            Assert.NotNull(horizontalPanel);
            Assert.Empty(items.GetVisualDescendants().OfType<WrapPanel>());
            Assert.Equal(25, viewModel.Chips.Count);

            window.Width = 1200;
            RunLayout(window);

            Assert.True(headerActions.IsVisible);
            Assert.True(connection.IsVisible);
            Assert.False(overflow.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static void RunLayout(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class MemoryStore(RemoteNotificationsSnapshot snapshot) : IRemoteNotificationsStore
    {
        private RemoteNotificationsSnapshot _snapshot = snapshot;

        public RemoteNotificationsSnapshot Load() => _snapshot;

        public void SaveMessages(IReadOnlyList<RemoteNotificationRecord> messagesOldestFirst)
        {
            _snapshot = _snapshot with { MessagesOldestFirst = messagesOldestFirst.ToArray() };
        }

        public void SaveFilter(string? label)
        {
            _snapshot = _snapshot with { FilterLabel = label };
        }

        public void SaveKnownLabels(IReadOnlyList<string> labels)
        {
            _snapshot = _snapshot with { KnownLabels = labels.ToArray() };
        }

        public void SavePersistentWindowsToasts(bool enabled)
        {
            _snapshot = _snapshot with { PersistentWindowsToasts = enabled };
        }

        public void SaveSeenMessageIds(IReadOnlyList<string> messageIdsOldestFirst)
        {
            _snapshot = _snapshot with { SeenMessageIds = messageIdsOldestFirst.ToArray() };
        }

        public void ClearMessages()
        {
            _snapshot = _snapshot with { MessagesOldestFirst = [] };
        }
    }
}
