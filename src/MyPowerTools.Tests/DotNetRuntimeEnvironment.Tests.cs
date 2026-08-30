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
    public void Child_process_uses_valid_private_runtime()
    {
        var privateRoot = CreatePrivateRuntime();
        var startInfo = NewStartInfo();
        startInfo.Environment[DotNetRuntimeEnvironment.VariableName] = @"C:\user-managed-dotnet";

        var resolved = DotNetRuntimeEnvironment.ConfigureChildProcess(startInfo, _root);

        Assert.Equal(privateRoot, resolved);
        Assert.Equal(privateRoot, startInfo.Environment[DotNetRuntimeEnvironment.VariableName]);
    }

    [Fact]
    public void Child_process_drops_inherited_override_when_private_runtime_is_missing()
    {
        Directory.CreateDirectory(_root);
        var startInfo = NewStartInfo();
        startInfo.Environment[DotNetRuntimeEnvironment.VariableName] = @"C:\stale-dotnet-root";

        var resolved = DotNetRuntimeEnvironment.ConfigureChildProcess(startInfo, _root);

        Assert.Null(resolved);
        Assert.False(startInfo.Environment.ContainsKey(DotNetRuntimeEnvironment.VariableName));
    }

    [Fact]
    public void Incomplete_private_runtime_falls_back_to_global_discovery()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Runtime", "dotnet", "host", "fxr", "10.0.1"));
        var startInfo = NewStartInfo();

        var resolved = DotNetRuntimeEnvironment.ConfigureChildProcess(startInfo, _root);

        Assert.Null(resolved);
        Assert.False(startInfo.Environment.ContainsKey(DotNetRuntimeEnvironment.VariableName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreatePrivateRuntime()
    {
        var privateRoot = Path.Combine(_root, "Runtime", "dotnet");
        var fxr = Path.Combine(privateRoot, "host", "fxr", "10.0.1");
        Directory.CreateDirectory(fxr);
        Directory.CreateDirectory(Path.Combine(privateRoot, "shared", "Microsoft.NETCore.App", "10.0.1"));
        File.WriteAllBytes(Path.Combine(fxr, "hostfxr.dll"), []);
        return privateRoot;
    }

    private static ProcessStartInfo NewStartInfo() => new()
    {
        FileName = "dotnet",
        UseShellExecute = false
    };
}
