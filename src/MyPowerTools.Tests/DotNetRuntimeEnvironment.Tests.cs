using System.Diagnostics;
using MyPowerTools.Platform.Abstractions;

namespace MyPowerTools.Tests;

public sealed class DotNetRuntimeEnvironmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "mypowertools-dotnet-runtime-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Private_runtime_wins_when_present_even_if_user_dotnet_root_is_valid()
    {
        var privateRoot = CreateRuntimeLayout(Path.Combine(_root, "install", "Runtime", "dotnet"), "10.0.1");
        var userRoot = CreateRuntimeLayout(Path.Combine(_root, "user-local", "Microsoft", "dotnet"), "8.0.11");
        var startInfo = NewStartInfo();
        startInfo.Environment[DotNetRuntimeEnvironment.VariableName] = userRoot;
        startInfo.Environment.Remove(DotNetRuntimeEnvironment.RollForwardVariableName);

        var resolved = DotNetRuntimeEnvironment.ConfigureChildProcess(startInfo, Path.Combine(_root, "install"));

        Assert.Equal(privateRoot, resolved);
        Assert.Equal(privateRoot, startInfo.Environment[DotNetRuntimeEnvironment.VariableName]);
        Assert.Equal(
            DotNetRuntimeEnvironment.DefaultRollForward,
            startInfo.Environment[DotNetRuntimeEnvironment.RollForwardVariableName]);
    }

    [Fact]
    public void Valid_inherited_user_dotnet_root_is_preserved_when_private_runtime_is_absent()
    {
        var installRoot = Path.Combine(_root, "install");
        Directory.CreateDirectory(installRoot);
        var userRoot = CreateRuntimeLayout(
            Path.Combine(_root, "localappdata", "Microsoft", "dotnet"),
            "8.0.11");
        var startInfo = NewStartInfo();
        startInfo.Environment[DotNetRuntimeEnvironment.VariableName] = userRoot;
        startInfo.Environment.Remove(DotNetRuntimeEnvironment.RollForwardVariableName);

        var resolved = DotNetRuntimeEnvironment.ConfigureChildProcess(startInfo, installRoot);

        Assert.Equal(userRoot, resolved);
        Assert.Equal(userRoot, startInfo.Environment[DotNetRuntimeEnvironment.VariableName]);
        Assert.Equal(
            DotNetRuntimeEnvironment.DefaultRollForward,
            startInfo.Environment[DotNetRuntimeEnvironment.RollForwardVariableName]);
    }

    [Fact]
    public void Stale_or_invalid_inherited_dotnet_root_is_cleared_when_no_fallback_exists()
    {
        var installRoot = Path.Combine(_root, "install");
        Directory.CreateDirectory(installRoot);
        var startInfo = NewStartInfo();
        startInfo.Environment[DotNetRuntimeEnvironment.VariableName] = Path.Combine(_root, "missing-dotnet-root");
        startInfo.Environment.Remove(DotNetRuntimeEnvironment.RollForwardVariableName);

        var resolved = DotNetRuntimeEnvironment.ConfigureChildProcessCore(
            startInfo,
            installRoot,
            isWindows: false,
            windowsLocalAppData: null,
            windowsProgramFiles: null);

        Assert.Null(resolved);
        Assert.False(startInfo.Environment.ContainsKey(DotNetRuntimeEnvironment.VariableName));
        Assert.Equal(
            DotNetRuntimeEnvironment.DefaultRollForward,
            startInfo.Environment[DotNetRuntimeEnvironment.RollForwardVariableName]);
    }

    [Fact]
    public void Incomplete_private_runtime_falls_back_to_system_discovery_when_no_override_exists()
    {
        var installRoot = Path.Combine(_root, "install");
        Directory.CreateDirectory(Path.Combine(installRoot, "Runtime", "dotnet", "host", "fxr", "10.0.1"));
        var startInfo = NewStartInfo();
        startInfo.Environment.Remove(DotNetRuntimeEnvironment.VariableName);

        var resolved = DotNetRuntimeEnvironment.ConfigureChildProcessCore(
            startInfo,
            installRoot,
            isWindows: false,
            windowsLocalAppData: null,
            windowsProgramFiles: null);

        Assert.Null(resolved);
        Assert.False(startInfo.Environment.ContainsKey(DotNetRuntimeEnvironment.VariableName));
    }

    [Fact]
    public void Windows_fallback_prefers_user_local_dotnet_then_program_files_and_rejects_net6()
    {
        var installRoot = Path.Combine(_root, "install");
        Directory.CreateDirectory(installRoot);
        var localAppData = Path.Combine(_root, "LocalAppData");
        var programFiles = Path.Combine(_root, "ProgramFiles");

        CreateRuntimeLayout(Path.Combine(localAppData, "Microsoft", "dotnet"), "6.0.36");
        var programFilesDotNet = CreateRuntimeLayout(Path.Combine(programFiles, "dotnet"), "9.0.4");

        var resolvedProgramFiles = DotNetRuntimeEnvironment.ResolveEffectiveRootCore(
            installRoot,
            inheritedDotNetRoot: null,
            isWindows: true,
            windowsLocalAppData: localAppData,
            windowsProgramFiles: programFiles);
        Assert.Equal(programFilesDotNet, resolvedProgramFiles);

        var userLocalDotNet = CreateRuntimeLayout(Path.Combine(localAppData, "Microsoft", "dotnet"), "8.0.12");
        var resolvedUserLocal = DotNetRuntimeEnvironment.ResolveEffectiveRootCore(
            installRoot,
            inheritedDotNetRoot: null,
            isWindows: true,
            windowsLocalAppData: localAppData,
            windowsProgramFiles: programFiles);
        Assert.Equal(userLocalDotNet, resolvedUserLocal);
    }

    [Fact]
    public void Current_process_preserves_valid_dotnet_root_and_sets_roll_forward_major()
    {
        var installRoot = Path.Combine(_root, "install");
        Directory.CreateDirectory(installRoot);
        var userRoot = CreateRuntimeLayout(Path.Combine(_root, "user-dotnet"), "8.0.11");

        var originalRoot = Environment.GetEnvironmentVariable(DotNetRuntimeEnvironment.VariableName);
        var originalRollForward = Environment.GetEnvironmentVariable(DotNetRuntimeEnvironment.RollForwardVariableName);
        try
        {
            Environment.SetEnvironmentVariable(
                DotNetRuntimeEnvironment.VariableName,
                userRoot,
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                DotNetRuntimeEnvironment.RollForwardVariableName,
                null,
                EnvironmentVariableTarget.Process);

            var resolved = DotNetRuntimeEnvironment.ConfigureCurrentProcess(installRoot);

            Assert.Equal(userRoot, resolved);
            Assert.Equal(userRoot, Environment.GetEnvironmentVariable(DotNetRuntimeEnvironment.VariableName));
            Assert.Equal(
                DotNetRuntimeEnvironment.DefaultRollForward,
                Environment.GetEnvironmentVariable(DotNetRuntimeEnvironment.RollForwardVariableName));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                DotNetRuntimeEnvironment.VariableName,
                originalRoot,
                EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(
                DotNetRuntimeEnvironment.RollForwardVariableName,
                originalRollForward,
                EnvironmentVariableTarget.Process);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string CreateRuntimeLayout(string root, string version)
    {
        var fullRoot = Path.GetFullPath(root);
        var fxr = Path.Combine(fullRoot, "host", "fxr", version);
        Directory.CreateDirectory(fxr);
        Directory.CreateDirectory(Path.Combine(fullRoot, "shared", "Microsoft.NETCore.App", version));
        File.WriteAllBytes(Path.Combine(fxr, "hostfxr.dll"), []);
        return fullRoot;
    }

    private static ProcessStartInfo NewStartInfo() => new()
    {
        FileName = "dotnet",
        UseShellExecute = false,
        CreateNoWindow = true
    };
}
