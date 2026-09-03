using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using MyPowerTools.Abstractions;
using MyPowerTools.Ipc;
using MyPowerTools.Shell.Avalonia.Services;

namespace MyPowerTools.Tests;

public sealed class ShellActivationTests
{
    private static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Every test gets its own endpoint so a parallel run cannot serve another test's activation.
    /// The name stays short because Unix maps it onto a socket path with a hard length limit.
    /// </summary>
    private static string UniquePipeName(string scenario) =>
        $"MptShellActivation.{scenario}.{Guid.NewGuid():N}";

    /// <summary>
    /// Sends a frame the production client would never produce, so the server's own framing and
    /// payload checks are what decides the outcome.
    /// </summary>
    private static async Task<bool> SendRawRequestAsync(
        string pipeName,
        int declaredLength,
        byte[] body)
    {
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            MptNamedPipePolicy.ClientOptions);
        using var timeout = new CancellationTokenSource(RequestBudget);
        await client.ConnectAsync(timeout.Token);

        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, declaredLength);
        await client.WriteAsync(header, timeout.Token);
        if (body.Length > 0)
        {
            await client.WriteAsync(body, timeout.Token);
        }

        await client.FlushAsync(timeout.Token);

        var response = new byte[1];
        try
        {
            var read = await client.ReadAsync(response, timeout.Token);
            return read == 1 && response[0] == 0x06;
        }
        catch (IOException)
        {
            // Windows surfaces a server-side close as a broken pipe rather than end of stream.
            return false;
        }
    }

    private static async Task RunSilentServerAsync(string pipeName, CancellationToken cancellationToken)
    {
        await using var server = MptNamedPipePolicy.CreateServer(
            pipeName,
            PipeDirection.InOut,
            maxInstances: 1);
        try
        {
            await server.WaitForConnectionAsync(cancellationToken);
            var drain = new byte[64];
            while (await server.ReadAsync(drain, cancellationToken) > 0)
            {
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
        }
    }

    private static async Task<IReadOnlyList<ShellActivationRequest>> WaitForDeliveredRequestsAsync(
        List<ShellActivationRequest> delivered,
        int expected)
    {
        var deadline = DateTimeOffset.UtcNow.Add(RequestBudget);
        while (true)
        {
            lock (delivered)
            {
                if (delivered.Count >= expected || DateTimeOffset.UtcNow >= deadline)
                {
                    Assert.Equal(expected, delivered.Count);
                    return delivered.ToArray();
                }
            }

            await Task.Delay(20);
        }
    }

    [Fact]
    public void Tool_activation_protocol_preserves_the_target_and_encoded_uri()
    {
        var expected = new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=message%2Fid%2042");

        var payload = ToolActivationProtocol.Serialize(expected);
        var parsed = ToolActivationProtocol.Parse([ToolActivationProtocol.ArgumentName, payload]);

        Assert.Equal(expected, parsed);
    }

    [Fact]
    public void Tool_activation_protocol_preserves_shell_window_suppression()
    {
        var expected = new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=message-42")
        {
            SuppressShellWindow = true
        };

        var payload = ToolActivationProtocol.Serialize(expected);
        var parsed = ToolActivationProtocol.Parse([ToolActivationProtocol.ArgumentName, payload]);

        Assert.Equal(expected, parsed);
        Assert.True(parsed!.SuppressShellWindow);
        Assert.False(ShellActivationRequest.ForTool(parsed).ShowShell);
    }

    [Fact]
    public void Product_activation_uri_round_trips_the_generic_surface_envelope()
    {
        var expected = new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=message%2F42")
        {
            SuppressShellWindow = true
        };

        var productUri = ToolActivationProtocol.CreateProductActivationUri(expected);
        var parsed = ToolActivationProtocol.ParseProductActivationUri(productUri.AbsoluteUri);

        Assert.Equal(expected, parsed);
        Assert.Equal("mypowertools", productUri.Scheme);
        Assert.Equal("activate", productUri.Host);
    }

    [Theory]
    [InlineData("mypowertools://activate?payload=%ZZ")]
    [InlineData("mypowertools://other?payload=%7B%7D")]
    [InlineData("https://activate?payload=%7B%7D")]
    public void Product_activation_uri_rejects_malformed_or_wrong_route(string value)
    {
        Assert.Null(ToolActivationProtocol.ParseProductActivationUri(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"ToolId\":\"bad id\",\"RouteId\":\"inbox\",\"ActivationUri\":\"mypowertools://remote-notification?id=1\"}")]
    [InlineData("{\"ToolId\":\"remote-notifications\",\"RouteId\":\"inbox\",\"ActivationUri\":\"relative\"}")]
    public void Tool_activation_protocol_rejects_malformed_or_unsafe_targets(string payload)
    {
        Assert.Null(ToolActivationProtocol.Deserialize(payload));
    }

    [Fact]
    public async Task Shell_activation_pipe_forwards_to_the_running_instance()
    {
        var pipeName = UniquePipeName("forward");
        var received = new TaskCompletionSource<ShellActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipe = new ShellActivationPipe(
            request =>
            {
                received.TrySetResult(request);
                return Task.CompletedTask;
            },
            pipeName);
        pipe.Start();

        var expected = ShellActivationRequest.ForTool(new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=exact-message-42")
        {
            SuppressShellWindow = true
        });
        var forwarded = await ShellActivationPipe.TryForwardAsync(
            expected,
            RequestBudget,
            pipeName: pipeName);
        var delivered = await received.Task.WaitAsync(RequestBudget);

        Assert.True(forwarded);
        Assert.Equal(expected, delivered);
        Assert.False(delivered.ShowShell);
    }

    /// <summary>
    /// A launcher waits milliseconds for the acknowledgement while presenting the window waits on
    /// the workspace. If the two shared a deadline, a slow UI would look like a dead Shell and the
    /// launcher would start a second one.
    /// </summary>
    [Fact]
    public async Task Shell_activation_pipe_acknowledges_before_the_presentation_completes()
    {
        var pipeName = UniquePipeName("ack");
        var presentationStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var presentationCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePresentation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipe = new ShellActivationPipe(
            async _ =>
            {
                presentationStarted.TrySetResult(true);
                await releasePresentation.Task;
                presentationCompleted.TrySetResult(true);
            },
            pipeName);
        pipe.Start();

        var forwarded = await ShellActivationPipe.TryForwardAsync(
            ShellActivationRequest.FocusShell,
            RequestBudget,
            pipeName: pipeName);

        Assert.True(forwarded);
        Assert.False(presentationCompleted.Task.IsCompleted);

        await presentationStarted.Task.WaitAsync(RequestBudget);
        releasePresentation.TrySetResult(true);
        await presentationCompleted.Task.WaitAsync(RequestBudget);
    }

    [Fact]
    public async Task Shell_activation_reports_failure_without_a_running_instance()
    {
        var elapsed = Stopwatch.StartNew();
        var forwarded = await ShellActivationPipe.TryForwardAsync(
            ShellActivationRequest.FocusShell,
            TimeSpan.FromMilliseconds(500),
            pipeName: UniquePipeName("absent"));
        elapsed.Stop();

        Assert.False(forwarded);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(3),
            $"An absent Shell took {elapsed.ElapsedMilliseconds} ms to report failure.");
    }

    [Fact]
    public async Task Shell_activation_reports_failure_when_the_instance_never_acknowledges()
    {
        var pipeName = UniquePipeName("silent");
        using var listening = new CancellationTokenSource();
        var silentServer = RunSilentServerAsync(pipeName, listening.Token);

        var elapsed = Stopwatch.StartNew();
        var forwarded = await ShellActivationPipe.TryForwardAsync(
            ShellActivationRequest.FocusShell,
            TimeSpan.FromSeconds(1),
            pipeName: pipeName);
        elapsed.Stop();

        Assert.False(forwarded);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5),
            $"A silent Shell took {elapsed.ElapsedMilliseconds} ms to report failure.");

        listening.Cancel();
        await silentServer;
    }

    [Fact]
    public async Task Shell_activation_serves_two_concurrent_requests()
    {
        var pipeName = UniquePipeName("concurrent");
        var delivered = new List<ShellActivationRequest>();
        await using var pipe = new ShellActivationPipe(
            request =>
            {
                lock (delivered)
                {
                    delivered.Add(request);
                }

                return Task.CompletedTask;
            },
            pipeName);
        pipe.Start();

        var first = ShellActivationRequest.ForTool(new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=first"));
        var second = ShellActivationRequest.ForTool(new ToolActivationRequest(
            "remote-notifications",
            "inbox",
            "mypowertools://remote-notification?id=second"));
        var results = await Task.WhenAll(
            ShellActivationPipe.TryForwardAsync(first, RequestBudget, pipeName: pipeName),
            ShellActivationPipe.TryForwardAsync(second, RequestBudget, pipeName: pipeName));

        Assert.Equal([true, true], results);
        var served = await WaitForDeliveredRequestsAsync(delivered, 2);
        Assert.Contains(first, served);
        Assert.Contains(second, served);
    }

    [Theory]
    // A length header past the payload cap, a non-positive length, and a well-framed body that is
    // not an activation request at all.
    [InlineData(64 * 1024 + 1, "")]
    [InlineData(0, "")]
    [InlineData(5, "hello")]
    public async Task Shell_activation_rejects_an_invalid_request_and_keeps_serving(
        int declaredLength,
        string body)
    {
        var pipeName = UniquePipeName("invalid");
        var received = new TaskCompletionSource<ShellActivationRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pipe = new ShellActivationPipe(
            request =>
            {
                received.TrySetResult(request);
                return Task.CompletedTask;
            },
            pipeName);
        pipe.Start();

        var acknowledged = await SendRawRequestAsync(
            pipeName,
            declaredLength,
            Encoding.UTF8.GetBytes(body));

        Assert.False(acknowledged);
        Assert.False(received.Task.IsCompleted);
        Assert.True(await ShellActivationPipe.TryForwardAsync(
            ShellActivationRequest.FocusShell,
            RequestBudget,
            pipeName: pipeName));
        Assert.Equal(
            ShellActivationRequest.FocusShell,
            await received.Task.WaitAsync(RequestBudget));
    }

    [Fact]
    public void Resident_lifecycle_requests_distinguish_prewarm_and_shutdown()
    {
        Assert.False(ShellActivationRequest.PrewarmShell.ShowShell);
        Assert.False(ShellActivationRequest.PrewarmShell.ShutdownShell);
        Assert.False(ShellActivationRequest.Shutdown.ShowShell);
        Assert.True(ShellActivationRequest.Shutdown.ShutdownShell);
    }

    [Fact]
    public void Shell_instance_lock_allows_one_owner()
    {
        var suffix = $"MyPowerTools.Shell.Tests.{Guid.NewGuid():N}";
        var mutexName = OperatingSystem.IsWindows() ? $@"Local\{suffix}" : suffix;
        using var first = ShellInstanceLock.Acquire(mutexName);
        var secondAcquired = true;
        Exception? contenderError = null;
        var contender = new Thread(() =>
        {
            try
            {
                using var second = ShellInstanceLock.Acquire(mutexName);
                secondAcquired = second.Acquired;
            }
            catch (Exception exception)
            {
                contenderError = exception;
            }
        });
        contender.Start();

        Assert.True(first.Acquired);
        Assert.True(contender.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(contenderError);
        Assert.False(secondAcquired);
    }
}
