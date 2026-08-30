using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using MyPowerTools.HostControl;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.Shell.Avalonia.Services;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Shell.Avalonia;

public static class ShellRunnerBootstrapper
{
    public static async Task<ShellRunnerBootstrapResult> EnsureStartedAsync(
        ShellStartupOptions options,
        CancellationToken cancellationToken = default)
    {
        return await EnsureStartedAsync(
            options,
            loadHomeTools: false,
            cancellationToken);
    }

    internal static async Task<ShellRunnerBootstrapResult> EnsureStartedAsync(
        ShellStartupOptions options,
        bool loadHomeTools,
        CancellationToken cancellationToken = default)
    {
        ApplyDataRootEnvironment(options);
        var readyResult = await EnsureRunnerReadyAsync(options, cancellationToken).ConfigureAwait(false);
        if (!loadHomeTools)
        {
            return readyResult;
        }

        var startupTools = await TryLoadStartupToolsAsync(
            TimeSpan.FromMilliseconds(700),
            cancellationToken);
        if (startupTools is not null)
        {
            return readyResult with { StartupTools = startupTools };
        }

        return readyResult;
    }

    private static async Task<ShellRunnerBootstrapResult> EnsureRunnerReadyAsync(
        ShellStartupOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.RunnerBootstrap)
        {
            return new ShellRunnerBootstrapResult("disabled", "Runner bootstrap disabled.");
        }

        if (await CanReachRunnerAsync(TimeSpan.FromMilliseconds(50), cancellationToken))
        {
            return new ShellRunnerBootstrapResult("already-running", "Runner is already available.");
        }

        var appRoot = FindApplicationRoot(AppContext.BaseDirectory);
        var runner = ResolveRunnerStartInfo(appRoot, options);
        if (runner is null)
        {
            return new ShellRunnerBootstrapResult("missing", "Runner executable was not found.");
        }

        Process.Start(runner);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CanReachRunnerAsync(TimeSpan.FromMilliseconds(250), cancellationToken))
            {
                return new ShellRunnerBootstrapResult("started", "Runner started.");
            }

            await Task.Delay(50, cancellationToken);
        }

        return new ShellRunnerBootstrapResult("starting", "Runner was started but HostControl is still warming up.");
    }

    private static async Task<bool> CanReachRunnerAsync(
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        var endpoint = IpcEndpoint.RunnerDefault(PlatformId.Current());
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutDuration);
            switch (endpoint.Transport)
            {
                case IpcTransport.NamedPipe:
                    await using (var pipe = new NamedPipeClientStream(
                        ".",
                        endpoint.Address,
                        PipeDirection.InOut,
                        MptNamedPipePolicy.ClientOptions))
                    {
                        await pipe.ConnectAsync(timeout.Token);
                    }
                    return true;
                case IpcTransport.UnixDomainSocket:
                    using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
                    {
                        await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), timeout.Token);
                    }
                    return true;
                default:
                    return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyDataRootEnvironment(ShellStartupOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DataRoot))
        {
            Environment.SetEnvironmentVariable(HostControlAuthTokenStore.DataRootEnvironmentVariable, options.DataRoot);
        }
    }

    private static async Task<IReadOnlyList<HostProto.ToolDescriptor>?> TryLoadStartupToolsAsync(
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutDuration);
            using var client = HostControlClient.ForDefaultEndpoint();
            var response = await client.ListToolsAsync(includeDisabled: true, timeout.Token);
            return response.Tools.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static ProcessStartInfo? ResolveRunnerStartInfo(string appRoot, ShellStartupOptions options)
    {
        var modulesRoot = options.ModulesRoot ?? Path.Combine(appRoot, "modules");
        var dataRoot = options.DataRoot ?? HostControlAuthTokenStore.DefaultDataRoot();
        var releaseRunner = Path.Combine(appRoot, "Runner", ExecutableName("MyPowerTools.Runner"));
        if (File.Exists(releaseRunner))
        {
            var releaseStartInfo = CreateRunnerStartInfo(releaseRunner, appRoot, modulesRoot, dataRoot);
            DotNetRuntimeEnvironment.ConfigureChildProcess(releaseStartInfo, appRoot);
            return releaseStartInfo;
        }

        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot is null)
        {
            return null;
        }

        modulesRoot = options.ModulesRoot ?? Path.Combine(repositoryRoot, "modules");
        var debugRunner = Path.Combine(repositoryRoot, "artifacts", "build", "bin", "MyPowerTools.Runner", "debug", ExecutableName("MyPowerTools.Runner"));
        if (File.Exists(debugRunner))
        {
            var debugStartInfo = CreateRunnerStartInfo(debugRunner, repositoryRoot, modulesRoot, dataRoot);
            DotNetRuntimeEnvironment.ConfigureChildProcess(debugStartInfo, repositoryRoot);
            return debugStartInfo;
        }

        var runnerProject = Path.Combine(repositoryRoot, "src", "MyPowerTools.Runner", "MyPowerTools.Runner.csproj");
        if (!File.Exists(runnerProject))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment[HostControlAuthTokenStore.DataRootEnvironmentVariable] = dataRoot;
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(runnerProject);
        startInfo.ArgumentList.Add("--");
        AddRunnerArguments(startInfo, modulesRoot, dataRoot);
        DotNetRuntimeEnvironment.ConfigureChildProcess(startInfo, repositoryRoot);
        return startInfo;
    }

    private static ProcessStartInfo CreateRunnerStartInfo(string runnerExe, string workingDirectory, string modulesRoot, string dataRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = runnerExe,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment[HostControlAuthTokenStore.DataRootEnvironmentVariable] = dataRoot;
        AddRunnerArguments(startInfo, modulesRoot, dataRoot);
        return startInfo;
    }

    private static void AddRunnerArguments(ProcessStartInfo startInfo, string modulesRoot, string dataRoot)
    {
        foreach (var argument in new[] { "--modules", modulesRoot, "--data-root", dataRoot })
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    internal static string FindApplicationRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Runner")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Shell")) &&
                Directory.Exists(Path.Combine(directory.FullName, "modules")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    }

    private static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPowerTools.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    internal static string ExecutableName(string baseName) =>
        OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
}

public sealed record ShellRunnerBootstrapResult(
    string State,
    string Message,
    IReadOnlyList<HostProto.ToolDescriptor>? StartupTools = null);
