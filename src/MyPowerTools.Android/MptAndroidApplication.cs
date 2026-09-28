using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Android.Files;
using A = global::Android;

namespace MyPowerTools.Android;

[A.App.Application]
public sealed class MptAndroidApplication(nint handle, JniHandleOwnership ownership)
    : AvaloniaAndroidApplication<App>(handle, ownership)
{
    /// <summary>
    /// The shared theme names Windows font families (<c>Microsoft YaHei UI</c>, <c>Segoe UI</c>, …)
    /// and every platform host is responsible for translating them to its own fonts: macOS does it in
    /// <c>Program.cs</c>. Android had no translation, so those names resolved to nothing and each
    /// control fell back on its own Han substitution — on the MPT AVD that rendered regular-weight
    /// Chinese as empty boxes while some bold text happened to resolve, on the startup screen, the
    /// assistant, the tool library and the tab bar alike.
    /// <para>
    /// <c>sans-serif</c> is Android's own UI family and its font configuration carries the CJK faces
    /// for every weight (NotoSansCJK is registered inside that family), so mapping to it makes every
    /// weight resolve to one real family instead of relying on per-glyph fallback.
    /// </para>
    /// </summary>
    internal static FontManagerOptions CreateFontManagerOptions() => new()
    {
        DefaultFamilyName = "sans-serif",
        FontFamilyMappings = new Dictionary<string, FontFamily>(StringComparer.OrdinalIgnoreCase)
        {
            ["Microsoft YaHei UI"] = new FontFamily("sans-serif"),
            ["Microsoft YaHei"] = new FontFamily("sans-serif"),
            ["Segoe UI Variable"] = new FontFamily("sans-serif"),
            ["Segoe UI"] = new FontFamily("sans-serif"),
            ["Segoe UI Emoji"] = new FontFamily("sans-serif"),
            ["Segoe UI Symbol"] = new FontFamily("sans-serif"),
            ["PingFang SC"] = new FontFamily("sans-serif"),
            ["Cascadia Mono"] = new FontFamily("monospace"),
            ["Consolas"] = new FontFamily("monospace"),
            ["Menlo"] = new FontFamily("monospace"),
            ["DejaVu Sans Mono"] = new FontFamily("monospace"),
            ["Roboto Mono"] = new FontFamily("monospace")
        }
    };

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        App.SingleViewFactory = MainActivity.CreateMainView;
        return base.CustomizeAppBuilder(builder.With(CreateFontManagerOptions()));
    }

    public override void OnCreate()
    {
        base.OnCreate();
        // Register before the first activity resumes, so its first viewer launch stays in this task.
        MptCurrentActivity.EnsureRegistered(this);
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
