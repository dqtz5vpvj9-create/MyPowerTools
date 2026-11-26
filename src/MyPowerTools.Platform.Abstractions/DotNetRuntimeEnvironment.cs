using System.Diagnostics;

namespace MyPowerTools.Platform.Abstractions;

/// <summary>
/// Configures .NET runtime discovery and major-version roll-forward for the current
/// process tree without mutating persistent user or machine environment variables.
/// </summary>
public static class DotNetRuntimeEnvironment
{
    public const string VariableName = "DOTNET_ROOT";
    public const string RollForwardVariableName = "DOTNET_ROLL_FORWARD";
    public const string DefaultRollForward = "Major";
    public const int MinimumRuntimeMajor = 8;

    private static readonly string[] HostFxrFileNames =
    [
        "hostfxr.dll",
        "libhostfxr.so",
        "libhostfxr.dylib"
    ];

    public static string? ConfigureCurrentProcess(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var currentDotNetRoot = Environment.GetEnvironmentVariable(VariableName);
        var effectiveRoot = ResolveEffectiveRootCore(
            installRoot,
            currentDotNetRoot,
            OperatingSystem.IsWindows(),
            GetWindowsLocalAppData(),
            GetWindowsProgramFiles());

        if (effectiveRoot is not null)
        {
            Environment.SetEnvironmentVariable(VariableName, effectiveRoot, EnvironmentVariableTarget.Process);
        }
        else if (!string.IsNullOrWhiteSpace(currentDotNetRoot))
        {
            Environment.SetEnvironmentVariable(VariableName, null, EnvironmentVariableTarget.Process);
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RollForwardVariableName)))
        {
            Environment.SetEnvironmentVariable(
                RollForwardVariableName,
                DefaultRollForward,
                EnvironmentVariableTarget.Process);
        }

        return effectiveRoot;
    }

    public static string? ConfigureChildProcess(ProcessStartInfo startInfo, string installRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        return ConfigureChildProcessCore(
            startInfo,
            installRoot,
            OperatingSystem.IsWindows(),
            GetWindowsLocalAppData(),
            GetWindowsProgramFiles());
    }

    public static void ConfigureInheritedChildProcess(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (startInfo.Environment.TryGetValue(VariableName, out var inheritedRoot) &&
            !string.IsNullOrWhiteSpace(inheritedRoot))
        {
            if (HasValidHostFxrAndCoreRuntime(inheritedRoot, MinimumRuntimeMajor))
            {
                startInfo.Environment[VariableName] = Path.GetFullPath(inheritedRoot);
            }
            else
            {
                startInfo.Environment.Remove(VariableName);
            }
        }

        EnsureChildRollForward(startInfo);
    }

    public static string? ResolvePrivateRoot(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        var root = Path.GetFullPath(Path.Combine(installRoot, "Runtime", "dotnet"));
        return HasValidHostFxrAndCoreRuntime(root, MinimumRuntimeMajor) ? root : null;
    }

    public static string? ResolveEffectiveRoot(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);

        return ResolveEffectiveRootCore(
            installRoot,
            Environment.GetEnvironmentVariable(VariableName),
            OperatingSystem.IsWindows(),
            GetWindowsLocalAppData(),
            GetWindowsProgramFiles());
    }

    public static bool HasValidHostFxrAndCoreRuntime(string? candidateRoot, int minMajor = MinimumRuntimeMajor)
    {
        if (string.IsNullOrWhiteSpace(candidateRoot))
        {
            return false;
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(candidateRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var fxrRoot = Path.Combine(fullRoot, "host", "fxr");
        var coreRoot = Path.Combine(fullRoot, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(fxrRoot) || !Directory.Exists(coreRoot))
        {
            return false;
        }

        return HasCompatibleFxrDirectory(fxrRoot, minMajor) &&
               HasCompatibleCoreRuntimeDirectory(coreRoot, minMajor);
    }

    internal static string? ConfigureChildProcessCore(
        ProcessStartInfo startInfo,
        string installRoot,
        bool isWindows,
        string? windowsLocalAppData,
        string? windowsProgramFiles)
    {
        startInfo.Environment.TryGetValue(VariableName, out var inheritedRoot);
        var effectiveRoot = ResolveEffectiveRootCore(
            installRoot,
            inheritedRoot,
            isWindows,
            windowsLocalAppData,
            windowsProgramFiles);

        if (effectiveRoot is not null)
        {
            startInfo.Environment[VariableName] = effectiveRoot;
        }
        else
        {
            startInfo.Environment.Remove(VariableName);
        }

        EnsureChildRollForward(startInfo);
        return effectiveRoot;
    }

    internal static string? ResolveEffectiveRootCore(
        string installRoot,
        string? inheritedDotNetRoot,
        bool isWindows,
        string? windowsLocalAppData,
        string? windowsProgramFiles)
    {
        var privateRoot = ResolvePrivateRoot(installRoot);
        if (privateRoot is not null)
        {
            return privateRoot;
        }

        if (!string.IsNullOrWhiteSpace(inheritedDotNetRoot) &&
            HasValidHostFxrAndCoreRuntime(inheritedDotNetRoot, MinimumRuntimeMajor))
        {
            return Path.GetFullPath(inheritedDotNetRoot);
        }

        if (isWindows)
        {
            if (!string.IsNullOrWhiteSpace(windowsLocalAppData))
            {
                var userDotNet = Path.Combine(windowsLocalAppData, "Microsoft", "dotnet");
                if (HasValidHostFxrAndCoreRuntime(userDotNet, MinimumRuntimeMajor))
                {
                    return Path.GetFullPath(userDotNet);
                }
            }

            if (!string.IsNullOrWhiteSpace(windowsProgramFiles))
            {
                var globalDotNet = Path.Combine(windowsProgramFiles, "dotnet");
                if (HasValidHostFxrAndCoreRuntime(globalDotNet, MinimumRuntimeMajor))
                {
                    return Path.GetFullPath(globalDotNet);
                }
            }
        }

        return null;
    }

    private static void EnsureChildRollForward(ProcessStartInfo startInfo)
    {
        if (!startInfo.Environment.TryGetValue(RollForwardVariableName, out var rollForward) ||
            string.IsNullOrWhiteSpace(rollForward))
        {
            startInfo.Environment[RollForwardVariableName] = DefaultRollForward;
        }
    }

    private static bool HasCompatibleFxrDirectory(string fxrRoot, int minMajor)
    {
        foreach (var directory in Directory.EnumerateDirectories(fxrRoot))
        {
            if (!TryParseMajorVersion(Path.GetFileName(directory), out var major) || major < minMajor)
            {
                continue;
            }

            if (HostFxrFileNames.Any(fileName => File.Exists(Path.Combine(directory, fileName))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasCompatibleCoreRuntimeDirectory(string coreRoot, int minMajor)
    {
        foreach (var directory in Directory.EnumerateDirectories(coreRoot))
        {
            if (TryParseMajorVersion(Path.GetFileName(directory), out var major) && major >= minMajor)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseMajorVersion(string versionFolderName, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(versionFolderName))
        {
            return false;
        }

        var numericPart = versionFolderName.Split('-', 2)[0];
        if (!Version.TryParse(numericPart, out var parsed))
        {
            return false;
        }

        major = parsed.Major;
        return true;
    }

    private static string? GetWindowsLocalAppData()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var envValue = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return !string.IsNullOrWhiteSpace(envValue)
            ? envValue
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private static string? GetWindowsProgramFiles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var envValue = Environment.GetEnvironmentVariable("ProgramFiles");
        return !string.IsNullOrWhiteSpace(envValue)
            ? envValue
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    }
}
