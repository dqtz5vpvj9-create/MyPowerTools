using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

namespace MyPowerTools.StandaloneHost;

internal sealed class StandaloneSession : IAsyncDisposable
{
    private int _disposed;

    public StandaloneSession(
        StandaloneCliOptions options,
        ResolvedStandaloneTool tool,
        StandaloneExecutionBackend backend,
        StandaloneNeighborhood neighborhood)
    {
        Options = options;
        Tool = tool;
        Backend = backend;
        Neighborhood = neighborhood;
    }

    public StandaloneCliOptions Options { get; }
    public ResolvedStandaloneTool Tool { get; }
    public StandaloneExecutionBackend Backend { get; }
    public StandaloneNeighborhood Neighborhood { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Backend.DisposeAsync().ConfigureAwait(false);
    }

    public void DisposeSynchronously()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

internal static class StandaloneBootstrap
{
    public static int Run(StandaloneCliOptions options, string[] rawArgs)
    {
        if (options.HeadlessSmoke)
        {
            return RunHeadlessSmokeAsync(options).GetAwaiter().GetResult();
        }

        return RunDesktop(options, rawArgs);
    }

    private static int RunDesktop(StandaloneCliOptions options, string[] rawArgs)
    {
        var session = InitializeSessionAsync(options, CancellationToken.None).GetAwaiter().GetResult();
        try
        {
            return AppBuilder.Configure(() => new StandaloneApp(session))
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(rawArgs);
        }
        finally
        {
            session.DisposeSynchronously();
        }
    }

    private static async Task<int> RunHeadlessSmokeAsync(StandaloneCliOptions options)
    {
        using var smokeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var session = await InitializeSessionAsync(options, smokeTimeout.Token).ConfigureAwait(false);
        try
        {
            var status = await session.Backend.GetStatusAsync(smokeTimeout.Token).ConfigureAwait(false);
            var commands = await session.Backend.ListCommandsAsync(smokeTimeout.Token).ConfigureAwait(false);

            AppBuilder.Configure(() => new StandaloneApp(session))
                .UseHeadless(new AvaloniaHeadlessPlatformOptions
                {
                    UseHeadlessDrawing = true
                })
                .SetupWithoutStarting();

            var window = new StandaloneWindow(session);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var surfaceControlType = window.ActiveSurfaceControl?.GetType().FullName ?? "";
            window.Close();
            Dispatcher.UIThread.RunJobs();

            if (string.IsNullOrWhiteSpace(surfaceControlType))
            {
                throw new InvalidOperationException("Surface factory did not produce a valid Avalonia Control.");
            }

            var output = new
            {
                ok = true,
                toolId = session.Tool.Descriptor.ToolId,
                title = session.Tool.Descriptor.Title,
                mode = session.Backend.Mode,
                state = status.State,
                commandsCount = commands.Count,
                surfaceControlType
            };

            Console.Out.WriteLine(JsonSerializer.Serialize(output));
            Console.Out.Flush();
        }
        finally
        {
            var disposeTask = session.DisposeAsync().AsTask();
            await Task.WhenAny(disposeTask, Task.Delay(2000)).ConfigureAwait(false);
        }

        // Avalonia headless platform keeps a non-background dispatcher thread alive after SetupWithoutStarting.
        Environment.Exit(0);
        return 0;
    }

    private static async Task<StandaloneSession> InitializeSessionAsync(
        StandaloneCliOptions options,
        CancellationToken cancellationToken)
    {
        var tool = StandaloneToolLocator.Resolve(options);
        var backend = await StandaloneExecutionBackend.CreateAsync(tool, options, cancellationToken);
        try
        {
            await backend.InitializeAsync(cancellationToken);
            await backend.StartAsync(cancellationToken);
            var neighborhood = StandaloneNeighborhoodScanner.Scan(tool, options);
            return new StandaloneSession(options, tool, backend, neighborhood);
        }
        catch
        {
            await backend.DisposeAsync();
            throw;
        }
    }
}
