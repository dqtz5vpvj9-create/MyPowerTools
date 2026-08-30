using System.Diagnostics;

namespace MyPowerTools.Platform.Abstractions;

/// <summary>
/// Keeps MyPowerTools runtime selection local to the current process tree.
/// Persistent DOTNET_ROOT mutations are forbidden because they alter runtime
/// discovery for every .NET application started by the Windows user.
/// </summary>
public static class DotNetRuntimeEnvironment
{
    public const string VariableName = "DOTNET_ROOT";

    public static string? ConfigureCurrentProcess(string installRoot)
    {
        var privateRoot = ResolvePrivateRoot(installRoot);
        Environment.SetEnvironmentVariable(VariableName, privateRoot, EnvironmentVariableTarget.Process);
        return privateRoot;
    }

    public static string? ConfigureChildProcess(ProcessStartInfo startInfo, string installRoot)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var privateRoot = ResolvePrivateRoot(installRoot);
        if (privateRoot is null)
        {
            startInfo.Environment.Remove(VariableName);
        }
        else
        {
            startInfo.Environment[VariableName] = privateRoot;
        }

        return privateRoot;
    }

    public static string? ResolvePrivateRoot(string installRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        var root = Path.GetFullPath(Path.Combine(installRoot, "Runtime", "dotnet"));
        var fxrRoot = Path.Combine(root, "host", "fxr");
        var coreRoot = Path.Combine(root, "shared", "Microsoft.NETCore.App");
        if (!Directory.Exists(fxrRoot) || !Directory.Exists(coreRoot))
        {
            return null;
        }

        return Directory.EnumerateFiles(fxrRoot, "hostfxr.dll", SearchOption.AllDirectories).Any()
            ? root
            : null;
    }
}
