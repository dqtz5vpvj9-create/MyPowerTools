using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using RemoteNotifications.Surface.Services;
using RemoteNotifications.Surface.ViewModels;
using Xunit;

namespace RemoteNotifications.Mac.Ui.Tests;

public sealed class RemoteNotificationDiagnosticsTests
{
    [AvaloniaFact]
    public void Failed_viewer_exposes_log_path_and_lifecycle_without_notification_contents()
    {
        const string variable = "MPT_SHELL_DIAGNOSTIC_LOG";
        const string expectedPath = "/tmp/mpt-diagnostic-test.jsonl";
        var previous = Environment.GetEnvironmentVariable(variable);
        using var trace = new StringWriter();
        using var listener = new TextWriterTraceListener(trace);
        Trace.Listeners.Add(listener);
        var store = new EmptyStore();
        using var service = new RemoteNotificationDetailWindowService(store, new FailedProvider());
        try
        {
            Environment.SetEnvironmentVariable(variable, expectedPath);
            var message = new RemoteNotificationMessageViewModel(new RemoteNotificationRecord(
                "private-raw-id", "default", "private-notification-body", "info", DateTimeOffset.UtcNow.ToString("O")));
            Assert.True(service.Open(message));
            Dispatcher.UIThread.RunJobs();
            var window = Assert.Single(service.OpenWindows);
            Assert.True(window.FindControl<ScrollViewer>("FallbackViewer")!.IsVisible);
            Assert.Contains(expectedPath, window.FindControl<TextBlock>("FallbackStatus")!.Text);
            window.Close();
            Trace.Flush();
            var log = trace.ToString();
            Assert.Contains("phase=open.request", log);
            Assert.Contains("phase=construct.begin", log);
            Assert.Contains("phase=render.begin", log);
            Assert.Contains("phase=renderer.create", log);
            Assert.Contains("phase=renderer.fallback", log);
            Assert.Contains("phase=window.closed", log);
            Assert.Contains("selectedMessageHash=", log);
            Assert.DoesNotContain("private-raw-id", log);
            Assert.DoesNotContain("private-notification-body", log);
        }
        finally
        {
            service.Dispose();
            Environment.SetEnvironmentVariable(variable, previous);
            Trace.Listeners.Remove(listener);
        }
    }

    private sealed class FailedProvider : IMptWebSurfaceService
    {
        public IMptWebSurfaceSession CreateSession(MptWebSurfaceRequest request) =>
            throw new InvalidOperationException("Intentional renderer initialization failure.");
    }

    private sealed class EmptyStore : IRemoteNotificationsStore
    {
        public RemoteNotificationsSnapshot Load() => new([], [], null, false);
        public void SaveMessages(IReadOnlyList<RemoteNotificationRecord> messagesOldestFirst) { }
        public void SaveFilter(string? label) { }
        public void SaveKnownLabels(IReadOnlyList<string> labels) { }
        public void SavePersistentWindowsToasts(bool enabled) { }
        public void SaveSeenMessageIds(IReadOnlyList<string> messageIdsOldestFirst) { }
        public void ClearMessages() { }
    }
}
