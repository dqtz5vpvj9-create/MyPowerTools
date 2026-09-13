using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyPowerTools.Abstractions;
using MyPowerTools.AvaloniaSdk;

namespace MyPowerTools.Shell.Avalonia;

/// <summary>
/// Installs diagnostic observers at the managed entry point. Observers never mark unknown
/// UI exceptions as handled and never attempt to recover from corrupted native state.
/// </summary>
internal static class ShellDiagnostics
{
    private static readonly DiagnosticTraceListener TraceSink = new();
    private static Dispatcher? _dispatcher;
    private static Window? _notice;
    internal static DiagnosticSession? Current { get; private set; }

    public static int Run(Func<int> action, bool captureNativeStderr = true)
    {
        Current = DiagnosticSession.TryStart(MptLogRedactor.Redact, captureNativeStderr);
        Trace.Listeners.Add(TraceSink);
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        MptCommandFaultBoundary.FaultObserved += OnCommandFault;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) Current?.RecordAssembly(assembly);
        try
        {
            var result = action();
            Current?.Complete(result);
            Uninstall();
            return result;
        }
        catch (Exception ex)
        {
            Current?.Write("main.unhandled", "The process will terminate.", ex, fatal: true);
            // Keep stderr redirected until the runtime emits its final fatal diagnostics.
            // In particular, do not close the native log in a finally block here.
            throw;
        }
    }

    public static void BeginSession() => Current?.BeginSession();

    public static void AttachToUi()
    {
        if (_dispatcher is not null) return;
        _dispatcher = Dispatcher.UIThread;
        _dispatcher.UnhandledException += OnUiUnhandledException;
        Current?.Write("ui.ready");
        if (Current?.PreviousExits.Count > 0)
        {
            _dispatcher.Post(() => ShowNotice(
                "Previous MyPowerTools session ended unexpectedly",
                "The previous session did not finish normally. This can result from a crash, force quit, or system shutdown. Diagnostic files were retained.",
                acknowledgePrevious: true));
        }
    }

    private static void OnUiUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Current?.Write("ui.unhandled", "Observed only; Handled is not changed.", e.Exception);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Current?.Write("runtime.unhandled", $"isTerminating={e.IsTerminating}",
            e.ExceptionObject as Exception, fatal: e.IsTerminating);
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Current?.Write("task.unobserved", "Observation depends on garbage collection; this is not a substitute for awaiting tasks.", e.Exception);
    }

    private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs e) => Current?.RecordAssembly(e.LoadedAssembly);

    private static void OnCommandFault(string operation, Exception exception)
    {
        Current?.Write("command.failed", operation, exception);
        // The command boundary already contained this failure. Let the user see it as well.
        if (_dispatcher is not null)
        {
            try { _dispatcher.Post(() => ShowNotice("MyPowerTools operation failed", operation)); }
            catch (Exception ex) { Current?.Write("diagnosticUi.queue.failed", null, ex); }
        }
    }

    private static void ShowNotice(string title, string message, bool acknowledgePrevious = false)
    {
        try
        {
            if (_notice is not null) { _notice.Activate(); return; }
            var session = Current;
            var paths = acknowledgePrevious && session is not null
                ? string.Join("\n", session.PreviousExits.TakeLast(3).Select(item => item.LogPath + "\n" + item.StderrPath))
                : session?.LogPath ?? "Persistent diagnostics could not be opened. Check the process standard error output.";
            var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var window = new Window { Title = title, Width = 760, Height = 410, MinWidth = 520, MinHeight = 280 };
            var openLogs = new Button { Content = "Open diagnostic logs", IsEnabled = session is not null };
            openLogs.Click += (_, _) => OpenFolder(session!.DirectoryPath, status);
            actions.Children.Add(openLogs);
            if (OperatingSystem.IsMacOS())
            {
                var reports = new Button { Content = "Open macOS crash reports" };
                reports.Click += (_, _) => OpenFolder(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "DiagnosticReports"), status);
                actions.Children.Add(reports);
            }
            var close = new Button { Content = "Dismiss" };
            close.Click += (_, _) => window.Close();
            actions.Children.Add(close);
            var panel = new StackPanel { Margin = new global::Avalonia.Thickness(20), Spacing = 16 };
            panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new SelectableTextBlock { Text = paths, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(actions);
            panel.Children.Add(status);
            window.Content = new ScrollViewer { Content = panel };
            window.Closed += (_, _) =>
            {
                _notice = null;
                if (acknowledgePrevious) session?.AcknowledgePreviousExits();
            };
            _notice = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _notice = null;
            Current?.Write("diagnosticUi.show.failed", title, ex);
        }
    }

    private static void OpenFolder(string path, TextBlock status)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            status.Text = $"Could not open {path}: {ex.Message}";
            Current?.Write("diagnosticUi.open.failed", path, ex);
        }
    }

    private static void Uninstall()
    {
        MptCommandFaultBoundary.FaultObserved -= OnCommandFault;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        if (_dispatcher is not null) _dispatcher.UnhandledException -= OnUiUnhandledException;
        _dispatcher = null;
        Trace.Listeners.Remove(TraceSink);
        Current?.Dispose();
        Current = null;
    }

    private sealed class DiagnosticTraceListener : TraceListener
    {
        public override bool IsThreadSafe => true;
        public override void Write(string? message) => Current?.Write("trace", message);
        public override void WriteLine(string? message) => Current?.Write("trace", message);
    }
}
