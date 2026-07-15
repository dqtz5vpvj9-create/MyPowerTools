using System.Diagnostics;

namespace MyPowerTools.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var root = FindApplicationRoot(AppContext.BaseDirectory);
        var shell = Path.Combine(root, "Shell", "MyPowerTools.Shell.Avalonia.exe");
        if (!File.Exists(shell))
        {
            return 2;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = root,
            UseShellExecute = false
        };

        PrependAndroidPlatformToolsToPath(startInfo, root);
        AddDefaultShellArguments(startInfo, root, args);
        Process.Start(startInfo);
        return 0;
    }

    private static void AddDefaultShellArguments(ProcessStartInfo startInfo, string root, IReadOnlyList<string> args)
    {
        if (!HasOption(args, "--modules"))
        {
            startInfo.ArgumentList.Add("--modules");
            startInfo.ArgumentList.Add(Path.Combine(root, "modules"));
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
    }

    private static bool HasOption(IReadOnlyList<string> args, string option)
    {
        return args.Any(arg => string.Equals(arg, option, StringComparison.OrdinalIgnoreCase));
    }

    private static void PrependAndroidPlatformToolsToPath(ProcessStartInfo startInfo, string root)
    {
        var platformTools = Path.Combine(root, "Tools", "AndroidPlatformTools");
        if (!Directory.Exists(platformTools))
        {
            return;
        }

        startInfo.Environment.TryGetValue("PATH", out var inheritedPath);
        startInfo.Environment["PATH"] = string.IsNullOrWhiteSpace(inheritedPath)
            ? platformTools
            : platformTools + Path.PathSeparator + inheritedPath;
    }

    private static string FindApplicationRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Shell", "MyPowerTools.Shell.Avalonia.exe")) &&
                Directory.Exists(Path.Combine(directory.FullName, "modules")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }
}
