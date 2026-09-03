using System.ComponentModel;
using System.Diagnostics;

namespace MyPowerTools.Platform.Mac;

/// <summary>One entry of <c>adb devices -l</c>.</summary>
public sealed record MacAdbDevice(string Id, string State, string Model, string Product, string TransportId)
{
    public bool IsOnline => string.Equals(State, "device", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Outcome of an adb discovery pass. <paramref name="AdbPath"/> is the resolved executable, or an
/// empty string when adb could not be located.
/// </summary>
public sealed record MacAdbDiscoveryResult(
    bool Success,
    string State,
    string Message,
    string AdbPath,
    IReadOnlyList<MacAdbDevice> Devices);

/// <summary>
/// macOS provider behind the <c>adb.devices</c> capability. Android Studio, Homebrew and a manual
/// platform-tools drop each install adb somewhere different, and a launchd-started GUI process
/// inherits a minimal <c>PATH</c>, so the executable is resolved explicitly before it is run.
/// </summary>
public sealed class MacAdbDeviceProvider
{
    private const string AdbFileName = "adb";
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Resolves adb from <c>PATH</c> first, then from the well-known macOS install locations.</summary>
    public string? ResolveAdbPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, AdbFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var candidate in WellKnownAdbPaths())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public async Task<MacAdbDiscoveryResult> ListDevicesAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new MacAdbDiscoveryResult(
                false,
                "unsupported",
                "The macOS adb provider runs only on macOS.",
                string.Empty,
                []);
        }

        var adbPath = ResolveAdbPath();
        if (adbPath is null)
        {
            return new MacAdbDiscoveryResult(
                false,
                "missing",
                $"adb was not found on PATH or at {string.Join(", ", WellKnownAdbPaths())}.",
                string.Empty,
                []);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ScanTimeout);

        var psi = new ProcessStartInfo
        {
            FileName = adbPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("devices");
        psi.ArgumentList.Add("-l");

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return new MacAdbDiscoveryResult(false, "failed", $"{adbPath} could not be started.", adbPath, []);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The scan can finish while the timeout is firing, so the tree may already be gone.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                var reason = cancellationToken.IsCancellationRequested
                    ? "was canceled"
                    : $"timed out after {ScanTimeout.TotalSeconds:n0}s";
                return new MacAdbDiscoveryResult(false, "failed", $"adb devices -l {reason}.", adbPath, []);
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
                return new MacAdbDiscoveryResult(
                    false,
                    "failed",
                    $"adb devices -l exited with code {process.ExitCode}. {stderr}".TrimEnd(),
                    adbPath,
                    []);
            }

            var devices = ParseDevices(stdout);
            return new MacAdbDiscoveryResult(
                true,
                "ready",
                $"adb at {adbPath} reported {devices.Count} device(s), {devices.Count(device => device.IsOnline)} online.",
                adbPath,
                devices);
        }
        catch (Win32Exception ex)
        {
            return new MacAdbDiscoveryResult(false, "missing", $"{adbPath} could not be executed: {ex.Message}", adbPath, []);
        }
    }

    internal static IReadOnlyList<MacAdbDevice> ParseDevices(string output)
    {
        var devices = new List<MacAdbDevice>();
        foreach (var rawLine in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith("List of devices", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith('*'))
            {
                continue;
            }

            var columns = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2)
            {
                continue;
            }

            var attributes = columns.Skip(2)
                .Select(value => value.Split(':', 2))
                .Where(pair => pair.Length == 2)
                .GroupBy(pair => pair[0], StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last()[1], StringComparer.OrdinalIgnoreCase);

            devices.Add(new MacAdbDevice(
                columns[0],
                columns[1],
                attributes.GetValueOrDefault("model", "Unknown device").Replace('_', ' '),
                attributes.GetValueOrDefault("product", ""),
                attributes.GetValueOrDefault("transport_id", "")));
        }

        return devices;
    }

    private static IEnumerable<string> WellKnownAdbPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0)
        {
            yield return Path.Combine(home, "Library", "Android", "sdk", "platform-tools", AdbFileName);
        }

        yield return "/opt/homebrew/bin/adb";
        yield return "/usr/local/bin/adb";
    }
}
