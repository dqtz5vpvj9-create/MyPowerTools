using System.Runtime.CompilerServices;
using MyPowerTools.Shell.Avalonia;
using MyPowerTools.Shell.Avalonia.Services;
using HostProto = MyPowerTools.Protocol.HostControl.V1;

namespace MyPowerTools.Tests;

public sealed class ShellRunnerBootstrapPolicyTests
{
    [Theory]
    [InlineData("--no-runner-bootstrap")]
    [InlineData("--NO-RUNNER-BOOTSTRAP")]
    public async Task Disabled_bootstrap_stays_disabled_after_repeated_connection_failures(string flag)
    {
        var options = ShellStartupOptions.FromArgs([flag]);
        var monitor = new HostControlConnectionMonitor(new OfflineProbe());
        var restarts = 0;
        await using var service = new ShellRunnerEventService(
            monitor,
            new HostControlEventStreamMonitor(new EmptyEventSource()),
            options,
            _ =>
            {
                Interlocked.Increment(ref restarts);
                return Task.CompletedTask;
            });

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await service.CheckOnceAsync();
        }

        Assert.False(options.RunnerBootstrap);
        Assert.Equal(4, monitor.LastSnapshot.ConsecutiveFailures);
        Assert.Null(monitor.RestartRunner);
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task Reconnect_preserves_the_explicit_startup_options()
    {
        var options = ShellStartupOptions.FromArgs(
            ["--modules", "isolated-modules", "--data-root", "isolated-data", "--command-palette"]);
        var restarted = new TaskCompletionSource<ShellStartupOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new HostControlConnectionMonitor(new OfflineProbe());
        await using var service = new ShellRunnerEventService(
            monitor,
            new HostControlEventStreamMonitor(new EmptyEventSource()),
            options,
            actual =>
            {
                restarted.TrySetResult(actual);
                return Task.CompletedTask;
            });

        await service.CheckOnceAsync();
        await service.CheckOnceAsync();
        Assert.False(restarted.Task.IsCompleted);
        await service.CheckOnceAsync();
        var actual = await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Same(options, actual);
        Assert.Equal("isolated-modules", actual.ModulesRoot);
        Assert.Equal("isolated-data", actual.DataRoot);
        Assert.True(actual.FocusCommandPalette);
        Assert.True(actual.RunnerBootstrap);
    }

    [Fact]
    public async Task Callers_without_explicit_options_keep_default_restart_behavior()
    {
        var restarted = new TaskCompletionSource<ShellStartupOptions>(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new HostControlConnectionMonitor(new OfflineProbe());
        await using var service = new ShellRunnerEventService(
            monitor,
            new HostControlEventStreamMonitor(new EmptyEventSource()),
            restartRunner: actual =>
            {
                restarted.TrySetResult(actual);
                return Task.CompletedTask;
            });

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await service.CheckOnceAsync();
        }

        Assert.Same(ShellStartupOptions.Default,
            await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class OfflineProbe : IHostControlConnectionProbe
    {
        public Task<HostControlConnectionProbeResult> PingAsync(CancellationToken cancellationToken) =>
            Task.FromException<HostControlConnectionProbeResult>(new IOException("Runner is offline"));
    }

    private sealed class EmptyEventSource : IHostControlEventSource
    {
        public async IAsyncEnumerable<HostProto.HostEvent> SubscribeAsync(
            ulong lastEventSeq,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
