using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using MyPowerTools.Shell.Avalonia;
using A = global::Android;

namespace MyPowerTools.Android;

[A.App.Application]
public sealed class MptAndroidApplication(nint handle, JniHandleOwnership ownership)
    : AvaloniaAndroidApplication<App>(handle, ownership)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.SingleViewFactory = MainActivity.CreateMainView;
        return base.CustomizeAppBuilder(builder);
    }

    public override void OnCreate()
    {
        // Set the app-owned temporary directory before the host loads any tool modules.
        ApplyWritableTempDirectory();
        base.OnCreate();
        AndroidStartupLog.UseRuntimeLogDirectory(null);
        AndroidStartupLog.Info("application", "Process start");
        // A managed exception that escapes the Avalonia loop otherwise only surfaces as a process
        // death in logcat; record it where the startup screen and `run-as` can read it.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AndroidStartupLog.Error("unhandled", args.ExceptionObject as Exception
                ?? new InvalidOperationException(args.ExceptionObject?.ToString() ?? "unknown unhandled exception"));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AndroidStartupLog.Error("unobserved-task", args.Exception);
            args.SetObserved();
        };
        A.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, args) =>
            AndroidStartupLog.Error("java-callable", args.Exception);
    }

    /// <summary>
    /// The CoreCLR PAL falls back to <c>/data/local/tmp</c> when <c>TMPDIR</c> is unset, and an app
    /// process cannot write there: named-mutex files and other temp paths then fail with EACCES, so
    /// a module that takes a cross-process lock dies while it loads ("The system cannot open the
    /// device or file specified ... mkdtemp(/data/local/tmp/.dotnet.*) == nullptr; errno == EACCES").
    /// Both the managed environment and the native environment are pointed at the app cache
    /// directory; the native <c>setenv</c> matters because the PAL reads <c>TMPDIR</c> itself.
    /// This only relocates temp files - it does not replace or weaken any host locking.
    /// </summary>
    private void ApplyWritableTempDirectory()
    {
        var directory = CacheDir!.AbsolutePath;
        Environment.SetEnvironmentVariable("TMPDIR", directory);
        A.Systems.Os.Setenv("TMPDIR", directory, true);
        AndroidStartupLog.Info("tmpdir", "TMPDIR=" + directory);
    }
}
