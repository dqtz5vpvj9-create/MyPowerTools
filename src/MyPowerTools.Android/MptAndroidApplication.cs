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
}
