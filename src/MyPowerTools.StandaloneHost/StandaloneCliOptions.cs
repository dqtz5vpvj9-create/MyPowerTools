using System.Text.Json;

namespace MyPowerTools.StandaloneHost;

public sealed record StandaloneCliOptions(
    string? ToolRoot,
    string? ToolId,
    string? RouteId,
    string? DataRoot,
    bool Portable,
    bool StandaloneOnly,
    bool Isolated,
    bool HeadlessSmoke)
{
    public const string LauncherExeEnvironmentVariable = "MPT_LAUNCHER_EXE";
    public const string ToolRootEnvironmentVariable = "MPT_TOOL_ROOT";
    public const string SharedUiDirsEnvironmentVariable = "MPT_SHARED_UI_DIRS";

    public static StandaloneCliOptions Parse(string[] args)
    {
        string? toolRoot = null;
        string? toolId = null;
        string? routeId = null;
        string? dataRoot = null;
        var portable = false;
        var standaloneOnly = false;
        var isolated = false;
        var headlessSmoke = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.Equals(arg, "--tool-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                toolRoot = args[++i];
            }
            else if (string.Equals(arg, "--tool-id", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                toolId = args[++i];
            }
            else if (string.Equals(arg, "--route", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                routeId = args[++i];
            }
            else if (string.Equals(arg, "--data-root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dataRoot = args[++i];
            }
            else if (string.Equals(arg, "--portable", StringComparison.OrdinalIgnoreCase))
            {
                portable = true;
            }
            else if (string.Equals(arg, "--standalone", StringComparison.OrdinalIgnoreCase))
            {
                standaloneOnly = true;
            }
            else if (string.Equals(arg, "--isolated", StringComparison.OrdinalIgnoreCase))
            {
                isolated = true;
            }
            else if (string.Equals(arg, "--headless-smoke", StringComparison.OrdinalIgnoreCase))
            {
                headlessSmoke = true;
            }
        }

        // Check companion .mptboot.json next to launcher exe if present.
        var launcherExe = GetLauncherExecutablePath();
        if (!string.IsNullOrWhiteSpace(launcherExe))
        {
            var bootConfigPath = Path.ChangeExtension(launcherExe, ".mptboot.json");
            if (File.Exists(bootConfigPath))
            {
                TryApplyBootConfig(bootConfigPath, ref toolRoot, ref toolId, ref portable);
            }
        }

        return new StandaloneCliOptions(
            toolRoot,
            toolId,
            routeId,
            dataRoot,
            portable,
            standaloneOnly,
            isolated,
            headlessSmoke);
    }

    public static string? GetLauncherExecutablePath()
    {
        var envLauncher = Environment.GetEnvironmentVariable(LauncherExeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envLauncher))
        {
            return envLauncher;
        }

        return Environment.ProcessPath;
    }

    public static string DetermineInitialToolRoot(StandaloneCliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ToolRoot))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(options.ToolRoot));
        }

        var envToolRoot = Environment.GetEnvironmentVariable(ToolRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envToolRoot))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(envToolRoot));
        }

        var envLauncher = Environment.GetEnvironmentVariable(LauncherExeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envLauncher))
        {
            var launcherDir = Path.GetDirectoryName(Path.GetFullPath(envLauncher));
            if (!string.IsNullOrWhiteSpace(launcherDir) && Directory.Exists(launcherDir))
            {
                return UnwrapAppSubdirectoryIfToolRoot(launcherDir);
            }
        }

        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (LooksLikeToolFolder(baseDir))
        {
            return baseDir;
        }

        var unwrappedBase = UnwrapAppSubdirectoryIfToolRoot(baseDir);
        if (!string.Equals(unwrappedBase, baseDir, StringComparison.OrdinalIgnoreCase))
        {
            return unwrappedBase;
        }

        var cwd = Directory.GetCurrentDirectory();
        if (LooksLikeToolFolder(cwd))
        {
            return Path.GetFullPath(cwd);
        }

        return baseDir;
    }

    private static string UnwrapAppSubdirectoryIfToolRoot(string directory)
    {
        var dirName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(dirName, "app", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(directory)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent) && LooksLikeToolFolder(parent))
            {
                return parent;
            }
        }

        return directory;
    }

    internal static bool LooksLikeToolFolder(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        return File.Exists(Path.Combine(directory, "module.json")) ||
               File.Exists(Path.Combine(directory, "tool.json")) ||
               File.Exists(Path.Combine(directory, "ui", "tool.json")) ||
               File.Exists(Path.Combine(directory, "app", "module.json")) ||
               File.Exists(Path.Combine(directory, "app", "tool.json")) ||
               File.Exists(Path.Combine(directory, "portable.flag"));
    }

    private static void TryApplyBootConfig(
        string bootConfigPath,
        ref string? toolRoot,
        ref string? toolId,
        ref bool portable)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(bootConfigPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var bootDir = Path.GetDirectoryName(Path.GetFullPath(bootConfigPath))!;
            if (string.IsNullOrWhiteSpace(toolRoot) &&
                root.TryGetProperty("toolRoot", out var rootProp) &&
                rootProp.ValueKind == JsonValueKind.String)
            {
                var raw = rootProp.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    toolRoot = Path.GetFullPath(Path.Combine(bootDir, Environment.ExpandEnvironmentVariables(raw)));
                }
            }

            if (string.IsNullOrWhiteSpace(toolId) &&
                root.TryGetProperty("toolId", out var idProp) &&
                idProp.ValueKind == JsonValueKind.String)
            {
                toolId = idProp.GetString();
            }

            if (!portable &&
                root.TryGetProperty("portable", out var portableProp) &&
                portableProp.ValueKind == JsonValueKind.True)
            {
                portable = true;
            }
        }
        catch
        {
            // Boot metadata is optional and must never prevent host startup.
        }
    }
}
