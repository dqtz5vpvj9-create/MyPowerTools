using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPowerTools.Packaging.Ota;

namespace MyPowerTools.Cli;

/// <summary>
/// <c>mpt ota check|apply|status</c> on macOS, backed by <see cref="MacNativeInstaller"/> so end
/// users need no PowerShell. Progress goes to stderr as one line per event; the
/// <c>download-progress</c> JSON lines are the format the Shell parses. The Shell that launched
/// this process is stopped during apply, so every console write tolerates a closed pipe.
/// </summary>
internal static class MacOtaCommand
{
    private const string AppBundleOption = "--app-bundle";
    private const string ExcludePidOption = "--exclude-pid";
    private const string BootstrappedOption = "--bootstrapped";

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static int Run(string[] args, Func<bool> confirmApply)
    {
        var subcommand = (args.FirstOrDefault() ?? "status").ToLowerInvariant();
        string? channel = null;
        string? feedUrl = null;
        string? appBundle = null;
        var force = false;
        var allowUnsigned = false;
        var confirmed = false;
        var bootstrapped = false;
        var excluded = new List<int>();
        var passthrough = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--yes" or "-y":
                    confirmed = true;
                    break;
                case "--force" or "-Force":
                    force = true;
                    passthrough.Add("--force");
                    break;
                case "--allow-unsigned" or "-AllowUnsigned":
                    allowUnsigned = true;
                    passthrough.Add("--allow-unsigned");
                    break;
                case "--channel" or "-Channel" when index + 1 < args.Length:
                    channel = args[++index];
                    passthrough.Add("--channel");
                    passthrough.Add(channel);
                    break;
                case "--feed-url" or "-FeedUrl" when index + 1 < args.Length:
                    feedUrl = args[++index];
                    passthrough.Add("--feed-url");
                    passthrough.Add(feedUrl);
                    break;
                case AppBundleOption when index + 1 < args.Length:
                    appBundle = args[++index];
                    break;
                case ExcludePidOption when index + 1 < args.Length &&
                                           int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid):
                    excluded.Add(pid);
                    index++;
                    break;
                case BootstrappedOption:
                    bootstrapped = true;
                    break;
                default:
                    WriteError($"未知参数：{argument}");
                    WriteError("用法：mpt ota check|apply|status [--channel stable|nightly] [--force] [--allow-unsigned] [--yes]");
                    return 1;
            }
        }

        appBundle ??= OtaUpdaterLocator.FindMacBundleRoot(AppContext.BaseDirectory)
                      ?? MacInstallOptions.CreateDefault().AppBundlePath;
        var defaults = MacInstallOptions.CreateDefault();
        var options = defaults with
        {
            AppBundlePath = appBundle,
            Channel = channel ?? InstalledChannel(defaults.DataRoot) ?? "stable",
            Force = force,
            AllowUnsigned = allowUnsigned,
            FeedUrl = feedUrl,
            ExcludedProcessIds = excluded
        };

        switch (subcommand)
        {
            case "status":
                WriteResult(new MacNativeInstaller(options).ReadStatus());
                return 0;
            case "check":
                return RunCheck(options);
            case "apply":
                if (!confirmed && !confirmApply())
                {
                    WriteError("已取消升级。");
                    return 1;
                }

                if (!bootstrapped && TryRunFromCopy(options, passthrough, out var exitCode))
                {
                    return exitCode;
                }

                return RunApply(options);
            default:
                WriteError($"未知的 ota 子命令：{subcommand}。可用：check、apply、status。");
                return 1;
        }
    }

    private static int RunCheck(MacInstallOptions options)
    {
        try
        {
            var installer = new MacNativeInstaller(options, ReportProgress);
            WriteResult(installer.CheckAsync().GetAwaiter().GetResult());
            return 0;
        }
        catch (Exception exception)
        {
            var message = exception is AggregateException { InnerException: { } inner } ? inner.Message : exception.Message;
            WriteError("检查更新失败：" + message);
            WriteResult(new JsonObject
            {
                ["success"] = false,
                ["channel"] = options.Channel,
                ["error"] = message,
                ["checkedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            });
            return 1;
        }
    }

    private static int RunApply(MacInstallOptions options)
    {
        var installer = new MacNativeInstaller(options, ReportProgress);
        var result = installer.ApplyAsync().GetAwaiter().GetResult();
        WriteResult(result);
        return result["success"]?.GetValue<bool>() == true ? 0 : 1;
    }

    /// <summary>
    /// A CLI started from inside the bundle it is about to replace must not keep running from
    /// there: after the swap its base directory holds the new version's assemblies, and a lazy
    /// assembly load would mix versions. Like the pwsh updater's bootstrap copy, the work runs
    /// from a copy in ota-state; this in-bundle process only waits (and is spared by the stop).
    /// </summary>
    private static bool TryRunFromCopy(MacInstallOptions options, IReadOnlyList<string> passthrough, out int exitCode)
    {
        exitCode = 1;
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('/');
        var bundle = Path.GetFullPath(options.AppBundlePath).TrimEnd('/');
        var executable = Environment.ProcessPath;
        if (!MacInstallLogic.IsInside(baseDirectory, bundle) || string.IsNullOrEmpty(executable))
        {
            return false;
        }

        try
        {
            var bootstrapRoot = Path.Combine(options.DataRoot, "ota-state", "bootstrap-cli");
            Directory.CreateDirectory(bootstrapRoot);
            RemoveStaleCopies(bootstrapRoot);
            var copy = Path.Combine(bootstrapRoot, Guid.NewGuid().ToString("N"));
            using (var ditto = Process.Start(new ProcessStartInfo("/usr/bin/ditto", [baseDirectory, copy])
                   {
                       UseShellExecute = false,
                       RedirectStandardOutput = true,
                       RedirectStandardError = true
                   }))
            {
                if (ditto is null)
                {
                    return false;
                }

                ditto.StandardOutput.ReadToEnd();
                var dittoError = ditto.StandardError.ReadToEnd();
                ditto.WaitForExit();
                if (ditto.ExitCode != 0)
                {
                    WriteError($"无法准备独立更新进程（{dittoError.Trim()}），改为直接更新。");
                    return false;
                }
            }

            var copiedExecutable = Path.Combine(copy, Path.GetFileName(executable));
            if (!File.Exists(copiedExecutable))
            {
                return false;
            }

            // Inherit stdio: the child writes straight into the Shell's pipes (or the terminal).
            var startInfo = new ProcessStartInfo(copiedExecutable)
            {
                UseShellExecute = false,
                WorkingDirectory = copy
            };
            foreach (var argument in new[] { "ota", "apply", "--yes" }.Concat(passthrough))
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.ArgumentList.Add(AppBundleOption);
            startInfo.ArgumentList.Add(bundle);
            startInfo.ArgumentList.Add(ExcludePidOption);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            foreach (var pid in options.ExcludedProcessIds)
            {
                startInfo.ArgumentList.Add(ExcludePidOption);
                startInfo.ArgumentList.Add(pid.ToString(CultureInfo.InvariantCulture));
            }

            startInfo.ArgumentList.Add(BootstrappedOption);
            using var child = Process.Start(startInfo);
            if (child is null)
            {
                return false;
            }

            // Nothing but waiting after this point: the bundle this process was loaded from is
            // replaced while the child works.
            child.WaitForExit();
            exitCode = child.ExitCode;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              System.ComponentModel.Win32Exception)
        {
            WriteError($"无法准备独立更新进程（{exception.Message}），改为直接更新。");
            return false;
        }
    }

    private static void RemoveStaleCopies(string bootstrapRoot)
    {
        foreach (var directory in Directory.GetDirectories(bootstrapRoot))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromHours(2))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? InstalledChannel(string dataRoot)
    {
        try
        {
            var path = Path.Combine(dataRoot, "ota-state", "installed-release.json");
            if (!File.Exists(path))
            {
                return null;
            }

            var channel = JsonNode.Parse(File.ReadAllText(path))?["channel"]?.GetValue<string>();
            return channel is "stable" or "nightly" ? channel : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static void ReportProgress(MacInstallProgress progress)
    {
        if (progress.Stage == "download" && progress.ReceivedBytes is { } received)
        {
            var line = new JsonObject
            {
                ["event"] = "download-progress",
                ["file"] = progress.Asset ?? string.Empty,
                ["received"] = received,
                ["total"] = progress.TotalBytes,
                ["percent"] = progress.Percent
            };
            WriteError(line.ToJsonString(CompactJson));
            return;
        }

        WriteError($"[{progress.Stage}] {progress.Message}");
    }

    private static void WriteResult(JsonNode node)
    {
        try
        {
            Console.Out.WriteLine(node.ToJsonString(IndentedJson));
            Console.Out.Flush();
        }
        catch (IOException)
        {
            // The Shell reading this pipe is stopped during apply.
        }
    }

    private static void WriteError(string line)
    {
        try
        {
            Console.Error.WriteLine(line);
            Console.Error.Flush();
        }
        catch (IOException)
        {
        }
    }
}
