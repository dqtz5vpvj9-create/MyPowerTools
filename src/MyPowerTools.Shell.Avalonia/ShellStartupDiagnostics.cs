using System.Diagnostics;
using System.Globalization;

namespace MyPowerTools.Shell.Avalonia;

internal static class ShellStartupDiagnostics
{
    internal const string TracePathEnvironmentVariable = "MPT_SHELL_STARTUP_TRACE";

    private static readonly object Gate = new();
    private static readonly string? TracePath =
        Environment.GetEnvironmentVariable(TracePathEnvironmentVariable);
    private static readonly long OriginTimestamp = Stopwatch.GetTimestamp();

    internal static void Mark(string phase)
    {
        if (string.IsNullOrWhiteSpace(TracePath))
        {
            return;
        }

        try
        {
            var elapsed = Stopwatch.GetElapsedTime(OriginTimestamp).TotalMilliseconds;
            var line = string.Concat(
                elapsed.ToString("F3", CultureInfo.InvariantCulture),
                '\t',
                phase,
                Environment.NewLine);
            lock (Gate)
            {
                File.AppendAllText(TracePath, line);
            }
        }
        catch
        {
            // Optional startup diagnostics cannot affect the product path.
        }
    }
}
