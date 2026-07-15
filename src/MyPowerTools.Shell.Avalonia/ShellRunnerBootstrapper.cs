using System.Diagnostics;
using MyPowerTools.HostControl;

namespace MyPowerTools.Shell.Avalonia;

public static class ShellRunnerBootstrapper
{
    public static async Task<ShellRunnerBootstrapResult> EnsureStartedAsync(
        ShellStartupOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!options.RunnerBootstrap)
        {
            ApplyDataRootEnvironment(options);
            return new ShellRunnerBootstrapResult("disabled", "Runner bootstrap disabled.");
        }

        ApplyDataRootEnvironment(options);
        if (await CanPingRunnerAsync(cancellationToken))
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
            if (await CanPingRunnerAsync(cancellationToken))
            {
                return new ShellRunnerBootstrapResult("started", "Runner started.");
            }

            await Task.Delay(250, cancellationToken);
        }

        return new ShellRunnerBootstrapResult("starting", "Runner was started but HostControl is still warming up.");
    }

    private static void ApplyDataRootEnvironment(ShellStartupOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DataRoot))
        {
            Environment.SetEnvironmentVariable(HostControlAuthTokenStore.DataRootEnvironmentVariable, options.DataRoot);
        }
    }

    private static async Task<bool> CanPingRunnerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
            using var client = HostControlClient.ForDefaultEndpoint();
            await client.PingAsync(timeout.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessStartInfo? ResolveRunnerStartInfo(string appRoot, ShellStartupOptions options)
    {
        var modulesRoot = options.ModulesRoot ?? Path.Combine(appRoot, "modules");
        var dataRoot = options.DataRoot ?? HostControlAuthTokenStore.DefaultDataRoot();
        var releaseRunner = Path.Combine(appRoot, "Runner", "MyPowerTools.Runner.exe");
        if (File.Exists(releaseRunner))
        {
            return CreateRunnerStartInfo(releaseRunner, appRoot, modulesRoot, dataRoot);
        }

        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        if (repositoryRoot is null)
        {
            return null;
        }

        modulesRoot = options.ModulesRoot ?? Path.Combine(repositoryRoot, "modules");
        var debugRunner = Path.Combine(repositoryRoot, "src", "MyPowerTools.Runner", "bin", "Debug", "net10.0", "MyPowerTools.Runner.exe");
        if (File.Exists(debugRunner))
        {
            return CreateRunnerStartInfo(debugRunner, repositoryRoot, modulesRoot, dataRoot);
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

    private static string FindApplicationRoot(string start)
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
}

public sealed record ShellRunnerBootstrapResult(string State, string Message);
