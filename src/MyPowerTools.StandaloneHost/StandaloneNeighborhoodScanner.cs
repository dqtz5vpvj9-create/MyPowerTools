using System.Diagnostics;
using MyPowerTools.Packaging;

namespace MyPowerTools.StandaloneHost;

public sealed record DiscoveredSiblingTool(
    string ToolId,
    string Title,
    string Directory,
    string? LauncherExecutablePath);

public sealed record StandaloneNeighborhood(
    IReadOnlyList<DiscoveredSiblingTool> SiblingTools,
    string? HubExecutablePath)
{
    public bool HasSiblingsOrHub => SiblingTools.Count > 0 || !string.IsNullOrWhiteSpace(HubExecutablePath);
}

/// <summary>
/// Scans adjacent directories for sibling portable tools (<c>Mpt*</c> or subfolders inside <c>tools/</c>)
/// and checks whether the full MyPowerTools Hub shell is present nearby or installed in LocalAppData.
/// </summary>
public static class StandaloneNeighborhoodScanner
{
    private static readonly HashSet<string> DedicatedSuiteParentNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "tools",
        "custom-tools",
        "composable-suite",
        "standalone"
    };

    public static StandaloneNeighborhood Scan(ResolvedStandaloneTool currentTool, StandaloneCliOptions options)
    {
        var reader = new PackageReader();
        var siblings = new List<DiscoveredSiblingTool>();
        var seenToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentTool.Descriptor.ToolId
        };

        var currentRoot = Path.GetFullPath(currentTool.ToolRoot);
        var parentDir = Directory.GetParent(currentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (parentDir is not null && parentDir.Exists)
        {
            var scanAllChildren = DedicatedSuiteParentNames.Contains(parentDir.Name);
            ScanDirectoryForSiblings(reader, parentDir.FullName, currentRoot, scanAllChildren, seenToolIds, siblings);
        }

        var hubPath = options.Isolated ? null : FindHubExecutable(currentRoot);
        if (!string.IsNullOrWhiteSpace(hubPath))
        {
            var hubDir = Path.GetDirectoryName(hubPath)!;
            var hubToolsDir = Path.Combine(hubDir, "tools");
            if (Directory.Exists(hubToolsDir))
            {
                ScanDirectoryForSiblings(reader, hubToolsDir, currentRoot, scanAllChildren: true, seenToolIds, siblings);
            }
        }

        siblings.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Title, b.Title));
        return new StandaloneNeighborhood(siblings, hubPath);
    }

    public static bool TryLaunchSiblingTool(DiscoveredSiblingTool sibling)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(sibling.LauncherExecutablePath) && File.Exists(sibling.LauncherExecutablePath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = sibling.LauncherExecutablePath,
                    WorkingDirectory = sibling.Directory,
                    UseShellExecute = false
                };
                Process.Start(psi);
                return true;
            }

            var currentHostDll = typeof(Program).Assembly.Location;
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = processPath,
                    WorkingDirectory = sibling.Directory,
                    UseShellExecute = false
                };
                if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(currentHostDll))
                {
                    psi.ArgumentList.Add(currentHostDll);
                }

                psi.ArgumentList.Add("--tool-root");
                psi.ArgumentList.Add(sibling.Directory);
                psi.ArgumentList.Add("--tool-id");
                psi.ArgumentList.Add(sibling.ToolId);
                Process.Start(psi);
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    public static bool TryLaunchHub(string hubExecutablePath, string? focusToolId = null)
    {
        if (string.IsNullOrWhiteSpace(hubExecutablePath) || !File.Exists(hubExecutablePath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = hubExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(hubExecutablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false
            };
            if (!string.IsNullOrWhiteSpace(focusToolId))
            {
                psi.ArgumentList.Add($"mypowertools://tools/{focusToolId}");
            }

            Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ScanDirectoryForSiblings(
        PackageReader reader,
        string containerDir,
        string currentToolRoot,
        bool scanAllChildren,
        HashSet<string> seenToolIds,
        List<DiscoveredSiblingTool> output)
    {
        IEnumerable<string> subDirs;
        try
        {
            subDirs = Directory.EnumerateDirectories(containerDir).ToArray();
        }
        catch
        {
            return;
        }

        foreach (var subDir in subDirs)
        {
            var fullSub = Path.GetFullPath(subDir);
            if (string.Equals(fullSub, currentToolRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var folderName = Path.GetFileName(fullSub);
            if (string.Equals(folderName, "shared-ui", StringComparison.OrdinalIgnoreCase) ||
                folderName.StartsWith(".", StringComparison.Ordinal))
            {
                continue;
            }

            if (!scanAllChildren && !folderName.StartsWith("Mpt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var manifestPath = FindQuickToolJson(fullSub);
            if (manifestPath is null)
            {
                continue;
            }

            try
            {
                var manifest = reader.ReadDevelopmentToolManifest(manifestPath, Path.GetDirectoryName(manifestPath)!);
                if (string.IsNullOrWhiteSpace(manifest.ToolId) || !seenToolIds.Add(manifest.ToolId))
                {
                    continue;
                }

                var title = string.IsNullOrWhiteSpace(manifest.Title) ? folderName : manifest.Title;
                var launcherExe = FindSiblingLauncherExecutable(fullSub, folderName);
                output.Add(new DiscoveredSiblingTool(manifest.ToolId, title, fullSub, launcherExe));
            }
            catch
            {
                // Ignore unrelated or incomplete folders.
            }
        }
    }

    private static string? FindQuickToolJson(string dir)
    {
        string[] paths =
        [
            Path.Combine(dir, "tool.json"),
            Path.Combine(dir, "ui", "tool.json"),
            Path.Combine(dir, "app", "tool.json"),
            Path.Combine(dir, "app", "ui", "tool.json"),
            Path.Combine(dir, "package", "ui", "tool.json"),
            Path.Combine(dir, "sdk-tool", "tool.json")
        ];

        return paths.FirstOrDefault(File.Exists);
    }

    private static string? FindSiblingLauncherExecutable(string siblingDir, string folderName)
    {
        if (OperatingSystem.IsWindows())
        {
            var preferred = Path.Combine(siblingDir, folderName + ".exe");
            if (File.Exists(preferred))
            {
                return preferred;
            }

            var anyBoot = Directory.EnumerateFiles(siblingDir, "*.mptboot.json").FirstOrDefault();
            if (anyBoot is not null)
            {
                var bootExe = Path.ChangeExtension(anyBoot, ".exe");
                if (File.Exists(bootExe))
                {
                    return bootExe;
                }
            }

            var hostExe = Path.Combine(siblingDir, "MptStandaloneHost.exe");
            if (File.Exists(hostExe))
            {
                return hostExe;
            }

            var appHostExe = Path.Combine(siblingDir, "app", "MptStandaloneHost.exe");
            if (File.Exists(appHostExe))
            {
                return appHostExe;
            }
        }

        return null;
    }

    private static string? FindHubExecutable(string toolRoot)
    {
        string[] exeNames = OperatingSystem.IsWindows()
            ? ["MyPowerTools.exe", "MyPowerTools.Shell.Avalonia.exe"]
            : ["MyPowerTools", "MyPowerTools.Shell.Avalonia"];

        var candidateDirs = new List<string>();
        var parent = Directory.GetParent(toolRoot)?.FullName;
        if (!string.IsNullOrWhiteSpace(parent))
        {
            candidateDirs.Add(parent);
            candidateDirs.Add(Path.Combine(parent, "Shell"));
            var grandParent = Directory.GetParent(parent)?.FullName;
            if (!string.IsNullOrWhiteSpace(grandParent))
            {
                candidateDirs.Add(grandParent);
                candidateDirs.Add(Path.Combine(grandParent, "Shell"));
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidateDirs.Add(Path.Combine(localAppData, "Programs", "MyPowerTools"));
            candidateDirs.Add(Path.Combine(localAppData, "Programs", "MyPowerTools", "Shell"));
        }

        foreach (var dir in candidateDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var exe in exeNames)
            {
                var full = Path.Combine(dir, exe);
                if (File.Exists(full))
                {
                    return Path.GetFullPath(full);
                }
            }
        }

        return null;
    }
}
