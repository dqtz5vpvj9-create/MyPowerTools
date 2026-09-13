using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.WebSurface.Avalonia;
using RemoteNotifications.Surface.Services;
using RemoteNotifications.Surface.ViewModels;

namespace RemoteNotifications.Mac.NativeSmoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("This smoke test requires macOS and real WKWebView.");
            return 2;
        }
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.AutoFlush = true;
        AppBuilder.Configure<SmokeApp>().UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        return SmokeApp.ExitCode;
    }
}

public sealed class SmokeApp : Application
{
    public static int ExitCode { get; private set; } = 1;
    public override void Initialize() => Styles.Add(new FluentTheme());
    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Dispatcher.UIThread.Post(() => { _ = RunAsync(desktop); });
    }

    private static async Task RunAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var provider = new TrackingProvider();
            var store = new MemoryStore();
            using var details = new RemoteNotificationDetailWindowService(store, provider);
            for (var cycle = 0; cycle < 3; cycle++)
            {
                details.Open(new RemoteNotificationMessageViewModel(store.Messages[0]));
                var window = details.OpenWindows.Single();
                await provider.WaitForReadyAsync();
                Require(window.IsVisible, "Detail window is not visible.");
                Require(!window.FindControl<ScrollViewer>("FallbackViewer")!.IsVisible,
                    "The real native viewer fell back to plain text.");
                Require(File.ReadAllText(provider.LatestPath).Contains("<table>"), "Markdown table is missing.");

                window.NavigateNext();
                await provider.WaitForReadyAsync();
                Require(((RemoteNotificationMessageViewModel)window.DataContext!).Id == "native-1", "Next failed.");
                window.NavigatePrevious();
                await provider.WaitForReadyAsync();
                Require(((RemoteNotificationMessageViewModel)window.DataContext!).Id == "native-0", "Previous failed.");
                window.RequestedThemeVariant = ThemeVariant.Dark;
                await provider.WaitForReadyAsync();
                Require(File.ReadAllText(provider.LatestPath).Contains("data-theme=\"dark\""), "Theme was not applied.");
                window.Close();
                await Task.Delay(100); // Allow Avalonia's queued native-handle destruction to execute.
                Require(details.OpenWindows.Count == 0, "Closed window is retained by its service.");
                Require(provider.Paths.All(path => !File.Exists(path)), "A closed document leaked its temporary file.");
                Console.WriteLine($"WKWebView cycle {cycle + 1}: load, navigation, theme, close PASS");
            }

            // Exercise a close while navigation and native callbacks can still be pending.
            details.Open(new RemoteNotificationMessageViewModel(store.Messages[0]));
            details.OpenWindows.Single().Close();
            await Task.Delay(500);
            Require(provider.Paths.All(path => !File.Exists(path)), "Early close leaked a document.");
            Require(provider.Sessions.All(session => session.DisposeCount == 1), "A native session was not disposed exactly once.");
            Console.WriteLine($"PASS: real WKWebView, {provider.Sessions.Count} sessions, no plain-text fallback; early close completed.");
            ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            ExitCode = 1;
        }
        finally { desktop.Shutdown(ExitCode); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TrackingProvider : IMptWindowWebSurfaceService
    {
        private readonly MacWebSurfaceService _native = new(new WebSurfaceOcclusionState());
        public List<string> Paths { get; } = [];
        public List<TrackingSession> Sessions { get; } = [];
        public string LatestPath => Paths[^1];
        public IMptWebSurfaceSession CreateSession(MptWebSurfaceRequest request) =>
            throw new InvalidOperationException("An independent detail must use CreateWindowSession.");
        public IMptWebSurfaceSession CreateWindowSession(MptWebSurfaceRequest request)
        {
            Paths.Add(request.Source.LocalPath);
            var session = new TrackingSession(_native.CreateWindowSession(request));
            Sessions.Add(session);
            return session;
        }
        public async Task WaitForReadyAsync()
        {
            await Sessions[^1].Ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
            // The detail consumes host-state changes on its next dispatcher turn.
            await Task.Delay(50);
        }
    }

    private sealed class TrackingSession : IMptWebSurfaceSession
    {
        private readonly IMptWebSurfaceSession _inner;
        public TrackingSession(IMptWebSurfaceSession inner)
        {
            _inner = inner;
            _inner.StateChanged += OnStateChanged;
        }
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public Control View => _inner.View;
        public MptWebSurfaceState State => _inner.State;
        public event EventHandler<MptWebSurfaceStateChangedEventArgs>? StateChanged;
        public void Reload() => _inner.Reload();
        private void OnStateChanged(object? sender, MptWebSurfaceStateChangedEventArgs args)
        {
            StateChanged?.Invoke(this, args);
            if (args.State == MptWebSurfaceState.Ready) Ready.TrySetResult(true);
            else if (args.State is MptWebSurfaceState.Failed or MptWebSurfaceState.Unavailable)
                Ready.TrySetException(new InvalidOperationException($"WKWebView {args.State}: {args.Message}"));
        }
        public void Dispose()
        {
            DisposeCount++;
            _inner.StateChanged -= OnStateChanged;
            _inner.Dispose();
        }
    }

    private sealed class MemoryStore : IRemoteNotificationsStore
    {
        public RemoteNotificationRecord[] Messages { get; } = Enumerable.Range(0, 2).Select(index =>
            new RemoteNotificationRecord($"native-{index}", "default",
                $"[smoke] # Message {index}\n\n**中文 Markdown**\n\n| A | B |\n|---|---|\n| 1 | 2 |",
                "info", $"2026-09-13T00:0{index}:00Z", $"2026-09-13T00:0{index}:00Z",
                "native-session", "smoke", "native-smoke")).ToArray();
        public RemoteNotificationsSnapshot Load() => new(Messages, ["smoke"], null, false, []);
        public void SaveMessages(IReadOnlyList<RemoteNotificationRecord> messagesOldestFirst) { }
        public void SaveFilter(string? label) { }
        public void SaveKnownLabels(IReadOnlyList<string> labels) { }
        public void SavePersistentWindowsToasts(bool enabled) { }
        public void SaveSeenMessageIds(IReadOnlyList<string> messageIdsOldestFirst) { }
        public void ClearMessages() { }
    }
}
