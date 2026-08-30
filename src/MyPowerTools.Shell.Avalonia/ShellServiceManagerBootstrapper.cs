using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using MyPowerTools.Ipc;
using MyPowerTools.Platform.Abstractions;
using MyPowerTools.ServiceManager.Client;

namespace MyPowerTools.Shell.Avalonia;

/// <summary>
/// Spawns the independent ServiceManager process from the Shell when it is not already reachable.
/// Mirrors <see cref="ShellRunnerBootstrapper"/>: cheap reachability probe against the ServiceManager
/// IPC endpoint, spawn the executable resolved from the application layout, then poll for readiness.
/// </summary>
public static class ShellServiceManagerBootstrapper
{
    private static readonly TimeSpan ReachableProbeTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StartupDeadline = TimeSpan.FromSeconds(8);

    public static async Task<ShellServiceManagerBootstrapResult> EnsureStartedAsync(
        CancellationToken cancellationToken = default)
    {
        if (await CanReachServiceManagerAsync(ReachableProbeTimeout, cancellationToken).ConfigureAwait(false))
        {
            return new ShellServiceManagerBootstrapResult(
                "already-running",
                "ServiceManager is already available.");
        }

        var startInfo = ResolveServiceManagerStartInfo();
        if (startInfo is null)
        {
            return new ShellServiceManagerBootstrapResult(
                "missing",
                "ServiceManager executable was not found in the application layout.");
        }

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            return new ShellServiceManagerBootstrapResult(
                "missing",
                $"ServiceManager could not be started: {ex.Message}");
        }

        var deadline = DateTimeOffset.UtcNow.Add(StartupDeadline);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await CanReachServiceManagerAsync(StartupPollInterval, cancellationToken).ConfigureAwait(false))
            {
                return new ShellServiceManagerBootstrapResult("started", "ServiceManager started.");
            }

            await Task.Delay(StartupPollInterval, cancellationToken).ConfigureAwait(false);
        }

        return new ShellServiceManagerBootstrapResult(
            "starting",
            "ServiceManager was started but is still warming up. Select Try again in a moment.");
    }

    private static async Task<bool> CanReachServiceManagerAsync(
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        var endpoint = IpcEndpoint.ServiceManagerDefault(PlatformId.Current());
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

    private static ProcessStartInfo? ResolveServiceManagerStartInfo()
    {
        var appRoot = ShellRunnerBootstrapper.FindApplicationRoot(AppContext.BaseDirectory);
        var releaseExe = Path.Combine(appRoot, "ServiceManager", ShellRunnerBootstrapper.ExecutableName("MyPowerTools.ServiceManager"));
        if (File.Exists(releaseExe))
        {
            var releaseStartInfo = CreateStartInfo(releaseExe, appRoot);
            DotNetRuntimeEnvironment.ConfigureChildProcess(releaseStartInfo, appRoot);
            return releaseStartInfo;
        }

        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot is null)
        {
            return null;
        }

        var debugExe = Path.Combine(
            repositoryRoot,
            "artifacts",
            "build",
            "bin",
            "MyPowerTools.ServiceManager",
            "debug",
            ShellRunnerBootstrapper.ExecutableName("MyPowerTools.ServiceManager"));
        if (File.Exists(debugExe))
        {
            var debugStartInfo = CreateStartInfo(debugExe, repositoryRoot);
            DotNetRuntimeEnvironment.ConfigureChildProcess(debugStartInfo, repositoryRoot);
            return debugStartInfo;
        }

        var projectPath = Path.Combine(repositoryRoot, "src", "MyPowerTools.ServiceManager", "MyPowerTools.ServiceManager.csproj");
        if (!File.Exists(projectPath))
        {
            return null;
        }

        var dotnetStartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        dotnetStartInfo.Environment[ServiceManagerAdminClient.DataRootEnvironmentVariable] = ResolveDataRoot();
        dotnetStartInfo.ArgumentList.Add("run");
        dotnetStartInfo.ArgumentList.Add("--project");
        dotnetStartInfo.ArgumentList.Add(projectPath);
        dotnetStartInfo.ArgumentList.Add("--");
        dotnetStartInfo.ArgumentList.Add("--data-root");
        dotnetStartInfo.ArgumentList.Add(ResolveDataRoot());
        DotNetRuntimeEnvironment.ConfigureChildProcess(dotnetStartInfo, repositoryRoot);
        return dotnetStartInfo;
    }

    internal static ProcessStartInfo CreateStartInfo(string exePath, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment[ServiceManagerAdminClient.DataRootEnvironmentVariable] = ResolveDataRoot();
        startInfo.ArgumentList.Add("--data-root");
        startInfo.ArgumentList.Add(ResolveDataRoot());
        var deployRoot = Path.Combine(workingDirectory, "ServiceUnits");
        if (Directory.Exists(Path.Combine(deployRoot, "units")))
        {
            startInfo.ArgumentList.Add("--deploy-root");
            startInfo.ArgumentList.Add(deployRoot);
        }
        return startInfo;
    }

    private static string ResolveDataRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable(ServiceManagerAdminClient.DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyPowerTools");
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
}

public sealed record ShellServiceManagerBootstrapResult(string State, string Message);
