namespace MyPowerTools.Android;

// The linked startup view uses real Avalonia controls; these stubs replace only its Android
// runtime/log sources so its visual lifecycle can be exercised on the build host.
internal sealed record StartupProgress(string Stage, string Message, int Step, int TotalSteps);
internal static class AndroidHost
{
    internal static StartupProgress Current { get; } = new("idle", "正在启动", 0, 0);
    internal static event Action<StartupProgress>? Progress;
    internal static void Report(StartupProgress progress) => Progress?.Invoke(progress);
}
internal static class AndroidStartupLog
{
    internal static string? Path => null;
    internal static string SnapshotText() => "";
    internal static void Error(string stage, Exception error) { }
}
