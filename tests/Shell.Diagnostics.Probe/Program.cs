using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using MyPowerTools.AvaloniaSdk;
using MyPowerTools.Shell.Avalonia;

internal static class Probe
{
    [STAThread]
    public static int Main(string[] args) => ShellDiagnostics.Run(() => Execute(args.Single()));

    private static int Execute(string mode)
    {
        ShellDiagnostics.BeginSession();
        var session = ShellDiagnostics.Current ?? throw new IOException("Diagnostic session was not created.");
        Console.WriteLine(JsonSerializer.Serialize(new { logPath = session.LogPath, stderrPath = session.StderrPath, previous = session.PreviousExits.Count }));
        Console.Out.Flush();
        Trace.WriteLine("probe.before-failure");
        switch (mode)
        {
            case "clean": return 0;
            case "recoverable":
                MptCommandFaultBoundary.Run(null, "probe.recoverable", (Action)ThrowWithInner);
                Trace.WriteLine("probe.after-recovery");
                return 0;
            case "main": ThrowWithInner(); return 3;
            case "thread":
                var thread = new Thread(ThrowWithInner);
                thread.Start();
                thread.Join();
                return 3;
            case "ui":
                SetupUi();
                Dispatcher.UIThread.Post(ThrowWithInner);
                Dispatcher.UIThread.RunJobs();
                return 3;
            case "native-abort":
                var bytes = Encoding.UTF8.GetBytes("NATIVE-STDERR-BEFORE-ABORT\n");
                if (OperatingSystem.IsMacOS()) { _ = MacWrite(2, bytes, (nuint)bytes.Length); MacAbort(); }
                else { _ = LinuxWrite(2, bytes, (nuint)bytes.Length); LinuxAbort(); }
                return 3;
            case "kill":
                Process.GetCurrentProcess().Kill();
                Thread.Sleep(10000);
                return 3;
            case "inspect":
                session.AcknowledgePreviousExits();
                return 0;
            case "recovery-ui":
                SetupUi();
                Dispatcher.UIThread.RunJobs();
                var notice = typeof(ShellDiagnostics).GetField("_notice", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as Window;
                if (notice is null || !notice.IsVisible) throw new InvalidOperationException("Recovery notice did not open.");
                if (!notice.Title!.Contains("unexpectedly")) throw new InvalidOperationException("Wrong recovery notice.");
                notice.Close();
                return 0;
            case "parallel":
                Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
                {
                    for (var i = 0; i < 64; i++) session.Write("probe.parallel", $"{worker}:{i}");
                }))).GetAwaiter().GetResult();
                return 0;
            default: throw new ArgumentException("Unknown probe mode.");
        }
    }

    private static void SetupUi() => AppBuilder.Configure<Application>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true })
        .AfterSetup(_ => ShellDiagnostics.AttachToUi()).SetupWithoutStarting();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowWithInner()
    {
        try { ThrowInner(); }
        catch (Exception ex) { throw new InvalidOperationException("PROBE-OUTER", ex); }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInner() => throw new IOException("PROBE-INNER password=never-log-this");

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "write")] private static extern nint MacWrite(int fd, byte[] bytes, nuint length);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "abort")] private static extern void MacAbort();
    [DllImport("libc", EntryPoint = "write")] private static extern nint LinuxWrite(int fd, byte[] bytes, nuint length);
    [DllImport("libc", EntryPoint = "abort")] private static extern void LinuxAbort();
}
